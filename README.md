# ModemKit

[Documentation](docs/README.md) | [Protocol branches](docs/protocol-branches.md) |
[Integration](docs/integration.md) | [Testing](docs/testing.md) |
[Changelog](CHANGELOG.md) | [Releases](docs/releases.md)

ModemKit provides transport-independent .NET file-transfer engines for
XMODEM, YMODEM, ZMODEM, JMODEM and historical modem protocols. It includes the
public branches used for streaming, sliding windows, larger packets and
legacy batch transfers.

The application owns its serial port or socket, file storage and clock.
Engines accept events through `Advance(...)` and return ordered actions and
state snapshots. They do not perform hidden transport or filesystem I/O.

## Libraries and coverage

All five protocol libraries target **netstandard2.0** and **net10.0**.

| Library | Implemented protocols and profiles |
| --- | --- |
| [Xmodem.Protocol](Xmodem.Protocol/README.md) | Checksum, CRC16, 1K, XMODEM-G/QMODEM-G/1K-G; WXMODEM's four-block window and fallback |
| [Ymodem.Protocol](Ymodem.Protocol/README.md) | Checksum/CRC single-file and batch transfers; fixed/mixed 128/1K blocks; YMODEM-G |
| [Zmodem.Protocol](Zmodem.Protocol/README.md) | CRC16/32; streaming/windows; 4K/8K, ZedZap/ZedZip; resume/skip; public legacy variable headers/RLE; ZCRC hooks |
| [LegacyModem.Protocol](LegacyModem.Protocol/README.md) | MODEM7, Telink, SEAlink, Overdrive and RESYNC/restart |
| [Jmodem.Protocol](Jmodem.Protocol/README.md) | Adaptive 64..8192-byte blocks, optional RLE and exact final lengths |

Each library provides sender/receiver engines, codecs, incremental decoding,
explicit timeouts and bounded retries where the protocol permits recovery.
Applications confirm received data after persisting it. Supported duplicate
recovery avoids delivering accepted data twice; G-mode data errors abort the
stream instead of requesting retransmission.

The [branch matrix](docs/protocol-branches.md) records exact API selections,
aliases, wire behavior and evidence for every implemented branch.

## Build and use this checkout

Use a **.NET 10 SDK** for the solution, test hosts and interoperability runner.
The libraries' netstandard2.0 assets remain available for compatible consumers.

```console
dotnet restore ModemKit.slnx
dotnet build ModemKit.slnx --configuration Release --no-restore
dotnet pack ModemKit.slnx --configuration Release --no-build --output artifacts/packages
```

`ModemKit.csproj` is an aggregate package referencing all five protocol
libraries. Add a project reference to it, or to the individual protocol project
you need. The pack command produces six NuGet packages in `artifacts/packages`;
the test-support projects are not packaged.

Coverage and examples here describe this checkout. Package availability and
release versions are separate; use source references or locally built packages
to reproduce the documented branch implementation.

## Event/action API

Starting a receiver produces actions for the host to execute. For example,
this first YMODEM step prints the byte that should be sent to the peer:

```csharp
using System;
using Ymodem.Protocol;

var receiver = new YModemBatchReceiver();
var step = receiver.Advance(new YModemEvent.StartRequested());

foreach (var action in step.Actions)
{
    if (action is YModemAction.SendControl control)
    {
        Console.WriteLine($"Transmit 0x{control.Value:X2}"); // Transmit 0x43
    }
}
```

A complete integration serializes engine calls, feeds peer bytes, executes
wire actions in order and fulfils source/storage requests. Queue application
responses so streaming transfers do not recursively grow the call stack.

X/Y packet actions must be encoded using their selected `Integrity`. WX, Z,
legacy and J engines accept raw byte chunks and emit `SendBytes` actions.
Partial-frame cleanup, application buffering and shutdown handling differ by
protocol; the [integration guide](docs/integration.md) provides the routing
matrix, configuration snippets and a runnable receiver walkthrough.

## Tests and interoperability

