# Testing and interoperability

[Documentation index](README.md) | [Integration](integration.md) |
[Protocol branches](protocol-branches.md)

## Prerequisites

Use a .NET 10 SDK to build the solution, run xUnit hosts and run the optional
interoperability console. The protocol libraries themselves target both
`netstandard2.0` and `net10.0`. Deterministic tests require neither a physical
serial device nor lrzsz.

Run the commands below from the repository root.

## Deterministic suites

```console
dotnet restore ModemKit.slnx
dotnet build ModemKit.slnx --configuration Release --no-restore
dotnet test ModemKit.slnx --configuration Release --no-build
```

To exercise the other protocol assets:

```console
dotnet test ModemKit.slnx --configuration Release --no-restore -p:ProtocolUnderTestTargetFramework=netstandard2.0
```

The test hosts still run on .NET 10. The property changes their referenced
protocol libraries to the netstandard2.0 builds; it does not attempt to run a
test host on netstandard. The
[asset-selection test](../ModemKit.IntegrationTests/TargetFrameworkTests.cs)
checks the actual loaded framework attributes for all five libraries.

Do not add `--no-build` when switching the asset property: the test outputs
must be rebuilt with the selected references. To switch back, run
`dotnet test ModemKit.slnx --configuration Release` without the property.

Run a focused suite or regression with:

```console
dotnet test Jmodem.Protocol.Tests/Jmodem.Protocol.Tests.csproj -c Release
dotnet test ModemKit.IntegrationTests/ModemKit.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~JModemWireTests"
```

## Verification layers

| Layer | Location | Evidence |
| --- | --- | --- |
| Packet codecs | `*.Protocol.Tests` | Independent wire/checksum constants, malformed framing, escaping and expansion limits |
| State machines | `*.Protocol.Tests` | Negotiation, decisions, retries, cancellation and terminal behavior |
| Protocol fixtures | `*.Protocol.Tests` | Paired engines and scripted expected peer transactions |
| Shared byte transport | [Integration tests](../ModemKit.IntegrationTests) | Fragmentation, coalescing, loss, corruption, duplication, truncation and queue purging |
| External executable | [Interop runner](../ModemKit.Interop/README.md) | Native process exit status, completion, byte comparison, binary captures and hashes |

The [duplex fixture guide](../ModemKit.Testing/README.md) documents fault
selection, occurrence limits and virtual time. Failure messages retain a
bounded wire transcript. The link preserves byte ordering while removing
write boundaries, so purging queued output cannot erase fragments already
delivered to a receiver.

## Independent lrzsz runs

Install or provide compatible Linux `sz` and `rz` executables separately.
The runner does not install them. On Linux:

```console
dotnet run --project ModemKit.Interop/ModemKit.Interop.csproj -c Release -- --lrzsz-directory /usr/bin
```

