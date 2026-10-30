"""Maintain curated release notes while importing real tag history with git-changelog."""

from __future__ import annotations

import argparse
import datetime as dt
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REPOSITORY_URL = "https://github.com/Modemkit/Modemkit"
EMPTY_UNRELEASED = "<!-- Add user-facing changes here before preparing a release. -->"
VERSION = re.compile(
    r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?"
)
HEADING = re.compile(
    r"^## +(?:\[(?P<label>[^\]]+)\](?:\([^\r\n)]*\))?|(?P<plain>[^ \r\n]+))"
    r"(?: +- +(?P<date>[0-9]{4}-[0-9]{2}-[0-9]{2}))? *$",
    re.MULTILINE,
)


@dataclass(frozen=True)
class Section:
    version: str | None
    date: dt.date | None
    heading: str
    body: str

    def render(self) -> str:
        return f"{self.heading}\n\n{self.body.strip()}\n"


def validate_version(value: str) -> str:
    """Return a NuGet-compatible SemVer without the optional lowercase v prefix."""
    if not isinstance(value, str) or len(value) > 128:
        raise ValueError("Version must be a SemVer string of at most 128 characters.")
    version = value[1:] if value.startswith("v") else value
    match = VERSION.fullmatch(version)
    if not match:
        raise ValueError("Expected MAJOR.MINOR.PATCH[-prerelease], with no build metadata or whitespace.")
    if match[4]:
        for identifier in match[4].split("."):
            if identifier.isdigit() and len(identifier) > 1 and identifier.startswith("0"):
                raise ValueError("Numeric prerelease identifiers cannot contain leading zeros.")
    return version


def version_key(value: str) -> tuple:
    match = VERSION.fullmatch(validate_version(value))
    assert match is not None
    base = tuple(int(match[index]) for index in (1, 2, 3))
    if not match[4]:
        return (*base, 1, ())
    prerelease = tuple(
        (0, int(part)) if part.isdigit() else (1, part)
        for part in match[4].split(".")
    )
    return (*base, 0, prerelease)


def parse_changelog(text: str, *, require_unreleased: bool = True) -> tuple[str, list[Section]]:
    text = text.replace("\r\n", "\n")
    headings = list(HEADING.finditer(text))
    if not headings:
        raise ValueError("Changelog contains no version sections.")
    if len(headings) != len(re.findall(r"^## +", text, flags=re.MULTILINE)):
        raise ValueError("Every level-two changelog heading must name Unreleased or a SemVer release.")
    sections: list[Section] = []
    seen: set[str | None] = set()
    for index, heading in enumerate(headings):
        label = heading["label"] or heading["plain"]
        version = None if label.lower() == "unreleased" else validate_version(label)
        if version in seen:
            raise ValueError(f"Duplicate changelog section: {label}")
        seen.add(version)
        date = dt.date.fromisoformat(heading["date"]) if heading["date"] else None
        if version is not None and date is None:
            raise ValueError(f"Release {version} needs a YYYY-MM-DD date.")
        if version is None and date is not None:
            raise ValueError("Unreleased must not have a release date.")
        end = headings[index + 1].start() if index + 1 < len(headings) else len(text)
        body = text[heading.end():end].strip()
        sections.append(Section(version, date, heading.group(), body))
    if require_unreleased and sections[0].version is not None:
        raise ValueError("The first changelog section must be Unreleased.")
    if any(section.version is None for section in sections[1:]):
        raise ValueError("Unreleased must precede every released version.")
    return text[:headings[0].start()].rstrip(), sections


def meaningful_notes(body: str) -> str:
    visible = re.sub(r"<!--.*?-->", "", body, flags=re.DOTALL).strip()
    lines = [line for line in visible.splitlines() if line.strip() and not line.startswith("#")]
    if not lines:
        raise ValueError("Release notes are empty; add user-facing changes first.")
    return visible + "\n"


