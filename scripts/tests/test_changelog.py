import datetime as dt
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import changelog  # noqa: E402


SAMPLE = """# Changelog

Curated notes.

## [Unreleased]

### Added

- New branches.

## [v0.2.0](https://example.test/v0.2.0) - 2026-03-22

### Changed

- Existing curated release.
"""


class ChangelogTests(unittest.TestCase):
    def test_version_validation_rejects_shell_input_and_non_semver(self):
        for value in (
            "", "1.2", "01.2.3", "1.2.3-01", "v1.2.3\n",
            "1.2.3+metadata", "$(whoami)", "1.2.3;touch x", "1.2.3/../../x",
            "1.2.3-rc..1", "1.2.3-" + "a" * 150,
        ):
            with self.subTest(value=value), self.assertRaises(ValueError):
                changelog.validate_version(value)
        self.assertEqual("1.2.3-rc.1", changelog.validate_version("v1.2.3-rc.1"))
        self.assertEqual("1.2.3", changelog.validate_version("1.2.3"))

    def test_notes_select_exact_version_not_latest_or_unreleased(self):
        notes = changelog.release_notes(SAMPLE, "v0.2.0")
        self.assertIn("Existing curated release.", notes)
        self.assertNotIn("New branches.", notes)
        with self.assertRaisesRegex(ValueError, "no entry"):
            changelog.release_notes(SAMPLE, "0.3.0")

    def test_empty_unreleased_cannot_be_released(self):
        empty = SAMPLE.replace("### Added\n\n- New branches.", "<!-- pending -->\n\n### Added")
        with self.assertRaisesRegex(ValueError, "empty"):
            changelog.prepare_release(empty, "0.3.0", dt.date(2026, 10, 30))

    def test_prepare_moves_curated_body_without_changing_prior_release(self):
        prepared = changelog.prepare_release(SAMPLE, "0.3.0", dt.date(2026, 10, 30))
        _, sections = changelog.parse_changelog(prepared)
        self.assertIsNone(sections[0].version)
        self.assertEqual("0.3.0", sections[1].version)
        self.assertEqual(dt.date(2026, 10, 30), sections[1].date)
        self.assertEqual("### Added\n\n- New branches.", sections[1].body)
        self.assertEqual(changelog.release_notes(SAMPLE, "0.2.0"), changelog.release_notes(prepared, "0.2.0"))
        with self.assertRaises(ValueError):
            changelog.unreleased_notes(prepared)

    def test_duplicate_invalid_date_and_nonfirst_unreleased_are_rejected(self):
        for text in (
            SAMPLE + "\n## 0.2.0 - 2026-03-22\n\n- Duplicate.\n",
            SAMPLE.replace("2026-03-22", "2026-02-30"),
            SAMPLE.replace("## [Unreleased]", "## 0.3.0"),
            SAMPLE.replace("## [Unreleased]", "## [0.3.0] - 2026-10-30") + "\n## [Unreleased]\n",
        ):
            with self.subTest(text=text), self.assertRaises(ValueError):
                changelog.parse_changelog(text)

    def test_prepare_rejects_older_or_existing_release(self):
        for value in ("0.2.0", "0.1.9", "0.2.0-rc.1"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                changelog.prepare_release(SAMPLE, value, dt.date(2026, 10, 30))

    def test_semver_prerelease_order(self):
        versions = ["1.0.0", "1.0.0-rc.1", "1.0.0-beta.11", "1.0.0-beta.2", "1.0.0-alpha"]
        self.assertEqual(versions, sorted(versions, key=changelog.version_key, reverse=True))

    def test_refresh_preserves_curated_unreleased_and_existing_versions(self):
        generated = """# Generated

## [Unreleased]

- Ignore generated pending commits.

## [v0.2.0] - 2026-03-22

- Do not overwrite curated notes.

## [v0.1.0] - 2026-03-19

- Imported tagged history.
"""
        merged = changelog.merge_history(SAMPLE, generated)
        self.assertEqual(changelog.unreleased_notes(SAMPLE), changelog.unreleased_notes(merged))
        self.assertEqual(changelog.release_notes(SAMPLE, "0.2.0"), changelog.release_notes(merged, "0.2.0"))
        self.assertIn("Imported tagged history.", changelog.release_notes(merged, "0.1.0"))
        self.assertNotIn("Ignore generated pending", merged)
        self.assertEqual(merged, changelog.merge_history(merged, generated))

    def test_refresh_places_new_tags_in_semver_order(self):
        generated = "# Generated\n\n## [v0.3.0] - 2026-10-30\n\n- New tagged release.\n"
        _, sections = changelog.parse_changelog(changelog.merge_history(SAMPLE, generated))
        self.assertEqual([None, "0.3.0", "0.2.0"], [section.version for section in sections])

    def test_notes_command_leaves_source_unchanged(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "CHANGELOG.md"
            output = Path(directory) / "notes.md"
            source.write_text(SAMPLE, encoding="utf-8")
            status = changelog.main(["--file", str(source), "notes", "--version", "0.2.0", "--output", str(output)])
            self.assertEqual(0, status)
            self.assertEqual(SAMPLE, source.read_text(encoding="utf-8"))
            self.assertIn("Existing curated release.", output.read_text(encoding="utf-8"))

    def test_git_changelog_invoked_without_shell_and_with_pinned_config(self):
        with patch("changelog.subprocess.run") as run:
            def write_generated(arguments, **kwargs):
                Path(arguments[arguments.index("--output") + 1]).write_text(SAMPLE, encoding="utf-8")
            run.side_effect = write_generated
            self.assertEqual(SAMPLE, changelog.generate_history(changelog.ROOT))
            args, kwargs = run.call_args
            self.assertIn("git_changelog", args[0])
            self.assertIn(str(changelog.ROOT / ".git-changelog.toml"), args[0])
            self.assertNotIn("shell", kwargs)
            self.assertTrue(kwargs["check"])


if __name__ == "__main__":
    unittest.main()
