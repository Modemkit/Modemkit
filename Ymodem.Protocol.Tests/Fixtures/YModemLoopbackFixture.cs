namespace Ymodem.Protocol.Tests
{
    /// <summary>
    /// Deterministic, byte-only duplex link with configurable packet damage and
    /// fragmentation. Application decisions run before the next transport callback.
    /// </summary>
    internal sealed class YModemLoopbackFixture
    {
        private readonly Func<YModemEvent, IReadOnlyList<YModemAction>> _send;
        private readonly Func<YModemEvent, IReadOnlyList<YModemAction>> _receive;
        private readonly Func<bool> _dataPhase;
        private readonly YModemPacketEncoder _encoder = new();
        private readonly YModemStreamDecoder _decoder = new();
        private readonly Queue<(bool ToReceiver, byte[] Bytes)> _wire = new();
        private readonly Queue<(bool Sender, YModemAction Action)> _application = new();
        private readonly byte[][] _files;
        private readonly int _chunkSize;
        private readonly Func<bool, int, byte[], byte[]?>? _fault;
        private readonly Dictionary<int, List<byte>> _received = new();
        private int _fileIndex = -1;
        private int _receiverFileIndex = -1;
        private int _offset;
        private int _sendIndex;
        private int _receiveIndex;

        public YModemLoopbackFixture(
            byte[][] files,
            bool batch,
            YModemTransferMode mode = YModemTransferMode.Crc,
            int chunkSize = 17,
            YModemBlockMode blockMode = YModemBlockMode.Dynamic1K,
            Func<bool, int, byte[], byte[]?>? fault = null)
        {
            _files = files;
            _chunkSize = chunkSize;
            _fault = fault;
            var options = new YModemTransferOptions(mode);
            _decoder.Integrity = mode == YModemTransferMode.Checksum ? YModemIntegrity.Checksum : YModemIntegrity.Crc16;
            if (batch)
            {
                var sender = new YModemBatchSender(new YModemBlockOptions(blockMode), options);
                var receiver = new YModemBatchReceiver(options);
                _send = input => sender.Advance(input).Actions;
                _receive = input => receiver.Advance(input).Actions;
                _dataPhase = () => receiver.Snapshot.NextBlockNumber > 1 &&
                    (receiver.Snapshot.Phase == YModemBatchReceiverPhase.WaitingDataPacketOrEot ||
                    receiver.Snapshot.Phase == YModemBatchReceiverPhase.WaitingDataBlockDecision);
            }
            else
            {
                var sender = new YModemSender(new YModemBlockOptions(blockMode), options);
                var receiver = new YModemReceiver(options);
                _send = input => sender.Advance(input).Actions;
                _receive = input => receiver.Advance(input).Actions;
                _dataPhase = () => receiver.Snapshot.NextBlockNumber > 1 &&
                    (receiver.Snapshot.Phase == YModemReceiverPhase.WaitingDataPacketOrEot ||
                    receiver.Snapshot.Phase == YModemReceiverPhase.WaitingDataBlockDecision);
            }
        }

        public bool SenderCompleted { get; private set; }

        public bool ReceiverCompleted { get; private set; }

        public string? Failure { get; private set; }

        public List<byte> ReceiverControls { get; } = new();

        public List<byte[]> SenderFrames { get; } = new();

        public IReadOnlyList<byte[]> Run()
        {
            Enqueue(false, _receive(new YModemEvent.StartRequested()));
            for (var steps = 0; steps < 100000 && Failure == null; steps++)
            {
                DrainApplication();
                if (SenderCompleted && ReceiverCompleted && _wire.Count == 0)
                {
                    break;
                }

                if (_wire.Count == 0)
                {
                    Enqueue(true, _send(new YModemEvent.TimeoutElapsed()));
                    DrainApplication();
                    if (_wire.Count == 0)
                    {
                        Enqueue(false, _receive(_decoder.Timeout()));
                    }

                    continue;
                }

                var (toReceiver, bytes) = _wire.Dequeue();
                for (var offset = 0; offset < bytes.Length; offset += _chunkSize)
                {
                    var chunk = bytes.Skip(offset).Take(_chunkSize).ToArray();
                    if (toReceiver)
                    {
                        _decoder.Feed(chunk, _dataPhase, input =>
                        {
                            Enqueue(false, _receive(input));
                            DrainApplication();
                        });
                    }
                    else
                    {
                        foreach (var value in chunk)
                        {
                            Enqueue(true, _send(new YModemEvent.PeerByteReceived(value)));
                            DrainApplication();
                        }
                    }
                }
            }

            if (Failure == null && (!SenderCompleted || !ReceiverCompleted))
            {
                Failure = "Loopback step limit exceeded.";
            }

            return _received.OrderBy(pair => pair.Key).Select(pair => pair.Value.ToArray()).ToArray();
        }

        private void Enqueue(bool sender, IReadOnlyList<YModemAction> actions)
        {
            foreach (var action in actions)
            {
                _application.Enqueue((sender, action));
            }
        }

        private void DrainApplication()
        {
            while (_application.Count > 0)
            {
                var (sender, action) = _application.Dequeue();
                switch (action)
                {
                    case YModemAction.RequestFileHeader:
                        _fileIndex++;
                        _offset = 0;
                        Enqueue(true, _send(_fileIndex < _files.Length
                            ? new YModemEvent.FileHeaderReady(new YModemFileDescriptor("file" + _fileIndex + ".bin", _files[_fileIndex].Length))
                            : new YModemEvent.NoMoreFiles()));
                        break;
                    case YModemAction.RequestDataBlock request:
                        var count = Math.Min(request.BlockSize, _files[_fileIndex].Length - _offset);
                        var payload = _files[_fileIndex].Skip(_offset).Take(count).ToArray();
                        _offset += count;
                        Enqueue(true, _send(new YModemEvent.DataBlockReady(
                            request.BlockNumber, payload, count, _offset == _files[_fileIndex].Length)));
                        break;
                    case YModemAction.OfferFileHeader:
                        _receiverFileIndex++;
                        _received.Add(_receiverFileIndex, new List<byte>());
                        Enqueue(false, _receive(new YModemEvent.FileHeaderAccepted()));
                        break;
                    case YModemAction.DeliverDataBlock deliver:
                        _received[_receiverFileIndex].AddRange(deliver.Payload.Take(deliver.DataLength));
                        Enqueue(false, _receive(new YModemEvent.DataBlockAccepted()));
                        break;
                    case YModemAction.SendPacket packet:
                        Write(sender, _encoder.Encode(packet.Packet, packet.Integrity));
                        break;
                    case YModemAction.SendControl control:
                        if (!sender)
                        {
                            ReceiverControls.Add(control.Value);
                        }

                        Write(sender, new[] { control.Value });
                        break;
                    case YModemAction.Complete:
                        if (sender)
                        {
                            SenderCompleted = true;
                        }
                        else
                        {
                            ReceiverCompleted = true;
                        }

                        break;
                    case YModemAction.Fail fail:
                        Failure = (sender ? "Sender: " : "Receiver: ") + fail.Reason;
                        break;
                    case YModemAction.Cancel cancel:
                        Failure = (sender ? "Sender: " : "Receiver: ") + cancel.Reason;
                        break;
                }
            }
        }

        private void Write(bool sender, byte[] bytes)
        {
            if (sender)
            {
                SenderFrames.Add((byte[])bytes.Clone());
            }

            var index = sender ? ++_sendIndex : ++_receiveIndex;
            var transmitted = _fault == null ? bytes : _fault(sender, index, bytes);
            if (transmitted != null)
            {
                _wire.Enqueue((sender, transmitted));
            }
        }
    }
}
