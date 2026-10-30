# Modem family branches

[Documentation index](README.md) | [Integration](integration.md) |
[Testing and evidence](testing.md)

This inventory describes executable wire behavior, not just names accepted by
an enum. Every implemented branch has sender/receiver state machines and byte
fixtures. Aliases share an implementation only when they describe the same
wire protocol.

!!! info "Implementation coverage is not the same as native interoperability"

    The validation column distinguishes an independently executed lrzsz peer
    from wire vectors and deterministic fixtures. Proprietary ZMODEM-90 modes
    are not implied by support for public legacy extensions.

## Implemented branches

| Branch | Selection / API | Distinct wire behavior | Validation |
| --- | --- | --- | --- |
| XMODEM checksum | `XModemMode.Checksum` | 128-byte blocks, additive checksum, NAK request | lrzsz, both directions |
| XMODEM-CRC | `XModemMode.Crc` | CRC16, C request | lrzsz, both directions |
| XMODEM-1K | `XModemMode.OneK` | STX 1024-byte CRC frames with 128-byte tail | lrzsz, both directions |
| XMODEM-G / QMODEM-G / XMODEM-1K-G | `XModemMode.G`, `QModemG`, `OneKG` | G request; no data ACK; abort rather than data retransmission | Source-derived vectors and deterministic fixtures; not covered by the selected native peer |
| WXMODEM | `WXModemSender`, `WXModemReceiver` | Four 128-byte blocks in flight, SYN/DLE framing, cumulative modulo-four ACK/NAK, XON/XOFF | Boswell author implementation and independent vectors; no native WX peer run |
| YMODEM checksum | `YModemTransferMode.Checksum` | NAK negotiation and one-byte checksum on block zero, data and final trailer | Native sz to managed receiver |
| YMODEM CRC / batch | Default Y engines | Metadata block zero, file lengths, two-EOT and direct-ACK compatibility, batch trailer | lrzsz, both directions |
| YMODEM 128 / 1K / mixed | `YModemBlockMode` and `YModemBlockOptions` | Fixed or dynamic packet capacities; optional small header/tail | Unit and wire matrices; standard mode also checked against lrzsz |
| YMODEM-G | `YModemTransferMode.G` | Streaming CRC data; G header negotiation; EOT acknowledgement | Native sz to managed receiver; managed sender has deterministic wire tests |
| MODEM7 batch | `LegacyModemVariant.Modem7` | Uppercase 8.3 filename character/ACK handshake, filename checksum, XMODEM data | FTS source vectors and scripted independent peer expectations |
| Telink | `LegacyModemVariant.Telink` | SYN block-zero metadata, mandatory header checksum, length and packed DOS timestamp | FTS source vectors and scripted peer expectations |
| SEAlink | `LegacyModemVariant.SeaLink` | Numbered ACK/NAK triples, sliding window (default six), metadata | FTS source vectors and window/fault fixtures |
| SEAlink Overdrive | `overdrive: true` on both legacy peers | Negotiated continuous sending with reduced ACK traffic | Negotiation and byte-stream fixtures |
| SEAlink RESYNC / restart | `enableResync: true`, `FileHeaderAccepted(restartBlock)` | SYN decimal block ETX plus little-endian CRC; absolute source offsets | Published block-27 vector and recovery fixtures |
| JMODEM | `JModemSender`, `JModemReceiver` | Adaptive 64..8192-byte length-prefixed frames, rotate/add checksum, exact EOF length | Johnson 3.03/3.10 source profile, golden vectors and byte fixtures |
| JMODEM compression / sizing policies | `EnableCompression`, `JModemBlockSizingPolicy` | Optional `0xBB` run-length encoding; documented or historical-source adaptive sizing | Literal/compressed vectors, retries and size-boundary fixtures |
| ZMODEM CRC16 / CRC32 | `ZModemOptions.UseCrc32` | ZHEX/ZBIN/ZBIN32 and negotiated FCS | lrzsz, both directions |
| ZMODEM streaming | `ZModemFlowControl.Streaming` | ZCRCG continuations, periodic ZCRCQ acknowledgements and ZCRCE EOF | lrzsz, both directions |
| ZMODEM sliding window | `ZModemFlowControl.Windowed` | ZCRCQ acknowledgements, bounded outstanding bytes and ZCRCW barriers | lrzsz, both directions |
| ZMODEM 4K / 8K | `ZModemProfile.Extended4K` / `Extended8K` with explicit block size | Larger encoded and decoded subpackets with corresponding limits | lrzsz start-4k/start-8k, both directions |
| ZedZap | `ZModemProfile.ZedZap`, `blockSize: 8192` | FTS-0006 ZMODEM batch profile with 8K maximum | Compatible lrzsz 8K data transfer; not a Fido mailer session |
| ZedZip | `ZModemProfile.ZedZip` | FTS-0006 ZedZap-compatible profile limited to 1K; empty batches allowed | Compatible lrzsz data transfer plus empty-batch fixtures |
| Public legacy variable headers | `useVariableHeaders: true` | Negotiated a/b/c/d formats, 0..16 header-data bytes | Historical source vectors, all split points, malformed-length tests |
| Public legacy ZMODEM RLE | `useRunLengthEncoding: true` with CRC32 and variable headers | Negotiated `d` sessions; codec also handles fixed `D`; run-length grammar and CRC32 | Independent vectors, expansion limits and paired wire fixtures |
| ZCRC file comparison | `FileCrcRequested` / `RequestFileCrc` application events/actions | Request and answer CRC without embedding filesystem policy | Scripted requests, response loss, caching and timeout fixtures |

