"""Regression tests for the documentation allowlist, links and owned outputs."""

from __future__ import annotations

import importlib.util
import json
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
        self.root = Path(self.temporary.name).resolve()
        for source in build_docs.PAGES:
            self.write(source, "# Example\n\n## Existing heading\n\nText.\n")
        self.write("Example/Example.cs", "namespace Example;\n")
        self.write(".github/workflows/ci.yml", "name: ci\n")
        shutil.copy2(SCRIPTS.parent / "zensical.toml", self.root / "zensical.toml")

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

    def test_links_in_tabs_admonitions_details_and_cards_are_rewritten(self) -> None:
        for opening, closing in (
            ('!!! note "Guide"\n', ""),
            ('???+ tip "Guide"\n', ""),
            ('=== "Windows"\n', ""),
            ("-   Card title\n", ""),
            ('<div class="grid cards" markdown>\n\n-   Card title\n', "\n</div>\n"),
            ("1. Ordered guide\n", ""),
        ):
            with self.subTest(opening=opening):
                text = opening + '\n    [Protocol](../Xmodem.Protocol/README.md#integration)\n' + closing
                rewritten = build_docs.rewrite_markdown(text, "docs/README.md", self.root)
                self.assertIn("[Protocol](../Xmodem.Protocol/index.md#integration)", rewritten)
                self.assertTrue(rewritten.startswith(opening))

    def test_nested_containers_rewrite_links_and_preserve_nested_code(self) -> None:
        text = (
            '=== "Windows"\n\n'
            '    !!! warning "Guide"\n\n'
            '        [Protocol](../Xmodem.Protocol/README.md)\n\n'
            '        ```markdown\n'
            '        [Example](missing.md)\n'
            '        ```\n\n'
            '            [Indented example](also-missing.md)\n\n'
            '        `[Inline example](missing-inline.md)`\n\n'
            '    [Guide](../docs/README.md)\n\n'
            '=== "Linux"\n\n'
            '    [Protocol](../Ymodem.Protocol/README.md)\n\n'
            'Outside.\n\n'
            '    [Real indented code](not-a-link.md)\n'
        )
        rewritten = build_docs.rewrite_markdown(text, "docs/README.md", self.root)
        self.assertIn("[Protocol](../Xmodem.Protocol/index.md)", rewritten)
        self.assertIn("[Protocol](../Ymodem.Protocol/index.md)", rewritten)
        self.assertIn("[Guide](index.md)", rewritten)
        for unchanged in (
            "[Example](missing.md)", "[Indented example](also-missing.md)",
            "[Inline example](missing-inline.md)", "[Real indented code](not-a-link.md)",
        ):
            self.assertIn(unchanged, rewritten)

    def test_nested_container_reference_links_are_rewritten(self) -> None:
        text = '!!! note "Guide"\n\n    [Protocol][api]\n\n    [api]: ../Xmodem.Protocol/README.md "API"\n'
        rewritten = build_docs.rewrite_markdown(text, "docs/README.md", self.root)
        self.assertIn('[api]: ../Xmodem.Protocol/index.md "API"', rewritten)

    def test_nested_markdown_links_still_reject_missing_and_private_targets(self) -> None:
        self.write("private.md", "# Not published\n")
        for target in ("../missing.md", "../private.md", "../artifacts/capture.bin"):
            with self.subTest(target=target):
                if target.endswith("capture.bin"):
                    self.write("artifacts/capture.bin", "private")
                text = f'=== "Windows"\n\n    !!! note "Guide"\n\n        [Protocol]({target})\n'
                with self.assertRaises(build_docs.DocumentationError):
                    build_docs.rewrite_markdown(text, "docs/README.md", self.root)

    def test_missing_unlisted_private_and_escaping_targets_fail(self) -> None:
        self.write("private.md", "# Not published\n")
        self.write("artifacts/capture.bin", "capture")
        self.write(".cache/entry", "private")
        self.write(".git/config", "private")
        for target in ("missing.cs", "private.md", "artifacts/capture.bin", ".cache/entry", ".git/config", "../../outside.cs"):
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

    def test_altered_renderer_paths_fail_before_staging_or_cleanup(self) -> None:
        original = (self.root / "zensical.toml").read_text(encoding="utf-8")
        source = build_docs.clean_owned_directory(self.root, build_docs.SOURCE_DIRECTORY)
        site = build_docs.clean_owned_directory(self.root, build_docs.SITE_DIRECTORY)
        (source / "keep.txt").write_text("source", encoding="utf-8")
        (site / "keep.txt").write_text("site", encoding="utf-8")
        self.write("artifacts/unrelated/keep.txt", "unrelated")
        for key, expected in (("docs_dir", build_docs.SOURCE_DIRECTORY), ("site_dir", build_docs.SITE_DIRECTORY)):
            for alternate in ("artifacts/unrelated", ".", "/", self.root.as_posix()):
                for prepare_only in (False, True):
                    with self.subTest(key=key, alternate=alternate, prepare_only=prepare_only):
                        self.write("zensical.toml", original.replace(f'{key} = "{expected}"', f'{key} = "{alternate}"'))
                        with self.assertRaises(build_docs.DocumentationError):
                            build_docs.build(self.root, prepare_only=prepare_only)
                        self.assertEqual("source", (source / "keep.txt").read_text(encoding="utf-8"))
                        self.assertEqual("site", (site / "keep.txt").read_text(encoding="utf-8"))
                        self.assertEqual("unrelated", (self.root / "artifacts/unrelated/keep.txt").read_text(encoding="utf-8"))

    def test_configuration_indirection_is_rejected_before_mutation(self) -> None:
        original = (self.root / "zensical.toml").read_text(encoding="utf-8")
        for text in (
            original.replace("[project]", '[project]\nINHERIT = "another.toml"'),
            original.replace("[project]", '[project]\nconfig_file_path = "another.toml"'),
            original.replace("[project]", '[project]\ncache_dir = "artifacts/unrelated"'),
            original.replace("[project]", '[project]\nroot_dir = "artifacts/unrelated"'),
            original.replace('[project]', 'root_dir = "artifacts/unrelated"\n[project]'),
            original.replace('site_dir = "artifacts/docs-site"', 'site_dir = !ENV [DOCS_OUTPUT, artifacts/docs-site]'),
        ):
            with self.subTest(configuration=text[-100:]):
                self.write("zensical.toml", text)
                with self.assertRaises(build_docs.DocumentationError):
                    build_docs.build(self.root)
                self.assertFalse((self.root / "artifacts").exists())

    def test_renderer_output_or_source_symlinks_are_rejected_before_mutation(self) -> None:
        outside = self.root / "outside"
        outside.mkdir()
        (outside / "keep.txt").write_text("keep", encoding="utf-8")
        (self.root / "artifacts").mkdir()
        for relative in build_docs.OWNED_DIRECTORIES:
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

    def test_renderer_receives_native_configuration_clean_and_strict_mode(self) -> None:
        with mock.patch.object(build_docs.subprocess, "run") as render:
            output = build_docs.build(self.root)
        command = render.call_args.args[0]
        self.assertEqual([sys.executable, "-m", "zensical", "build"], command[:4])
        self.assertIn("--clean", command)
        self.assertIn("--strict", command)
        self.assertNotIn("--site-dir", command)
        self.assertEqual(str(self.root / "zensical.toml"), command[command.index("--config-file") + 1])
        self.assertEqual(self.root / build_docs.SITE_DIRECTORY, output)

    @unittest.skipUnless(importlib.util.find_spec("zensical"), "Zensical is not installed")
    def test_strict_build_accepts_valid_pages_and_rejects_missing_anchor(self) -> None:
        output = build_docs.build(self.root)
        self.assertTrue((output / "index.html").is_file())
        index = json.loads((output / "search.json").read_text(encoding="utf-8"))
        self.assertIsInstance(index["items"], list)
        self.assertTrue(index["items"])
        self.write("README.md", "# Example\n\n[Bad anchor](docs/integration.md#missing-anchor)\n")
        with self.assertRaises(subprocess.CalledProcessError):
            build_docs.build(self.root)
        self.assertEqual(
            build_docs.MARKER_CONTENT,
            (output / build_docs.MARKER_NAME).read_text(encoding="utf-8"),
        )
        self.assertEqual(
            build_docs.MARKER_CONTENT,
            (self.root / build_docs.CACHE_DIRECTORY / build_docs.MARKER_NAME).read_text(encoding="utf-8"),
        )

    def test_unmarked_cache_is_rejected_before_staged_files_change(self) -> None:
        original = build_docs.prepare_sources(self.root)
        (original / "sentinel.txt").write_text("keep", encoding="utf-8")
        self.write(".cache/unrelated.txt", "private")
        for prepare_only in (False, True):
            with self.subTest(prepare_only=prepare_only):
                with self.assertRaises(build_docs.DocumentationError):
                    build_docs.build(self.root, prepare_only=prepare_only)
                self.assertEqual("keep", (original / "sentinel.txt").read_text(encoding="utf-8"))
                self.assertEqual("private", (self.root / ".cache/unrelated.txt").read_text(encoding="utf-8"))
                self.assertFalse((self.root / build_docs.SITE_DIRECTORY).exists())

    def test_prepare_only_marks_real_cache_for_preview_without_rendering(self) -> None:
        with mock.patch.object(build_docs.subprocess, "run") as render:
            source = build_docs.build(self.root, prepare_only=True)
        render.assert_not_called()
        self.assertEqual(self.root / build_docs.SOURCE_DIRECTORY, source)
        self.assertEqual(
            build_docs.MARKER_CONTENT,
            (self.root / build_docs.CACHE_DIRECTORY / build_docs.MARKER_NAME).read_text(encoding="utf-8"),
        )

    def test_failed_renderer_restores_cache_ownership_after_native_clean(self) -> None:
        def fail_after_cache_clean(*args: object, **kwargs: object) -> None:
            shutil.rmtree(self.root / build_docs.CACHE_DIRECTORY)
            raise subprocess.CalledProcessError(1, ["zensical"])

        with mock.patch.object(build_docs.subprocess, "run", side_effect=fail_after_cache_clean):
            with self.assertRaises(subprocess.CalledProcessError):
                build_docs.build(self.root)
        for relative in (build_docs.SITE_DIRECTORY, build_docs.CACHE_DIRECTORY):
            self.assertEqual(
                build_docs.MARKER_CONTENT,
                (self.root / relative / build_docs.MARKER_NAME).read_text(encoding="utf-8"),
            )
        with mock.patch.object(build_docs.subprocess, "run"):
            build_docs.build(self.root)

    def test_navigation_requires_each_allowlisted_page_exactly_once(self) -> None:
        original = (self.root / "zensical.toml").read_text(encoding="utf-8")
        for alternate in (
            original.replace('{ "Home" = "index.md" },', ""),
            original.replace('{ "Home" = "index.md" },', '{ "Home" = "index.md" }, { "Duplicate" = "index.md" },'),
            original.replace('{ "Home" = "index.md" },', '{ "Home" = "typo.md" },'),
            original.replace('{ "Home" = "index.md" },', '{ "Home" = "https://example.com/" },'),
        ):
            with self.subTest(configuration=alternate[:500]):
                self.write("zensical.toml", alternate)
                with self.assertRaises(build_docs.DocumentationError):
                    build_docs.build(self.root)
                self.assertFalse((self.root / "artifacts").exists())
                self.assertFalse((self.root / ".cache").exists())

    def test_link_anchor_validation_cannot_be_disabled(self) -> None:
        original = (self.root / "zensical.toml").read_text(encoding="utf-8")
        for setting in ("strict", "invalid_links", "invalid_link_anchors"):
            with self.subTest(setting=setting):
                self.write("zensical.toml", original.replace(f"{setting} = true", f"{setting} = false"))
                with self.assertRaises(build_docs.DocumentationError):
                    build_docs.build(self.root)
                self.assertFalse((self.root / "artifacts").exists())

    def test_nested_cache_symlink_is_rejected_without_touching_other_outputs(self) -> None:
        cache = build_docs.clean_owned_directory(self.root, build_docs.CACHE_DIRECTORY)
        nested = cache / "nested"
        nested.mkdir()
        outside = self.root / "outside"
        outside.mkdir()
        self.write("outside/keep.txt", "private")
        link = nested / "redirect"
        try:
            link.symlink_to(outside, target_is_directory=True)
        except OSError as error:
            self.skipTest(f"Creating symlinks is unavailable: {error}")
        with self.assertRaises(build_docs.DocumentationError):
            build_docs.build(self.root)
        self.assertTrue(link.is_symlink())
        self.assertEqual("private", (outside / "keep.txt").read_text(encoding="utf-8"))
        self.assertFalse((self.root / "artifacts").exists())

    @unittest.skipUnless(importlib.util.find_spec("zensical"), "Zensical is not installed")
    def test_fresh_build_recreates_missing_pages_and_removes_stale_output(self) -> None:
        output = build_docs.build(self.root)
        (output / "docs/integration/index.html").unlink()
        (output / "obsolete.html").write_text("obsolete", encoding="utf-8")
        self.write("docs/integration.md", "# Changed integration\n\nUnique replacement text.\n")
        build_docs.build(self.root)
        rendered = (output / "docs/integration/index.html").read_text(encoding="utf-8")
        self.assertIn("Unique replacement text.", rendered)
        self.assertFalse((output / "obsolete.html").exists())
        index = json.loads((output / "search.json").read_text(encoding="utf-8"))
        self.assertTrue(any("Unique replacement text." in item.get("text", "") for item in index["items"]))

    @unittest.skipUnless(importlib.util.find_spec("zensical"), "Zensical is not installed")
    def test_renderer_strict_mode_rejects_missing_page_independently(self) -> None:
        build_docs.build(self.root)
        source = self.root / build_docs.SOURCE_DIRECTORY
        build_docs.clean_owned_directory(self.root, build_docs.SITE_DIRECTORY)
        (source / "index.md").write_text("# Example\n\n[Missing](missing.md)\n", encoding="utf-8")
        with self.assertRaises(subprocess.CalledProcessError):
            subprocess.run(
                [sys.executable, "-m", "zensical", "build", "--clean", "--strict", "--config-file", str(self.root / "zensical.toml")],
                cwd=self.root,
                check=True,
            )

    @unittest.skipUnless(importlib.util.find_spec("zensical"), "Zensical is not installed")
    def test_native_theme_features_render_from_real_markdown(self) -> None:
        self.write(
            "README.md",
            '# Feature smoke test\n\n'
            '=== "Windows"\n\n'
            '    ```console\n'
            '    dotnet test\n'
            '    ```\n\n'
            '=== "Linux"\n\n'
            '    ```console\n'
            '    dotnet build\n'
            '    ```\n\n'
            '!!! warning "Persist before acknowledging"\n\n'
            '    Save the received bytes first. [API](Xmodem.Protocol/README.md)\n\n'
            '??? note "Protocol detail"\n\n'
            '    Retries preserve the committed offset.\n\n'
            '```mermaid\n'
            'flowchart LR\n'
            '  Host --> Engine\n'
            '```\n\n'
            '```csharp\n'
            'var accepted = true; // (1)!\n'
            '```\n\n'
            '1. Confirm persistence before acknowledging.\n',
        )
        output = build_docs.build(self.root)
        rendered = (output / "index.html").read_text(encoding="utf-8")
        self.assertIn("tabbed-set", rendered)
        self.assertIn("admonition warning", rendered)
        self.assertIn("<details", rendered)
        self.assertIn('class="mermaid"', rendered)
        self.assertIn("content.code.annotate", rendered)
        self.assertIn("content.code.copy", rendered)
        self.assertIn("navigation.instant", rendered)


if __name__ == "__main__":
    unittest.main()
