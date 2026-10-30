using ModemKit.Testing;
using Ymodem.Protocol;

namespace ModemKit.IntegrationTests
{
    public sealed class YModemWireTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var mode in Enum.GetValues<YModemTransferMode>())
            {
                foreach (var blocks in Enum.GetValues<YModemBlockMode>())
                {
                    foreach (var chunkSize in new[] { 1, 17, 4096 })
                    {
                        yield return new object[] { mode, blocks, chunkSize };
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void TransfersMixedBatchThroughFragmentedAndCoalescedWire(
            YModemTransferMode mode, YModemBlockMode blocks, int chunkSize)
        {
            var fixture = new Fixture(mode, blocks, new[] { 0, 1, 128, 129, 1024, 1025, 33025 });
            fixture.Run(chunkSize);
            fixture.AssertPayload();
        }

        [Theory]
        [InlineData(YModemTransferMode.Crc)]
        [InlineData(YModemTransferMode.G)]
        public void EmptyBatchCompletes(YModemTransferMode mode)
        {
            var fixture = new Fixture(mode, YModemBlockMode.Dynamic1K, Array.Empty<int>());
            fixture.Run(1);
            fixture.AssertPayload();
        }

        [Fact]
        public void CoalescedStreamingReadCrossesBlockNumberRollover()
        {
            var fixture = new Fixture(YModemTransferMode.G, YModemBlockMode.Fixed128, new[] { 33025 });
            fixture.Run(65536);
            fixture.AssertPayload();
        }

        [Fact]
        public void CrcModeRecoversFromDamagedDataAndLostDataAck()
        {
            var fixture = new Fixture(YModemTransferMode.Crc, YModemBlockMode.Dynamic1K, new[] { 3000 });
            fixture.Link.Faults.Add(WireFault.Corrupt(WireDirection.SenderToReceiver, IsData, 25));
            var acknowledgements = 0;
            fixture.Link.Faults.Add(WireFault.Drop(
                WireDirection.ReceiverToSender,
                bytes => bytes.Length == 1 && bytes[0] == YModemControlBytes.Ack && ++acknowledgements == 2));
            fixture.Run(17);
            fixture.AssertPayload();
            Assert.All(fixture.Link.Faults, fault => Assert.Equal(0, fault.Remaining));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void LostHeaderHandshakeByteRecovers(int handshakeByte)
        {
            var fixture = new Fixture(YModemTransferMode.Crc, YModemBlockMode.Dynamic1K, new[] { 200 });
            var requests = 0;
            fixture.Link.Faults.Add(WireFault.Drop(
                WireDirection.ReceiverToSender,
                bytes => bytes.Length == 1 && (handshakeByte == 1
                    ? bytes[0] == YModemControlBytes.Ack
                    : bytes[0] == YModemControlBytes.CrcRequest && ++requests == 2)));
            fixture.Run(4096);
            fixture.AssertPayload();
        }

        private static bool IsData(byte[] bytes)
        {
            return bytes.Length > 3 && (bytes[0] == YModemControlBytes.Soh || bytes[0] == YModemControlBytes.Stx) && bytes[1] != 0;
        }

        private sealed class Fixture
        {
            private readonly YModemBatchSender _sender;
            private readonly YModemBatchReceiver _receiver;
            private readonly YModemStreamDecoder _decoder = new YModemStreamDecoder();
            private readonly YModemPacketEncoder _encoder = new YModemPacketEncoder();
            private readonly ProtocolActionQueue _actions = new ProtocolActionQueue();
            private readonly byte[][] _files;
            private readonly List<List<byte>> _received = new List<List<byte>>();
            private int _fileIndex = -1;
            private int _sourceOffset;

            public Fixture(YModemTransferMode mode, YModemBlockMode blocks, int[] lengths)
            {
                var options = new YModemTransferOptions(mode);
                _sender = new YModemBatchSender(new YModemBlockOptions(blocks), options);
                _receiver = new YModemBatchReceiver(options);
                _decoder.Integrity = _receiver.Integrity;
                _files = lengths.Select(length => Enumerable.Range(0, length).Select(i => (byte)(i * 17)).ToArray()).ToArray();
            }

            public InMemoryDuplexLink Link { get; } = new InMemoryDuplexLink();

            public void Run(int chunkSize)
            {
                ReceiverEvent(new YModemEvent.StartRequested());
                Link.Run(
                    bytes =>
                    {
                        foreach (var value in bytes)
                        {
                            SenderEvent(new YModemEvent.PeerByteReceived(value));
                        }
                    },
                    bytes => _decoder.Feed(
                        bytes,
                        () => _receiver.Snapshot.NextBlockNumber > 1 &&
                            (_receiver.Snapshot.Phase == YModemBatchReceiverPhase.WaitingDataPacketOrEot ||
                             _receiver.Snapshot.Phase == YModemBatchReceiverPhase.WaitingDataBlockDecision),
                        ReceiverEvent),
                    () => _sender.Snapshot.Phase == YModemBatchSenderPhase.Completed &&
                          _receiver.Snapshot.Phase == YModemBatchReceiverPhase.Completed,
                    () =>
                    {
                        _decoder.Reset();
                        SenderEvent(new YModemEvent.TimeoutElapsed());
                        if (Link.PendingBytes == 0)
                        {
                            ReceiverEvent(new YModemEvent.TimeoutElapsed());
                        }
                    },
                    chunkSize);
            }

            public void AssertPayload()
            {
                Assert.Equal(_files.Length, _received.Count);
                for (var index = 0; index < _files.Length; index++)
                {
                    Assert.Equal(_files[index], _received[index].ToArray());
                }
            }

            private void SenderEvent(YModemEvent protocolEvent)
            {
                _actions.Post(() =>
                {
                    foreach (var action in _sender.Advance(protocolEvent).Actions)
                    {
                        switch (action)
                        {
                            case YModemAction.RequestFileHeader:
                                _fileIndex++;
                                _sourceOffset = 0;
                                SenderEvent(_fileIndex == _files.Length
                                    ? new YModemEvent.NoMoreFiles()
                                    : new YModemEvent.FileHeaderReady(new YModemFileDescriptor("file" + _fileIndex + ".bin", _files[_fileIndex].Length)));
                                break;
                            case YModemAction.RequestDataBlock request:
                                var length = Math.Min(request.BlockSize, _files[_fileIndex].Length - _sourceOffset);
                                var data = _files[_fileIndex].Skip(_sourceOffset).Take(length).ToArray();
                                _sourceOffset += length;
                                SenderEvent(new YModemEvent.DataBlockReady(
                                    request.BlockNumber, data, data.Length, _sourceOffset == _files[_fileIndex].Length));
                                break;
                            case YModemAction.SendControl control:
                                Link.WriteFromSender(new[] { control.Value });
                                break;
                            case YModemAction.SendPacket send:
                                Link.WriteFromSender(_encoder.Encode(send.Packet, send.Integrity));
                                break;
                            case YModemAction.Fail fail:
                                Assert.Fail(fail.Reason + "\n" + Link.DescribeTrace());
                                break;
                            case YModemAction.Cancel cancel:
                                Assert.Fail(cancel.Reason + "\n" + Link.DescribeTrace());
                                break;
                        }
                    }
                });
            }

            private void ReceiverEvent(YModemEvent protocolEvent)
            {
                _actions.Post(() =>
                {
                    foreach (var action in _receiver.Advance(protocolEvent).Actions)
                    {
                        switch (action)
                        {
                            case YModemAction.OfferFileHeader:
                                _received.Add(new List<byte>());
                                ReceiverEvent(new YModemEvent.FileHeaderAccepted());
                                break;
                            case YModemAction.DeliverDataBlock deliver:
                                _received[_received.Count - 1].AddRange(deliver.Payload.Take(deliver.DataLength));
                                ReceiverEvent(new YModemEvent.DataBlockAccepted());
                                break;
                            case YModemAction.SendControl control:
                                Link.WriteFromReceiver(new[] { control.Value });
                                break;
                            case YModemAction.Fail fail:
                                Assert.Fail(fail.Reason + "\n" + Link.DescribeTrace());
                                break;
                            case YModemAction.Cancel cancel:
                                Assert.Fail(cancel.Reason + "\n" + Link.DescribeTrace());
                                break;
                        }
                    }
                });
            }
        }
    }
}
