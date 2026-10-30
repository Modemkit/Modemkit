#!/usr/bin/env python3
"""Prepare, verify, and publish an explicit ModemKit release bundle."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess
import sys
from typing import Callable, Mapping
import xml.etree.ElementTree as ET
import zipfile

import changelog


PACKAGE_IDS = (
    "Jmodem.Protocol", "LegacyModem.Protocol", "ModemKit",
    "Xmodem.Protocol", "Ymodem.Protocol", "Zmodem.Protocol",
)
DOCUMENTS = ("CHANGELOG.md", "release-notes.md")
CHECKSUM_FILE = "SHA256SUMS"
NUGET_SOURCE = "https://api.nuget.org/v3/index.json"
Runner = Callable[..., subprocess.CompletedProcess[str]]


class ReleaseError(ValueError):
    """A release input or artifact failed validation."""


def _version(value: str) -> str:
    try:
        return changelog.validate_version(value)
    except ValueError as error:
        raise ReleaseError(str(error)) from error


def _execute(arguments: list[str], *, runner: Runner, cwd: Path | None = None) -> subprocess.CompletedProcess[str]:
    # Never echo commands: a NuGet invocation includes a temporary API key.
    try:
        result = runner(arguments, cwd=cwd, capture_output=True, text=True, check=False, timeout=300)
    except subprocess.TimeoutExpired:
        raise ReleaseError(f"{arguments[0]} {arguments[1]} exceeded its five-minute deadline.") from None
    if result.returncode:
        raise ReleaseError(f"{arguments[0]} {arguments[1]} failed with exit code {result.returncode}.")
    return result


def tag_commit(root: Path, tag: str, *, runner: Runner = subprocess.run) -> str:
    version = _version(tag)
    if tag != f"v{version}":
        raise ReleaseError("Release tags must be exactly v followed by the normalized version.")
    head = _execute(["git", "rev-parse", "--verify", "HEAD"], runner=runner, cwd=root).stdout.strip()
    tagged = _execute(
        ["git", "rev-parse", "--verify", "--end-of-options", f"refs/tags/{tag}^{{commit}}"],
        runner=runner, cwd=root,
    ).stdout.strip()
    if not re.fullmatch(r"(?:[0-9a-f]{40}|[0-9a-f]{64})", head) or tagged != head:
        raise ReleaseError("The release tag must point at the checked-out HEAD commit.")
    return head


def resolve_metadata(root: Path, environment: Mapping[str, str], *, runner: Runner = subprocess.run) -> dict[str, str]:
    event = environment.get("GITHUB_EVENT_NAME", "")
    requested = environment.get("INPUT_VERSION", "")
    publish = environment.get("INPUT_PUBLISH", "None") or "None"
    nuget_user = environment.get("NUGET_USER", "")
    if publish not in ("None", "NuGet"):
        raise ReleaseError("Publish must be None or NuGet.")
    if nuget_user != nuget_user.strip() or "\n" in nuget_user or "\r" in nuget_user:
        raise ReleaseError("NUGET_USER must not contain surrounding whitespace or line breaks.")
    if event == "push":
        ref = environment.get("GITHUB_REF", "")
        if not ref.startswith("refs/tags/v"):
            raise ReleaseError("A push release must be triggered by a version tag.")
        tag = ref[len("refs/tags/") :]
        version = _version(tag)
        commit = tag_commit(root, tag, runner=runner)
        is_release, publish_nuget = True, bool(nuget_user)
    elif event == "workflow_dispatch":
        if publish == "NuGet" and (not requested or not nuget_user):
            raise ReleaseError("Manual NuGet publishing requires an explicit version and NUGET_USER.")
        if requested:
            version = _version(requested)
        else:
            run = environment.get("GITHUB_RUN_NUMBER", "")
            attempt = environment.get("GITHUB_RUN_ATTEMPT", "")
            if not re.fullmatch(r"[1-9][0-9]*", run) or not re.fullmatch(r"[1-9][0-9]*", attempt):
                raise ReleaseError("Preview run number and attempt must be positive decimal integers.")
            prefixes = ET.parse(root / "ModemKit.csproj").findall(".//VersionPrefix")
            if len(prefixes) != 1 or not prefixes[0].text:
                raise ReleaseError("ModemKit.csproj must declare exactly one VersionPrefix.")
            version = _version(f"{_version(prefixes[0].text)}-ci.{run}.{attempt}")
        tag, is_release, publish_nuget = "", False, publish == "NuGet"
        commit = _execute(["git", "rev-parse", "--verify", "HEAD"], runner=runner, cwd=root).stdout.strip()
        if not re.fullmatch(r"(?:[0-9a-f]{40}|[0-9a-f]{64})", commit):
            raise ReleaseError("Invalid checked-out commit.")
    else:
        raise ReleaseError("Only tag pushes and explicit manual runs can prepare release artifacts.")
    return {
        "version": version, "tag": tag, "commit": commit,
        "is_release": str(is_release).lower(),
        "publish_nuget": str(publish_nuget).lower(),
        "prerelease": str("-" in version).lower(),
    }


def write_outputs(path: Path, metadata: Mapping[str, str]) -> None:
    if set(metadata) != {"version", "tag", "is_release", "publish_nuget", "prerelease", "commit"}:
        raise ReleaseError("Unexpected release metadata output keys.")
    for key, value in metadata.items():
        if not isinstance(value, str) or "\r" in value or "\n" in value:
            raise ReleaseError(f"Invalid multiline release output: {key}.")
    version = _version(metadata["version"])
    if version != metadata["version"]:
        raise ReleaseError("Output versions must be normalized.")
    if any(metadata[key] not in ("true", "false") for key in ("is_release", "publish_nuget", "prerelease")):
        raise ReleaseError("Output boolean values must be true or false.")
    if metadata["tag"] != (f"v{version}" if metadata["is_release"] == "true" else ""):
        raise ReleaseError("Output tag does not match the release version.")
    if metadata["prerelease"] != str("-" in version).lower() or not re.fullmatch(r"(?:[0-9a-f]{40}|[0-9a-f]{64})", metadata["commit"]):
        raise ReleaseError("Output prerelease or commit metadata is inconsistent.")
    with path.open("a", encoding="utf-8", newline="\n") as output:
        for key in sorted(metadata):
            output.write(f"{key}={metadata[key]}\n")


def prepare(root: Path, directory: Path, environment: Mapping[str, str], *, runner: Runner = subprocess.run) -> dict[str, str]:
    metadata = resolve_metadata(root, environment, runner=runner)
    source = (root / "CHANGELOG.md").read_bytes()
    text = source.decode("utf-8-sig")
    if metadata["is_release"] == "true":
        notes = changelog.release_notes(text, metadata["version"])
    else:
        # Validate structure separately; an empty Unreleased section after a
        # prepared release is a valid manual build, not a malformed changelog.
        changelog.parse_changelog(text)
        try:
            notes = changelog.unreleased_notes(text)
        except ValueError:
            notes = "Manual build. No Unreleased changes have been recorded.\n"
    if not notes.strip():
        raise ReleaseError("Release notes must not be empty.")
    directory.mkdir(parents=True, exist_ok=True)
    for name in DOCUMENTS:
        if (directory / name).is_symlink():
            raise ReleaseError(f"Release output must not be a symlink: {name}.")
    (directory / "CHANGELOG.md").write_bytes(source)
    (directory / "release-notes.md").write_text(notes.rstrip() + "\n", encoding="utf-8", newline="\n")
    if environment.get("GITHUB_OUTPUT"):
        write_outputs(Path(environment["GITHUB_OUTPUT"]), metadata)
    return metadata


def _package_metadata(package: Path) -> tuple[str, str, list[tuple[str, list[tuple[str, str]]]], str]:
    try:
        with zipfile.ZipFile(package) as archive:
            entries = archive.infolist()
            if len(entries) > 10000 or sum(entry.file_size for entry in entries) > 512 * 1024 * 1024:
                raise ReleaseError(f"Package exceeds release validation bounds: {package.name}.")
            names: set[str] = set()
            for entry in entries:
                original_name = entry.orig_filename
                name = PurePosixPath(original_name)
                if (
                    name.is_absolute() or ".." in name.parts or "\\" in original_name
                    or ":" in original_name or "\x00" in original_name or stat.S_ISLNK(entry.external_attr >> 16)
                    or entry.filename.casefold() in names
                ):
                    raise ReleaseError(f"Unsafe or duplicate archive member in {package.name}.")
                names.add(entry.filename.casefold())
            manifests = [entry for entry in entries if entry.filename.lower().endswith(".nuspec")]
            if len(manifests) != 1 or manifests[0].file_size > 1024 * 1024:
                raise ReleaseError(f"Package must contain one bounded nuspec: {package.name}.")
            xml = archive.read(manifests[0]).decode("utf-8-sig")
            if "\x00" in xml or "<!DOCTYPE" in xml.upper() or "<!ENTITY" in xml.upper():
                raise ReleaseError("Nuspec DTDs and entities are not permitted.")
            metadata = ET.fromstring(xml).findall("{*}metadata")
            if len(metadata) != 1:
                raise ReleaseError("Nuspec must contain exactly one metadata element.")
            ids, versions = metadata[0].findall("{*}id"), metadata[0].findall("{*}version")
            if len(ids) != 1 or len(versions) != 1 or not ids[0].text or not versions[0].text:
                raise ReleaseError("Nuspec must contain one package ID and version.")
            groups = [
                (
                    group.get("targetFramework", ""),
                    [(item.get("id", ""), item.get("version", "")) for item in group.findall("{*}dependency")],
                )
                for group in metadata[0].findall("{*}dependencies/{*}group")
            ]
            repositories = metadata[0].findall("{*}repository")
            commit = repositories[0].get("commit", "") if len(repositories) == 1 else ""
            if not re.fullmatch(r"(?:[0-9a-f]{40}|[0-9a-f]{64})", commit):
                raise ReleaseError("Every package must record its source commit.")
            return ids[0].text, versions[0].text, groups, commit
    except (zipfile.BadZipFile, ET.ParseError, RuntimeError, UnicodeError) as error:
        raise ReleaseError(f"Invalid package archive: {package.name}.") from error


def validate_packages(directory: Path, version: str, expected_commit: str | None = None) -> list[Path]:
    version = _version(version)
    packages = sorted(directory.glob("*.nupkg"), key=lambda path: path.name)
    if len(packages) != len(PACKAGE_IDS):
        raise ReleaseError("A release requires exactly six NuGet packages.")
    found: set[str] = set()
    for package in packages:
        if package.is_symlink() or not package.is_file():
            raise ReleaseError(f"Package must be an ordinary file: {package.name}.")
        package_id, package_version, dependencies, commit = _package_metadata(package)
        if expected_commit is None:
            expected_commit = commit
        if commit != expected_commit:
            raise ReleaseError("Package source commits do not match the artifact build commit.")
        if package_id not in PACKAGE_IDS or package_id in found:
            raise ReleaseError(f"Unexpected or duplicate package ID: {package_id}.")
        if package_version != version or package.name != f"{package_id}.{version}.nupkg":
            raise ReleaseError(f"Package filename or nuspec version does not match {version}: {package.name}.")
        if package_id == "ModemKit":
            required = set(PACKAGE_IDS) - {"ModemKit"}
            if len(dependencies) != 2 or {group[0] for group in dependencies} != {"net10.0", ".NETStandard2.0"}:
                raise ReleaseError("ModemKit must declare dependencies for both target frameworks.")
            for _, group in dependencies:
                if len(group) != len(required) or {item[0] for item in group} != required:
                    raise ReleaseError("ModemKit must depend on exactly the five protocol packages.")
                if any(item[1] not in (version, f"[{version}]") for item in group):
                    raise ReleaseError("ModemKit dependency versions must match the release version.")
        found.add(package_id)
    if found != set(PACKAGE_IDS):
        raise ReleaseError("The release package set is incomplete.")
    return packages


def _bundle_files(directory: Path, version: str, *, manifest_required: bool, expected_commit: str | None = None) -> list[Path]:
    files = validate_packages(directory, version, expected_commit) + [directory / name for name in DOCUMENTS]
    for path in files:
        if path.is_symlink() or not path.is_file() or path.stat().st_size == 0:
            raise ReleaseError(f"Missing, empty, or redirected release file: {path.name}.")
    expected, actual = {path.name for path in files}, {path.name for path in directory.iterdir()}
    if manifest_required or CHECKSUM_FILE in actual:
        expected.add(CHECKSUM_FILE)
    if actual != expected:
        raise ReleaseError("The release directory contains missing or unexpected files.")
    manifest = directory / CHECKSUM_FILE
    if manifest.is_symlink() or (manifest.exists() and not manifest.is_file()):
        raise ReleaseError("The checksum manifest must be an ordinary file.")
    return sorted(files, key=lambda path: path.name)


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def bundle(directory: Path, version: str, expected_commit: str | None = None) -> list[Path]:
    files = _bundle_files(directory, version, manifest_required=False, expected_commit=expected_commit)
    (directory / CHECKSUM_FILE).write_text(
        "".join(f"{_sha256(path)}  {path.name}\n" for path in files), encoding="ascii", newline="\n",
    )
    return verify_bundle(directory, version, expected_commit)


def verify_bundle(directory: Path, version: str, expected_commit: str | None = None) -> list[Path]:
    files = _bundle_files(directory, version, manifest_required=True, expected_commit=expected_commit)
    manifest, entries = directory / CHECKSUM_FILE, {}
    for line in manifest.read_text(encoding="ascii").splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  ([A-Za-z0-9._-]+)", line)
        if not match or match[2] in entries:
            raise ReleaseError("Invalid or duplicate checksum manifest entry.")
        entries[match[2]] = match[1]
    if set(entries) != {path.name for path in files}:
        raise ReleaseError("The checksum manifest must cover every package and document exactly once.")
    for path in files:
        if entries[path.name] != _sha256(path):
            raise ReleaseError(f"Checksum mismatch: {path.name}.")
    return files + [manifest]


def _api_json(endpoint: str, *, runner: Runner, allow_missing: bool = False) -> dict | None:
    try:
        result = runner(
            ["gh", "api", "--include", endpoint],
            capture_output=True, text=True, check=False, timeout=60,
        )
    except subprocess.TimeoutExpired:
        raise ReleaseError("GitHub lookup exceeded its one-minute deadline.") from None
    headers, separator, body = result.stdout.replace("\r\n", "\n").partition("\n\n")
    status = re.match(r"HTTP/\S+ (\d{3})(?:\s|$)", headers)
    if not separator or not status:
        raise ReleaseError("GitHub returned an unrecognized release response.")
    if status[1] == "404" and allow_missing:
        return None
    if result.returncode or status[1] != "200":
        raise ReleaseError(f"GitHub release lookup failed with HTTP {status[1]}.")
    try:
        release = json.loads(body)
    except json.JSONDecodeError as error:
        raise ReleaseError("GitHub returned invalid release metadata.") from error
    if not isinstance(release, dict):
        raise ReleaseError("GitHub returned inconsistent release metadata.")
    return release


def _release_status(repository: str, tag: str, *, runner: Runner) -> dict | None:
    release = _api_json(f"repos/{repository}/releases/tags/{tag}", runner=runner, allow_missing=True)
    if release is not None and (release.get("tag_name") != tag or not isinstance(release.get("draft"), bool)):
        raise ReleaseError("GitHub returned inconsistent release metadata.")
    return release


def verify_remote_tag(repository: str, tag: str, expected_commit: str, *, runner: Runner = subprocess.run) -> None:
    if tag != f"v{_version(tag)}" or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*", repository):
        raise ReleaseError("Invalid release repository or tag.")
    reference = _api_json(f"repos/{repository}/git/ref/tags/{tag}", runner=runner)
    for _ in range(8):
        target = reference.get("object", {}) if reference is not None else {}
        sha = target.get("sha", "")
        if not re.fullmatch(r"(?:[0-9a-f]{40}|[0-9a-f]{64})", sha):
            raise ReleaseError("GitHub returned an invalid tag object.")
        if target.get("type") == "commit":
            if sha != expected_commit:
                raise ReleaseError("The remote tag no longer matches the artifact build commit.")
            return
        if target.get("type") != "tag":
            raise ReleaseError("The release tag does not resolve to a commit.")
        reference = _api_json(f"repos/{repository}/git/tags/{sha}", runner=runner)
    raise ReleaseError("The annotated release tag is nested too deeply.")


def _check_assets(release: dict, assets: list[Path], *, complete: bool) -> None:
    remote = release.get("assets", [])
    if not isinstance(remote, list) or any(not isinstance(asset, dict) or not isinstance(asset.get("name"), str) for asset in remote):
        raise ReleaseError("GitHub returned invalid release asset metadata.")
    names = [asset["name"] for asset in remote]
    expected = {path.name: path for path in assets}
    extras = set(names) - set(expected)
    if extras or len(names) != len(set(names)):
        raise ReleaseError("Existing draft contains unexpected or duplicate assets; review and remove them before retrying: " + ", ".join(sorted(extras)))
    if complete:
        if set(names) != set(expected):
            raise ReleaseError("Draft upload did not produce the exact release asset set.")
        for asset in remote:
            local = expected[asset["name"]]
            if asset.get("state") != "uploaded" or asset.get("size") != local.stat().st_size:
                raise ReleaseError(f"Draft asset is incomplete: {local.name}.")
            digest = asset.get("digest")
            if digest and digest != "sha256:" + _sha256(local):
                raise ReleaseError(f"Draft asset digest does not match: {local.name}.")


def github_release(
    directory: Path, tag: str, repository: str, *,
    expected_commit: str | None = None, runner: Runner = subprocess.run,
) -> str:
    version = _version(tag)
    if tag != f"v{version}":
        raise ReleaseError("GitHub releases require a canonical v-prefixed tag.")
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*", repository):
        raise ReleaseError("Invalid GitHub repository name.")
    assets = verify_bundle(directory, version, expected_commit)
    expected_commit = expected_commit or _package_metadata(next(path for path in assets if path.suffix == ".nupkg"))[3]
    notes = (directory / "release-notes.md").read_text(encoding="utf-8")
    expected_notes = changelog.release_notes((directory / "CHANGELOG.md").read_text(encoding="utf-8-sig"), version)
    if notes.strip() != expected_notes.strip():
        raise ReleaseError("Tagged release notes must match the exact CHANGELOG release entry.")
    existing = _release_status(repository, tag, runner=runner)
    if existing is not None and not existing["draft"]:
        return "already-published"
    if existing is not None:
        _check_assets(existing, assets, complete=False)
    verify_remote_tag(repository, tag, expected_commit, runner=runner)
    prerelease = "-" in version
    notes_file = str((directory / "release-notes.md").resolve())
    if existing is None:
        command = [
            "gh", "release", "create", tag, "--repo", repository,
            "--verify-tag", "--draft", "--title", tag, "--notes-file", notes_file,
        ]
        if prerelease:
            command.append("--prerelease")
        _execute(command, runner=runner)
    else:
        _execute([
            "gh", "release", "edit", tag, "--repo", repository,
            "--draft=true", f"--prerelease={str(prerelease).lower()}",
            "--title", tag, "--notes-file", notes_file,
        ], runner=runner)
    _execute(
        ["gh", "release", "upload", tag, "--repo", repository, "--clobber"] + [str(path.resolve()) for path in assets],
        runner=runner,
    )
    uploaded = _release_status(repository, tag, runner=runner)
    if uploaded is None or not uploaded["draft"]:
        raise ReleaseError("The release draft disappeared or was published concurrently; no further changes were made.")
    _check_assets(uploaded, assets, complete=True)
    verify_remote_tag(repository, tag, expected_commit, runner=runner)
    _execute([
        "gh", "release", "edit", tag, "--repo", repository,
        "--draft=false", f"--prerelease={str(prerelease).lower()}",
    ], runner=runner)
    return "published"


def publish_nuget(
    directory: Path, version: str, api_key: str, *,
    expected_commit: str | None = None, repository: str = "", tag: str = "",
    runner: Runner = subprocess.run,
) -> None:
    if not api_key or "\r" in api_key or "\n" in api_key:
        raise ReleaseError("NuGet publishing requires a valid temporary API key.")
    verify_bundle(directory, version, expected_commit)
    packages = validate_packages(directory, version, expected_commit)
    expected_commit = expected_commit or _package_metadata(packages[0])[3]
    if tag:
        verify_remote_tag(repository, tag, expected_commit, runner=runner)
    for package in packages:
        _execute([
            "dotnet", "nuget", "push", str(package.resolve()),
            "--api-key", api_key, "--source", NUGET_SOURCE, "--skip-duplicate",
        ], runner=runner)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    preparing = commands.add_parser("prepare")
    preparing.add_argument("--output-dir", type=Path, required=True)
    for name in ("bundle", "verify", "github", "nuget"):
        command = commands.add_parser(name)
        command.add_argument("--directory", type=Path, required=True)
        if name == "github":
            command.add_argument("--tag", default=os.environ.get("RELEASE_TAG", ""))
            command.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY", ""))
        else:
            command.add_argument("--version", default=os.environ.get("RELEASE_VERSION", ""))
    args = parser.parse_args(argv)
    root = Path(__file__).resolve().parents[1]
    expected_commit = os.environ.get("RELEASE_COMMIT") or None
    try:
        if args.command == "prepare":
            print(json.dumps(prepare(root, args.output_dir, os.environ), sort_keys=True))
        elif args.command == "bundle":
            bundle(args.directory, args.version, expected_commit)
            print("Release bundle created and verified.")
        elif args.command == "verify":
            verify_bundle(args.directory, args.version, expected_commit)
            if os.environ.get("RELEASE_TAG"):
                if tag_commit(root, os.environ["RELEASE_TAG"]) != expected_commit:
                    raise ReleaseError("The local tag does not match the artifact build commit.")
                verify_remote_tag(os.environ.get("GITHUB_REPOSITORY", ""), os.environ["RELEASE_TAG"], expected_commit or "")
            print("Release bundle verified.")
        elif args.command == "github":
            actual_commit = tag_commit(root, args.tag)
            if actual_commit != expected_commit:
                raise ReleaseError("The current release tag no longer matches the commit that built the artifact.")
            print(github_release(args.directory, args.tag, args.repository, expected_commit=expected_commit))
        else:
            if not expected_commit:
                raise ReleaseError("NuGet publishing requires the artifact build commit.")
            if os.environ.get("RELEASE_TAG") and tag_commit(root, os.environ["RELEASE_TAG"]) != expected_commit:
                raise ReleaseError("The local tag does not match the artifact build commit.")
            publish_nuget(
                args.directory, args.version, os.environ.get("NUGET_API_KEY", ""),
                expected_commit=expected_commit,
                repository=os.environ.get("GITHUB_REPOSITORY", ""),
                tag=os.environ.get("RELEASE_TAG", ""),
            )
            print("NuGet packages published.")
    except (ReleaseError, ValueError, OSError, ET.ParseError) as error:
        print(f"Release failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
