"""Regression tests for the documentation allowlist, links and owned outputs."""

from __future__ import annotations

import importlib.util
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))
import build_docs  # noqa: E402


class DocumentationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="modemkit-docs-tests-")
        self.root = Path(self.temporary.name)
        for source in build_docs.PAGES:
            self.write(source, "# Example\n\n## Existing heading\n\nText.\n")
        self.write("Example/Example.cs", "namespace Example;\n")
        self.write(".github/workflows/ci.yml", "name: ci\n")
        shutil.copy2(SCRIPTS.parent / "mkdocs.yml", self.root / "mkdocs.yml")

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def write(self, relative: str, text: str) -> None:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def test_page_links_are_mapped_relative_to_staged_pages(self) -> None:
        self.assertEqual(
            "../index.md#existing-heading",
            build_docs.rewrite_target("../README.md#existing-heading", "docs/README.md", self.root),
        )
        self.assertEqual(
            "index.md",
            build_docs.rewrite_target("README.md", "Zmodem.Protocol/PROTOCOL_NOTES.md", self.root),
        )
        self.assertEqual(
            "../license.md",
            build_docs.rewrite_target("../LICENSE", "docs/README.md", self.root),
        )
        self.assertEqual(
            "docs/integration.md?view=all#host-event-loop",
            build_docs.rewrite_target("/docs/integration.md?view=all#host-event-loop", "README.md", self.root),
        )

    def test_code_workflows_and_directories_link_to_verified_repository_paths(self) -> None:
        self.assertEqual(
            "https://github.com/Modemkit/Modemkit/blob/main/Example/Example.cs#L1",
            build_docs.rewrite_target("../Example/Example.cs#L1", "docs/README.md", self.root),
        )
        self.assertEqual(
            "https://github.com/Modemkit/Modemkit/tree/main/Example",
            build_docs.rewrite_target("Example", "README.md", self.root),
        )
        self.assertEqual(
            "https://github.com/Modemkit/Modemkit/blob/main/.github/workflows/ci.yml",
            build_docs.rewrite_target("../.github/workflows/ci.yml", "docs/testing.md", self.root),
        )

    def test_quoted_paths_titles_and_reference_definitions_survive(self) -> None:
        self.write("Example/With Spaces.cs", "// Source\n")
        source = (
            '[code](<../Example/With Spaces.cs> "source title")\n'
            '[guide][integration]\n'
            '[integration]: ../docs/integration.md#existing-heading "Guide"\n'
            '[`Example.cs`](../Example/Example.cs)\n'
        )
        result = build_docs.rewrite_markdown(source, "docs/README.md", self.root)
        self.assertIn('<https://github.com/Modemkit/Modemkit/blob/main/Example/With%20Spaces.cs> "source title"', result)
        self.assertIn('[integration]: integration.md#existing-heading "Guide"', result)
        self.assertIn('[`Example.cs`](https://github.com/Modemkit/Modemkit/blob/main/Example/Example.cs)', result)

    def test_external_and_same_page_links_are_unchanged(self) -> None:
        for target in ("https://example.com/a?q=b#c", "mailto:reader@example.com", "//example.com/a", "#existing-heading"):
            with self.subTest(target=target):
                self.assertEqual(target, build_docs.rewrite_target(target, "README.md", self.root))

    def test_code_examples_are_not_rewritten_or_validated_as_links(self) -> None:
        text = (
            "```markdown\n[example](missing.md)\n```\n"
            "~~~text\n[example](also-missing.cs)\n~~~\n"
            "`[example](missing.md)` and ``[example](missing.cs)``\n"
            "    [indented](missing.md)\n"
        )
        self.assertEqual(text, build_docs.rewrite_markdown(text, "README.md", self.root))

    def test_missing_unlisted_private_and_escaping_targets_fail(self) -> None:
        self.write("private.md", "# Not published\n")
        self.write("artifacts/capture.bin", "capture")
        self.write(".git/config", "private")
        for target in ("missing.cs", "private.md", "artifacts/capture.bin", ".git/config", "../../outside.cs"):
            with self.subTest(target=target):
                with self.assertRaises(build_docs.DocumentationError):
                    build_docs.rewrite_target(target, "README.md", self.root)

    def test_only_allowlisted_pages_are_staged(self) -> None:
        self.write("private.md", "Do not publish.\n")
        output = build_docs.prepare_sources(self.root)
        actual = {
            path.relative_to(output).as_posix()
            for path in output.rglob("*")
            if path.is_file() and path.name != build_docs.MARKER_NAME
        }
        self.assertEqual(set(build_docs.PAGES.values()), actual)
        self.assertNotIn("private.md", actual)
        self.assertIn("# License", (output / "license.md").read_text(encoding="utf-8"))
        self.assertIn("/blob/main/README.md", (output / "index.md").read_text(encoding="utf-8"))

    def test_validation_precedes_cleanup(self) -> None:
        output = build_docs.prepare_sources(self.root)
        sentinel = output / "preserve-until-validation.txt"
        sentinel.write_text("keep", encoding="utf-8")
        (self.root / "docs/integration.md").unlink()
        with self.assertRaises(build_docs.DocumentationError):
            build_docs.prepare_sources(self.root)
        self.assertEqual("keep", sentinel.read_text(encoding="utf-8"))

    def test_owned_cleanup_preserves_other_artifacts_and_rejects_unmarked_outputs(self) -> None:
        self.write("artifacts/unrelated.txt", "keep")
        output = build_docs.clean_owned_directory(self.root, build_docs.SOURCE_DIRECTORY)
        (output / "stale.md").write_text("old", encoding="utf-8")
        build_docs.clean_owned_directory(self.root, build_docs.SOURCE_DIRECTORY)
        self.assertFalse((output / "stale.md").exists())
        self.assertEqual("keep", (self.root / "artifacts/unrelated.txt").read_text(encoding="utf-8"))
        unmarked = self.root / build_docs.SITE_DIRECTORY
        unmarked.mkdir()
        (unmarked / "keep.txt").write_text("keep", encoding="utf-8")
        with self.assertRaises(build_docs.DocumentationError):
            build_docs.clean_owned_directory(self.root, build_docs.SITE_DIRECTORY)
        self.assertTrue((unmarked / "keep.txt").exists())
        with self.assertRaises(build_docs.DocumentationError):
            build_docs.clean_owned_directory(self.root, "artifacts")

    def test_redirected_output_is_rejected(self) -> None:
        outside = self.root / "outside"
        outside.mkdir()
        (outside / "keep.txt").write_text("keep", encoding="utf-8")
        (self.root / "artifacts").mkdir()
        try:
            (self.root / build_docs.SOURCE_DIRECTORY).symlink_to(outside, target_is_directory=True)
        except OSError as error:
            self.skipTest(f"Creating symlinks is unavailable: {error}")
        with self.assertRaises(build_docs.DocumentationError):
            build_docs.clean_owned_directory(self.root, build_docs.SOURCE_DIRECTORY)
        self.assertTrue((outside / "keep.txt").exists())

    def test_allowlisted_name_cannot_publish_a_redirected_source(self) -> None:
        self.write("private.md", "# Do not publish\n")
        source = self.root / "README.md"
        source.unlink()
        try:
            source.symlink_to(self.root / "private.md")
        except OSError as error:
            self.skipTest(f"Creating symlinks is unavailable: {error}")
        with self.assertRaises(build_docs.DocumentationError):
            build_docs.prepare_sources(self.root)

    @unittest.skipUnless(importlib.util.find_spec("yaml"), "PyYAML is not installed")
    def test_altered_renderer_paths_fail_before_staging_or_cleanup(self) -> None:
        original = (self.root / "mkdocs.yml").read_text(encoding="utf-8")
        source = build_docs.clean_owned_directory(self.root, build_docs.SOURCE_DIRECTORY)
        site = build_docs.clean_owned_directory(self.root, build_docs.SITE_DIRECTORY)
        (source / "keep.txt").write_text("source", encoding="utf-8")
        (site / "keep.txt").write_text("site", encoding="utf-8")
        self.write("artifacts/unrelated/keep.txt", "unrelated")
        for key, expected in (("docs_dir", build_docs.SOURCE_DIRECTORY), ("site_dir", build_docs.SITE_DIRECTORY)):
            for alternate in ("artifacts/unrelated", ".", "/", self.root.as_posix()):
                for prepare_only in (False, True):
                    with self.subTest(key=key, alternate=alternate, prepare_only=prepare_only):
                        self.write("mkdocs.yml", original.replace(f"{key}: {expected}", f"{key}: {alternate}"))
                        with self.assertRaises(build_docs.DocumentationError):
                            build_docs.build(self.root, prepare_only=prepare_only)
                        self.assertEqual("source", (source / "keep.txt").read_text(encoding="utf-8"))
                        self.assertEqual("site", (site / "keep.txt").read_text(encoding="utf-8"))
                        self.assertEqual("unrelated", (self.root / "artifacts/unrelated/keep.txt").read_text(encoding="utf-8"))

    @unittest.skipUnless(importlib.util.find_spec("yaml"), "PyYAML is not installed")
    def test_configuration_indirection_is_rejected_before_mutation(self) -> None:
        original = (self.root / "mkdocs.yml").read_text(encoding="utf-8")
        for text in (
            original + "\nINHERIT: another.yml\n",
            original + "\nconfig_file_path: another.yml\n",
            original.replace("site_dir: artifacts/docs-site", "site_dir: !ENV [DOCS_OUTPUT, artifacts/docs-site]"),
        ):
            with self.subTest(configuration=text[-100:]):
                self.write("mkdocs.yml", text)
                with self.assertRaises(build_docs.DocumentationError):
                    build_docs.build(self.root)
                self.assertFalse((self.root / "artifacts").exists())

    @unittest.skipUnless(importlib.util.find_spec("yaml"), "PyYAML is not installed")
    def test_renderer_output_or_source_symlinks_are_rejected_before_mutation(self) -> None:
        outside = self.root / "outside"
        outside.mkdir()
        (outside / "keep.txt").write_text("keep", encoding="utf-8")
        (self.root / "artifacts").mkdir()
        for relative in (build_docs.SOURCE_DIRECTORY, build_docs.SITE_DIRECTORY):
            link = self.root / relative
            try:
                link.symlink_to(outside, target_is_directory=True)
            except OSError as error:
                self.skipTest(f"Creating symlinks is unavailable: {error}")
            try:
                with self.assertRaises(build_docs.DocumentationError):
                    build_docs.build(self.root)
                self.assertEqual("keep", (outside / "keep.txt").read_text(encoding="utf-8"))
            finally:
                link.unlink()

    @unittest.skipUnless(importlib.util.find_spec("yaml"), "PyYAML is not installed")
    def test_renderer_receives_fixed_explicit_output_and_strict_mode(self) -> None:
        with mock.patch.object(build_docs.subprocess, "run") as render:
            output = build_docs.build(self.root)
        command = render.call_args.args[0]
        self.assertIn("--strict", command)
        self.assertEqual(str(output), command[command.index("--site-dir") + 1])

    @unittest.skipUnless(importlib.util.find_spec("mkdocs"), "MkDocs is not installed")
    def test_strict_build_accepts_valid_pages_and_rejects_missing_anchor(self) -> None:
        output = build_docs.build(self.root)
        self.assertTrue((output / "index.html").is_file())
        self.assertTrue((output / "search/search_index.json").is_file())
        self.write("README.md", "# Example\n\n[Bad anchor](docs/integration.md#missing-anchor)\n")
        with self.assertRaises(subprocess.CalledProcessError):
            build_docs.build(self.root)
        self.assertEqual(
            build_docs.MARKER_CONTENT,
            (output / build_docs.MARKER_NAME).read_text(encoding="utf-8"),
        )


if __name__ == "__main__":
    unittest.main()
