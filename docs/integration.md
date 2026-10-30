# Integrating ModemKit

[Documentation index](README.md) | [Protocol branches](protocol-branches.md) |
[Testing](testing.md)

Each engine advances through explicit inputs and returns actions plus a
snapshot. It does not read a stream, open a filename, start a timer or write
received data. One serialized host event loop performs those operations.

## Choose the transport boundary

| Engine | Incoming transport data | Outgoing transport actions |
| --- | --- | --- |
| XMODEM sender | One `XModemEvent.PeerByteReceived` per response byte | Encode `SendPacket` with its `Integrity`; write `SendControl.Value` |
| XMODEM receiver, including G | `XModemStreamDecoder.Feed(...)`, then pass each event to the engine | Write `SendControl.Value` |
| WXMODEM, either role | `XModemEvent.PeerBytesReceived` | Write `XModemAction.SendBytes.Bytes` |
| YMODEM sender, single or batch | One `YModemEvent.PeerByteReceived` per response byte | Encode `SendPacket` with its `Integrity`; write `SendControl.Value` |
| YMODEM receiver, single or batch | `YModemStreamDecoder.Feed(...)`, then pass each event to the engine | Write `SendControl.Value` |
| ZMODEM, either role | `ZModemEvent.PeerBytesReceived` | Write `ZModemAction.SendBytes.Bytes` |
| MODEM7 / Telink / SEAlink, either role | `LegacyModemEvent.BytesReceived` | Write `LegacyModemAction.SendBytes.Bytes`; execute `PurgeOutput` in order |
| JMODEM, either role | `JModemEvent.PeerBytesReceived` | Write `JModemAction.SendBytes.Bytes`; handle receiver `DiscardInput` |

Start a receiver with its `StartRequested` event. Senders wait for the peer's
request; ZMODEM can also initiate negotiation with `StartRequested`.
Transport reads may contain a partial frame, several frames or adjacent
control bytes. Preserve their order and retain partial input between reads.

## Host event loop

1. Feed the next peer event or queued application response into `Advance`.
2. Execute the returned actions in their original order.
3. On a source request, read the requested bytes and enqueue the corresponding
   ready event.
4. On a file offer, choose acceptance, rejection or a supported restart offset.
5. On delivered data, persist the payload before enqueuing acceptance.
6. Resume peer input after any required application decision has been handled.

Queue callbacks instead of recursively invoking the engine. G-mode and
streaming senders can request another block immediately, without a peer ACK
between source reads. The repository's
[application queue](../ModemKit.IntegrationTests/ProtocolActionQueue.cs)
demonstrates this ordering.

The bytes returned in an action still need to be written by the host. Handle
short transport writes, cancellation and I/O failures in that layer. Flush
final ACK/OO/control output before closing the transport, even when the same
step also returns `Complete`.

## Configure a branch

These configuration snippets are independent. They create engines and
decoders; the host must execute their actions using the boundary table.

### XMODEM and WXMODEM

```csharp
using Xmodem.Protocol;

var options = new XModemOptions(XModemMode.OneK, maxRetries: 10);
var sender = new XModemSender(options);
var receiver = new XModemReceiver(options);
var decoder = new XModemStreamDecoder(receiver.Snapshot.Integrity);
```

Use `Checksum`, `Crc`, `OneK` or `G`; `QModemG` and `OneKG` alias `G`.
When checksum fallback is enabled, update the receiver decoder's `Integrity`
from the snapshot before supplying new bytes. A partially collected frame
must be discarded before changing integrity.

WXMODEM uses a different engine and owns its decoder:

```csharp
using Xmodem.Protocol;

var options = new WXModemOptions(allowFallback: true);
var sender = new WXModemSender(options);
var receiver = new WXModemReceiver(options);
var first = receiver.Advance(new XModemEvent.StartRequested());
```

### YMODEM checksum, CRC and G

```csharp
using Ymodem.Protocol;

var blocks = new YModemBlockOptions(
    mode: YModemBlockMode.Fixed1K,
    use1KBlock0: false,
    use1KFinalDataBlock: false);
var transfer = new YModemTransferOptions(YModemTransferMode.Checksum);
var sender = new YModemBatchSender(blocks, transfer);
var receiver = new YModemBatchReceiver(transfer);
var decoder = new YModemStreamDecoder(receiver.Integrity);
var encoder = new YModemPacketEncoder();
```

Select `Crc` for the default acknowledged CRC mode or `G` for streaming.
Non-G senders use the receiver's initial C/NAK to select integrity. Always
encode an outgoing packet with `encoder.Encode(action.Packet, action.Integrity)`;
the one-argument encoder overload remains CRC16.

Block zero means a header at a file boundary and a data packet after sequence
rollover. Use the sequential decoder callback when a read can contain multiple
frames. The callback must process decisions before decoding the next frame;
see the walkthrough below.

