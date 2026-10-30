# ModemKit test fixtures

[Documentation index](../docs/README.md) | [Testing guide](../docs/testing.md) |
[Interop runner](../ModemKit.Interop/README.md)

`ModemKit.Testing` is a reusable, single-threaded test-support library targeting
netstandard2.0 and net10.0. It has no dependency on a particular protocol and
does not open devices or files.

## Byte transport

Connect each endpoint's outgoing bytes to `WriteFromSender` or
`WriteFromReceiver`. `Pump` delivers at most `maxChunkSize` bytes in each
direction. Reads intentionally split writes or combine several writes.

```csharp
using ModemKit.Testing;

var link = new InMemoryDuplexLink();
link.Faults.Add(WireFault.Drop(
    WireDirection.ReceiverToSender,
    bytes => bytes.Length == 1 && bytes[0] == 0x06));

// Start the engines and execute their outgoing actions first.
link.Run(
    receiveAtSender: FeedSenderBytes,
    receiveAtReceiver: FeedReceiverBytes,
    completed: BothEndpointsCompleted,
    onIdle: AdvanceVirtualTimers,
    maxChunkSize: 7,
    maxSteps: 100000);
```

Fault rules apply to matching writes **before** fragmentation, in insertion
order. Each has a bounded occurrence count. `Drop`, `Corrupt`, `Duplicate` and
`Truncate` provide common faults; a custom `WireFault` transforms arbitrary
bytes, for example to insert noise. Writes clone their input buffers.

`Run` calls the virtual timer only when neither direction has data to deliver.
Choose which endpoint's timer expires in that callback; no wall-clock sleeps
are needed. For X/Y partial-frame loss, reset the stream decoder before
delivering a retransmission. Protocol retry exhaustion should terminate the
fixture immediately through the host's failure-action handling.

`maxBufferedBytes`, `maxTraceEntries` and `maxSteps` bound resource use.
`DescribeTrace()` reports the latest writes, their direction and faulted bytes,
with previews limited to 16 bytes. `Trace` also exposes full copies for exact
assertions. Trace entries are diagnostic data and do not affect queued bytes.

Windowed protocols can call `DiscardPending(direction)` in response to a
transport-purge action. This removes only bytes not yet read from that
direction. Already delivered fragments remain the receiver's responsibility,
which exposes recovery bugs hidden by fixtures that discard complete packets.

## Verification layers

| Layer | Location | Purpose |
| --- | --- | --- |
| Codec | `*.Protocol.Tests` | Independent check vectors, framing, escaping and malformed inputs |
| State machine | `*.Protocol.Tests` | Valid/invalid transitions, retry budgets, application decisions |
| Protocol wire fixtures | `*.Protocol.Tests` | End-to-end transfer, loss/corruption recovery and terminal behavior |
| Common byte link | `ModemKit.IntegrationTests` | Fragmentation/coalescing, cross-project API integration, exact payloads |
| External peer | `ModemKit.Interop` | Optional interoperability against independently implemented lrzsz |

Run all automated tests from the repository root:

```console
dotnet test ModemKit.slnx -c Release
dotnet test ModemKit.slnx -c Release -p:ProtocolUnderTestTargetFramework=netstandard2.0
```

The asset-selection regression in `ModemKit.IntegrationTests` verifies that the
requested protocol target frameworks are actually loaded at runtime.

Common test matrices exercise 1-byte, 17-byte and 4096-byte reads; 0/1/127/128/
129/1023/1024/1025-byte files; files crossing sequence 255; bytes spanning the
entire 0..255 alphabet; and batches mixing empty and nonempty files. Loss rules
target handshake, data and final acknowledgements. G-mode corruption must
abort rather than silently recover.

Additional branch fixtures exercise 4K/8K packets, bounded windows, SEAlink
RESYNC/Overdrive, JMODEM adaptive sizing and 2 MiB rollover. Compound failures
include a lost startup ACK and a damaged retry after the final ACK was lost.
See the [branch matrix](../docs/protocol-branches.md) and
[recorded validation](../docs/testing.md#recorded-validation) for coverage and
the distinction between scripted fixtures and native-peer runs.

Fixture drivers queue application responses so streaming transfers do not
grow the call stack. All action ordering, including final control writes, is
preserved before subsequent application events are processed.
