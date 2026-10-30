using Xmodem.Protocol;
using Ymodem.Protocol;
using Zmodem.Protocol;

namespace ModemKit.Interop;

internal abstract class Endpoint : IDisposable
{
    protected Endpoint(byte[] source, bool isSender)
    {
        Source = source;
        IsSender = isSender;
    }

    public abstract bool Completed { get; }

    public abstract string State { get; }

    public byte[] Received => Sink.ToArray();

    protected List<byte[]> Outgoing { get; } = new();

    protected MemoryStream Sink { get; } = new();

    protected byte[] Source { get; }

    protected bool IsSender { get; }

    public abstract IReadOnlyList<byte[]> Start();

    public abstract IReadOnlyList<byte[]> Feed(byte[] bytes);

    public abstract IReadOnlyList<byte[]> Timeout();

    public static Endpoint Create(string protocol, byte[] source, bool sender)
    {
        return protocol switch
        {
            "z32" => new ZEndpoint(source, sender, true),
            "z16" => new ZEndpoint(source, sender, false),
            "z4k" => new ZEndpoint(source, sender, true, new ZModemOptions(blockSize: 4096, profile: ZModemProfile.Extended4K)),
            "z8k" => new ZEndpoint(source, sender, true, new ZModemOptions(blockSize: 8192, profile: ZModemProfile.Extended8K)),
            "zedzap" => new ZEndpoint(source, sender, true, new ZModemOptions(blockSize: 8192, profile: ZModemProfile.ZedZap)),
            "zedzip" => new ZEndpoint(source, sender, true, new ZModemOptions(profile: ZModemProfile.ZedZip)),
            "zstream" => new ZEndpoint(source, sender, true, new ZModemOptions(flowControl: ZModemFlowControl.Streaming)),
            "zwindow" => new ZEndpoint(source, sender, true, new ZModemOptions(flowControl: ZModemFlowControl.Windowed)),
            "y" => new YEndpoint(source, sender, YModemTransferMode.Crc),
            "yg" => new YEndpoint(source, sender, YModemTransferMode.G),
            "ychecksum" => new YEndpoint(source, sender, YModemTransferMode.Checksum),
            "xchecksum" => new XEndpoint(source, sender, XModemMode.Checksum),
            "xcrc" => new XEndpoint(source, sender, XModemMode.Crc),
            "x1k" => new XEndpoint(source, sender, XModemMode.OneK),
            _ => throw new ArgumentException("Unknown protocol: " + protocol)
        };
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Sink.Dispose();
        }
    }

    protected IReadOnlyList<byte[]> TakeOutput()
    {
        var result = Outgoing.ToArray();
        Outgoing.Clear();
        return result;
    }
}

internal sealed class ZEndpoint : Endpoint
{
    private readonly ZModemSession _engine;
    private bool _offered;
    private bool _receivedFile;

    public ZEndpoint(byte[] source, bool sender, bool crc32, ZModemOptions? options = null)
        : base(source, sender)
    {
        var settings = options ?? new ZModemOptions(useCrc32: crc32);
        _engine = sender ? new ZModemSender(settings) : new ZModemReceiver(settings);
    }

    public override bool Completed => _engine.Snapshot.Phase == ZModemPhase.Completed;

    public override string State => _engine.Snapshot.Phase.ToString();

    public override IReadOnlyList<byte[]> Start()
    {
        Advance(new ZModemEvent.StartRequested());
        return TakeOutput();
    }

    public override IReadOnlyList<byte[]> Feed(byte[] bytes)
    {
        Advance(new ZModemEvent.PeerBytesReceived(bytes));
        return TakeOutput();
    }

    public override IReadOnlyList<byte[]> Timeout()
    {
        Advance(new ZModemEvent.TimeoutElapsed());
        return TakeOutput();
    }