### ZMODEM profiles and windows

```csharp
using Zmodem.Protocol;

var options = new ZModemOptions(
    profile: ZModemProfile.ZedZap,
    blockSize: 8192,
    flowControl: ZModemFlowControl.Windowed,
    windowSize: 16384,
    acknowledgementInterval: 4096);
var sender = new ZModemSender(options);
var receiver = new ZModemReceiver(options);
```

Both peers must support the selected maximum subpacket size. `Profile`
controls the decoder limit; `BlockSize` controls outgoing packetization.
Streaming/windowed sending activates only with suitable peer capabilities.
The source must support reads at the requested offsets because recovery can
seek backwards.

Public legacy variable headers and RLE are opt-in:

```csharp
using Zmodem.Protocol;

var options = new ZModemOptions(
    useVariableHeaders: true,
    useRunLengthEncoding: true);
var sender = new ZModemSender(options);
var receiver = new ZModemReceiver(options);
```

These options implement the documented historical subset, with capability
checks. They do not enable all proprietary ZMODEM-90 features.

### Historical batches and JMODEM

```csharp
using LegacyModem.Protocol;

var options = new LegacyModemOptions(
    variant: LegacyModemVariant.SeaLink,
    windowSize: 6,
    overdrive: true,
    enableResync: true);
var sender = new LegacyModemSender(options);
var receiver = new LegacyModemReceiver(options);
```

`Modem7` and `Telink` select their own filename/metadata handshakes. For
SEAlink restart, use `FileHeaderAccepted(restartBlock)` only when the offered
file reports `CanRestart` and the existing prefix has been validated.
`RequestDataBlock.Offset` is an absolute offset; do not replace it with a
counter that only moves forwards.

```csharp
using Jmodem.Protocol;

var options = new JModemOptions(
    enableCompression: true,
    blockSizingPolicy: JModemBlockSizingPolicy.SourceCompatible);
var sender = new JModemSender(options);
var receiver = new JModemReceiver(options);
```

JMODEM source requests vary in size. Maintain a cumulative source offset for
new requests; retries reuse the cached current frame. `Documented` and
`SourceCompatible` select the two documented size-adjustment policies, using
the same wire framing.

## Receiver walkthrough

This complete .NET 10 example receives an in-memory YMODEM checksum file.
`outgoing` represents ordered transport output, and storage acceptance is
synchronous. It does not open a serial port or write a peer-supplied path.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using Ymodem.Protocol;

var receiver = new YModemBatchReceiver(
    new YModemTransferOptions(YModemTransferMode.Checksum));
var decoder = new YModemStreamDecoder(receiver.Integrity);
var encoder = new YModemPacketEncoder();
var outgoing = new List<byte[]>();
using var sink = new MemoryStream();
var pending = new Queue<YModemEvent>();

void Dispatch(YModemEvent input)
{
    pending.Enqueue(input);
    while (pending.Count > 0)
    {
        foreach (var action in receiver.Advance(pending.Dequeue()).Actions)
        {
            switch (action)
            {
                case YModemAction.SendControl control:
                    outgoing.Add(new[] { control.Value });
                    break;
                case YModemAction.OfferFileHeader:
                    pending.Enqueue(new YModemEvent.FileHeaderAccepted());
                    break;
                case YModemAction.DeliverDataBlock data:
                    sink.Write(data.Payload, 0, data.DataLength);
                    pending.Enqueue(new YModemEvent.DataBlockAccepted());
                    break;
                case YModemAction.Fail fail:
                    throw new InvalidOperationException(fail.Reason);
                case YModemAction.Cancel cancel:
                    throw new InvalidOperationException(cancel.Reason);
            }
        }
    }
}

void Receive(byte[] bytes) => decoder.Feed(
    bytes,
    () => receiver.Snapshot.NextBlockNumber > 1 &&
        (receiver.Snapshot.Phase == YModemBatchReceiverPhase.WaitingDataPacketOrEot ||
         receiver.Snapshot.Phase == YModemBatchReceiverPhase.WaitingDataBlockDecision),
    Dispatch);

Dispatch(new YModemEvent.StartRequested());
Receive(encoder.Encode(
    new YModemPacket.Header(new YModemFileDescriptor("demo.bin", 3)),
    receiver.Integrity));
Receive(encoder.Encode(
    new YModemPacket.Data(1, new byte[] { 0x41, 0x42, 0x43 }, 3, 128),
    receiver.Integrity));
Receive(new[] { YModemControlBytes.Eot });
Receive(new[] { YModemControlBytes.Eot });
Receive(encoder.Encode(new YModemPacket.BatchTrailer(), receiver.Integrity));

