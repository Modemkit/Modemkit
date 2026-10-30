# LegacyModem.Protocol

[Documentation index](../docs/README.md) | [Integration guide](../docs/integration.md) |
[Testing](../docs/testing.md)

Transport-independent MODEM7, TeLink, and SEAlink file-transfer engines for
`netstandard2.0` and `net10.0`. All variants support batches, empty files,
128-byte data blocks, checksum/CRC negotiation, cancellation, explicit
timeouts, and bounded retries. Protocol instances use a serialized event loop;
they do not open files, create timers, or own a serial connection.

## Protocol coverage

| Variant | File information | Transfer behavior |
| --- | --- | --- |
| MODEM7 | Uppercase DOS 8.3 name, acknowledged one character at a time; checksum includes SUB | XMODEM checksum or CRC data, unknown received length |
| TeLink | SYN block zero, binary length, packed DOS timestamp, 16-character name; header always uses an additive checksum | Acknowledged XMODEM data; final padding trimmed by advertised size |
| SEAlink | SOH block zero with binary metadata and capability flags | Numbered ACK/NAK triples; sliding window, default six blocks and configurable from 1 through 127 |
| SEAlink Overdrive | Header capability flag | Streams data without per-block ACKs; header/EOT remain acknowledged; switches away from Overdrive if a peer continues acknowledging blocks |
| SEAlink RESYNC | Header capability flag | Absolute block restart for an interrupted file and for data-error recovery; ASCII block number and little-endian CRC |

The engines negotiate the documented fallbacks: SEAlink can retry with a TeLink
header, then proceed without metadata when a peer rejects headers. A receiver
can fall back from initial CRC polling to MODEM7 filename negotiation.
`AllowFallback = false` limits these adaptations. In a checksum-only session,
the initial NAK selects MODEM7 filename negotiation and TeLink metadata rather
than SEAlink's CRC/window negotiation.

The Macintosh flow-control capability bit is left clear; it is not advertised.

## Event/action integration

```csharp
using LegacyModem.Protocol;

var options = new LegacyModemOptions(
    variant: LegacyModemVariant.SeaLink,
    windowSize: 6,
    overdrive: false,
    enableResync: true,
    maxRetries: 10);

var sender = new LegacyModemSender(options);
var receiver = new LegacyModemReceiver(options);

LegacyModemStepResult start = receiver.Advance(
    new LegacyModemEvent.StartRequested());
```

Execute actions in the order returned:

- `SendBytes`: queue every byte to the transport unchanged.
- `PurgeOutput`: discard this endpoint's queued, unsent transport output before
  processing subsequent actions. Bytes already delivered to the peer cannot be
  recalled. SEAlink resynchronization relies on this operation.
- `RequestFileHeader`: answer with `FileHeaderReady(file)` or `NoMoreFiles`.
- `RequestDataBlock`: read exactly `DataLength` bytes at the absolute `Offset`
  and answer with `DataBlockReady(BlockNumber, bytes)`. Requests can repeat or
  move backward during recovery, so the file source must support rereading.
- `OfferFileHeader`: answer with `FileHeaderAccepted()` or `FileHeaderRejected`.
- `DeliverDataBlock`: persist the bytes at the supplied `Offset`, then answer
  with `DataBlockAccepted` or `DataBlockRejected`.
- `FileCompleted`: the current file has finished; the batch may continue.
- `Complete`, `Cancel`, and `Fail`: batch termination outcomes.

Supply transport reads directly:

```csharp
LegacyModemStepResult step = receiver.Advance(
    new LegacyModemEvent.BytesReceived(incomingBytes));
```

Reads may split any frame or combine several frames. The receiver retains
remaining input while waiting for the application's file/data decision, then
continues decoding after that decision. `MaxBufferedBytes` limits retained
input, including input received while application work is pending. A local
cancellation sends two consecutive CAN bytes; peer cancellation also requires
two consecutive CAN bytes outside frames.

Continue servicing incoming transport data while executing source-read
requests; this matters for a full-duplex sliding window and Overdrive.

## Metadata and resuming files

Senders require an exact size, from zero through `uint.MaxValue`:

