# Changelog and releases

[Documentation index](README.md) | [Documentation deployment](deployment.md) |
[Changelog](../CHANGELOG.md)

The repository has three separate paths:

| Path | Trigger | Result |
| --- | --- | --- |
| CI | Pull requests and branch pushes | Tests, packages and interoperability evidence |
| Documentation | Pull requests; deployment from the default `main` branch | Strict Zensical build; GitHub Pages deployment on `main` |
| Release | A pushed `vMAJOR.MINOR.PATCH[-prerelease]` tag | Versioned packages, exact changelog notes and GitHub Release; optional NuGet publishing |

The release workflow remains named `.github/workflows/cd.yml` so an existing
NuGet trusted-publisher configuration can continue to identify that file.

## Install the maintenance tools

Use Python 3.12 and a .NET 10 SDK:

```console
python -m pip install -r requirements-release.txt -r requirements-docs.txt
```

`git-changelog` is pinned in `requirements-release.txt`. Its configuration uses
the basic commit convention and includes unrecognized messages, so the
repository's historical emoji and ordinary commit subjects are not discarded.

## Maintain the changelog

Add user-facing changes to [CHANGELOG.md](../CHANGELOG.md) under `Unreleased`.
Describe protocol/API changes, fixes and compatibility limits rather than
listing every implementation detail.

```console
python scripts/changelog.py check
python scripts/changelog.py refresh
```

`refresh` runs git-changelog against the full local Git history and imports
missing **tagged** release entries. It preserves the curated Unreleased body
and existing release entries. Fetch tags first when the local clone lacks
history:

```console
git fetch --tags origin
```

The initial history contains the real `v0.1.0` and `v0.2.0` tags. Historical
version-bump commits without a corresponding tag remain within their actual
tag range; the generator does not invent releases for them.

You can extract notes without changing the source file:

```console
python scripts/changelog.py notes --unreleased --output artifacts/unreleased-notes.md
python scripts/changelog.py notes --version 0.2.0 --output artifacts/release-notes.md
```

Version-specific extraction requires an exact entry and meaningful notes.
It never silently substitutes the latest entry or Unreleased.

## Prepare a tagged release

Before preparing a version, use this checklist:

- [ ] Run both protocol asset selections and the automation tests.
- [ ] Build the documentation with strict validation.
- [ ] Review the user-facing changes and compatibility notes.
- [ ] Verify the intended version and all six package identities.
- [ ] Confirm the publishing account and trusted-publisher settings.

Start from the reviewed, committed implementation on `main`. Use the actual
next version; **0.3.0 below is an example**, not a release created by this work.

!!! warning "A pushed version tag starts publication"

    Review the exact changelog entry and source commit before pushing the tag.
    Use the manual workflow with `publish = None` for an artifact-only preview.
    Do not tag the example version simply to test the pipeline.

```console
python scripts/changelog.py prepare 0.3.0
python scripts/changelog.py check
git diff -- CHANGELOG.md
```

The command moves the curated Unreleased body into a dated version section and
creates an empty Unreleased placeholder. It rejects an empty release, duplicate
version or version older than the existing history. It only edits the file.
Use `--date YYYY-MM-DD` when a specific release date is needed.

After reviewing the entry, commit it with the intended release source and push
the corresponding tag:

```console
git add CHANGELOG.md
git commit -m "docs: prepare v0.3.0 release"
git tag v0.3.0
git push origin main
git push origin v0.3.0
```

Prereleases use tags such as `v0.3.0-rc.1` and an entry for `0.3.0-rc.1`.
The tag must resolve to the checked-out release commit. The workflow fails
before publication if its version is invalid or the matching changelog entry
is missing. Build metadata (`+...`), leading-zero numeric identifiers,
whitespace and shell syntax are rejected.

Do not move a published tag to a different commit. Corrections should receive
a new version.

## Release pipeline

The read-only build job:

1. Resolves the tag/manual version from environment values and validates it.
2. Validates the changelog and runs the Python automation tests.
3. Restores/builds .NET, runs both protocol asset selections, and builds the
   Zensical documentation strictly with the pinned renderer.
4. Packs all six NuGet packages with the same explicit release version.
5. Checks package IDs/versions, the aggregate package's dependencies for both
   targets and each package's source commit, then creates a bundle with
   `CHANGELOG.md`, `release-notes.md` and `SHA256SUMS`.

The package IDs are `ModemKit`, `Xmodem.Protocol`, `Ymodem.Protocol`,
`Zmodem.Protocol`, `LegacyModem.Protocol` and `Jmodem.Protocol`. The explicit
release version overrides each project's development `VersionPrefix`.

The GitHub Release job has `contents: write`; the build job does not. It
creates or resumes a **draft**, uploads the validated bundle, and only then
publishes the release. A prerelease tag produces a prerelease. An already
published release is left intact on a rerun, so immutable releases are not
overwritten. Publication checks that the local and remote tag still resolve
to the commit that built the packages. A draft with unexpected assets fails
for maintainer review; it is not published with stale attachments.

The NuGet job is independent. A GitHub Release does not require NuGet account
configuration, and a NuGet login failure does not remove an already created
GitHub Release. The pipeline does not automatically commit changelog updates
or create tags.

## NuGet trusted publishing

For automatic NuGet publishing on a version tag:

- Set the repository variable `NUGET_USER` to the NuGet account name.
- Register the repository and `.github/workflows/cd.yml` as a trusted
  publisher in the NuGet account, with conditions that allow the intended tags.
- Ensure that account can publish every package ID in the six-package bundle.

No long-lived NuGet API key is stored in the workflow. `NuGet/login@v1`
exchanges the job's OIDC identity for a temporary key; only the publishing
job receives `id-token: write`. Existing package versions are skipped on
reruns.

When `NUGET_USER` is unset, tagged GitHub Releases still work and the automatic
NuGet job is skipped. An explicitly requested manual NuGet run requires that
configuration and an explicit version.

## Manual runs and retries

The workflow's manual `publish` input defaults to `None`. With no version,
it creates a CI preview version derived from the development version and
GitHub run identifiers. It builds an artifact bundle but creates no tag or
GitHub Release. An empty Unreleased section is valid for a manual build and
produces an explicit note that no upcoming changes have been recorded.

Set an explicit `version` and choose `NuGet` only when you intend to publish
that package version. Manual builds use preview/Unreleased notes; official
GitHub Release notes come from the exact tagged changelog entry.

For failures, inspect the build bundle and failed job. Draft releases can be
completed by a rerun; published GitHub releases are not rewritten, and NuGet
uses duplicate-version skipping. Protected tags and trusted-publisher account
rules remain repository/account settings.

## Workflow files and local checks

- [CI](../.github/workflows/ci.yml)
- [Pages](../.github/workflows/docs.yml)
- [Release](../.github/workflows/cd.yml)
- [Changelog maintenance](../scripts/changelog.py)
- [Release validation/publishing helper](../scripts/release.py)

```console
python -m unittest discover -s scripts/tests -v
python scripts/changelog.py check
python scripts/build_docs.py
```

These checks build and validate locally. They do not push commits, create a
remote release or publish a package.
