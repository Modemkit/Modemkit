using ModemKit.Testing;
using Xmodem.Protocol;

namespace ModemKit.IntegrationTests
{
    public sealed class WXModemWireTests
    {
        [Fact]
        public void ReceiverTimerBeforeStartDoesNotConsumeHandshakeAttempts()
        {
            var receiver = new WXModemReceiver();
            for (var attempt = 0; attempt < 20; attempt++)
            {
                Assert.Empty(receiver.Advance(new XModemEvent.TimeoutElapsed()).Actions);
            }

            Assert.Equal(XModemPhase.Idle, receiver.Snapshot.Phase);
            var request = Assert.Single(receiver.Advance(new XModemEvent.StartRequested()).Actions.OfType<XModemAction.SendBytes>());
            Assert.Equal(new[] { WXModemControlBytes.WindowRequest }, request.Bytes);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        [InlineData(129, 1)]
        [InlineData(33025, 1)]
        [InlineData(33025, 11)]
        [InlineData(33025, 4096)]
        public void TransfersWindowedStreamAcrossModuloAndBlockRollover(int length, int chunkSize)
        {
            Transfer(length, chunkSize);
        }

        [Theory]
        [InlineData("drop")]
        [InlineData("corrupt")]
        [InlineData("truncate")]
        [InlineData("duplicate")]
        [InlineData("ack")]
        public void RecoversWindowFaultsThroughBoundarylessByteStream(string kind)
        {
            Func<byte[], bool> data = bytes => bytes.Length > 100 && bytes[0] == WXModemControlBytes.Syn;
            var fault = kind switch
            {
                "drop" => WireFault.Drop(WireDirection.SenderToReceiver, data),
                "corrupt" => WireFault.Corrupt(WireDirection.SenderToReceiver, data, 45, 0x80),
                "truncate" => WireFault.Truncate(WireDirection.SenderToReceiver, data, 12),
                "duplicate" => WireFault.Duplicate(WireDirection.SenderToReceiver, data),
                _ => WireFault.Drop(WireDirection.ReceiverToSender, bytes => bytes.Length == 2 && bytes[0] == XModemControlBytes.Ack),
            };
            Transfer(2000, 11, fault);
            Assert.Equal(0, fault.Remaining);
        }

        private static void Transfer(int length, int chunkSize, WireFault? fault = null)
        {
            var sender = new WXModemSender();
            var receiver = new WXModemReceiver();
            var source = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
            var received = new List<byte>();
            var queue = new ProtocolActionQueue();
            var link = new InMemoryDuplexLink();
            if (fault != null)
            {
                link.Faults.Add(fault);
            }

            void Advance(bool sending, XModemEvent protocolEvent)
            {
                queue.Post(() =>
                {
                    var step = sending ? sender.Advance(protocolEvent) : receiver.Advance(protocolEvent);
                    foreach (var action in step.Actions)
                    {
                        switch (action)
                        {
                            case XModemAction.SendBytes write:
                                if (sending)
                                {
                                    link.WriteFromSender(write.Bytes);
                                }
                                else
                                {
                                    link.WriteFromReceiver(write.Bytes);
                                }

                                break;
                            case XModemAction.RequestDataBlock request:
                                var offset = (request.BlockNumber - 1) * 128;
                                var block = source.Skip(offset).Take(request.BlockSize).ToArray();
                                Advance(true, new XModemEvent.DataBlockReady(
                                    request.BlockNumber, block, block.Length, offset + block.Length == source.Length));
                                break;
                            case XModemAction.DeliverDataBlock data:
                                received.AddRange(data.Payload);
                                Advance(false, new XModemEvent.DataBlockAccepted());
                                break;
                            case XModemAction.Fail fail:
                                Assert.Fail(fail.Reason + "\n" + link.DescribeTrace());
                                break;
                            case XModemAction.Cancel cancel:
                                Assert.Fail(cancel.Reason + "\n" + link.DescribeTrace());
                                break;
                        }
                    }
                });
            }

            Advance(false, new XModemEvent.StartRequested());
            link.Run(
                bytes => Advance(true, new XModemEvent.PeerBytesReceived(bytes)),
                bytes => Advance(false, new XModemEvent.PeerBytesReceived(bytes)),
                () => sender.Snapshot.Phase == XModemPhase.Completed && receiver.Snapshot.Phase == XModemPhase.Completed,
                () =>
                {
                    Advance(true, new XModemEvent.TimeoutElapsed());
                    if (link.PendingBytes == 0)
                    {
                        Advance(false, new XModemEvent.TimeoutElapsed());
                    }
                },
                chunkSize);
            Assert.Equal(source, received.Take(length).ToArray());
            Assert.InRange(received.Count - length, 0, 127);
            Assert.All(received.Skip(length), value => Assert.Equal(0x1a, value));
            Assert.Equal(WXModemMode.Windowed, sender.Snapshot.Mode);
            Assert.Equal(source.Length, sender.Snapshot.BytesTransferred);
        }
    }
}
