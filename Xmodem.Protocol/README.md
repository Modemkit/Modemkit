# Xmodem.Protocol

[Documentation index](../docs/README.md) | [Integration guide](../docs/integration.md) |
[Testing](../docs/testing.md)

Transport-independent XMODEM (8-bit checksum), XMODEM-CRC, XMODEM-1K,
XMODEM-G and WXMODEM engines for `netstandard2.0` and `net10.0`.
No serial port, clock or filesystem
is owned by the library.

| Selection | Wire behavior |
| --- | --- |
| `XModemMode.Checksum` | 128-byte SOH packets, additive 8-bit checksum |
| `XModemMode.Crc` | 128-byte SOH packets, CRC16 |
| `XModemMode.OneK` | 1024-byte STX packets with optional 128-byte tail, CRC16 |
| `XModemMode.G` | G negotiation, CRC16, streaming without data ACK/retry |
| `XModemMode.QModemG`, `XModemMode.OneKG` | Explicit aliases of `G` |
| `WXModemSender`, `WXModemReceiver` | Separate four-packet window protocol with SYN/DLE framing |

## Integration

Create `XModemSender` and/or `XModemReceiver` with `XModemOptions`.
Call `receiver.Advance(new XModemEvent.StartRequested())` to begin.
Pass returned `SendControl` bytes and `SendPacket` frames to your transport.
Encode packets with `XModemPacketEncoder.Encode(action.Packet, action.Integrity)`.

The sender responds to `C` with CRC frames and to NAK with 128-byte checksum
frames. A checksum-only sender waits for NAK. `OneK` uses 1024-byte CRC frames
with a 128-byte final frame when possible; CRC receivers accept both sizes.
Supply each `RequestDataBlock` with `DataBlockReady`. Non-final blocks must
fill the requested size. Mark the final block with `isLastBlock: true`;
an empty final block sends EOT immediately.

Use `XModemStreamDecoder.Feed(byte[])` for arbitrary transport chunks. It
returns zero or more protocol events for frames, controls, or checksum/CRC
errors. Feed those events into the receiver. Feed receiver control bytes to
the sender as `PeerByteReceived`. Set the stream decoder's `Integrity` to the
receiver snapshot's negotiated value before feeding new bytes, including
after optional checksum fallback.

Persist each receiver `DeliverDataBlock` and respond with
`DataBlockAccepted` or `DataBlockRejected`. A block is acknowledged only
after acceptance. Retransmitted accepted blocks are acknowledged without
being delivered twice; duplicates while a write is pending are ignored.

XMODEM carries **no file length or filename**. Receivers deliver complete
128/1024-byte blocks, including the final `0x1A` padding. Never strip trailing
`0x1A` blindly from binary files: obtain the exact length through an external
contract or retain the padded bytes. The sender snapshot counts source bytes;
the receiver snapshot counts wire payload bytes.

## XMODEM-G / QMODEM-G

Select `new XModemOptions(XModemMode.G)` at both ends. `QModemG` and `OneKG`
name the same implemented wire protocol; they are not separate formats.
The receiver starts with ASCII `G`. The sender uses CRC16 and advances
directly from each `DataBlockReady` to the next `RequestDataBlock`, emitting
packets without waiting for per-block ACKs. An EOT still requires a final ACK.
The sender snapshot counts bytes emitted in G mode, because data ACKs do not
exist.

The receiver queues incoming packets while an application write is pending,
up to `MaxBufferedBlocks` (default 16). Persist every `DeliverDataBlock` and
then signal acceptance. Damaged frames, sequence errors, storage rejection,
queue overflow or a mid-transfer receive timeout cancel the transfer;
streaming data cannot be retransmitted. Checksum fallback is incompatible
with G and is rejected at construction. Only the startup request and EOT
handshakes may be retried.

## WXMODEM

WXMODEM is a separate protocol, not a block-size alias. Use
`WXModemSender.Advance(XModemEvent)` / `WXModemReceiver.Advance(XModemEvent)`.
Both accept arbitrary raw chunks through `XModemEvent.PeerBytesReceived`.
Execute `XModemAction.SendBytes` directly on the transport; the WX engines
own their stream parsers and codecs. Source and sink actions reuse the
existing XMODEM events and actions. `WXModemStepResult.Snapshot` additionally
exposes negotiated `Mode`, `OutstandingBlocks` and `IsPaused`.