```csharp
var file = new LegacyModemFileDescriptor("UPDATE.BIN", fileLength, rawTimestamp);
sender.Advance(new LegacyModemEvent.FileHeaderReady(file));
```

File names contain printable ASCII base names, with no path separator, and are
limited to 16 characters. MODEM7 additionally requires a DOS 8.3 name and
transmits it in uppercase. MODEM7 does not transmit the exact file size, so its
receive descriptor has `FileSize = null` and final CP/M EOF padding is
delivered unchanged. TeLink and SEAlink trim padding to the header length and
reject premature EOT.

`Timestamp` deliberately stores the raw 32-bit wire value. TeLink packs the
DOS time word followed by the date word. FTS-0007 describes SEAlink seconds
from 1979, while historical source implementations use other epochs. The
application selects the appropriate interpretation for its peer; the codec
does not silently reinterpret or alter this field.

When `OfferFileHeader.CanRestart` is true and the application already has
validated data for the start of the file, it may accept from an absolute block:

```csharp
receiver.Advance(new LegacyModemEvent.FileHeaderAccepted(restartBlock: 27));
```

Block 1 starts at byte zero. Block 27 starts at byte 3328. A restart at the
block after the final data block requests EOT. The receiver preserves the
caller-owned prefix and delivers only the remaining file data. If RESYNC is
not available, accepting any restart block other than 1 fails explicitly.

## Timeouts and historical wire limits

The host supplies `TimeoutElapsed` when its phase-specific deadline expires.
The receiver drops truncated input before retrying. Application-decision
states do not time out internally. MODEM7 traditionally uses short per-character
deadlines; metadata/data and SEAlink resynchronization use longer deadlines.
Select appropriate deadlines for the actual serial, socket, or bridge latency.
`MaxRetries` bounds unsuccessful protocol retries, independent of clock speed.

Use RESYNC with Overdrive. An eight-bit numbered NAK cannot safely identify
an old damaged block when more than 127 blocks remain unconfirmed and sequence
numbers can wrap. The sender aborts that ambiguous recovery request instead of
replaying a potentially wrong file range. Absolute RESYNC requests do not have
this ambiguity.

The historical batch terminator is a single EOT without a final ACK. Keep
feeding polls to a completed sender during a short transport grace period;
it can resend a lost batch terminator. In SEAlink a new, unnumbered CRC header
poll can also recover a lost per-file EOT ACK. MODEM7 and TeLink's unnumbered
poll/EOT exchanges cannot distinguish
every combination of a lost EOF ACK and a subsequent batch poll; such
failures remain bounded and must be retried as a new session. The library
does not claim stronger delivery guarantees than the historical wire format.

## Codecs and verification

`LegacyModemCodec` exposes metadata/data framing, MODEM7 names and their
checksum, numbered responses, and RESYNC packets. Payloads and queued byte
actions own their buffers. This isolates retries from application mutation.

The companion test project contains a byte-only duplex fixture with packet
fragmentation, corruption, dropping, queue purging, and deterministic timeout
injection. Independent scripted peer transactions exercise both engine
directions. Golden vectors include the published RESYNC block-27 packet and
CRC/checksum values calculated independently with Python `binascii`.

```bash
dotnet test LegacyModem.Protocol.Tests/LegacyModem.Protocol.Tests.csproj -c Release
dotnet test LegacyModem.Protocol.Tests/LegacyModem.Protocol.Tests.csproj -c Release -p:ProtocolUnderTestTargetFramework=netstandard2.0
```

The implementation is based on the original protocol specifications:

- [FTS-0001.016](https://github.com/zoomosis/ftsc/blob/master/fts/fts-0001.016),
  batch transfer, TeLink structures, and MODEM7 filename state machines.
- [FTS-0007.003, October 15, 1990](https://github.com/zoomosis/ftsc/blob/master/fts/fts-0007.003),
  pages 11-27: SEAlink headers, sliding-window acknowledgements, Overdrive,
  absolute RESYNC, and compatibility negotiation.
- [Historical SEAlink reference source](https://github.com/cpeterso/sealink/blob/master/sealink.c),
  for comparison with deployed negotiation behavior.

The copyrighted specifications are referenced, not redistributed in this
package. This package implements file transfer; the surrounding FidoNet mail
session and message storage formats are separate protocols.