def release_notes(text: str, version: str) -> str:
    wanted = validate_version(version)
    _, sections = parse_changelog(text)
    for section in sections:
        if section.version == wanted:
            return meaningful_notes(section.body)
    raise ValueError(f"CHANGELOG.md has no entry for {wanted}; prepare it before tagging.")


def unreleased_notes(text: str) -> str:
    _, sections = parse_changelog(text)
    return meaningful_notes(sections[0].body)


def render_changelog(preamble: str, sections: list[Section]) -> str:
    return preamble.rstrip() + "\n\n" + "\n".join(section.render() for section in sections)


def prepare_release(text: str, version: str, date: dt.date) -> str:
    version = validate_version(version)
    preamble, sections = parse_changelog(text)
    if any(section.version == version for section in sections):
        raise ValueError(f"Release {version} is already recorded.")
    released = [section for section in sections if section.version is not None]
    if released and version_key(version) <= max(version_key(section.version) for section in released):
        raise ValueError("The prepared version must be newer than every recorded release.")
    notes = meaningful_notes(sections[0].body)
    release = Section(
        version,
        date,
        f"## [v{version}]({REPOSITORY_URL}/releases/tag/v{version}) - {date.isoformat()}",
        notes,
    )
    return render_changelog(
        preamble,
        [Section(None, None, "## [Unreleased]", EMPTY_UNRELEASED), release, *released],
    )


def merge_history(text: str, generated: str) -> str:
    """Keep curated text, including Unreleased, and import only missing tagged entries."""
    preamble, current = parse_changelog(text)
    _, historical = parse_changelog(generated, require_unreleased=False)
    released = {section.version: section for section in current if section.version is not None}
    for section in historical:
        if section.version is not None:
            released.setdefault(section.version, section)
    ordered = sorted(released.values(), key=lambda section: version_key(section.version), reverse=True)
    return render_changelog(preamble, [current[0], *ordered])


def generate_history(repository: Path) -> str:
    with tempfile.TemporaryDirectory(prefix="modemkit-changelog-") as directory:
        output = Path(directory) / "history.md"
        subprocess.run(
            [
                sys.executable, "-m", "git_changelog",
                "--config-file", str(repository / ".git-changelog.toml"),
                "--output", str(output), str(repository),
            ],
            cwd=repository,
            check=True,
            timeout=120,
        )
        return output.read_text(encoding="utf-8")


def write_text(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text.rstrip() + "\n", encoding="utf-8", newline="\n")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--file", type=Path, default=ROOT / "CHANGELOG.md")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("check", help="Validate section names, dates and recorded release notes.")
    commands.add_parser("refresh", help="Import missing Git-tag entries without replacing curated notes.")
    prepare = commands.add_parser("prepare", help="Move Unreleased into a dated version; no Git or network writes.")
    prepare.add_argument("version")
    prepare.add_argument("--date", type=dt.date.fromisoformat, default=dt.date.today())
    notes = commands.add_parser("notes", help="Extract the exact release section, or explicitly select Unreleased.")
    selection = notes.add_mutually_exclusive_group(required=True)
    selection.add_argument("--version")
    selection.add_argument("--unreleased", action="store_true")
    notes.add_argument("--output", type=Path)
    args = parser.parse_args(argv)
    try:
        text = args.file.read_text(encoding="utf-8")
        if args.command == "check":
            _, sections = parse_changelog(text)
            for section in sections:
                if section.version is not None:
                    meaningful_notes(section.body)
            print(f"Validated {len(sections) - 1} releases and Unreleased.")
        elif args.command == "refresh":
            write_text(args.file, merge_history(text, generate_history(ROOT)))
            print(f"Refreshed tagged history in {args.file}; curated entries preserved.")
        elif args.command == "prepare":
            write_text(args.file, prepare_release(text, args.version, args.date))
            print(f"Prepared v{validate_version(args.version)} in {args.file}. No commit or tag was created.")
        else:
            result = unreleased_notes(text) if args.unreleased else release_notes(text, args.version)
            if args.output:
                write_text(args.output, result)
            else:
                print(result, end="")
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        print(f"changelog: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