Run the deterministic suites against both protocol assets:

```console
dotnet test ModemKit.slnx --configuration Release --no-build
dotnet test ModemKit.slnx --configuration Release --no-restore -p:ProtocolUnderTestTargetFramework=netstandard2.0
```

The second command rebuilds .NET 10 test hosts against the netstandard2.0
libraries. An integration test checks the framework attributes of the actual
loaded assemblies. Omit `--no-build` when changing asset selection.

- [ModemKit.Testing](ModemKit.Testing/README.md) supplies a deterministic duplex
  byte link with fragmentation, coalescing, corruption, loss, duplication,
  truncation, queue purging and virtual timeouts.
- [Integration tests](ModemKit.IntegrationTests) exercise every implemented
  family through encoded bytes, including empty files, rollover, binary data,
  windows, restart and closing-handshake recovery.
- [ModemKit.Interop](ModemKit.Interop/README.md) runs a default **156-case**
  matrix against independent lrzsz executables, preserving binary captures,
  command arguments, stderr and hashes.

The [testing guide](docs/testing.md) contains prerequisites, focused commands,
CI behavior and the dated validation record: **759/759** deterministic tests
for each asset selection and **156/156** native cases. Branches without a
compatible native peer use independent wire vectors and scripted/paired
fixtures; their evidence is listed separately.

## Documentation and releases

The [documentation workflow](.github/workflows/docs.yml) builds this Markdown
as a searchable MkDocs site and deploys it to GitHub Pages from `main`.
Pull requests run the strict build and link checks. See the
[deployment guide](docs/deployment.md) for local previews and the one-time
repository Pages configuration.

[CHANGELOG.md](CHANGELOG.md) keeps upcoming work under `Unreleased` and imports
historical releases from actual Git tags using `git-changelog`. A version tag
triggers the [release workflow](.github/workflows/cd.yml), which validates the
matching changelog entry, tests both library assets and bundles six packages
at one version with release notes and SHA-256 checksums.

GitHub Release and optional NuGet trusted publishing run in separate jobs.
Manual runs default to an artifact-only preview. The
[release guide](docs/releases.md) explains changelog preparation, tagging,
NuGet account configuration and safe retries.

## Compatibility boundaries

Choose the profile implemented by the peer; these families do not share one
universal negotiation mechanism. XMODEM, WXMODEM and MODEM7 do not carry exact
file lengths. Preserve their padding unless an external contract provides the
length. The host decides where offered files are stored.

The public legacy ZMODEM extensions are a defined subset. MobyTurbo, Pack-7,
ambiguous ESC8 and other unresolved proprietary ZMODEM-90 modes are not
implemented. ZedZap/ZedZip provide file-transfer profiles; a Fido mailer's
session negotiation and routing remain outside these engines. See the
[branch limits](docs/protocol-branches.md#limits-that-remain-explicit) and
[ZMODEM reference notes](Zmodem.Protocol/PROTOCOL_NOTES.md).

The project is in the `0.x` stage. Follow the protocol package README for the
version you consume, especially its timer, source-read and storage contracts.

## Repository layout

```text
ModemKit.csproj             Aggregate package
Xmodem.Protocol/            XMODEM and WXMODEM
Ymodem.Protocol/            YMODEM and YMODEM-G
Zmodem.Protocol/            ZMODEM profiles and extensions
LegacyModem.Protocol/       MODEM7, Telink and SEAlink
Jmodem.Protocol/            Adaptive JMODEM
*.Protocol.Tests/           Protocol tests and wire fixtures
ModemKit.Testing/           Shared fault-injecting byte transport
ModemKit.IntegrationTests/  Family-wide byte-stream integration tests
ModemKit.Interop/           Independent lrzsz runner
docs/                      Integration, testing, coverage and deployment guides
scripts/                   Documentation, changelog and release tooling
.github/workflows/         Validation, Pages deployment and release pipelines
CHANGELOG.md               Curated upcoming changes and tagged release history
```

## License

MIT; see [LICENSE](LICENSE).
