using Jmodem.Protocol;
using ModemKit.Testing;

namespace ModemKit.IntegrationTests
{
    public sealed class JModemWireTests
    {
        [Theory]
        [InlineData(0, false, 1)]
        [InlineData(1, false, 1)]
        [InlineData(512, false, 1)]
        [InlineData(513, true, 17)]
        [InlineData(8192, true, 1)]
        [InlineData(8193, false, 17)]
        [InlineData(65537, true, 4096)]
        [InlineData(65537, false, 4096)]
        [InlineData(2097153, false, 4096)]
        public void TransfersAdaptiveFramesWithExactFinalLength(int length, bool compressible, int chunkSize)
        {
            Transfer(length, compressible, chunkSize);
        }

        [Theory]
        [InlineData("drop")]
        [InlineData("corrupt")]
        [InlineData("truncate")]
        [InlineData("ack")]
        public void RecoversCorruptionLossAndPartialFrames(string kind)
        {
            Func<byte[], bool> frame = bytes => bytes.Length >= 6;
            var fault = kind switch
            {
                "drop" => WireFault.Drop(WireDirection.SenderToReceiver, frame),
                "corrupt" => WireFault.Corrupt(WireDirection.SenderToReceiver, frame, 5, 0x80),
                "truncate" => WireFault.Truncate(WireDirection.SenderToReceiver, frame, 12),
                _ => WireFault.Drop(WireDirection.ReceiverToSender, bytes => bytes.Length == 1 && bytes[0] == JModemControlBytes.Ack),
            };
            Transfer(17000, false, 11, fault);
            Assert.Equal(0, fault.Remaining);
        }

        [Fact]
        public void LostStartupAcknowledgementRecoversForCompressedFirstFrame()
        {
            var fault = WireFault.Drop(
                WireDirection.SenderToReceiver,
                bytes => bytes.Length == 1 && bytes[0] == JModemControlBytes.Ack);
            Transfer(17000, true, 11, fault);
            Assert.Equal(0, fault.Remaining);
        }

        [Fact]
        public void FinalAcknowledgementLossAndDamagedRetransmissionStillComplete()
        {
            var frameNumber = 0;
            var faults = new[]
            {
                WireFault.Drop(WireDirection.ReceiverToSender, bytes => bytes.Length == 1 && bytes[0] == JModemControlBytes.Ack),
                WireFault.Corrupt(WireDirection.SenderToReceiver, bytes => bytes.Length >= 6 && ++frameNumber == 2, 5, 0x80),
            };
            Transfer(129, false, 7, extraFaults: faults);
            Assert.All(faults, fault => Assert.Equal(0, fault.Remaining));
        }

        private static void Transfer(int length, bool compressible, int chunkSize, WireFault? fault = null, WireFault[]? extraFaults = null)
        {
            var sender = new JModemSender();
            var receiver = new JModemReceiver();
            var source = Enumerable.Range(0, length).Select(i => (byte)(compressible ? (i / 128) % 8 : i)).ToArray();
            var received = new List<byte>();
            var offset = 0;
            var queue = new ProtocolActionQueue();
            var link = new InMemoryDuplexLink();
            if (fault != null)
            {
                link.Faults.Add(fault);
            }

            foreach (var extraFault in extraFaults ?? Array.Empty<WireFault>())
            {
                link.Faults.Add(extraFault);
            }

            void Advance(bool sending, JModemEvent protocolEvent)
            {
                queue.Post(() =>
                {
                    foreach (var action in (sending ? sender.Advance(protocolEvent) : receiver.Advance(protocolEvent)).Actions)
                    {
                        switch (action)
                        {
                            case JModemAction.SendBytes send:
                                if (sending)
                                {
                                    link.WriteFromSender(send.Bytes);
                                }
                                else
                                {
                                    link.WriteFromReceiver(send.Bytes);
                                }

                                break;
                            case JModemAction.RequestDataBlock request:
                                var block = source.Skip(offset).Take(request.BlockSize).ToArray();
                                offset += block.Length;
                                Advance(true, new JModemEvent.DataBlockReady(
                                    request.BlockNumber, block, block.Length, offset == source.Length));
                                break;
                            case JModemAction.DeliverDataBlock deliver:
                                received.AddRange(deliver.Payload);
                                Advance(false, new JModemEvent.DataBlockAccepted());
                                break;
                            case JModemAction.DiscardInput:
                                link.DiscardPending(sending ? WireDirection.ReceiverToSender : WireDirection.SenderToReceiver);
                                break;
                            case JModemAction.Fail fail:
                                Assert.Fail(fail.Reason + "\n" + link.DescribeTrace());
                                break;
                            case JModemAction.Cancel cancel:
                                Assert.Fail(cancel.Reason + "\n" + link.DescribeTrace());
                                break;
                        }
                    }
                });
            }

            Advance(false, new JModemEvent.StartRequested());
            link.Run(
                bytes => Advance(true, new JModemEvent.PeerBytesReceived(bytes)),
                bytes => Advance(false, new JModemEvent.PeerBytesReceived(bytes)),
                () => sender.Snapshot.Phase == JModemPhase.Completed && receiver.Snapshot.Phase == JModemPhase.Completed,
                () =>
                {
                    if (receiver.Snapshot.Phase == JModemPhase.DrainingInput)
                    {
                        Advance(false, new JModemEvent.TimeoutElapsed());
                    }
                    else
                    {
                        Advance(true, new JModemEvent.TimeoutElapsed());
                        if (link.PendingBytes == 0)
                        {
                            Advance(false, new JModemEvent.TimeoutElapsed());
                        }
                    }
                },
                chunkSize,
                maxSteps: 300000);
            Assert.Equal(source, received.ToArray());
            Assert.Equal(source.Length, sender.Snapshot.BytesTransferred);
            Assert.Equal(source.Length, receiver.Snapshot.BytesTransferred);
        }
    }
}
