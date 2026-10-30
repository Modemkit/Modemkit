using ModemKit.Testing;
using Xmodem.Protocol;

namespace ModemKit.IntegrationTests
{
    public sealed class XModemWireTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var mode in Enum.GetValues<XModemMode>().Distinct())
            {
                foreach (var length in new[] { 0, 1, 127, 128, 129, 1023, 1024, 1025, 33025 })
                {
                    yield return new object[] { mode, length, length % 2 == 0 ? 1 : 4096 };
                }
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void TransfersEncodedBytesIncludingWrapAndPadding(XModemMode mode, int length, int chunkSize)
        {
            var fixture = new Fixture(mode, length);
            fixture.Run(chunkSize);
            fixture.AssertPayload();
        }

        [Theory]
        [InlineData(XModemMode.Checksum)]
        [InlineData(XModemMode.Crc)]
        [InlineData(XModemMode.OneK)]
        public void CorruptFrameIsRetriedWithoutDuplicateDelivery(XModemMode mode)
        {
            var fixture = new Fixture(mode, 2400);
            fixture.Link.Faults.Add(WireFault.Corrupt(
                WireDirection.SenderToReceiver, IsData, 10, 0x40));
            fixture.Run(7);
            fixture.AssertPayload();
            Assert.Equal(0, fixture.Link.Faults[0].Remaining);
        }

        [Fact]
        public void LostAcknowledgementAndTruncatedFrameRecoverOnVirtualTimeout()
        {
            var fixture = new Fixture(XModemMode.Crc, 1300);
            fixture.Link.Faults.Add(WireFault.Truncate(WireDirection.SenderToReceiver, IsData, 12));
            fixture.Link.Faults.Add(WireFault.Drop(
                WireDirection.ReceiverToSender, b => b.Length == 1 && b[0] == XModemControlBytes.Ack));
            fixture.Run(3);
            fixture.AssertPayload();
            Assert.True(fixture.Timeouts >= 2);
        }

        private static bool IsData(byte[] bytes)
        {
            return bytes.Length > 3 && (bytes[0] == XModemControlBytes.Soh || bytes[0] == XModemControlBytes.Stx);
        }

        private sealed class Fixture
        {
            private readonly XModemSender _sender;
            private readonly XModemReceiver _receiver;
            private readonly XModemStreamDecoder _decoder;
            private readonly XModemPacketEncoder _encoder = new XModemPacketEncoder();
            private readonly ProtocolActionQueue _actions = new ProtocolActionQueue();
            private readonly byte[] _source;
            private readonly List<byte> _received = new List<byte>();
            private int _sourceOffset;

            public Fixture(XModemMode mode, int length)
            {
                var options = new XModemOptions(mode);
                _sender = new XModemSender(options);
                _receiver = new XModemReceiver(options);
                _decoder = new XModemStreamDecoder(mode == XModemMode.Checksum ? XModemIntegrity.Checksum : XModemIntegrity.Crc16);
                _source = Enumerable.Range(0, length).Select(i => (byte)(i * 31)).ToArray();
            }

            public InMemoryDuplexLink Link { get; } = new InMemoryDuplexLink();

            public int Timeouts { get; private set; }

            public void Run(int chunkSize)
            {
                ReceiverEvent(new XModemEvent.StartRequested());
                Link.Run(
                    bytes =>
                    {
                        foreach (var value in bytes)
                        {
                            SenderEvent(new XModemEvent.PeerByteReceived(value));
                        }
                    },
                    bytes =>
                    {
                        foreach (var protocolEvent in _decoder.Feed(bytes))
                        {
                            ReceiverEvent(protocolEvent);
                        }
                    },
                    () => _sender.Snapshot.Phase == XModemPhase.Completed && _receiver.Snapshot.Phase == XModemPhase.Completed,
                    () =>
                    {
                        Timeouts++;

                        // Clear a partial frame before the retransmission reaches the sink.
                        _decoder.OnTimeout();
                        SenderEvent(new XModemEvent.TimeoutElapsed());
                        if (Link.PendingBytes == 0)
                        {
                            ReceiverEvent(new XModemEvent.TimeoutElapsed());
                        }
                    },
                    chunkSize);
            }

            public void AssertPayload()
            {
                Assert.Equal(_source, _received.Take(_source.Length).ToArray());
                Assert.All(_received.Skip(_source.Length), value => Assert.Equal(XModemControlBytes.Padding, value));
                Assert.Equal(_source.Length, _sender.Snapshot.BytesTransferred);
                Assert.InRange(_received.Count - _source.Length, 0, 1023);
            }

            private void SenderEvent(XModemEvent protocolEvent)
            {
                _actions.Post(() =>
                {
                    foreach (var action in _sender.Advance(protocolEvent).Actions)
                    {
                        switch (action)
                        {
                            case XModemAction.SendControl control:
                                Link.WriteFromSender(new[] { control.Value });
                                break;
                            case XModemAction.SendPacket send:
                                Link.WriteFromSender(_encoder.Encode(send.Packet, send.Integrity));
                                break;
                            case XModemAction.RequestDataBlock request:
                                var length = Math.Min(request.BlockSize, _source.Length - _sourceOffset);
                                var data = _source.Skip(_sourceOffset).Take(length).ToArray();
                                _sourceOffset += length;
                                SenderEvent(new XModemEvent.DataBlockReady(request.BlockNumber, data, data.Length, _sourceOffset == _source.Length));
                                break;
                            case XModemAction.Fail fail:
                                Assert.Fail(fail.Reason + "\n" + Link.DescribeTrace());
                                break;
                            case XModemAction.Cancel cancel:
                                Assert.Fail(cancel.Reason + "\n" + Link.DescribeTrace());
                                break;
                        }
                    }
                });
            }

            private void ReceiverEvent(XModemEvent protocolEvent)
            {
                _actions.Post(() =>
                {
                    foreach (var action in _receiver.Advance(protocolEvent).Actions)
                    {
                        switch (action)
                        {
                            case XModemAction.SendControl control:
                                Link.WriteFromReceiver(new[] { control.Value });
                                break;
                            case XModemAction.DeliverDataBlock deliver:
                                _received.AddRange(deliver.Payload);
                                ReceiverEvent(new XModemEvent.DataBlockAccepted());
                                break;
                            case XModemAction.Fail fail:
                                Assert.Fail(fail.Reason + "\n" + Link.DescribeTrace());
                                break;
                            case XModemAction.Cancel cancel:
                                Assert.Fail(cancel.Reason + "\n" + Link.DescribeTrace());
                                break;
                        }
                    }
                });
            }
        }
    }
}
