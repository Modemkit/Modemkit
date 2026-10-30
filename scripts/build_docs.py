#!/usr/bin/env python3
"""Stage the documented allowlist and strictly build the ModemKit site."""

from __future__ import annotations

import argparse
from collections import Counter
from pathlib import Path, PurePosixPath
import posixpath
import re
import shutil
import subprocess
import sys
import tomllib
from urllib.parse import quote, unquote, urlsplit, urlunsplit


REPOSITORY_URL = "https://github.com/Modemkit/Modemkit"
SOURCE_REF = "main"
SOURCE_DIRECTORY = "artifacts/docs-source"
SITE_DIRECTORY = "artifacts/docs-site"
CACHE_DIRECTORY = ".cache"
OWNED_DIRECTORIES = (SOURCE_DIRECTORY, SITE_DIRECTORY, CACHE_DIRECTORY)
MARKER_NAME = ".modemkit-docs-owned"
MARKER_CONTENT = "ModemKit generated documentation directory v1\n"

# Publishing is opt-in. A new page must be added here and to zensical.toml.
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
    r"^[ \t]*\[[^\]\n]+\]:[ \t]*(?P<target><[^>\n]+>|[^\s\n]+)"
)
_INLINE_CODE = re.compile(r"(?P<fence>`+)(?!`)(?P<code>.*?)(?<!`)(?P=fence)(?!`)")
_FENCE = re.compile(r"^[ \t]{0,3}(?P<fence>`{3,}|~{3,})(?P<tail>.*)$")
_MARKDOWN_CONTAINER = re.compile(
    r"""^(?:(?:!!!|\?\?\?\+?)[ \t]+|===[ \t]+["']|(?:[-+*]|\d+[.)])[ \t]+)"""
)
_EXCLUDED_COMPONENTS = {".git", ".cache", "artifacts", "bin", "obj", "__pycache__"}


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
    if relative not in OWNED_DIRECTORIES:
        raise DocumentationError(f"Not an owned documentation output: {relative}")
    root = root.resolve()
    expected = root.joinpath(*PurePosixPath(relative).parts)
    resolved = expected.resolve()
    boundary = root if relative == CACHE_DIRECTORY else root / "artifacts"
    if resolved != expected or not _inside(resolved, boundary):
        raise DocumentationError(f"Documentation output is redirected: {expected}")
    return resolved


def validate_configuration(root: Path) -> Path:
    """Validate all renderer path inputs before creating or cleaning anything."""
    root = root.resolve()
    config_path = root / "zensical.toml"
    if config_path.resolve() != config_path or not config_path.is_file():
        raise DocumentationError(f"Zensical configuration is missing or redirected: {config_path}")
    try:
        configuration = tomllib.loads(config_path.read_text(encoding="utf-8-sig"))
    except tomllib.TOMLDecodeError as error:
        raise DocumentationError(f"Invalid Zensical TOML configuration: {error}") from error
    if set(configuration) != {"project"} or not isinstance(configuration["project"], dict):
        raise DocumentationError("Zensical configuration must use a single [project] table.")
    project = configuration["project"]
    if any(key in project for key in ("INHERIT", "config_file_path", "root_dir", "cache_dir")):
        raise DocumentationError("Configuration inheritance, path indirection or cache_dir overrides are not supported.")
    for key, relative in (("docs_dir", SOURCE_DIRECTORY), ("site_dir", SITE_DIRECTORY)):
        if project.get(key) != relative:
            raise DocumentationError(f"Zensical {key} must be exactly {relative!r}.")
    if project.get("strict") is not True:
        raise DocumentationError("Zensical strict mode must remain enabled.")
    validation = project.get("validation")
    if not isinstance(validation, dict) or any(
        validation.get(key) is not True for key in ("invalid_links", "invalid_link_anchors")
    ):
        raise DocumentationError("Zensical link and anchor validation must remain enabled.")
    _validate_navigation(project.get("nav"))
    # 0.0.73 hardcodes .cache beside the configuration file. A TOML cache_dir
    # would be silently ignored, so protect that real path before any mutation.
    for relative in OWNED_DIRECTORIES:
        _validate_owned_directory(root, relative)
    return config_path


