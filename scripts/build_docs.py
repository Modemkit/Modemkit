#!/usr/bin/env python3
"""Stage the documented allowlist and strictly build the ModemKit site."""

from __future__ import annotations

import argparse
from pathlib import Path, PurePosixPath
import posixpath
import re
import shutil
import subprocess
import sys
from urllib.parse import quote, unquote, urlsplit, urlunsplit


REPOSITORY_URL = "https://github.com/Modemkit/Modemkit"
SOURCE_REF = "main"
SOURCE_DIRECTORY = "artifacts/docs-source"
SITE_DIRECTORY = "artifacts/docs-site"
MARKER_NAME = ".modemkit-docs-owned"
MARKER_CONTENT = "ModemKit generated documentation directory v1\n"

# Publishing is opt-in. A new page must be added here and to mkdocs.yml.
PAGES: dict[str, str] = {
    "README.md": "index.md",
    "docs/README.md": "docs/index.md",
    "docs/protocol-branches.md": "docs/protocol-branches.md",
    "docs/integration.md": "docs/integration.md",
    "docs/testing.md": "docs/testing.md",
    "docs/releases.md": "docs/releases.md",
    "docs/deployment.md": "docs/deployment.md",
    "Xmodem.Protocol/README.md": "Xmodem.Protocol/index.md",
    "Ymodem.Protocol/README.md": "Ymodem.Protocol/index.md",
    "Zmodem.Protocol/README.md": "Zmodem.Protocol/index.md",
    "Zmodem.Protocol/PROTOCOL_NOTES.md": "Zmodem.Protocol/PROTOCOL_NOTES.md",
    "LegacyModem.Protocol/README.md": "LegacyModem.Protocol/index.md",
    "Jmodem.Protocol/README.md": "Jmodem.Protocol/index.md",
    "ModemKit.Testing/README.md": "ModemKit.Testing/index.md",
    "ModemKit.Interop/README.md": "ModemKit.Interop/index.md",
    "CHANGELOG.md": "CHANGELOG.md",
    "LICENSE": "license.md",
}

_INLINE_LINK = re.compile(
    r"(?<!\\)!?\[[^\]\n]*\]\(\s*(?P<target><[^>\n]+>|[^\s)]+)"
    r"(?:\s+(?:\"[^\"]*\"|'[^']*'))?\s*\)"
)
_REFERENCE_LINK = re.compile(
    r"^[ \t]{0,3}\[[^\]\n]+\]:[ \t]*(?P<target><[^>\n]+>|[^\s\n]+)"
)
_INLINE_CODE = re.compile(r"(?P<fence>`+)(?!`)(?P<code>.*?)(?<!`)(?P=fence)(?!`)")
_FENCE = re.compile(r"^[ \t]{0,3}(?P<fence>`{3,}|~{3,})(?P<tail>.*)$")
_EXCLUDED_COMPONENTS = {".git", "artifacts", "bin", "obj", "__pycache__"}


class DocumentationError(ValueError):
    """A source link or generated output would violate the build contract."""


def _inside(path: Path, root: Path) -> bool:
    return path == root or root in path.parents


def _repository_path(root: Path, relative: str) -> Path:
    root = root.resolve()
    path = root.joinpath(*PurePosixPath(relative).parts).resolve()
    if not _inside(path, root):
        raise DocumentationError(f"Path escapes the repository: {relative}")
    return path


def _output_path(root: Path, relative: str) -> Path:
    if relative not in {SOURCE_DIRECTORY, SITE_DIRECTORY}:
        raise DocumentationError(f"Not an owned documentation output: {relative}")
    root = root.resolve()
    expected = root.joinpath(*PurePosixPath(relative).parts)
    resolved = expected.resolve()
    if resolved != expected or not _inside(resolved, root / "artifacts"):
        raise DocumentationError(f"Documentation output is redirected: {expected}")
    return resolved


