# Jmodem.Protocol

[Documentation index](../docs/README.md) | [Integration guide](../docs/integration.md) |
[Testing](../docs/testing.md)

An original C# implementation of Richard B. Johnson's JMODEM wire protocol for
`netstandard2.0` and `net10.0`. It includes sender/receiver engines, an incremental
stream decoder, variable-sized packets, run-length encoding and exact final
file lengths. The application supplies transport I/O, timers and storage.

## Source and wire contract

The implementation follows Johnson's original C sources, cross-checked between
version 3.03 (1990) and version 3.10 (November 19, 1991):

- [Original 3.03 sources and author documentation](https://github.com/cpeterso/jmodem)
- [Original 3.10 distribution, including Microsoft C sources](https://www.synchro.net/files/BBS.Archives/Protocols/jmod310.zip)

`JMODEM.H`, `JMODEM_A.C` and `JMODEM_D.C` establish the implemented grammar.
No historical source code or executable is included in this package.

| Bytes | Meaning |
| --- | --- |
| 0..1 | Total frame length, little-endian, including the six-byte overhead |
| 2 | Type bits: normal `1`, compressed `2`, final `4` |
| 3 | Block number, starting at one and wrapping from 255 to zero |
| 4..length-3 | Raw or run-length encoded payload |
| length-2..length-1 | Little-endian 16-bit rotate/add checksum |

The original prose diagram swaps the type/sequence fields and gives incorrect
type-bit values. This codec follows the actual interoperating C structures and
constants. It emits type `1`/`3` for ordinary raw/compressed packets and `5`/`7`
for final packets. The decoder also accepts earlier final forms `4`/`6`.

JMODEM calls its check a "CRC", but it is **not a polynomial CRC16**.
Starting at zero, add each header/data byte modulo 65536, then rotate the
16-bit accumulator left by `remainingByteCount & 7`. The count excludes the
two check bytes and decreases after each processed byte. `ComputeChecksum`
takes precisely those protected bytes.

RLE uses `BB <count-low> <count-high> <value>`. Literal `BB` bytes must also be
represented by a run when compression is used. The sender uses the compressed
form only if it is shorter. Both transmitted payloads and expanded data are
limited to 8192 bytes; malformed/truncated/zero-length runs are rejected before
expansion. There is no byte escaping, filename header or batch metadata.

## Integration

```csharp
using Jmodem.Protocol;

var sender = new JModemSender();
var receiver = new JModemReceiver();
var first = receiver.Advance(new JModemEvent.StartRequested());
```

The receiver starts with NAK. A sender receiving NAK responds with ACK, then
requests the first source block. Feed raw transport chunks through
`PeerBytesReceived` and execute each `SendBytes` action in order. Starting the
sender is optional; it initially waits for synchronization.

The receiver validates a bounded first-frame candidate, with or without a
preceding synchronization ACK. This recovers a lost startup ACK and prevents
payload bytes or the length-low byte from being mistaken for CAN/ACK/NAK.
If only a single synchronization control arrives without a following packet,
the next quiet `TimeoutElapsed` commits that control and completes the
handshake.

Provide every `RequestDataBlock` using
`DataBlockReady(blockNumber, bytes, dataLength, isLastBlock)`. Keep a cumulative
source offset because block sizes change. A short non-final source read is
allowed; only the explicit final flag ends the file. A zero-length final
packet represents an empty file without padding.

Persist each `DeliverDataBlock.Payload`, then send `DataBlockAccepted` or
`DataBlockRejected`. The receiver acknowledges only accepted storage and does
not deliver retransmitted accepted blocks twice. `Complete` occurs after the
final block is accepted/acknowledged. Keep the completed receiver alive briefly
to acknowledge a retransmitted final packet when its ACK was lost.
Its terminal responder matches the exact cached final wire frame, with bounded
linear-time matching, so a damaged retry cannot prevent a later valid retry
from being acknowledged. It never reopens storage or reports completion twice.

Unlike the historical sender's EOF-detection bug, callers can mark a completely
full final block explicitly, and empty files use a valid six-byte final frame.
This uses the existing final bit and does not introduce another wire format.

## Adaptive sizing

The default source request starts at 512 bytes. Clean block acknowledgements
increase the next request by 512, up to 8192. A failed transmission retains its
original encoded frame for every retry; size adaptation applies only to the
following block.

`JModemBlockSizingPolicy.Documented` halves the next size once per retry, down
to 64 bytes, matching the author's prose description. The explicit
`SourceCompatible` policy reproduces the C implementation's retry divisor:
divide the previous size by the retry count, with a 64-byte minimum. Thus one
retry leaves the size unchanged in that policy. Both policies use identical
wire framing and can communicate with the same receivers.

## Timeouts and cancellation

The host supplies `TimeoutElapsed` for synchronization, ACK and inter-byte
deadlines. Do not run peer timers while an application source/storage request
is pending. Retries are bounded by `JModemOptions.MaxRetries`; successful blocks
reset the budget. Local cancellation and exhausted retries send six CAN bytes.
During data reception, six CAN bytes are recognized only at a frame boundary,
so repeated binary CAN bytes inside data and the valid length `0x1818` remain
safe.

JMODEM has no synchronization marker before its length field. After a malformed
frame, the receiver emits `DiscardInput` and enters `DrainingInput`. Discard
incoming bytes until the line has been quiet for an inter-byte interval, then
provide `TimeoutElapsed`. This clears any partial frame and sends NAK. A
transport fixture can model that quiet interval with a virtual timeout after
its byte queue is empty. Do not try to locate another frame by searching payload
bytes for a guessed length field.

`JModemStreamDecoder.Feed` also supports a callback overload that dispatches
incrementally without accumulating all decoded packets. Each parser retains
at most one bounded frame. Session input chunks are bounded by `MaxInputBytes`.
`OnTimeout()` resets an independently used decoder, including its discard mode.

## Verification

The tests include independent source-derived checksum/wire constants, the
author's RLE example with corrected source field order, all byte values,
8192-byte payloads, 256-block rollover, every fragmentation split, malformed
lengths and runs, handshake, final ACK loss, storage decisions, exact retries,
adaptive sizing policies and bounded cancellation. The repository's shared
duplex fixture adds dropped/corrupt/truncated byte transfers and 2 MiB files.

For example, the empty final frame is `06 00 05 01 2A 18`, and an uncompressed
final `"abc"` frame is `09 00 05 01 61 62 63 19 30`. These checks were calculated
independently from the source algorithm, not through this encoder.

No native DOS JMODEM executable interoperability is claimed. lrzsz does not
implement JMODEM; validation uses the original-source wire contract and the
deterministic fixtures rather than treating a different protocol as a peer.