The receiver requests ASCII `W`. A packet consists of an unescaped SYN
followed by the ordinary 128-byte CRC16 XMODEM frame. DLE, XON, XOFF and SYN
are escaped as DLE followed by the byte XOR `0x40`, including occurrences in
the sequence/complement and CRC. Ordinary CAN bytes inside data are retained.
The decoder accepts repeated SYN prefixes and can resynchronize after a
truncated packet at the next unescaped SYN.

The sender may transmit four packets before waiting. ACK and NAK are followed
by a raw value `0..3`, the low two bits of the block number. ACKs acknowledge
all outstanding blocks through that sequence. NAK requests replay from the
specified block. Retransmissions retain the original encoded bytes, and
application delivery is never repeated for already accepted blocks.
Timeouts probe the last outstanding block to obtain a cumulative response.
The last data window must be acknowledged before EOT; its ACK is a single
unsequenced byte.

WXMODEM carries neither a file size nor an integrity-protected ACK sequence.
It retains the original two-bit control-channel ambiguity; the fixture tests
lost ACKs and malformed ACK sequence bytes without claiming protection against
every combination of valid-looking control corruption and lost data.

`PauseRequested` / `ResumeRequested` on the receiver send XOFF / XON.
The sender stops issuing data while paused, including a block whose source
request was already outstanding. After ten seconds of XOFF, the host may send
`FlowControlTimeoutElapsed` to the sender to release the pause, matching the
original bounded flow-control behavior. Normal `TimeoutElapsed` does not
release XOFF. Buffering while the sink is deciding is bounded to the four
in-flight packets.

With fallback enabled (the default), the receiver tries `W` three times,
`C` three times and NAK four times. Each stage and the retry budget are
configurable in `WXModemOptions`. Once a frame starts, fallback stops.
CRC/checksum fallback delegates to the actual XMODEM engines and uses ordinary
SOH frames, without WX escaping or sequenced ACKs. The original author's v4
code tries `W` seven times despite its nearby comment saying three; set
`windowedAttempts: 7` to reproduce that attempt count. The host supplies all
timers; no wall-clock delays run inside these engines.

## Timeouts and failures

The host schedules timeouts. When an inter-byte timeout expires, pass
`decoder.OnTimeout()` to the receiver; this first discards an incomplete
frame so a retransmission can be decoded. Pass `TimeoutElapsed` to the sender
when a handshake/ACK timeout expires. Do not run protocol timers while an
application block request or storage decision is pending.

Retries are bounded by `MaxRetries` (retransmissions after the initial attempt).
Successful blocks reset that budget. Receiver checksum fallback is opt-in
and can occur only before a valid frame arrives. Two consecutive CAN bytes
cancel a session. Local cancellation and retry exhaustion emit two CAN bytes
followed by a terminal `Cancel` or `Fail` action.

Keep a completed receiver alive for the sender's retry interval: it re-ACKs
duplicate EOT bytes if the final ACK was lost. All other terminal operations
are inert. State machines are single-threaded; serialize calls to `Advance`.

## Verification

`Xmodem.Protocol.Tests` includes a deterministic, fragmented byte-wire fixture
with mutation/drop injection. Coverage includes checksum and the published
CRC-16/XMODEM `123456789 -> 0x31C3` vector, 128/1024 boundaries, all byte values,
sequence rollover, damaged/truncated frames, lost ACK/EOT ACK, cancellation,
fallback, application rejection and bounded timeouts.

Protocol references:

- Ward Christensen's XMODEM description and Chuck Forsberg's
  X/YMODEM Protocol Reference (1988).
- [Synchronet's official protocol-driver documentation](https://www.synchro.net/docs/sexyz.txt?raw),
  Compatibility section, identifies XMODEM-G and QMODEM-G as the same
  streaming CRC protocol.
- [Peter Boswell's original WXMODEM implementation](https://github.com/cpeterso/wxmodem/blob/master/src/wxmoxfer.inc),
  July-October 1986, provides the SYN/DLE, four-packet window, sequenced
  ACK/NAK, XON/XOFF and fallback behavior. Its
  [original distribution notice](https://github.com/cpeterso/wxmodem/blob/master/wxmodem.txt)
  identifies the author and public-domain publication.

The WX codec's default single SYN matches Boswell's v4 transmitter. Repeated
SYN prefixes remain accepted for peer compatibility. No historical source
code is bundled in this package.