def _validate_navigation(navigation: object) -> None:
    destinations: list[str] = []

    def visit(items: object) -> None:
        if not isinstance(items, list) or not items:
            raise DocumentationError("Navigation sections must be nonempty lists.")
        for item in items:
            if not isinstance(item, dict) or len(item) != 1:
                raise DocumentationError("Each navigation entry must have one title and target.")
            title, target = next(iter(item.items()))
            if not isinstance(title, str) or not title.strip():
                raise DocumentationError("Navigation titles must be nonempty strings.")
            if isinstance(target, str):
                destinations.append(target)
            else:
                visit(target)

    visit(navigation)
    if Counter(destinations) != Counter(PAGES.values()):
        raise DocumentationError("Navigation must contain every allowlisted page exactly once and no other targets.")


def _validate_owned_directory(root: Path, relative: str) -> Path:
    output = _output_path(root, relative)
    marker = output / MARKER_NAME
    if output.exists():
        if not output.is_dir() or not marker.is_file():
            raise DocumentationError(f"Refusing to use an unmarked output or cache: {output}")
        if marker.is_symlink() or marker.read_text(encoding="utf-8") != MARKER_CONTENT:
            raise DocumentationError(f"Invalid ownership marker: {marker}")
        pending = [output]
        while pending:
            for child in pending.pop().iterdir():
                if child.is_symlink() or (
                    hasattr(child, "is_junction") and child.is_junction()
                ):
                    raise DocumentationError(f"Refusing to clean a linked output: {child}")
                if not _inside(child.resolve(), output):
                    raise DocumentationError(f"Generated child escapes its output: {child}")
                if child.is_dir():
                    pending.append(child)
    return output


def clean_owned_directory(root: Path, relative: str) -> Path:
    """Clean only a fixed output carrying this builder's ownership marker."""
    output = _validate_owned_directory(root, relative)
    marker = output / MARKER_NAME
    if output.exists():
        for child in output.iterdir():
            if child == marker:
                continue
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
    """Rewrite links in paragraphs and known Markdown containers, not code."""
    if source not in PAGES:
        raise DocumentationError(f"Source is not allowlisted: {source}")
    rewritten: list[str] = []
    fence_char: str | None = None
    fence_length = 0
    fence_indent = 0
    containers: list[int] = []
    for line in text.splitlines(keepends=True):
        expanded = line.expandtabs(4)
        indentation = len(expanded) - len(expanded.lstrip(" "))
        if fence_char is not None:
            fence = _FENCE.match(expanded[fence_indent:]) if indentation >= fence_indent else None
            if fence:
                marker = fence.group("fence")
                if marker[0] == fence_char and len(marker) >= fence_length and not fence.group("tail").strip():
                    fence_char = None
            rewritten.append(line)
            continue
        if not line.strip():
            rewritten.append(line)
            continue
        while containers and indentation < containers[-1]:
            containers.pop()
        content_indent = containers[-1] if containers else 0
        fence = _FENCE.match(expanded[content_indent:])
        if fence:
            marker = fence.group("fence")
            fence_char, fence_length, fence_indent = marker[0], len(marker), content_indent
            rewritten.append(line)
            continue
        # Four spaces inside a tab, admonition or list are ordinary Markdown;
        # another four spaces are an indented code block in that container.
        if indentation - content_indent >= 4:
            rewritten.append(line)
            continue
        if _MARKDOWN_CONTAINER.match(expanded[indentation:]):
            containers.append(indentation + 4)

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
    clean_owned_directory(root, CACHE_DIRECTORY)
    if prepare_only:
        return source
    output = clean_owned_directory(root, SITE_DIRECTORY)
    try:
        subprocess.run(
            [
                sys.executable, "-m", "zensical", "build", "--clean", "--strict",
                "--config-file", str(config_path),
            ],
            cwd=root,
            check=True,
        )
    finally:
        # Zensical --clean replaces .cache; restore ownership on failed builds too.
        for relative in (SITE_DIRECTORY, CACHE_DIRECTORY):
            owned = _output_path(root, relative)
            owned.mkdir(parents=True, exist_ok=True)
            (owned / MARKER_NAME).write_text(MARKER_CONTENT, encoding="utf-8")
    return output


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepare-only", action="store_true", help="Stage documents for zensical serve without building the site.")
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
