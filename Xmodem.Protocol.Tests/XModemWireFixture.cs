namespace Xmodem.Protocol.Tests
{
    internal sealed class XModemWireFixture
    {
        private readonly byte[] _source;
        private readonly XModemPacketEncoder _encoder = new();
        private readonly XModemStreamDecoder _receiverDecoder = new();
        private readonly Queue<(bool ToReceiver, byte[] Bytes)> _wire = new();
        private int _sourceOffset;

        internal XModemWireFixture(byte[] source, XModemMode mode, int chunkSize = 17, int maxRetries = 10)
        {
            _source = source;
            ChunkSize = chunkSize;
            Sender = new XModemSender(new XModemOptions(mode, maxRetries));
            Receiver = new XModemReceiver(new XModemOptions(mode, maxRetries));
        }

        internal XModemSender Sender { get; }

        internal XModemReceiver Receiver { get; }

        internal int ChunkSize { get; }

        internal List<byte> Received { get; } = new();

        internal List<(bool ToReceiver, byte[] Bytes)> Transcript { get; } = new();

        internal Func<bool, byte[], byte[]?>? MutateWire { get; set; }

        internal void Run(int stepLimit = 100000)
        {
            Apply(false, Receiver.Advance(new XModemEvent.StartRequested()));
            for (var step = 0; step < stepLimit; step++)
            {
                if (_wire.Count == 0)
                {
                    if (Sender.Snapshot.Phase == XModemPhase.Completed
                        || Sender.Snapshot.Phase == XModemPhase.Faulted
                        || Sender.Snapshot.Phase == XModemPhase.Cancelled)
                    {
                        return;
                    }

                    if (_receiverDecoder.BufferedByteCount != 0
                        || Sender.Snapshot.Phase == XModemPhase.WaitingReceiverRequest)
                    {
                        Apply(false, Receiver.Advance(_receiverDecoder.OnTimeout()));
                    }
                    else
                    {
                        Apply(true, Sender.Advance(new XModemEvent.TimeoutElapsed()));
                    }

                    continue;
                }

                var item = _wire.Dequeue();
                Transcript.Add((item.ToReceiver, (byte[])item.Bytes.Clone()));
                var bytes = MutateWire == null ? item.Bytes : MutateWire(item.ToReceiver, (byte[])item.Bytes.Clone());
                if (bytes == null)
                {
                    continue;
                }

                if (!item.ToReceiver)
                {
                    foreach (var value in bytes)
                    {
                        Apply(true, Sender.Advance(new XModemEvent.PeerByteReceived(value)));
                    }

                    continue;
                }

                _receiverDecoder.Integrity = Receiver.Snapshot.Integrity;
                for (var offset = 0; offset < bytes.Length; offset += ChunkSize)
                {
                    var chunk = bytes.Skip(offset).Take(ChunkSize).ToArray();
                    foreach (var protocolEvent in _receiverDecoder.Feed(chunk))
                    {
                        Apply(false, Receiver.Advance(protocolEvent));
                    }
                }
            }

            throw new InvalidOperationException("Fixture exceeded its deterministic step limit.");
        }

        private void Apply(bool sender, XModemStepResult result)
        {
            foreach (var action in result.Actions)
            {
                switch (action)
                {
                    case XModemAction.RequestDataBlock request:
                        var count = Math.Min(request.BlockSize, _source.Length - _sourceOffset);
                        var block = new byte[count];
                        Buffer.BlockCopy(_source, _sourceOffset, block, 0, count);
                        _sourceOffset += count;
                        Apply(true, Sender.Advance(new XModemEvent.DataBlockReady(
                            request.BlockNumber, block, count, _sourceOffset == _source.Length)));
                        break;
                    case XModemAction.SendPacket packet:
                        _wire.Enqueue((sender, _encoder.Encode(packet.Packet, packet.Integrity)));
                        break;
                    case XModemAction.SendControl control:
                        _wire.Enqueue((sender, new[] { control.Value }));
                        break;
                    case XModemAction.DeliverDataBlock data:
                        Received.AddRange(data.Payload);
                        Apply(false, Receiver.Advance(new XModemEvent.DataBlockAccepted()));
                        break;
                }
            }
        }
    }
}