YMODEM's 128/1K choices are packet policies, not new handshakes. Historical
products sometimes called headerless XMODEM-1K "YMODEM"; select `XModemMode.OneK`
for that peer instead of silently dropping YMODEM metadata.

The explicit ZedZip profile follows FTS-0006: its name does **not** imply ZIP
compression. The larger Z profiles require an agreed maximum on both peers;
ordinary ZRINIT does not universally negotiate an 8K parser.

## Limits that remain explicit

The public variable-header and RLE implementations are a verified historical
subset, not a claim to reproduce every commercial feature marketed as
ZMODEM-90. MobyTurbo, Pack-7, vendor-specific `'1'` compression and conflicting
ESC8 interpretations are not implemented. No complete, unambiguous public
wire specification and compatible test peer was established for them. Adding
an enum name or treating them as ordinary ZMODEM would not implement them.

ESC8 is not advertised. An incoming obsolete ESC8 capability flag is ignored;
ordinary unambiguous ZDLE high-bit bytes remain supported. This behavior and
the legacy RLE distinction are documented in the Z package protocol notes.

Remote command execution, encryption, sparse-file semantics and arbitrary
vendor file-management policies remain outside the binary transfer engines.
Fido YOOHOO/EMSI authentication, mail routing and bidirectional session role
turns belong to an enclosing mailer; the ZedZap/ZedZip engines handle file
transfer sessions and null batches.

X/WX/MODEM7 cannot communicate an exact file length. Their fixtures validate
source bytes and bounded SUB padding rather than inventing length metadata.
Raw historical Telink/SEAlink timestamps are preserved because their epoch
and platform interpretations are not interchangeable.

## Verification and sources

Run the complete deterministic suite against each library target:

```console
dotnet test ModemKit.slnx -c Release
dotnet test ModemKit.slnx -c Release -p:ProtocolUnderTestTargetFramework=netstandard2.0
```

`ModemKit.IntegrationTests` connects independent endpoint drivers through a
boundaryless `InMemoryDuplexLink`, including fragmented/coalesced reads,
corruption, loss, duplicate writes and partial-frame recovery. For window
restart, `DiscardPending(direction)` models purging only bytes the transport
has not delivered. It cannot retract already received partial frames.

The external runner's default lrzsz matrix also includes 65,537-byte files,
which exceed a 16K window. Each case retains command arguments, raw binary
traffic, native stderr, source/destination bytes and hashes.

The [testing guide](testing.md) lists the recorded per-project results and the
native matrix. Test counts describe a dated validation run, while this table
describes implemented behavior.

Primary protocol sources are identified in the
[package references](README.md#protocol-reference) and
[ZMODEM protocol notes](../Zmodem.Protocol/PROTOCOL_NOTES.md):

- Ward Christensen and Chuck Forsberg's X/YMODEM references.
- Peter Boswell's original public-domain WXMODEM implementation (1986).
- Synchronet's protocol-driver documentation for XMODEM-G aliases.
- FTS-0001.016, FTS-0007.003 and FTS-0006.002 for historical Fido transfers.
- Richard Johnson's JMODEM C 3.03/3.10 implementations and accompanying manual.
- Forsberg's public historical ZMODEM wire grammar and lrzsz source/documents.

Protocol-specific independent vectors are kept in the test projects. Historic
source archives and third-party binaries are not bundled in the NuGet packages.
