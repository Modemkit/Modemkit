# Zmodem.Protocol

[Documentation index](../docs/README.md) | [Integration guide](../docs/integration.md) |
[Testing](../docs/testing.md)

Transport-independent ZMODEM file transfers for `netstandard2.0` and `net10.0`.
`ZModemSender` and `ZModemReceiver` accept explicit events, return actions and
expose snapshots. The application owns clocks, transport I/O and file storage.

## Profiles and branches

| Selection | Implemented wire behavior |
| --- | --- |
| Default `Standard` | 1K maximum subpacket, CRC16/negotiated CRC32, ZCRCW stop-and-wait |
| `Extended4K` | Explicit 4096-byte subpacket limit; interoperates with lrzsz `--start-4k` |
| `Extended8K` | Explicit 8192-byte subpacket limit; interoperates with lrzsz `--start-8k` |
| `ZedZap` | 8K maximum ZMODEM subpackets used by the FidoNet ZedZap profile |
| `ZedZip` | 1K maximum ZMODEM subpackets and null batches, as specified by FTS-0006.002 |
| `FlowControl.Streaming` | Open ZDATA frames with ZCRCG continuations, periodic ZCRCQ acknowledgements, ZCRCE EOF |
| `FlowControl.Windowed` | Streaming with a byte-count window; ZCRCW stops transmission at the window/peer-segment boundary |
| `UseVariableHeaders` | Negotiated legacy ZVBIN/ZVHEX/ZVBIN32 (`a/b/c`), 0..16-byte header payloads |
| `UseRunLengthEncoding` | Negotiated legacy ZVBINR32 (`d`), ZRESC run-length encoding and CRC32 of compressed bytes; codec also supports fixed ZBINR32 (`D`) |
| ZCRC comparison | Caller-supplied whole-file/prefix CRC32 comparison before accept, skip or resume |

Defaults remain compatible with ordinary 1K peers. Large packets are explicit
because no universal capability bit negotiates their maximum size. The sender
honors a nonzero ZRINIT receive-segment size even when a larger profile is chosen.
Streaming falls back to stop-and-wait unless the receiver advertises both full
duplex and overlapped I/O. Retry budgets apply to control loss and recovery;
ZRPOS always restarts from the receiver's committed offset and synchronizes the
first recovery frame with ZCRCW.

```csharp
using Zmodem.Protocol;

var largePackets = new ZModemOptions(
    blockSize: 8192,
    profile: ZModemProfile.ZedZap);

var windowed = new ZModemOptions(
    flowControl: ZModemFlowControl.Windowed,
    windowSize: 16384,
    acknowledgementInterval: 4096);

var legacyRle = new ZModemOptions(
    useVariableHeaders: true,
    useRunLengthEncoding: true);
```

`BlockSize` selects the actual outgoing subpacket size and, for stop-and-wait
receivers, the advertised segment size. `Profile` sets the decoder's upper
bound. Standard/ZedZip allow up to 1024, Extended4K up to 4096, and
Extended8K/ZedZap up to 8192 bytes. All profiles also accept smaller packets.

## Integration

Start the receiver with `StartRequested`; starting the sender is optional if it
receives the receiver's ZRINIT. Write every `SendBytes.Bytes` to the transport and
feed received chunks as `PeerBytesReceived`. Chunk boundaries have no protocol
significance. Decoding accepts fragmented/coalesced frames, ZDLE quoting and
software-flow-control characters. Five consecutive CAN bytes cancel reception.

On `RequestFileHeader`, supply `FileHeaderReady(new ZModemFileDescriptor(name,
size))`, or `NoMoreFiles` to finish the batch. For each `RequestDataBlock`, read
exactly `MaxLength` bytes at `Offset` and supply `DataBlockReady`. The sender can
seek backwards after ZRPOS, so file sources must support seeking. Streaming
accepts acknowledgements and recovery requests while a source read is pending;
a superseded read at an obsolete offset is ignored. `AcknowledgedOffset` and
`NextSendOffset` distinguish committed bytes from bytes in flight.

