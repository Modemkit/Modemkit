# Ymodem.Protocol

[Documentation index](../docs/README.md) | [Integration guide](../docs/integration.md) |
[Testing](../docs/testing.md)

`Ymodem.Protocol` is a transport-agnostic .NET implementation of the YMODEM file transfer protocol.

The library models transfers as explicit events, actions, and state snapshots so it can be embedded into desktop tools, device updaters, bootloader utilities, and custom transport layers.

## Highlights

- YMODEM single-file sender and receiver state machines
- YMODEM batch sender and receiver state machines
- Packet encoding and decoding with CRC16 validation
- Original additive-checksum YMODEM, including metadata and batch trailers
- YMODEM-G streaming with single-file and batch transfers
- Explicit timeout events, bounded retries, and duplicate-frame recovery
- Incremental decoding for fragmented and coalesced transport reads
- Explicit integration model based on `Advance(...)` + returned actions
- Works with custom transports such as serial ports, sockets, or device bridges
- Targets `netstandard2.0` and `net10.0`

## Target frameworks

- `netstandard2.0`
- `net10.0`

## Use this checkout

Reference `Ymodem.Protocol.csproj`, or build a local NuGet package using the
[repository build instructions](../README.md#build-and-use-this-checkout).
The API described here belongs to this checkout; when using a published
package, check its version and accompanying documentation.

## Transfer modes and retries

Existing receivers select acknowledged YMODEM/CRC. Configure streaming
YMODEM-G or the retry budget with `YModemTransferOptions`:

```csharp
using Ymodem.Protocol;

var transferOptions = new YModemTransferOptions(
    mode: YModemTransferMode.G,
    maxRetries: 10);
var sender = new YModemBatchSender(new YModemBlockOptions(), transferOptions);
var receiver = new YModemBatchReceiver(transferOptions);
```

For the historical checksum branch, use
`new YModemTransferOptions(YModemTransferMode.Checksum)`. The receiver requests
it with NAK and validates one additive checksum byte on every header/data/
trailer frame. Both 128-byte and 1024-byte framing are supported. Non-G senders
select their integrity from the receiver's initial C or NAK request; the
sender's `Integrity` property and every `SendPacket.Integrity` expose that
selection. Encode outgoing actions with
`encoder.Encode(action.Packet, action.Integrity)`. The original one-argument
encoder overload continues to use CRC16.

Construct the stream decoder with the receiver's integrity:
`new YModemStreamDecoder(receiver.Integrity)`. Full-frame decoding also accepts
`Decode(bytes, YModemIntegrity.Checksum, isDataPhase)`. A decoder's integrity
cannot change while a frame is incomplete. YMODEM-G always uses CRC16.

YMODEM-G uses `G` to request the header and file data, sends data without
per-block acknowledgements, acknowledges EOT, then requests the next header
with `G`. An empty final header completes the batch without waiting for ACK.
Corruption, an unexpected data sequence, or a rejected data block aborts the
stream with two consecutive CAN bytes. Header and EOT negotiation can retry;
streamed file data cannot.

For ordinary YMODEM, NAK and timeout retransmissions preserve the exact packet
content and block size. `MaxRetries` applies to each stalled transfer step.
Successful progress resets the budget. Exhaustion emits CAN CAN and a `Fail`
action. A local cancellation also emits CAN CAN before `Cancel`.

The host supplies `new YModemEvent.TimeoutElapsed()` when its transport timeout
expires. The engines do not start timers or perform I/O. Do not send timeout
events while the application is still deciding whether to accept a header or
persisting a delivered data block; those waiting states intentionally do not
retry. Keep the receive loop active after `Complete` for an appropriate
transport timeout so a retransmitted final trailer can be acknowledged if its
first ACK was lost.

## Incremental byte input

`YModemStreamDecoder` accepts transport chunks, validates framing and the
selected CRC/checksum, and emits `PacketReceived`, `InvalidFrameReceived`, or cancellation events.
Use sequential dispatch when one read can contain several frames:

```csharp
var decoder = new YModemStreamDecoder(receiver.Integrity);
decoder.Feed(
    incomingBytes,
    () => receiver.Snapshot.NextBlockNumber > 1 &&
        (receiver.Snapshot.Phase == YModemBatchReceiverPhase.WaitingDataPacketOrEot ||
         receiver.Snapshot.Phase == YModemBatchReceiverPhase.WaitingDataBlockDecision),
    input =>
    {
        var step = receiver.Advance(input);
        // Execute step.Actions, including application acceptance decisions.
        // Queue every SendControl/SendPacket action to the transport in order.
    });
```

The phase callback distinguishes a repeated initial header from block zero
after the 255-to-0 sequence rollover. Sequential dispatch re-evaluates it after
each event. The `Feed(bytes, isDataPhase)` overload returns a list and is useful
when a chunk cannot cross a change in the interpretation of block zero.

The callback must finish the application decision before the next frame is
dispatched. For asynchronous storage, buffer unconsumed raw bytes in the host
and resume decoding after acceptance; YMODEM-G can send the next data block
without waiting for a wire ACK. See
[buffering and asynchronous storage](../docs/integration.md#buffering-and-asynchronous-storage)
for the integration contract.

On a transport timeout, call `receiver.Advance(decoder.Timeout())`; this clears
any truncated frame before retrying. Complete corrupt frames produce
`InvalidFrameReceived` automatically. Sender-side responses are control bytes
and can be supplied as `PeerByteReceived` events.

## File length and binary data

`new YModemFileDescriptor(name, length)` advertises an exact byte length. Send
the full requested block size except for the final block, and set `IsLastBlock`
only when the declared length has been supplied. For an empty file, answer the
first `RequestDataBlock` with zero bytes and `IsLastBlock = true`; the sender
emits EOT without a padded data frame. The receiver trims final-block padding
using the advertised length and rejects premature EOT.

YMODEM also permits headers without a length. Use
`new YModemFileDescriptor(name)` for this form; `HasFileSize` is false, and a
receiver snapshot reports `RemainingFileBytes = -1`. All received block bytes,
including final padding, are delivered because the protocol does not provide
enough information to trim them. File names are ASCII on the wire; control
characters and protocol delimiters are rejected. Optional metadata fields
after the length are accepted but are not exposed by this API.

Data packets own a copy of their payload and return a copy from `Payload`, so
mutating application buffers cannot change an in-flight packet or its retries.
Protocol instances and stream decoders are intended for a single serialized
event loop.

## Tests

`Ymodem.Protocol.Tests/Fixtures/YModemLoopbackFixture.cs` connects sender and
receiver only through encoded wire bytes. It can fragment frames, drop either
direction's messages, corrupt CRCs, and inject deterministic timeouts without
sleeping. The integration matrix covers checksum/CRC/G, single/batch, empty files,
128/1K boundaries, binary control bytes, rollover, cancellation, truncated
frames, and retries.

```bash
dotnet test Ymodem.Protocol.Tests/Ymodem.Protocol.Tests.csproj -c Release
dotnet test Ymodem.Protocol.Tests/Ymodem.Protocol.Tests.csproj -c Release -p:ProtocolUnderTestTargetFramework=netstandard2.0
```

The wire behavior follows Chuck Forsberg's X/YMODEM reference and the
[lrzsz sender](https://github.com/UweOhse/lrzsz/blob/master/src/lsz.c),
especially `getnak`, `wctx`, and `wcputsec` for YMODEM-G negotiation.

## Status

This package is currently in an early `0.x` stage. The public API is usable, but it may continue to evolve as more protocol scenarios and integration helpers are added.
