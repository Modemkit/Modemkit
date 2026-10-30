# Documentation deployment

[Documentation index](README.md) | [Release process](releases.md) |
[Testing](testing.md)

The static documentation site is built from the Markdown already maintained in
this repository. Its configured address is
[modemkit.github.io/Modemkit](https://modemkit.github.io/Modemkit/). Creating or
building this configuration does not publish the site; publication occurs only
when the GitHub Pages workflow successfully deploys it.

## Build locally

Use Python 3.12, matching the workflow:

```console
python -m venv artifacts/docs-venv
```

On Windows, use the virtual environment's Python directly:

```console
artifacts\docs-venv\Scripts\python -m pip install -r requirements-docs.txt
artifacts\docs-venv\Scripts\python -m unittest discover -s scripts/tests -p test_build_docs.py -v
artifacts\docs-venv\Scripts\python scripts/build_docs.py
```

On Linux or macOS:

```console
artifacts/docs-venv/bin/python -m pip install -r requirements-docs.txt
artifacts/docs-venv/bin/python -m unittest discover -s scripts/tests -p test_build_docs.py -v
artifacts/docs-venv/bin/python scripts/build_docs.py
```

[requirements-docs.txt](../requirements-docs.txt) pins MkDocs to 1.6.1.
The build script stages the allowlisted pages under `artifacts/docs-source` and
builds HTML under `artifacts/docs-site`. The strict build rejects missing
pages, broken local anchors, invalid navigation and source links whose targets
do not exist. Generated files stay in the ignored `artifacts` directory.

The script cleans only its two fixed output directories and requires its
ownership marker before replacing an existing output. It refuses redirected
paths, unmarked directories, and MkDocs configurations that change `docs_dir`
or `site_dir`. Configuration inheritance and custom YAML tags are not accepted.
These checks run before staging or cleanup. MkDocs' separate recursive cleanup
is restricted to the same validated output by an explicit `--site-dir` argument.
If an output directory was created by another
tool, choose a different location for that unrelated work before building;
do not add a marker to files that should be preserved.

## Preview while editing

After installing dependencies, prepare the sources and run MkDocs with the
same Python interpreter:

```console
python scripts/build_docs.py --prepare-only
python -m mkdocs serve --dev-addr 127.0.0.1:8000
```

Replace `python` with the virtual environment's path if it is not activated.
Open the local URL printed by MkDocs. The server watches the generated staging
directory; after editing an original Markdown file, run `--prepare-only` again
to refresh it. Edit the originals, because the next build replaces staged files.

The configured `/Modemkit/` base path is included in generated canonical and
navigation URLs. Page links are relative and work under the project site.

## Source mapping and links

[scripts/build_docs.py](../scripts/build_docs.py) owns the page allowlist.
It includes the root overview, documentation guides, protocol package READMEs,
test-tool READMEs, ZMODEM reference notes, changelog and license. No source code,
test captures, build output or arbitrary workspace Markdown is copied into
the public site.

Root and package `README.md` files become `index.md` in the staging directory.
Local page links are rewritten to their corresponding staged pages while
preserving anchors. Links to existing code files, workflow files and source
directories become GitHub `blob/main` or `tree/main` links. Fenced and inline
code examples are preserved. Every rendered page also links to its original
Markdown source; the license links to the original `LICENSE`.

To add a page, add its source and staged path to the script's `PAGES` mapping,
then add it to the navigation in [mkdocs.yml](../mkdocs.yml). A relative link to
an unlisted Markdown page fails instead of publishing it implicitly. Run the
tests and strict build after changing navigation or heading anchors.

## GitHub Pages setup

An administrator configures the repository once:

1. Open **Settings > Pages**.
2. Select **GitHub Actions** as the build and deployment source.
3. Check the `github-pages` environment's deployment policy. It should permit
   `main`, the repository's default branch.
4. Push a documentation change to `main`, or run the **documentation** workflow
   manually with `main` selected.

The [documentation workflow](../.github/workflows/docs.yml) runs staging tests
and a strict build for pull requests, pushes to `main`, and manual runs. Pull
requests only build; they cannot upload or deploy a Pages artifact. A manual
run from another branch also only builds. Deployment requires both the `main`
ref and `main` as the repository's configured default branch.

The build job has only `contents: read`. Its separate deployment job receives
`contents: read`, `pages: write` and `id-token: write`, uses the `github-pages`
environment and publishes the artifact produced by the successful build.
Before deployment it checks the current `main` commit and skips obsolete runs,
including reruns of an older commit. It does not publish packages or create
releases. Concurrency serializes runs for `main`; newer pull-request runs may
cancel older builds without interrupting a deployment.

If the repository or project-site path changes, update `site_url`, `repo_url`
and `repo_name` in `mkdocs.yml`, plus `REPOSITORY_URL` and `SOURCE_REF` in the
staging script. If the default branch changes, update the workflow's branch
guards as well.

## Troubleshooting

| Failure | Action |
| --- | --- |
| Required documentation source is missing | Restore the allowlisted file or update both the allowlist and navigation intentionally. |
| Local Markdown link is not allowlisted | Add the page to the site explicitly, or use an intentional external reference. |
| Missing anchor under strict validation | Update the link to match the rendered heading; then rebuild. |
| Refusing to clean an unmarked or redirected output | Preserve the unrelated files and remove the path conflict; the builder does not clean arbitrary locations. |
| Pages configuration or deployment is denied | Check Settings > Pages, the `github-pages` environment policy, the selected branch and Actions permissions. |
| Successful build but no deployment job | Pull requests and non-default/non-main manual runs are build-only by design. |

Workflow behavior follows GitHub's
[custom Pages workflow documentation](https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages).
Site validation settings follow the official
[MkDocs configuration reference](https://www.mkdocs.org/user-guide/configuration/).