    private void Advance(ZModemEvent initial)
    {
        var pending = new Queue<ZModemEvent>();
        pending.Enqueue(initial);
        while (pending.TryDequeue(out var next))
        {
            foreach (var action in _engine.Advance(next).Actions)
            {
                switch (action)
                {
                    case ZModemAction.SendBytes send:
                        Outgoing.Add(send.Bytes);
                        break;
                    case ZModemAction.RequestFileHeader:
                        pending.Enqueue(_offered
                            ? new ZModemEvent.NoMoreFiles()
                            : new ZModemEvent.FileHeaderReady(new ZModemFileDescriptor("payload.bin", (uint)Source.Length)));
                        _offered = true;
                        break;
                    case ZModemAction.RequestDataBlock request:
                        pending.Enqueue(new ZModemEvent.DataBlockReady(
                            request.Offset, Source.AsSpan((int)request.Offset, request.MaxLength).ToArray()));
                        break;
                    case ZModemAction.OfferFileHeader offer:
                        if (_receivedFile || offer.File.FileName != "payload.bin")
                        {
                            throw new InvalidOperationException("Unexpected native ZMODEM file: " + offer.File.FileName);
                        }

                        _receivedFile = true;
                        pending.Enqueue(new ZModemEvent.FileAccepted());
                        break;
                    case ZModemAction.DeliverDataBlock data:
                        if (Sink.Length != data.Offset)
                        {
                            throw new InvalidOperationException("ZMODEM delivered an unexpected offset.");
                        }

                        Sink.Write(data.Payload);
                        pending.Enqueue(new ZModemEvent.DataBlockAccepted());
                        break;
                    case ZModemAction.Fail fail:
                        throw new InvalidOperationException("ZMODEM failed: " + fail.Reason);
                    case ZModemAction.Cancel cancel:
                        throw new InvalidOperationException("ZMODEM cancelled: " + cancel.Reason);
                }
            }
        }
    }
}

internal sealed class YEndpoint : Endpoint
{
    private readonly YModemBatchSender _sender;
    private readonly YModemBatchReceiver _receiver;
    private readonly YModemStreamDecoder _decoder = new();
    private readonly YModemPacketEncoder _encoder = new();
    private bool _offered;
    private bool _receivedFile;
    private int _sourceOffset;

    public YEndpoint(byte[] source, bool sender, YModemTransferMode mode)
        : base(source, sender)
    {
        var options = new YModemTransferOptions(mode);
        _sender = new YModemBatchSender(new YModemBlockOptions(), options);
        _receiver = new YModemBatchReceiver(options);
        _decoder.Integrity = _receiver.Integrity;
    }

    public override bool Completed => IsSender
        ? _sender.Snapshot.Phase == YModemBatchSenderPhase.Completed
        : _receiver.Snapshot.Phase == YModemBatchReceiverPhase.Completed;

    public override string State => IsSender ? _sender.Snapshot.Phase.ToString() : _receiver.Snapshot.Phase.ToString();

    public override IReadOnlyList<byte[]> Start()
    {
        if (!IsSender)
        {
            Advance(new YModemEvent.StartRequested());
        }

        return TakeOutput();
    }

    public override IReadOnlyList<byte[]> Feed(byte[] bytes)
    {
        foreach (var value in bytes)
        {
            if (IsSender)
            {
                Advance(new YModemEvent.PeerByteReceived(value));
            }
            else
            {
                // A coalesced EOT/header changes how the following block zero is interpreted.
                var snapshot = _receiver.Snapshot;
                var dataPhase = snapshot.NextBlockNumber > 1
                    && (snapshot.Phase == YModemBatchReceiverPhase.WaitingDataPacketOrEot
                        || snapshot.Phase == YModemBatchReceiverPhase.WaitingDataBlockDecision);
                foreach (var protocolEvent in _decoder.Feed(new[] { value }, dataPhase))
                {
                    Advance(protocolEvent);
                }
            }
        }

        return TakeOutput();
    }

    public override IReadOnlyList<byte[]> Timeout()
    {
        Advance(IsSender ? new YModemEvent.TimeoutElapsed() : _decoder.Timeout());
        return TakeOutput();
    }