If EOF confirmation arrives while recovery reads are still pending, the
sender reports `FileCompleted` but drains their `DataBlockReady` callbacks
before requesting the next file. Finish already-issued source reads even after
`FileCompleted`; their bytes are discarded rather than reused for another file.

On `OfferFileHeader`, supply `FileAccepted(resumeOffset)` or `FileSkipped`.
`OfferFileHeader.Header` exposes file management/conversion flags to the caller;
the library never applies filesystem policies or opens incoming paths. Persist
each `DeliverDataBlock.Payload` at `Offset` before supplying `DataBlockAccepted`.
Use `DataBlockRejected` for a recoverable persistence failure. Incoming streaming
subpackets wait in a bounded queue while the application decides.

`FileCompleted` marks a file boundary; `Complete` marks the finished session.
The received ZFILE length is an estimate: matching ZEOF and committed offsets
determine the actual length. The supplied sender descriptor specifies the exact
source length. Wire offsets are 32-bit unsigned values.

To compare an existing local file before accepting an offer, send
`FileCrcRequested(byteCount)` to the receiver; zero requests the whole source.
The sender emits `RequestFileCrc(File, ByteCount)`. Compute ordinary IEEE CRC32
(`ZModemCodec.ComputeCrc32`) over the requested prefix and supply `FileCrcReady`.
The receiver returns `FileCrcReceived`, then waits for accept/skip. Repeated CRC
requests reuse the sender's last result. Applications own the comparison and any
resulting storage decision.

Supply `TimeoutElapsed` when the peer-response deadline expires. Timeouts during
application decisions are ignored. During shutdown, the receiver completes on
the next timeout if OO is lost. Keep passing final peer traffic briefly so
completed sessions can respond to repeated ZFIN without repeating completion.

`MaxBufferedBytes` bounds each supplied input chunk and the queued application
decisions. It must accommodate the selected profile's escaped maximum packet.
Subpacket decoding is incremental with linear work in received bytes. RLE
expansion is independently limited by the decoded subpacket bound.

## Compatibility limits

ZedZip/ZedZap here implement the file-transfer portion of the FidoNet profile.
WaZOO/YOOHOO negotiation, mail-batch classification, sender/receiver role turns
and the rule to omit initiating an empty ZedZap batch belong to the caller's
session scheduler. ZedZip is not a ZIP compressor.

Legacy variable headers and RLE are explicit opt-ins and activate only after
CANVHDR/CANRLE/CRC32 capability checks. Unknown extra variable-header bytes are
preserved by the codec, but their undocumented higher-level meanings are not
invented. The shared CANRLE/encryption bit alone cannot enable RLE.

The implemented legacy branches do **not** claim full compatibility with every
proprietary ZMODEM-90 product mode. MobyTurbo, Pack-7, Omen's quoted RLE `1` header,
LZW/encrypted transport and sparse-file extensions are not implemented: the
available public documentation does not define a complete unambiguous profile
for these modes. ESC8/TESC8 have incompatible historical interpretations, so the
library never advertises them and ignores them on reception. Ordinary high-bit
binary data remains fully supported. Remote command frames receive ZFERR and
never execute a command.

Streaming's CRC protects individual subpackets. A transport that silently drops
an entire continuation subpacket including its CRC has a historical ZMODEM
ambiguity because only ZDATA headers carry offsets; use stop-and-wait for such a
transport. Corrupted subpacket bytes and lost acknowledgement/control frames
have explicit recovery coverage.

## Validation and references

Tests include fixed standard CRC vectors, an external ZRINIT vector, independent
variable-header/RLE vectors, every variable-header size/split, all byte values,
large packets, streaming/window bounds, CRC negotiation, malformed RLE expansion,
recovery, dropped acknowledgements, batches, resume and comparison hooks. The
repository's native lrzsz fixture additionally exercises normal, large-packet
and streaming/windowed interoperability in both directions.

See [PROTOCOL_NOTES.md](PROTOCOL_NOTES.md) for the primary references, legacy grammar and the exact
boundary between implemented branches and unresolved proprietary modes.
