"""Release metadata, artifact validation, and mocked publication regressions."""

from __future__ import annotations

import json
import contextlib
import io
import hashlib
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import changelog
import release


HEAD = "a" * 40
CHANGELOG = """# Changelog

## [Unreleased]

### Added
- Preview changes.

## [1.2.3] - 2026-10-30

### Fixed
- Stable release notes.

## [1.2.3-rc.1] - 2026-10-29

### Added
- Release candidate notes.
"""


def command_result(output: str = "", code: int = 0, error: str = "") -> subprocess.CompletedProcess[str]:
    return subprocess.CompletedProcess([], code, output, error)


class ReleaseTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="modemkit-release-test-")
        self.root = Path(self.temp.name)
        (self.root / "ModemKit.csproj").write_text(
            "<Project><PropertyGroup><VersionPrefix>0.2.0</VersionPrefix></PropertyGroup></Project>",
            encoding="utf-8",
        )
        (self.root / "CHANGELOG.md").write_text(CHANGELOG, encoding="utf-8")
        self.directory = self.root / "release"
        self.git = Mock(return_value=command_result(HEAD + "\n"))

    def tearDown(self) -> None:
        self.temp.cleanup()

    def env(self, **overrides: str) -> dict[str, str]:
        result = {
            "GITHUB_EVENT_NAME": "workflow_dispatch",
            "GITHUB_REF": "refs/heads/master",
            "GITHUB_RUN_NUMBER": "42",
            "GITHUB_RUN_ATTEMPT": "2",
            "INPUT_VERSION": "",
            "INPUT_PUBLISH": "None",
            "NUGET_USER": "",
        }
        result.update(overrides)
        return result

    def package(
        self, package_id: str, version: str, *,
        metadata_id: str | None = None, metadata_version: str | None = None,
        dependency_version: str | None = None, commit: str = HEAD,
    ) -> Path:
        self.directory.mkdir(exist_ok=True)
        path = self.directory / f"{package_id}.{version}.nupkg"
        dependencies = ""
        if package_id == "ModemKit":
            groups = []
            for target in ("net10.0", ".NETStandard2.0"):
                group = "".join(
                    f'<dependency id="{item}" version="{dependency_version or version}"/>'
                    for item in release.PACKAGE_IDS if item != "ModemKit"
                )
                groups.append(f'<group targetFramework="{target}">{group}</group>')
            dependencies = "<dependencies>" + "".join(groups) + "</dependencies>"
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr(
                f"{package_id}.nuspec",
                '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">'
                f"<metadata><id>{metadata_id or package_id}</id><version>{metadata_version or version}</version>"
                f'{dependencies}<repository type="git" commit="{commit}"/></metadata></package>',
            )
            archive.writestr("lib/netstandard2.0/example.dll", b"example")
        return path

    def bundle(self, version: str = "1.2.3") -> list[Path]:
        for package_id in release.PACKAGE_IDS:
            self.package(package_id, version)
        (self.directory / "CHANGELOG.md").write_text(CHANGELOG, encoding="utf-8")
        (self.directory / "release-notes.md").write_text(changelog.release_notes(CHANGELOG, version) + "\n", encoding="utf-8")
        return release.bundle(self.directory, version)

    def gh(
        self, *, draft: bool | None = None, tag: str = "v1.2.3",
        upload_failure: bool = False, initial_assets: list[dict] | None = None,
        incomplete_upload: bool = False, remote_commit: str = HEAD, annotated: bool = False,
    ) -> Mock:
        created = draft is not None
        uploaded = False

        def run(arguments: list[str], **kwargs) -> subprocess.CompletedProcess[str]:
            nonlocal created, uploaded
            self.assertNotIn("shell", kwargs)
            self.assertIsInstance(arguments, list)
            if arguments[:2] == ["gh", "api"]:
                if "/git/ref/tags/" in arguments[-1]:
                    target = {"type": "tag", "sha": "c" * 40} if annotated else {"type": "commit", "sha": remote_commit}
                    return command_result("HTTP/2.0 200 OK\r\n\r\n" + json.dumps({"object": target}))
                if "/git/tags/" in arguments[-1]:
                    return command_result("HTTP/2.0 200 OK\r\n\r\n" + json.dumps({"object": {"type": "commit", "sha": remote_commit}}))
                if not created:
                    return command_result('HTTP/2.0 404 Not Found\r\n\r\n{"message":"Not Found"}', 1)
                assets = initial_assets or []
                if uploaded:
                    assets = [
                        {
                            "name": path.name, "state": "uploaded", "size": path.stat().st_size,
                            "digest": "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest(),
                        }
                        for path in sorted(self.directory.iterdir())
                    ]
                    if incomplete_upload:
                        assets = assets[:-1]
                return command_result("HTTP/2.0 200 OK\r\n\r\n" + json.dumps({
                    "tag_name": tag, "draft": True if draft is None else draft, "assets": assets,
                }))
            if arguments[:3] == ["gh", "release", "create"]:
                created = True
            if arguments[:3] == ["gh", "release", "upload"]:
                if upload_failure:
                    return command_result("", 1)
                uploaded = True
            return command_result()
        return Mock(side_effect=run)

    def test_manual_preview_uses_root_prefix_and_attempt_without_tagging(self) -> None:
        metadata = release.resolve_metadata(self.root, self.env(), runner=self.git)
        self.assertEqual("0.2.0-ci.42.2", metadata["version"])
        self.assertEqual("", metadata["tag"])
        self.assertEqual("false", metadata["is_release"])
        self.assertEqual("false", metadata["publish_nuget"])
        self.assertEqual("true", metadata["prerelease"])
        self.assertEqual([["git", "rev-parse", "--verify", "HEAD"]], [call.args[0] for call in self.git.call_args_list])

    def test_manual_run_on_a_tag_remains_manual(self) -> None:
        metadata = release.resolve_metadata(self.root, self.env(GITHUB_REF="refs/tags/v1.2.3", INPUT_VERSION="v1.2.3"), runner=self.git)
        self.assertEqual("1.2.3", metadata["version"])
        self.assertEqual("false", metadata["is_release"])

    def test_tag_is_checked_against_head_and_nuget_is_optional(self) -> None:
        metadata = release.resolve_metadata(self.root, self.env(GITHUB_EVENT_NAME="push", GITHUB_REF="refs/tags/v1.2.3"), runner=self.git)
        self.assertEqual("true", metadata["is_release"])
        self.assertEqual("false", metadata["publish_nuget"])
        self.assertEqual("refs/tags/v1.2.3^{commit}", self.git.call_args_list[1].args[0][-1])
        enabled = release.resolve_metadata(
            self.root, self.env(GITHUB_EVENT_NAME="push", GITHUB_REF="refs/tags/v1.2.3", NUGET_USER="publisher"),
            runner=self.git,
        )
        self.assertEqual("true", enabled["publish_nuget"])

    def test_moved_tag_is_rejected(self) -> None:
        self.git.side_effect = [command_result(HEAD), command_result("b" * 40)]
        with self.assertRaises(release.ReleaseError):
            release.resolve_metadata(self.root, self.env(GITHUB_EVENT_NAME="push", GITHUB_REF="refs/tags/v1.2.3"), runner=self.git)

    def test_invalid_versions_and_ref_injection_never_reach_git(self) -> None:
        for value in ("1.02.3", "1.2.3-01", "1.2.3+build", "1.2.3\nbad=true", "1.2.3;echo PWN", "$(echo PWN)", "--help", "v1.2.3/evil"):
            with self.subTest(value=value), self.assertRaises(release.ReleaseError):
                release.resolve_metadata(self.root, self.env(INPUT_VERSION=value), runner=self.git)
        self.git.assert_not_called()
        for ref in ("refs/heads/master", "refs/tags/v1.2.3\nmalicious=x", "refs/tags/vv1.2.3"):
            with self.subTest(ref=ref), self.assertRaises(release.ReleaseError):
                release.resolve_metadata(self.root, self.env(GITHUB_EVENT_NAME="push", GITHUB_REF=ref), runner=self.git)
        self.git.assert_not_called()

    def test_manual_nuget_requires_explicit_version_and_publisher_before_build(self) -> None:
        for version, user in (("", ""), ("1.2.3", ""), ("", "publisher")):
            with self.subTest(version=version, user=user), self.assertRaises(release.ReleaseError):
                release.resolve_metadata(self.root, self.env(INPUT_VERSION=version, INPUT_PUBLISH="NuGet", NUGET_USER=user), runner=self.git)
        self.git.assert_not_called()
        result = release.resolve_metadata(
            self.root, self.env(INPUT_VERSION="v1.2.3-rc.1", INPUT_PUBLISH="NuGet", NUGET_USER="publisher"), runner=self.git,
        )
        self.assertEqual("true", result["publish_nuget"])
        self.assertEqual("false", result["is_release"])

    def test_invalid_preview_context_and_event_are_rejected(self) -> None:
        for overrides in (
            {"GITHUB_RUN_NUMBER": "01"}, {"GITHUB_RUN_ATTEMPT": "2\nbad"},
            {"GITHUB_EVENT_NAME": "pull_request"}, {"INPUT_PUBLISH": "NuGet;echo"},
            {"NUGET_USER": "publisher\nbad"},
        ):
            with self.subTest(overrides=overrides), self.assertRaises(release.ReleaseError):
                release.resolve_metadata(self.root, self.env(**overrides), runner=self.git)

    def test_prepare_requires_exact_tag_notes_and_manual_uses_unreleased(self) -> None:
        manual = release.prepare(self.root, self.directory, self.env(INPUT_VERSION="1.2.3"), runner=self.git)
        self.assertEqual("1.2.3", manual["version"])
        self.assertIn("Preview changes.", (self.directory / "release-notes.md").read_text(encoding="utf-8"))
        release.prepare(self.root, self.directory, self.env(GITHUB_EVENT_NAME="push", GITHUB_REF="refs/tags/v1.2.3"), runner=self.git)
        self.assertIn("Stable release notes.", (self.directory / "release-notes.md").read_text(encoding="utf-8"))
        with self.assertRaises(ValueError):
            release.prepare(self.root, self.directory, self.env(GITHUB_EVENT_NAME="push", GITHUB_REF="refs/tags/v1.2.4"), runner=self.git)

    def test_manual_preview_allows_empty_unreleased_without_hiding_invalid_structure(self) -> None:
        empty = CHANGELOG.replace("### Added\n- Preview changes.", "<!-- No changes yet. -->")
        (self.root / "CHANGELOG.md").write_text(empty, encoding="utf-8")
        release.prepare(self.root, self.directory, self.env(), runner=self.git)
        self.assertIn("No Unreleased changes", (self.directory / "release-notes.md").read_text(encoding="utf-8"))
        (self.root / "CHANGELOG.md").write_text("not a changelog", encoding="utf-8")
        with self.assertRaises(ValueError):
            release.prepare(self.root, self.directory, self.env(), runner=self.git)

    def test_output_metadata_is_single_line_and_allowlisted(self) -> None:
        metadata = release.resolve_metadata(self.root, self.env(), runner=self.git)
        output = self.root / "github-output"
        release.write_outputs(output, metadata)
        self.assertEqual(6, len(output.read_text(encoding="utf-8").splitlines()))
        with self.assertRaises(release.ReleaseError):
            release.write_outputs(output, dict(metadata, version="1.2.3\nis_release=true"))
        with self.assertRaises(release.ReleaseError):
            release.write_outputs(output, dict(metadata, **{"bad\nkey": "x"}))

    def test_bundle_contains_six_packages_two_documents_and_manifest(self) -> None:
        paths = self.bundle()
        self.assertEqual(9, len(paths))
        self.assertEqual(paths, release.verify_bundle(self.directory, "1.2.3"))
        manifest = (self.directory / "SHA256SUMS").read_text(encoding="ascii").splitlines()
        self.assertEqual(8, len(manifest))
        self.assertFalse(any(line.endswith("  SHA256SUMS") for line in manifest))

    def test_nuspec_ids_versions_and_package_filenames_are_verified(self) -> None:
        self.bundle()
        for override in ({"metadata_id": "Other.Package"}, {"metadata_id": "ModemKit"}, {"metadata_version": "1.2.4"}):
            with self.subTest(override=override):
                self.package("Xmodem.Protocol", "1.2.3", **override)
                with self.assertRaises(release.ReleaseError):
                    release.validate_packages(self.directory, "1.2.3")
        self.package("Xmodem.Protocol", "1.2.3")
        (self.directory / "Xmodem.Protocol.1.2.3.nupkg").rename(self.directory / "renamed.nupkg")
        with self.assertRaises(release.ReleaseError):
            release.validate_packages(self.directory, "1.2.3")

    def test_missing_package_and_unexpected_file_are_rejected(self) -> None:
        self.bundle()
        extra = self.directory / "unexpected.txt"
        extra.write_text("extra", encoding="utf-8")
        with self.assertRaises(release.ReleaseError):
            release.verify_bundle(self.directory, "1.2.3")
        extra.unlink()
        (self.directory / "ModemKit.1.2.3.nupkg").unlink()
        with self.assertRaises(release.ReleaseError):
            release.verify_bundle(self.directory, "1.2.3")

    def test_umbrella_dependency_versions_match_both_assets(self) -> None:
        self.bundle()
        self.package("ModemKit", "1.2.3", dependency_version="0.1.0")
        with self.assertRaises(release.ReleaseError):
            release.validate_packages(self.directory, "1.2.3")

    def test_package_source_commits_are_consistent_and_bound_to_build(self) -> None:
        self.bundle()
        with self.assertRaises(release.ReleaseError):
            release.verify_bundle(self.directory, "1.2.3", "b" * 40)
        self.package("Xmodem.Protocol", "1.2.3", commit="b" * 40)
        with self.assertRaises(release.ReleaseError):
            release.validate_packages(self.directory, "1.2.3")

    def test_zip_traversal_duplicates_and_dtds_are_rejected(self) -> None:
        self.bundle()
        for member in ("../evil", "/absolute", "C:/outside", "lib\\outside"):
            with self.subTest(member=member):
                package = self.package("ModemKit", "1.2.3")
                with zipfile.ZipFile(package, "a") as archive:
                    entry = zipfile.ZipInfo(member)
                    entry.filename = member
                    archive.writestr(entry, b"bad")
                with self.assertRaises(release.ReleaseError):
                    release.validate_packages(self.directory, "1.2.3")
        package = self.package("ModemKit", "1.2.3")
        with zipfile.ZipFile(package, "a") as archive:
            archive.writestr("LIB/netstandard2.0/EXAMPLE.dll", b"duplicate")
        with self.assertRaises(release.ReleaseError):
            release.validate_packages(self.directory, "1.2.3")
        with zipfile.ZipFile(package, "w") as archive:
            archive.writestr("ModemKit.nuspec", '<!DOCTYPE package [<!ENTITY x "y">]><package/>')
        with self.assertRaises(release.ReleaseError):
            release.validate_packages(self.directory, "1.2.3")

    def test_manifest_detects_mutation_and_duplicate_entries(self) -> None:
        self.bundle()
        notes = self.directory / "release-notes.md"
        original = notes.read_bytes()
        notes.write_bytes(original + b"Changed")
        with self.assertRaises(release.ReleaseError):
            release.verify_bundle(self.directory, "1.2.3")
        notes.write_bytes(original)
        manifest = self.directory / "SHA256SUMS"
        text = manifest.read_text(encoding="ascii")
        manifest.write_text(text + text.splitlines()[0] + "\n", encoding="ascii")
        with self.assertRaises(release.ReleaseError):
            release.verify_bundle(self.directory, "1.2.3")

    def test_github_creates_draft_uploads_all_assets_and_publishes_last(self) -> None:
        self.bundle()
        runner = self.gh()
        self.assertEqual("published", release.github_release(self.directory, "v1.2.3", "Modemkit/Modemkit", runner=runner))
        commands = [call.args[0] for call in runner.call_args_list if call.args[0][:2] != ["gh", "api"]]
        self.assertEqual(["gh", "release", "create"], commands[0][:3])
        self.assertIn("--verify-tag", commands[0])
        self.assertIn("--draft", commands[0])
        self.assertNotIn("--prerelease", commands[0])
        self.assertEqual(["gh", "release", "upload"], commands[1][:3])
        self.assertEqual(9, len(commands[1]) - 7)
        self.assertIn("--draft=false", commands[-1])
        self.assertIn("--prerelease=false", commands[-1])

    def test_prerelease_flags_are_correct(self) -> None:
        self.bundle("1.2.3-rc.1")
        runner = self.gh(tag="v1.2.3-rc.1")
        release.github_release(self.directory, "v1.2.3-rc.1", "Modemkit/Modemkit", runner=runner)
        create = next(call.args[0] for call in runner.call_args_list if call.args[0][:3] == ["gh", "release", "create"])
        self.assertIn("--prerelease", create)
        self.assertIn("--prerelease=true", runner.call_args_list[-1].args[0])

    def test_existing_draft_is_reused_and_published_release_is_never_changed(self) -> None:
        self.bundle()
        draft = self.gh(draft=True)
        release.github_release(self.directory, "v1.2.3", "Modemkit/Modemkit", runner=draft)
        edit = next(call.args[0] for call in draft.call_args_list if call.args[0][:3] == ["gh", "release", "edit"])
        self.assertIn("--draft=true", edit)
        self.assertFalse(any(call.args[0][:3] == ["gh", "release", "create"] for call in draft.call_args_list))
        published = self.gh(draft=False)
        self.assertEqual("already-published", release.github_release(self.directory, "v1.2.3", "Modemkit/Modemkit", runner=published))
        self.assertEqual(1, published.call_count)

    def test_upload_failure_leaves_draft_unpublished(self) -> None:
        self.bundle()
        runner = self.gh(upload_failure=True)
        with self.assertRaises(release.ReleaseError):
            release.github_release(self.directory, "v1.2.3", "Modemkit/Modemkit", runner=runner)
        self.assertFalse(any("--draft=false" in call.args[0] for call in runner.call_args_list))

    def test_remote_tag_movement_blocks_both_publish_targets(self) -> None:
        self.bundle()
        github = self.gh(remote_commit="b" * 40)
        with self.assertRaises(release.ReleaseError):
            release.github_release(self.directory, "v1.2.3", "Modemkit/Modemkit", runner=github)
        self.assertFalse(any(call.args[0][:2] == ["gh", "release"] for call in github.call_args_list))
        nuget = self.gh(remote_commit="b" * 40)
        with self.assertRaises(release.ReleaseError):
            release.publish_nuget(self.directory, "1.2.3", "temporary-key", tag="v1.2.3", repository="Modemkit/Modemkit", runner=nuget)
        self.assertFalse(any(call.args[0][0] == "dotnet" for call in nuget.call_args_list))

    def test_annotated_remote_tag_is_dereferenced(self) -> None:
        self.bundle()
        runner = self.gh(annotated=True)
        release.github_release(self.directory, "v1.2.3", "Modemkit/Modemkit", runner=runner)
        self.assertTrue(any("/git/tags/" in call.args[0][-1] for call in runner.call_args_list))

    def test_unexpected_existing_draft_assets_are_not_deleted_or_published(self) -> None:
        self.bundle()
        runner = self.gh(draft=True, initial_assets=[{"name": "stale.nupkg", "size": 1, "state": "uploaded"}])
        with self.assertRaises(release.ReleaseError) as caught:
            release.github_release(self.directory, "v1.2.3", "Modemkit/Modemkit", runner=runner)
        self.assertIn("stale.nupkg", str(caught.exception))
        self.assertEqual(1, runner.call_count)

    def test_incomplete_remote_asset_set_is_not_published(self) -> None:
        self.bundle()
        runner = self.gh(incomplete_upload=True)
        with self.assertRaises(release.ReleaseError):
            release.github_release(self.directory, "v1.2.3", "Modemkit/Modemkit", runner=runner)
        self.assertFalse(any("--draft=false" in call.args[0] for call in runner.call_args_list))

    def test_api_permission_error_never_creates_a_release(self) -> None:
        self.bundle()
        runner = Mock(return_value=command_result('HTTP/2.0 403 Forbidden\n\n{"message":"Forbidden"}', 1))
        with self.assertRaises(release.ReleaseError):
            release.github_release(self.directory, "v1.2.3", "Modemkit/Modemkit", runner=runner)
        self.assertEqual(1, runner.call_count)

    def test_publish_rejects_changed_notes_or_untrusted_repository_argument(self) -> None:
        self.bundle()
        runner = self.gh()
        for repository in ("--repo malicious", "owner/repo\nbad", "owner/repo/extra"):
            with self.subTest(repository=repository), self.assertRaises(release.ReleaseError):
                release.github_release(self.directory, "v1.2.3", repository, runner=runner)
        (self.directory / "release-notes.md").write_text("Different notes", encoding="utf-8")
        release.bundle(self.directory, "1.2.3")
        with self.assertRaises(release.ReleaseError):
            release.github_release(self.directory, "v1.2.3", "Modemkit/Modemkit", runner=runner)
        runner.assert_not_called()

    def test_nuget_uses_argument_lists_for_exactly_six_verified_packages(self) -> None:
        self.bundle()
        runner = Mock(return_value=command_result())
        release.publish_nuget(self.directory, "1.2.3", "temporary-test-key", runner=runner)
        self.assertEqual(6, runner.call_count)
        for call in runner.call_args_list:
            self.assertEqual(["dotnet", "nuget", "push"], call.args[0][:3])
            self.assertIn("--skip-duplicate", call.args[0])
            self.assertNotIn("shell", call.kwargs)
        failed = Mock(return_value=command_result("", 1, "temporary-test-key"))
        with self.assertRaises(release.ReleaseError) as caught:
            release.publish_nuget(self.directory, "1.2.3", "temporary-test-key", runner=failed)
        self.assertNotIn("temporary-test-key", str(caught.exception))
        self.assertEqual(1, failed.call_count)

    def test_subprocess_timeouts_are_bounded_and_do_not_leak_secrets(self) -> None:
        self.bundle()
        runner = Mock(side_effect=subprocess.TimeoutExpired(["dotnet", "secret-key"], 300))
        with self.assertRaises(release.ReleaseError) as caught:
            release.publish_nuget(self.directory, "1.2.3", "secret-key", runner=runner)
        self.assertNotIn("secret-key", str(caught.exception))
        self.assertEqual(300, runner.call_args.kwargs["timeout"])

    def test_publish_cli_binds_tag_to_artifact_build_commit(self) -> None:
        with (
            patch.object(release, "tag_commit", return_value=HEAD),
            patch.object(release, "github_release") as publish,
            patch.dict(release.os.environ, {"RELEASE_COMMIT": "b" * 40}),
            contextlib.redirect_stderr(io.StringIO()),
        ):
            result = release.main([
                "github", "--directory", str(self.directory),
                "--tag", "v1.2.3", "--repository", "Modemkit/Modemkit",
            ])
        self.assertEqual(1, result)
        publish.assert_not_called()


if __name__ == "__main__":
    unittest.main()
