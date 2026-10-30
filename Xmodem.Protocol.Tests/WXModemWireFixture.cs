namespace Xmodem.Protocol.Tests
{
    internal sealed class WXModemWireFixture
    {
        private readonly byte[] _source;
        private readonly Queue<(bool ToReceiver, byte[] Bytes)> _wire = new();

        internal WXModemWireFixture(byte[] source, int fragmentSize = 7)
        {
            _source = source;
            FragmentSize = fragmentSize;
        }

        internal WXModemSender Sender { get; } = new();

        internal WXModemReceiver Receiver { get; } = new();

        internal int FragmentSize { get; }

        internal List<byte> Received { get; } = new();

        internal List<(bool ToReceiver, byte[] Bytes)> Transcript { get; } = new();

        internal Func<bool, byte[], byte[]?>? MutateWire { get; set; }

        internal void Run(int maxSteps = 100000)
        {
            Apply(false, Receiver.Advance(new XModemEvent.StartRequested()));
            for (var step = 0; step < maxSteps; step++)
            {
                if (_wire.Count == 0)
                {
                    if (Sender.Snapshot.Phase == XModemPhase.Completed && Receiver.Snapshot.Phase == XModemPhase.Completed)
                    {
                        return;
                    }

                    if (Sender.Snapshot.Phase == XModemPhase.WaitingReceiverRequest)
                    {
                        Apply(false, Receiver.Advance(new XModemEvent.TimeoutElapsed()));
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

                for (var offset = 0; offset < bytes.Length; offset += FragmentSize)
                {
                    var incoming = new XModemEvent.PeerBytesReceived(bytes.Skip(offset).Take(FragmentSize).ToArray());
                    Apply(!item.ToReceiver, item.ToReceiver ? Receiver.Advance(incoming) : Sender.Advance(incoming));
                }
            }

            throw new InvalidOperationException("WXMODEM fixture exceeded its deterministic step budget.");
        }

        private void Apply(bool sender, WXModemStepResult result)
        {
            Assert.Empty(result.Actions.OfType<XModemAction.Fail>());
            Assert.Empty(result.Actions.OfType<XModemAction.Cancel>());
            foreach (var action in result.Actions)
            {
                switch (action)
                {
                    case XModemAction.SendBytes send:
                        _wire.Enqueue((sender, send.Bytes));
                        break;
                    case XModemAction.RequestDataBlock request:
                        var offset = (request.BlockNumber - 1) * 128;
                        var count = Math.Min(128, _source.Length - offset);
                        Apply(true, Sender.Advance(new XModemEvent.DataBlockReady(
                            request.BlockNumber, _source.AsSpan(offset, count).ToArray(), count, offset + count == _source.Length)));
                        break;
                    case XModemAction.DeliverDataBlock delivery:
                        Received.AddRange(delivery.Payload);
                        Apply(false, Receiver.Advance(new XModemEvent.DataBlockAccepted()));
                        break;
                }
            }
        }
    }
}