def validate_configuration(root: Path) -> Path:
    """Validate all renderer path inputs before creating or cleaning anything."""
    try:
        import yaml
    except ImportError as error:
        raise DocumentationError("Install requirements-docs.txt before building documentation.") from error

    root = root.resolve()
    config_path = root / "mkdocs.yml"
    if config_path.resolve() != config_path or not config_path.is_file():
        raise DocumentationError(f"MkDocs configuration is missing or redirected: {config_path}")
    try:
        configuration = yaml.safe_load(config_path.read_text(encoding="utf-8-sig"))
    except yaml.YAMLError as error:
        raise DocumentationError(f"MkDocs configuration must be plain safe YAML: {error}") from error
    if not isinstance(configuration, dict):
        raise DocumentationError("MkDocs configuration must be a mapping.")
    if "INHERIT" in configuration or "config_file_path" in configuration:
        raise DocumentationError("MkDocs configuration inheritance or path indirection is not supported.")
    for key, relative in (("docs_dir", SOURCE_DIRECTORY), ("site_dir", SITE_DIRECTORY)):
        if configuration.get(key) != relative:
            raise DocumentationError(f"MkDocs {key} must be exactly {relative!r}.")
        _output_path(root, relative)
    return config_path


def clean_owned_directory(root: Path, relative: str) -> Path:
    """Clean only a fixed output carrying this builder's ownership marker."""
    output = _output_path(root, relative)
    marker = output / MARKER_NAME
    if output.exists():
        if not output.is_dir() or not marker.is_file():
            raise DocumentationError(f"Refusing to clean an unmarked output: {output}")
        if marker.is_symlink() or marker.read_text(encoding="utf-8") != MARKER_CONTENT:
            raise DocumentationError(f"Invalid ownership marker: {marker}")
        for child in output.iterdir():
            if child == marker:
                continue
            if child.is_symlink() or (
                hasattr(child, "is_junction") and child.is_junction()
            ):
                raise DocumentationError(f"Refusing to clean a linked output: {child}")
            if not _inside(child.resolve(), output):
                raise DocumentationError(f"Generated child escapes its output: {child}")
            if child.is_dir():
                shutil.rmtree(child)
            else:
                child.unlink()
    else:
        output.mkdir(parents=True)
    marker.write_text(MARKER_CONTENT, encoding="utf-8")
    return output


def rewrite_target(target: str, source: str, root: Path) -> str:
    """Resolve local links against original sources, then map them to the site."""
    bracketed = target.startswith("<") and target.endswith(">")
    raw = target[1:-1] if bracketed else target
    parsed = urlsplit(raw)
    if parsed.scheme or parsed.netloc or not parsed.path:
        return target

    path_text = unquote(parsed.path).replace("\\", "/")
    if path_text.startswith("/"):
        relative = path_text.lstrip("/")
    else:
        relative = posixpath.join(posixpath.dirname(source), path_text)
    original = _repository_path(root, relative)
    relative = original.relative_to(root.resolve()).as_posix()
    if not original.exists():
        raise DocumentationError(f"{source}: link target does not exist: {raw}")
    if any(part.casefold() in _EXCLUDED_COMPONENTS for part in original.relative_to(root.resolve()).parts):
        raise DocumentationError(f"{source}: link targets generated/private content: {raw}")

    if relative in PAGES:
        staged_source = PAGES[source]
        staged_target = posixpath.relpath(PAGES[relative], posixpath.dirname(staged_source) or ".")
        rewritten = urlunsplit(("", "", quote(staged_target, safe="/."), parsed.query, parsed.fragment))
    elif original.suffix.lower() == ".md":
        raise DocumentationError(f"{source}: Markdown page is not allowlisted: {relative}")
    else:
        kind = "tree" if original.is_dir() else "blob"
        rewritten = f"{REPOSITORY_URL}/{kind}/{SOURCE_REF}/{quote(relative, safe='/')}"
        if parsed.query:
            rewritten += "?" + parsed.query
        if parsed.fragment:
            rewritten += "#" + parsed.fragment
    return f"<{rewritten}>" if bracketed else rewritten


