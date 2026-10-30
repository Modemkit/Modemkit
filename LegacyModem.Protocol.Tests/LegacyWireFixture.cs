namespace LegacyModem.Protocol.Tests
{
    internal sealed class LegacyWireFixture
    {
        private readonly Queue<(bool Sender, LegacyModemAction Action)> _actions = new();
        private readonly Queue<(bool Sender, byte[] Bytes)> _wire = new();
        private readonly byte[][] _files;
        private readonly int _chunkSize;
        private readonly int _restartBlock;
        private readonly Func<bool, int, byte[], byte[]?>? _fault;
        private readonly List<List<byte>> _received = new();
        private int _sendFile = -1;
        private int _receiveFile = -1;
        private int _sendCount;
        private int _receiveCount;

        internal LegacyWireFixture(
            byte[][] files,
            LegacyModemOptions? senderOptions = null,
            LegacyModemOptions? receiverOptions = null,
            int chunkSize = 17,
            int restartBlock = 1,
            Func<bool, int, byte[], byte[]?>? fault = null)
        {
            _files = files;
            _chunkSize = chunkSize;
            _restartBlock = restartBlock;
            _fault = fault;
            Sender = new LegacyModemSender(senderOptions);
            Receiver = new LegacyModemReceiver(receiverOptions ?? senderOptions);
        }

        internal LegacyModemSender Sender { get; }

        internal LegacyModemReceiver Receiver { get; }

        internal string? Failure { get; private set; }

        internal bool SenderCompleted { get; private set; }

        internal bool ReceiverCompleted { get; private set; }

        internal int MaximumOutstanding { get; private set; }

        internal List<byte[]> SenderWrites { get; } = new();

        internal List<byte[]> ReceiverWrites { get; } = new();

        internal List<int> RequestedBlocks { get; } = new();

        internal IReadOnlyList<byte[]> Run()
        {
            Enqueue(false, Receiver.Advance(new LegacyModemEvent.StartRequested()));
            for (var step = 0; step < 200000 && Failure == null; step++)
            {
                DrainActions();
                if (SenderCompleted && ReceiverCompleted && _wire.Count == 0)
                {
                    break;
                }

                if (_wire.Count == 0)
                {
                    Enqueue(true, Sender.Advance(new LegacyModemEvent.TimeoutElapsed()));
                    DrainActions();
                    if (_wire.Count == 0)
                    {
                        Enqueue(false, Receiver.Advance(new LegacyModemEvent.TimeoutElapsed()));
                    }

                    continue;
                }

                var (sender, bytes) = _wire.Dequeue();
                for (var offset = 0; offset < bytes.Length; offset += _chunkSize)
                {
                    var chunk = bytes.Skip(offset).Take(_chunkSize).ToArray();
                    var result = sender
                        ? Receiver.Advance(new LegacyModemEvent.BytesReceived(chunk))
                        : Sender.Advance(new LegacyModemEvent.BytesReceived(chunk));
                    Enqueue(!sender, result);
                    DrainActions();
                }
            }

            if (Failure == null && (!SenderCompleted || !ReceiverCompleted))
            {
                Failure = "Fixture exceeded its progress limit.";
            }

            return _received.Select(bytes => bytes.ToArray()).ToArray();
        }

        private void Enqueue(bool sender, LegacyModemStepResult step)
        {
            MaximumOutstanding = Math.Max(MaximumOutstanding, Sender.Snapshot.OutstandingBlocks);
            foreach (var action in step.Actions)
            {
                _actions.Enqueue((sender, action));
            }
        }

        private void DrainActions()
        {
            while (_actions.Count > 0)
            {
                var (sender, action) = _actions.Dequeue();
                switch (action)
                {
                    case LegacyModemAction.RequestFileHeader:
                        _sendFile++;
                        Enqueue(true, Sender.Advance(_sendFile < _files.Length
                            ? new LegacyModemEvent.FileHeaderReady(new LegacyModemFileDescriptor("FILE" + _sendFile + ".BIN", _files[_sendFile].Length, 0x12345678))
                            : new LegacyModemEvent.NoMoreFiles()));
                        break;
                    case LegacyModemAction.RequestDataBlock request:
                        RequestedBlocks.Add(request.BlockNumber);
                        Enqueue(true, Sender.Advance(new LegacyModemEvent.DataBlockReady(
                            request.BlockNumber, _files[_sendFile].Skip((int)request.Offset).Take(request.DataLength).ToArray())));
                        break;
                    case LegacyModemAction.OfferFileHeader:
                        _receiveFile++;
                        var prefix = _files[_receiveFile].Take(Math.Min((_restartBlock - 1) * 128, _files[_receiveFile].Length)).ToList();
                        _received.Add(prefix);
                        Enqueue(false, Receiver.Advance(new LegacyModemEvent.FileHeaderAccepted(_restartBlock)));
                        break;
                    case LegacyModemAction.DeliverDataBlock data:
                        if (data.Offset != _received[_receiveFile].Count)
                        {
                            Failure = "Data was delivered twice or out of order.";
                            break;
                        }

                        _received[_receiveFile].AddRange(data.Data);
                        Enqueue(false, Receiver.Advance(new LegacyModemEvent.DataBlockAccepted()));
                        break;
                    case LegacyModemAction.SendBytes send:
                        var bytes = send.Bytes;
                        (sender ? SenderWrites : ReceiverWrites).Add((byte[])bytes.Clone());
                        var index = sender ? ++_sendCount : ++_receiveCount;
                        var transformed = _fault == null ? bytes : _fault(sender, index, bytes);
                        if (transformed != null)
                        {
                            _wire.Enqueue((sender, transformed));
                        }

                        break;
                    case LegacyModemAction.PurgeOutput:
                        var retained = _wire.Where(item => item.Sender != sender).ToArray();
                        _wire.Clear();
                        foreach (var item in retained)
                        {
                            _wire.Enqueue(item);
                        }

                        break;
                    case LegacyModemAction.Complete:
                        if (sender)
                        {
                            SenderCompleted = true;
                        }
                        else
                        {
                            ReceiverCompleted = true;
                        }

                        break;
                    case LegacyModemAction.Fail fail:
                        Failure = (sender ? "Sender: " : "Receiver: ") + fail.Reason;
                        break;
                    case LegacyModemAction.Cancel cancel:
                        Failure = (sender ? "Sender: " : "Receiver: ") + cancel.Reason;
                        break;
                }
            }
        }
    }
}
