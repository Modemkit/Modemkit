# ModemKit documentation

[Repository overview](../README.md)

ModemKit exposes deterministic protocol engines. Your application supplies the
transport, file access and clock, then executes the actions returned by
`Advance(...)`. The five protocol libraries target `netstandard2.0` and
`net10.0`; the repository's test hosts and interoperability runner use .NET 10.

## Start here

| Task | Guide |
| --- | --- |
| Select a protocol, branch or compatibility profile | [Protocol branches](protocol-branches.md) |
| Connect an engine to a transport and file source/sink | [Integration guide](integration.md) |
| Handle buffering, timeouts, retries and completion | [Host event loop](integration.md#host-event-loop) and [timeouts and shutdown](integration.md#timeouts-and-shutdown) |
| Run tests against both library targets | [Testing guide](testing.md) |
| Inject loss, corruption, fragmentation or truncation | [Duplex fixture](../ModemKit.Testing/README.md) |
| Check compatibility with an independent executable | [lrzsz runner](../ModemKit.Interop/README.md) |
| Inspect recorded results and their limits | [Recorded validation](testing.md#recorded-validation) |
| Build, preview or deploy the documentation site | [Documentation deployment](deployment.md) |
| Prepare release notes, tags and package publication | [Release process](releases.md) |
| Inspect upcoming changes and tagged history | [Changelog](../CHANGELOG.md) |

## Protocol reference

| Library | Reference | Main engines |
| --- | --- | --- |
| XMODEM and WXMODEM | [Xmodem.Protocol](../Xmodem.Protocol/README.md) | `XModemSender`, `XModemReceiver`, `WXModemSender`, `WXModemReceiver` |
| YMODEM and YMODEM-G | [Ymodem.Protocol](../Ymodem.Protocol/README.md) | `YModemSender`, `YModemReceiver`, `YModemBatchSender`, `YModemBatchReceiver` |
| ZMODEM and extensions | [Zmodem.Protocol](../Zmodem.Protocol/README.md) | `ZModemSender`, `ZModemReceiver` |
| MODEM7, Telink and SEAlink | [LegacyModem.Protocol](../LegacyModem.Protocol/README.md) | `LegacyModemSender`, `LegacyModemReceiver` |
| JMODEM | [Jmodem.Protocol](../Jmodem.Protocol/README.md) | `JModemSender`, `JModemReceiver` |

The branch matrix distinguishes wire protocols, aliases and packet-size
policies. It also records which branches have an independently executed peer
and which use source-derived vectors, scripted peers and deterministic
fixtures. The [ZMODEM reference notes](../Zmodem.Protocol/PROTOCOL_NOTES.md)
describe the public legacy extensions and unresolved proprietary modes.

## Executable integration examples

The following tests exercise the public APIs through encoded bytes. They
demonstrate event dispatch, application decisions and fault recovery without
requiring a physical serial port.

- [XMODEM driver](../ModemKit.IntegrationTests/XModemWireTests.cs)
- [WXMODEM driver](../ModemKit.IntegrationTests/WXModemWireTests.cs)
- [YMODEM driver](../ModemKit.IntegrationTests/YModemWireTests.cs)
- [ZMODEM driver](../ModemKit.IntegrationTests/ZModemWireTests.cs)
- [Historical batch driver](../ModemKit.IntegrationTests/LegacyModemWireTests.cs)
- [JMODEM driver](../ModemKit.IntegrationTests/JModemWireTests.cs)
- [Queued application callbacks](../ModemKit.IntegrationTests/ProtocolActionQueue.cs)
- [Real process-pipe adapters](../ModemKit.Interop/Endpoints.cs)

Examples and coverage describe this checkout. When integrating a packaged
release, check its API and README against the package version you actually use.