def rewrite_markdown(text: str, source: str, root: Path) -> str:
    """Rewrite Markdown links without changing fenced or inline code examples."""
    if source not in PAGES:
        raise DocumentationError(f"Source is not allowlisted: {source}")
    rewritten: list[str] = []
    fence_char: str | None = None
    fence_length = 0
    for line in text.splitlines(keepends=True):
        fence = _FENCE.match(line)
        if fence:
            marker = fence.group("fence")
            if fence_char is None:
                fence_char, fence_length = marker[0], len(marker)
            elif marker[0] == fence_char and len(marker) >= fence_length and not fence.group("tail").strip():
                fence_char = None
            rewritten.append(line)
            continue
        if fence_char is not None or line.startswith("    ") or line.startswith("\t"):
            rewritten.append(line)
            continue

        code_spans = [(match.start(), match.end()) for match in _INLINE_CODE.finditer(line)]
        replacements: list[tuple[int, int, str]] = []
        for pattern in (_INLINE_LINK, _REFERENCE_LINK):
            for match in pattern.finditer(line):
                if any(start <= match.start() < end for start, end in code_spans):
                    continue
                start, end = match.span("target")
                replacements.append((start, end, rewrite_target(match.group("target"), source, root)))
        for start, end, value in sorted(replacements, reverse=True):
            line = line[:start] + value + line[end:]
        rewritten.append(line)
    return "".join(rewritten)


def prepare_sources(root: Path) -> Path:
    """Validate every input before replacing the previous staged documents."""
    root = root.resolve()
    staged: dict[str, str] = {}
    for source, destination in PAGES.items():
        destination_path = PurePosixPath(destination)
        if destination_path.is_absolute() or ".." in destination_path.parts or destination in staged:
            raise DocumentationError(f"Invalid or duplicate staged page path: {destination}")
        original = _repository_path(root, source)
        if original != root.joinpath(*PurePosixPath(source).parts):
            raise DocumentationError(f"Documentation source is redirected: {source}")
        if not original.is_file():
            raise DocumentationError(f"Required documentation source is missing: {source}")
        text = original.read_text(encoding="utf-8-sig")
        if source == "LICENSE":
            text = "# License\n\n" + text
        text = rewrite_markdown(text, source, root)
        text += f"\n\n---\n\n[View this page's source]({REPOSITORY_URL}/blob/{SOURCE_REF}/{quote(source, safe='/')})\n"
        staged[destination] = text

    output = clean_owned_directory(root, SOURCE_DIRECTORY)
    for relative, text in staged.items():
        destination = output.joinpath(*PurePosixPath(relative).parts)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(text, encoding="utf-8", newline="\n")
    return output


def build(root: Path, prepare_only: bool = False) -> Path:
    root = root.resolve()
    config_path = validate_configuration(root)
    source = prepare_sources(root)
    if prepare_only:
        return source
    output = clean_owned_directory(root, SITE_DIRECTORY)
    try:
        subprocess.run(
            [
                sys.executable, "-m", "mkdocs", "build", "--strict",
                "--site-dir", str(output), "--config-file", str(config_path),
            ],
            cwd=root,
            check=True,
        )
    finally:
        # MkDocs cleans the same validated owned output; retain its ownership.
        output = _output_path(root, SITE_DIRECTORY)
        if output.is_dir():
            (output / MARKER_NAME).write_text(MARKER_CONTENT, encoding="utf-8")
    return output


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepare-only", action="store_true", help="Stage documents for mkdocs serve without building the site.")
    args = parser.parse_args(argv)
    root = Path(__file__).resolve().parents[1]
    try:
        output = build(root, prepare_only=args.prepare_only)
    except (DocumentationError, OSError, subprocess.CalledProcessError) as error:
        print(f"Documentation build failed: {error}", file=sys.stderr)
        return 1
    print(f"Documentation {'sources' if args.prepare_only else 'site'}: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