    private void Advance(YModemEvent initial)
    {
        var pending = new Queue<YModemEvent>();
        pending.Enqueue(initial);
        while (pending.TryDequeue(out var next))
        {
            var actions = IsSender ? _sender.Advance(next).Actions : _receiver.Advance(next).Actions;
            foreach (var action in actions)
            {
                switch (action)
                {
                    case YModemAction.SendControl control:
                        Outgoing.Add(new[] { control.Value });
                        break;
                    case YModemAction.SendPacket packet:
                        Outgoing.Add(_encoder.Encode(packet.Packet, packet.Integrity));
                        break;
                    case YModemAction.RequestFileHeader:
                        pending.Enqueue(_offered
                            ? new YModemEvent.NoMoreFiles()
                            : new YModemEvent.FileHeaderReady(new YModemFileDescriptor("payload.bin", Source.Length)));
                        _offered = true;
                        break;
                    case YModemAction.RequestDataBlock request:
                        var count = Math.Min(request.BlockSize, Source.Length - _sourceOffset);
                        var block = Source.AsSpan(_sourceOffset, count).ToArray();
                        _sourceOffset += count;
                        pending.Enqueue(new YModemEvent.DataBlockReady(
                            request.BlockNumber, block, count, _sourceOffset == Source.Length));
                        break;
                    case YModemAction.OfferFileHeader offer:
                        if (_receivedFile || offer.File.FileName != "payload.bin")
                        {
                            throw new InvalidOperationException("Unexpected native YMODEM file: " + offer.File.FileName);
                        }

                        _receivedFile = true;
                        pending.Enqueue(new YModemEvent.FileHeaderAccepted());
                        break;
                    case YModemAction.DeliverDataBlock data:
                        Sink.Write(data.Payload, 0, data.DataLength);
                        pending.Enqueue(new YModemEvent.DataBlockAccepted());
                        break;
                    case YModemAction.Fail fail:
                        throw new InvalidOperationException("YMODEM failed: " + fail.Reason);
                    case YModemAction.Cancel cancel:
                        throw new InvalidOperationException("YMODEM cancelled: " + cancel.Reason);
                }
            }
        }
    }
}

internal sealed class XEndpoint : Endpoint
{
    private readonly XModemSender _sender;
    private readonly XModemReceiver _receiver;
    private readonly XModemStreamDecoder _decoder = new();
    private readonly XModemPacketEncoder _encoder = new();
    private int _sourceOffset;

    public XEndpoint(byte[] source, bool sender, XModemMode mode)
        : base(source, sender)
    {
        var options = new XModemOptions(mode);
        _sender = new XModemSender(options);
        _receiver = new XModemReceiver(options);
    }

    public override bool Completed => IsSender
        ? _sender.Snapshot.Phase == XModemPhase.Completed
        : _receiver.Snapshot.Phase == XModemPhase.Completed;

    public override string State => IsSender ? _sender.Snapshot.Phase.ToString() : _receiver.Snapshot.Phase.ToString();

    public override IReadOnlyList<byte[]> Start()
    {
        if (!IsSender)
        {
            Advance(new XModemEvent.StartRequested());
        }

        return TakeOutput();
    }

    public override IReadOnlyList<byte[]> Feed(byte[] bytes)
    {
        if (IsSender)
        {
            foreach (var value in bytes)
            {
                Advance(new XModemEvent.PeerByteReceived(value));
            }
        }
        else
        {
            _decoder.Integrity = _receiver.Snapshot.Integrity;
            foreach (var protocolEvent in _decoder.Feed(bytes))
            {
                Advance(protocolEvent);
            }
        }

        return TakeOutput();
    }

    public override IReadOnlyList<byte[]> Timeout()
    {
        Advance(IsSender ? new XModemEvent.TimeoutElapsed() : _decoder.OnTimeout());
        return TakeOutput();
    }

    private void Advance(XModemEvent initial)
    {
        var pending = new Queue<XModemEvent>();
        pending.Enqueue(initial);
        while (pending.TryDequeue(out var next))
        {
            foreach (var action in (IsSender ? _sender.Advance(next) : _receiver.Advance(next)).Actions)
            {
                switch (action)
                {
                    case XModemAction.SendControl control:
                        Outgoing.Add(new[] { control.Value });
                        break;
                    case XModemAction.SendPacket packet:
                        Outgoing.Add(_encoder.Encode(packet.Packet, packet.Integrity));
                        break;
                    case XModemAction.RequestDataBlock request:
                        var count = Math.Min(request.BlockSize, Source.Length - _sourceOffset);
                        var block = Source.AsSpan(_sourceOffset, count).ToArray();
                        _sourceOffset += count;
                        pending.Enqueue(new XModemEvent.DataBlockReady(
                            request.BlockNumber, block, count, _sourceOffset == Source.Length));
                        break;
                    case XModemAction.DeliverDataBlock data:
                        Sink.Write(data.Payload);
                        pending.Enqueue(new XModemEvent.DataBlockAccepted());
                        break;
                    case XModemAction.Fail fail:
                        throw new InvalidOperationException("XMODEM failed: " + fail.Reason);
                    case XModemAction.Cancel cancel:
                        throw new InvalidOperationException("XMODEM cancelled: " + cancel.Reason);
                }
            }
        }
    }
}
