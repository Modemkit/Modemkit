# ModemKit

`ModemKit` is a .NET toolkit for modem-style file transfer workflows.

The repository currently ships **`Ymodem.Protocol`**, a transport-agnostic implementation of the **YMODEM** file transfer protocol for .NET. Instead of coupling the protocol to a specific serial port or stream API, the library models the transfer as explicit **events**, **actions**, and **state snapshots** so it can be embedded into desktop tools, device updaters, bootloader utilities, and custom transport layers.

## Highlights

- YMODEM **single-file** sender and receiver state machines
- YMODEM **batch** sender and receiver state machines
- Packet **encoding** and **decoding** with CRC16 validation
- Explicit integration model based on `Advance(...)` + returned actions
- Works with custom transports such as serial ports, sockets, or device bridges
- Targets **`netstandard2.0`** and **`net10.0`**
- Covered by xUnit tests

## Package

Current package in this repository:

- [`Ymodem.Protocol`](./Ymodem.Protocol/) — a .NET implementation of the YMODEM protocol

The NuGet package ID is `Ymodem.Protocol`.

## Install

```bash
dotnet add package Ymodem.Protocol
```

## Target frameworks

- `netstandard2.0`
- `net10.0`

## Core API model

The library is built around a small protocol engine pattern:

- **Events** (`YModemEvent`) represent protocol inputs
  - examples: `StartRequested`, `PeerByteReceived`, `PacketReceived`, `FileHeaderReady`, `DataBlockReady`
- **Actions** (`YModemAction`) represent the next thing your app should do
  - examples: `SendControl`, `SendPacket`, `RequestFileHeader`, `RequestDataBlock`, `DeliverDataBlock`, `Complete`
- **State machines** advance one step at a time
  - `YModemSender`
  - `YModemReceiver`
  - `YModemBatchSender`
  - `YModemBatchReceiver`
- **Snapshots** expose current state after each step

This design keeps the protocol logic deterministic and makes it easy to plug into your own I/O layer.

## How integration typically works

### Sender flow

1. Wait for the receiver's `C` request.
2. Call `Advance(new YModemEvent.PeerByteReceived(...))`.
3. When the library returns `YModemAction.RequestFileHeader`, provide a `YModemFileDescriptor`.
4. When it returns `YModemAction.RequestDataBlock`, read the next chunk from your file source.
5. When it returns `YModemAction.SendPacket`, encode and send the packet bytes.
6. Repeat until you receive `YModemAction.Complete` or a cancel/failure action.

### Receiver flow

1. Start the session with `new YModemEvent.StartRequested()`.
2. Send the returned control bytes or packets to the peer.
3. When the library returns `YModemAction.OfferFileHeader`, decide whether to accept the incoming file.
4. When it returns `YModemAction.DeliverDataBlock`, persist the payload and then respond with `DataBlockAccepted` or `DataBlockRejected`.
5. Continue until the transfer completes.

## Minimal examples

### Send a file with `YModemSender`

```csharp
using Ymodem.Protocol;

var bytes = new byte[] { 0x41, 0x42, 0x43 };
var sender = new YModemSender();
var file = new YModemFileDescriptor("demo.bin", bytes.Length);

YModemStepResult step = sender.Advance(
    new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));

foreach (YModemAction action in step.Actions)
{
    if (action is YModemAction.RequestFileHeader)
    {
        step = sender.Advance(new YModemEvent.FileHeaderReady(file));
    }
}

// In a real transport loop, keep feeding peer bytes back into Advance(...)
// and execute SendPacket / RequestDataBlock actions until completion.
```

To force the sender to stay in 128-byte data-block mode instead of using the
default dynamic 1K mode:

```csharp
using Ymodem.Protocol;

var sender = new YModemSender(YModemBlockMode.Fixed128);
```

Mode differences:

- `YModemBlockMode.Dynamic1K`: both the block 0 header and subsequent data
  blocks use the same capacity rule, `<=128 => 128` and `>128 => 1024`.
- `YModemBlockMode.Fixed128`: both the block 0 header and subsequent data
  blocks stay on 128-byte packets; if header metadata does not fit, encoding
  fails instead of switching to 1K.
- `YModemBlockMode.Fixed1K`: both the block 0 header and subsequent data
  blocks always use 1K/STX packets, even for very small files or metadata.

If you need to configure block 0 and data blocks independently, use `YModemBlockOptions`:

```csharp
using Ymodem.Protocol;

var blockOptions = new YModemBlockOptions(
    block0Mode: YModemBlockMode.Fixed1K,
    dataBlockMode: YModemBlockMode.Fixed1K);

var sender = new YModemSender(blockOptions);
```

### Receive a file with `YModemReceiver`

```csharp
using Ymodem.Protocol;

var receiver = new YModemReceiver();
YModemReceiveStepResult step = receiver.Advance(new YModemEvent.StartRequested());

foreach (YModemAction action in step.Actions)
{
    if (action is YModemAction.SendControl control)
    {
        // Write control.Value to your transport.
    }
}

// When a header packet arrives:
step = receiver.Advance(
    new YModemEvent.PacketReceived(new YModemPacket.Header(
        new YModemFileDescriptor("demo.bin", 3))));

// If your app accepts the file:
step = receiver.Advance(new YModemEvent.FileHeaderAccepted());
```

### Batch transfer

Use the batch variants when you want to transfer multiple files in one YMODEM session:

- `YModemBatchSender`
- `YModemBatchReceiver`

For senders, provide `YModemEvent.FileHeaderReady(...)` for each file and finish with `YModemEvent.NoMoreFiles()`.

## Low-level packet helpers

If you want to connect the state machines to raw byte I/O, the library also includes:

- `YModemPacketEncoder` — converts `YModemPacket` to wire-format bytes
- `YModemPacketDecoder` — parses raw frames into `YModemPacket`
- `YModemReceiverEventAdapter` — adapts received bytes into receiver-side events

This makes it straightforward to layer the protocol engine over `SerialPort`, USB bridges, sockets, or custom bootloader transports.

## Running tests

```bash
dotnet restore ModemKit.slnx
dotnet test Ymodem.Protocol.Tests/Ymodem.Protocol.Tests.csproj --configuration Release
```

## Repository layout

```text
ModemKit/
├── Ymodem.Protocol/        # Protocol library
├── Ymodem.Protocol.Tests/  # xUnit test project
└── codeAnalysis/           # Analyzer and StyleCop rules
```

## Status

This project is currently in an early `0.x` stage. The package is published and usable, but the public API may continue to evolve as more protocol scenarios and integration helpers are added.

## License

MIT — see [`LICENSE`](./LICENSE).