On Windows, the runner uses WSL. Supply a Windows drive-letter directory
containing the Linux executables, readable by the selected distribution.
See the [Windows setup example](../ModemKit.Interop/README.md#run).

The default matrix runs six file sizes across these managed roles:

| Modes | Managed roles | Cases |
| --- | --- | ---: |
| `xchecksum`, `xcrc`, `x1k` | Send and receive | 36 |
| `y` | Send and receive | 12 |
| `yg`, `ychecksum` | Receive only | 12 |
| `z16`, `z32`, `z4k`, `z8k`, `zedzap`, `zedzip`, `zstream`, `zwindow` | Send and receive | 96 |
| **Total** | | **156** |

The file sizes are 0, 1, 129, 1025, 8193 and 65537 bytes. The largest case
crosses a 16K send window. In `--direction`, send/receive always describes the
managed ModemKit endpoint.

For a focused window/streaming check:

```console
dotnet run --project ModemKit.Interop/ModemKit.Interop.csproj -c Release -- --protocol zstream,zwindow --sizes 65537 --timeout-seconds 15
```

YMODEM-G/checksum sending is skipped because the native receiver used by this
fixture does not request those modes. XMODEM-G, WXMODEM, MODEM7, Telink,
SEAlink, JMODEM and legacy variable-header/RLE ZMODEM do not have a compatible
native peer in this matrix. Their independent vectors and scripted/paired
fixtures provide different evidence; they are not counted as native
interoperability passes.

### Evidence files

Each run creates `artifacts/interop-<UTC timestamp>-<id>/`, with a cumulative
`results.json`. Every case retains:

```text
<protocol>-<managed role>-<size>/
  command.json
  to-native.bin
  from-native.bin
  native-stderr.txt
  source/payload.bin
  received/payload.bin
```

Y/Z data must match exactly. XMODEM checks the exact source prefix plus bounded
SUB padding, so source and received hashes can legitimately differ. Stderr
is kept separate from the binary protocol stream. Each case has a wall-clock
deadline, and failed native processes are terminated with their child tree.

Exit codes are `0` for all executed cases passing, `1` for failures or no
executed cases, and `2` for invalid command-line options.

## Recorded validation

The branch implementation was validated on **October 30, 2026**, using
.NET SDK **10.0.401**. Native runs used Windows/WSL Ubuntu with lrzsz
`0.12.21rc`. This is a recorded working-tree result, not a statement about
every published package or a substitute for rerunning after code changes.

| Test project | net10.0 library assets | netstandard2.0 library assets |
| --- | ---: | ---: |
| Xmodem.Protocol.Tests | 121 | 121 |
| Ymodem.Protocol.Tests | 260 | 260 |
| Zmodem.Protocol.Tests | 82 | 82 |
| LegacyModem.Protocol.Tests | 90 | 90 |
| Jmodem.Protocol.Tests | 53 | 53 |
| ModemKit.IntegrationTests | 153 | 153 |
| **Passed / total** | **759 / 759** | **759 / 759** |

The complete native matrix passed **156/156**. A subsequent focused
streaming/windowed check passed **4/4** after the final ZMODEM decoder changes.
Release builds, six NuGet packages and `git diff --check` also passed.
The recorded build had non-blocking analyzer warnings; it was not warning-free.
Local logs and native evidence are under the ignored `artifacts/` directory
or ignored build-log files, rather than committed test output.

## Packaging and CI

```console
dotnet pack ModemKit.slnx --configuration Release --output artifacts/packages
```

The six packages are `ModemKit`, `Xmodem.Protocol`, `Ymodem.Protocol`,
`Zmodem.Protocol`, `LegacyModem.Protocol` and `Jmodem.Protocol`.
`ModemKit.Testing`, `ModemKit.Interop` and test projects are not packaged.
Package creation is separate from publication.

The [CI workflow](../.github/workflows/ci.yml) tests the Python automation
helpers and checks the changelog, then restores/builds the solution, runs both
asset selections, creates packages, installs its independent lrzsz peer and
uploads interoperability evidence. The
[documentation workflow](../.github/workflows/docs.yml) separately stages the
allowlisted Markdown and strictly validates the site and links on pull
requests; only the default `main` branch can deploy to Pages.

Run the automation and documentation checks locally with Python 3.12:

```console
python -m pip install -r requirements-docs.txt -r requirements-release.txt
python -m unittest discover -s scripts/tests -v
python scripts/changelog.py check
python scripts/build_docs.py
```

These tests cover guarded documentation output directories, link rewriting,
strict site validation, changelog parsing/preparation, release metadata,
package identities, dependency versions and checksums. Publishing commands
are mocked in the tests.

The [release workflow](../.github/workflows/cd.yml) runs these checks and both
protocol asset selections, then packages all six IDs at the same explicit
version. GitHub Release and optional NuGet publishing use separate jobs after
bundle validation. See [release preparation](releases.md) and
[Pages setup](deployment.md) for the repository/account configuration.