Console.WriteLine(BitConverter.ToString(sink.ToArray())); // 41-42-43
Console.WriteLine(receiver.Snapshot.Phase);              // Completed
```

For a real batch, select a separate destination for each accepted header.
Execute transport writes rather than leaving them in `outgoing`. The complete
[YMODEM test driver](../ModemKit.IntegrationTests/YModemWireTests.cs) connects
both endpoints, supplies multiple source files and injects wire faults.

## Buffering and asynchronous storage

Serialize engine access even when reads and writes use asynchronous I/O.
Enqueue completed source reads and storage decisions back onto the event loop.
Do not acknowledge received data until persistence has succeeded.

Keep servicing the transport while a source read is pending. ZMODEM and
SEAlink can receive acknowledgements or restart requests during that read;
blocking the entire loop until disk I/O finishes prevents those events from
being handled. Buffer or dispatch incoming data according to the receiver
contract below.

For ZMODEM, EOF confirmation can overtake outstanding recovery reads.
Complete already-issued source callbacks even if `FileCompleted` has arrived;
the sender discards obsolete bytes and defers the next file request until the
reads are drained. Do not close a source while an outstanding read still uses it.

XMODEM-G has `MaxBufferedBlocks`; WXMODEM retains its bounded window, and
ZMODEM/legacy engines have byte-buffer limits. YMODEM's application-decision
states do not buffer a new sequence of G-mode data packets. For asynchronous
YMODEM storage, retain unconsumed raw bytes in a bounded host buffer, feed at
most one frame until its decision is resolved, then resume decoding. A
callback that starts an asynchronous write and immediately returns is not
equivalent to the synchronous walkthrough.

When using streaming source requests, bound the host's pending transport
output too. Protocol buffer limits do not limit a separate queue owned by
your application.

## File lengths and source reads

| Protocol | Source response | Received length |
| --- | --- | --- |
| X/WX | `DataBlockReady` with block number, length and final flag; fill non-final blocks | Full padded blocks; exact length must come from an external contract |
| Y | `DataBlockReady` with length/final flag matching the descriptor | Exact when `HasFileSize`; otherwise padded data and `RemainingFileBytes = -1` |
| Z | `DataBlockReady` with exactly the requested offset and `MaxLength` | ZFILE length is an estimate; matching ZEOF/committed offset gives actual length |
| Legacy | Exactly `RequestDataBlock.DataLength` bytes at `Offset` | MODEM7 is padded; Telink/SEAlink use the advertised exact length |
| J | `DataBlockReady` with actual length and explicit final flag | Exact frame payloads, including empty/full final blocks; no padding |

Never infer a binary file's length by stripping trailing `0x1A`. File offers
describe peer metadata; the application decides whether to accept them and
where to store the data. Engines do not open those paths.

## Timeouts and shutdown

| Boundary | Host action |
| --- | --- |
| X receiver partial frame | Pass `decoder.OnTimeout()` into the receiver |
| Y receiver partial frame | Pass `decoder.Timeout()` into the receiver |
| WX, Z or legacy session | Supply the session's `TimeoutElapsed` event |
| WX sender paused by XOFF | Use `FlowControlTimeoutElapsed` after the documented ten-second pause deadline |
| J receiver in `DrainingInput` | Drain/discard until the line is quiet, then supply `TimeoutElapsed` |
| Application source or storage decision | Wait for the host operation; do not spend a peer retry budget on it |

Choose deadlines for the actual transport and protocol phase. The in-memory
fixture's `onIdle` callback is a virtual-time test mechanism, not a production
rule to time out a live connection whenever a read currently has no bytes.

Execute a legacy `PurgeOutput` before following output actions. It removes
unsent bytes from that endpoint's output queue; bytes already delivered to the
peer cannot be recalled. JMODEM `DiscardInput` instead means draining incoming
bytes until a quiet interval has elapsed.

Keep a completed receiver available for a closing grace interval so lost
final acknowledgements can be recovered. Continue servicing required closing
traffic without reopening storage or reporting completion twice.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Y checksum frames fail validation or appear truncated | Use the action's `Integrity` when encoding and the receiver's integrity when constructing its decoder |
| A large Y file fails near sequence 255/0 | Re-evaluate block-zero interpretation per frame; use sequential dispatch |
| A G-mode transfer fails after a slow storage operation | Verify host buffering and serialized acceptance; G data cannot be retransmitted |
| The first retry after truncation also fails | Clear the partial X/Y frame, or follow the session's drain/purge contract |
| A resumed file repeats or omits bytes | Honor absolute Z/legacy request and delivery offsets |
| The sender retries after the receiver says complete | Send final control actions and retain the closing responder for the grace interval |

For protocol-specific limits and source references, follow the
[package reference index](README.md#protocol-reference).
