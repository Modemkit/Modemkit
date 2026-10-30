using Zmodem.Protocol;

namespace Zmodem.Protocol.Tests;

internal sealed class WireFixture
{
    private readonly Queue<(bool Sender, ZModemEvent Event)> _events = new();
    private readonly byte[][] _files;
    private readonly int _chunkSize;
    private readonly uint _resumeOffset;
    private readonly bool _skipFirst;
    private readonly bool _requestCrc;
    private int _senderFile = -1;
    private int _receiverFile = -1;
    private List<byte>? _current;

    public WireFixture(
        byte[][] files,
        bool crc32 = true,
        int chunkSize = 17,
        uint resumeOffset = 0,
        bool skipFirst = false,
        ZModemOptions? options = null,
        ZModemOptions? receiverOptions = null,
        bool requestCrc = false)
    {
        _files = files;
        _chunkSize = chunkSize;
        _resumeOffset = resumeOffset;
        _skipFirst = skipFirst;
        _requestCrc = requestCrc;
        Sender = new ZModemSender(options ?? new ZModemOptions(useCrc32: crc32));
        Receiver = new ZModemReceiver(receiverOptions ?? options ?? new ZModemOptions(useCrc32: crc32));
    }

    public ZModemSender Sender
    {
        get;
    }

    public ZModemReceiver Receiver
    {
        get;
    }

    public List<byte[]> Received { get; } = new();

    public List<ZModemAction> Trace { get; } = new();

    public List<(bool Sender, byte[] Bytes)> WireTrace { get; } = new();

    public uint MaximumBytesInFlight
    {
        get; private set;
    }

    public Func<bool, byte[], byte[]?>? Mutate
    {
        get; set;
    }

    public void Run()
    {
        _events.Enqueue((false, new ZModemEvent.StartRequested()));
        for (int step = 0; step < 100000; step++)
        {
            if (_events.Count == 0)
            {
                if (Sender.Snapshot.Phase == ZModemPhase.Completed && Receiver.Snapshot.Phase == ZModemPhase.Completed)
                {
                    return;
                }

                _events.Enqueue((true, new ZModemEvent.TimeoutElapsed()));
                _events.Enqueue((false, new ZModemEvent.TimeoutElapsed()));
            }

            var work = _events.Dequeue();
            ZModemStepResult result = work.Sender ? Sender.Advance(work.Event) : Receiver.Advance(work.Event);
            MaximumBytesInFlight = Math.Max(MaximumBytesInFlight, Sender.NextSendOffset - Sender.AcknowledgedOffset);
            foreach (ZModemAction action in result.Actions)
            {
                Trace.Add(action);
                Assert.False(action is ZModemAction.Fail fail, action is ZModemAction.Fail failure ? failure.Reason : string.Empty);
                Assert.False(action is ZModemAction.Cancel);
                switch (action)
                {
                    case ZModemAction.SendBytes send:
                        WireTrace.Add((work.Sender, (byte[])send.Bytes.Clone()));
                        byte[]? bytes = Mutate == null ? send.Bytes : Mutate(work.Sender, send.Bytes);
                        if (bytes == null)
                        {
                            break;
                        }

                        for (int offset = 0; offset < bytes.Length; offset += _chunkSize)
                        {
                            _events.Enqueue((!work.Sender, new ZModemEvent.PeerBytesReceived(bytes.Skip(offset).Take(_chunkSize).ToArray())));
                        }

                        break;
                    case ZModemAction.RequestFileHeader:
                        _senderFile++;
                        _events.Enqueue((true, _senderFile < _files.Length
                            ? new ZModemEvent.FileHeaderReady(new ZModemFileDescriptor("file" + _senderFile + ".bin", (uint)_files[_senderFile].Length))
                            : new ZModemEvent.NoMoreFiles()));
                        break;
                    case ZModemAction.RequestDataBlock request:
                        _events.Enqueue((true, new ZModemEvent.DataBlockReady(request.Offset, _files[_senderFile].Skip((int)request.Offset).Take(request.MaxLength).ToArray())));
                        break;
                    case ZModemAction.RequestFileCrc request:
                        int count = request.ByteCount == 0 ? _files[_senderFile].Length : (int)request.ByteCount;
                        _events.Enqueue((true, new ZModemEvent.FileCrcReady(ZModemCodec.ComputeCrc32(_files[_senderFile].Take(count).ToArray()))));
                        break;
                    case ZModemAction.FileCrcReceived received:
                        Assert.Equal(ZModemCodec.ComputeCrc32(_files[_receiverFile]), received.Crc);
                        _events.Enqueue((false, new ZModemEvent.FileAccepted(_resumeOffset)));
                        break;
                    case ZModemAction.OfferFileHeader:
                        _receiverFile++;
                        if (_skipFirst && _receiverFile == 0)
                        {
                            _events.Enqueue((false, new ZModemEvent.FileSkipped()));
                        }
                        else
                        {
                            _current = new List<byte>(_files[_receiverFile].Take((int)_resumeOffset));
                            _events.Enqueue((false, _requestCrc ? new ZModemEvent.FileCrcRequested() : new ZModemEvent.FileAccepted(_resumeOffset)));
                        }

                        break;
                    case ZModemAction.DeliverDataBlock delivery:
                        Assert.NotNull(_current);
                        Assert.Equal((uint)_current.Count, delivery.Offset);
                        _current.AddRange(delivery.Payload);
                        _events.Enqueue((false, new ZModemEvent.DataBlockAccepted()));
                        break;
                    case ZModemAction.FileCompleted when !work.Sender:
                        Received.Add(_current!.ToArray());
                        _current = null;
                        break;
                }
            }
        }

        Assert.Fail("Fixture exceeded its deterministic step budget.");
    }

    public static ZModemHeaderType? HeaderType(byte[] wire)
    {
        return new ZModemStreamDecoder().Feed(wire).OfType<ZModemWireEvent.HeaderReceived>().FirstOrDefault()?.Header.Type;
    }
}
