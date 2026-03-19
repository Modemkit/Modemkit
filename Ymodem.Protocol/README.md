# Ymodem.Protocol

`Ymodem.Protocol` is a transport-agnostic .NET implementation of the YMODEM file transfer protocol.

The library models transfers as explicit events, actions, and state snapshots so it can be embedded into desktop tools, device updaters, bootloader utilities, and custom transport layers.

## Highlights

- YMODEM single-file sender and receiver state machines
- YMODEM batch sender and receiver state machines
- Packet encoding and decoding with CRC16 validation
- Explicit integration model based on `Advance(...)` + returned actions
- Works with custom transports such as serial ports, sockets, or device bridges
- Targets `netstandard2.0` and `net10.0`

## Target frameworks

- `netstandard2.0`
- `net10.0`

## Install

```bash
dotnet add package Ymodem.Protocol
```

## Status

This package is currently in an early `0.x` stage. The public API is usable, but it may continue to evolve as more protocol scenarios and integration helpers are added.
