using LegacyModem.Protocol;
using ModemKit.Testing;

namespace ModemKit.IntegrationTests
{
    public sealed class LegacyModemWireTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var variant in Enum.GetValues<LegacyModemVariant>())
            {
                foreach (var integrity in Enum.GetValues<LegacyModemIntegrity>())
                {
                    foreach (var chunkSize in new[] { 1, 19, 4096 })
                    {
                        yield return new object[] { variant, integrity, chunkSize };
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void TransfersLegacyBatchWithBinaryBytesEmptyFilesAndRollover(
            LegacyModemVariant variant, LegacyModemIntegrity integrity, int chunkSize)
        {
            var fixture = new Fixture(new LegacyModemOptions(variant, integrity), new[] { 0, 1, 129, 33025 });
            fixture.Run(chunkSize);
            fixture.AssertPayload();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SeaLinkResumesFromSpecifiedBlock(bool overdrive)
        {
            var fixture = new Fixture(
                new LegacyModemOptions(overdrive: overdrive),
                new[] { 10000 },
                restartBlock: 27);
            fixture.Run(17);
            fixture.AssertPayload();
        }

        [Fact]
        public void SeaLinkOverdriveStreamsMultipleFiles()
        {
            var fixture = new Fixture(new LegacyModemOptions(overdrive: true), new[] { 0, 129, 33025 });
            fixture.Run(4096);
            fixture.AssertPayload();
        }

        [Theory]
        [InlineData(LegacyModemVariant.Modem7)]
        [InlineData(LegacyModemVariant.Telink)]
        [InlineData(LegacyModemVariant.SeaLink)]
        public void DamagedDataRecoversWithoutDuplicateDelivery(LegacyModemVariant variant)
        {
            var fixture = new Fixture(new LegacyModemOptions(variant), new[] { 2000 });
            fixture.Link.Faults.Add(WireFault.Corrupt(
                WireDirection.SenderToReceiver,
                bytes => bytes.Length >= 132 && bytes[0] == 1 && bytes[1] == 1,
                50));
            fixture.Run(11);
            fixture.AssertPayload();
            Assert.Equal(0, fixture.Link.Faults[0].Remaining);
        }

        private sealed class Fixture
        {
            private readonly LegacyModemSender _sender;
            private readonly LegacyModemReceiver _receiver;
            private readonly ProtocolActionQueue _actions = new ProtocolActionQueue();
            private readonly byte[][] _files;
            private readonly List<List<byte>> _received = new List<List<byte>>();
            private readonly int _restartBlock;
            private readonly bool _padded;
            private int _fileIndex = -1;
            private int _completedFiles;

            public Fixture(LegacyModemOptions options, int[] lengths, int restartBlock = 1)
            {
                _sender = new LegacyModemSender(options);
                _receiver = new LegacyModemReceiver(options);
                _padded = options.Variant == LegacyModemVariant.Modem7;
                _files = lengths.Select(length => Enumerable.Range(0, length).Select(i => (byte)(i * 17)).ToArray()).ToArray();
                _restartBlock = restartBlock;
            }

            public InMemoryDuplexLink Link { get; } = new InMemoryDuplexLink();

            public void Run(int chunkSize)
            {
                ReceiverEvent(new LegacyModemEvent.StartRequested());
                Link.Run(
                    bytes => SenderEvent(new LegacyModemEvent.BytesReceived(bytes)),
                    bytes => ReceiverEvent(new LegacyModemEvent.BytesReceived(bytes)),
                    () => _sender.Snapshot.Phase == LegacyModemPhase.Completed &&
                          _receiver.Snapshot.Phase == LegacyModemPhase.Completed,
                    () =>
                    {
                        SenderEvent(new LegacyModemEvent.TimeoutElapsed());
                        if (Link.PendingBytes == 0)
                        {
                            ReceiverEvent(new LegacyModemEvent.TimeoutElapsed());
                        }
                    },
                    chunkSize,
                    maxSteps: 300000);
            }

            public void AssertPayload()
            {
                Assert.Equal(_files.Length, _received.Count);
                Assert.Equal(_files.Length, _completedFiles);
                for (var index = 0; index < _files.Length; index++)
                {
                    Assert.Equal(_files[index], _received[index].Take(_files[index].Length).ToArray());
                    if (_padded)
                    {
                        Assert.InRange(_received[index].Count - _files[index].Length, 0, 127);
                        Assert.All(_received[index].Skip(_files[index].Length), value => Assert.Equal(0x1a, value));
                    }
                    else
                    {
                        Assert.Equal(_files[index].Length, _received[index].Count);
                    }
                }
            }

            private void SenderEvent(LegacyModemEvent protocolEvent)
            {
                _actions.Post(() =>
                {
                    foreach (var action in _sender.Advance(protocolEvent).Actions)
                    {
                        switch (action)
                        {
                            case LegacyModemAction.RequestFileHeader:
                                _fileIndex++;
                                SenderEvent(_fileIndex == _files.Length
                                    ? new LegacyModemEvent.NoMoreFiles()
                                    : new LegacyModemEvent.FileHeaderReady(new LegacyModemFileDescriptor("F" + _fileIndex + ".BIN", _files[_fileIndex].Length)));
                                break;
                            case LegacyModemAction.RequestDataBlock request:
                                SenderEvent(new LegacyModemEvent.DataBlockReady(
                                    request.BlockNumber,
                                    _files[_fileIndex].Skip((int)request.Offset).Take(request.DataLength).ToArray()));
                                break;
                            case LegacyModemAction.SendBytes send:
                                Link.WriteFromSender(send.Bytes);
                                break;
                            case LegacyModemAction.PurgeOutput:
                                Link.DiscardPending(WireDirection.SenderToReceiver);
                                break;
                            case LegacyModemAction.Fail fail:
                                Assert.Fail(fail.Reason + "\n" + Link.DescribeTrace());
                                break;
                            case LegacyModemAction.Cancel cancel:
                                Assert.Fail(cancel.Reason + "\n" + Link.DescribeTrace());
                                break;
                        }
                    }
                });
            }

            private void ReceiverEvent(LegacyModemEvent protocolEvent)
            {
                _actions.Post(() =>
                {
                    foreach (var action in _receiver.Advance(protocolEvent).Actions)
                    {
                        switch (action)
                        {
                            case LegacyModemAction.OfferFileHeader:
                                _received.Add(_files[_received.Count].Take((_restartBlock - 1) * 128).ToList());
                                ReceiverEvent(new LegacyModemEvent.FileHeaderAccepted(_restartBlock));
                                break;
                            case LegacyModemAction.DeliverDataBlock deliver:
                                Assert.Equal(_received[^1].Count, deliver.Offset);
                                _received[^1].AddRange(deliver.Data);
                                ReceiverEvent(new LegacyModemEvent.DataBlockAccepted());
                                break;
                            case LegacyModemAction.FileCompleted:
                                _completedFiles++;
                                break;
                            case LegacyModemAction.SendBytes send:
                                Link.WriteFromReceiver(send.Bytes);
                                break;
                            case LegacyModemAction.PurgeOutput:
                                Link.DiscardPending(WireDirection.ReceiverToSender);
                                break;
                            case LegacyModemAction.Fail fail:
                                Assert.Fail(fail.Reason + "\n" + Link.DescribeTrace());
                                break;
                            case LegacyModemAction.Cancel cancel:
                                Assert.Fail(cancel.Reason + "\n" + Link.DescribeTrace());
                                break;
                        }
                    }
                });
            }
        }
    }
}
