using ModemKit.Testing;
using Zmodem.Protocol;

namespace ModemKit.IntegrationTests
{
    public sealed class ZModemWireTests
    {
        [Theory]
        [InlineData(false, 1)]
        [InlineData(false, 17)]
        [InlineData(false, 4096)]
        [InlineData(true, 1)]
        [InlineData(true, 17)]
        [InlineData(true, 4096)]
        public void TransfersBatchWithEmptyFilesAndAllByteValues(bool crc32, int chunkSize)
        {
            var fixture = new Fixture(crc32, new[] { 0, 1, 128, 1024, 1025, 65537 });
            fixture.Run(chunkSize);
            fixture.AssertPayload();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ResumesFileAtUnalignedOffset(bool crc32)
        {
            var fixture = new Fixture(crc32, new[] { 5000 }, resumeOffset: 997);
            fixture.Run(7);
            fixture.AssertPayload();
            Assert.Equal(997U, fixture.FirstRequestedOffset);
        }

        [Fact]
        public void LostAcknowledgementRecoversByPositionWithoutDuplicateDelivery()
        {
            var fixture = new Fixture(true, new[] { 4000 });
            fixture.Link.Faults.Add(WireFault.Drop(WireDirection.ReceiverToSender, IsAck));
            fixture.Run(19);
            fixture.AssertPayload();
            Assert.Equal(0, fixture.Link.Faults[0].Remaining);
        }

        [Fact]
        public void DamagedDataRecoversFromLastCommittedPosition()
        {
            var fixture = new Fixture(true, new[] { 9000 });
            fixture.Link.Faults.Add(WireFault.Corrupt(
                WireDirection.SenderToReceiver, bytes => bytes.Length > 1000, 100, 0x40));
            fixture.Run(11);
            fixture.AssertPayload();
            Assert.Equal(0, fixture.Link.Faults[0].Remaining);
        }

        [Fact]
        public void EmptyBatchClosesWithFinishHandshake()
        {
            var fixture = new Fixture(true, Array.Empty<int>());
            fixture.Run(1);
            fixture.AssertPayload();
        }

        [Theory]
        [InlineData(ZModemProfile.Extended4K, 4096, ZModemFlowControl.StopAndWait)]
        [InlineData(ZModemProfile.Extended8K, 8192, ZModemFlowControl.StopAndWait)]
        [InlineData(ZModemProfile.ZedZap, 8192, ZModemFlowControl.StopAndWait)]
        [InlineData(ZModemProfile.ZedZip, 1024, ZModemFlowControl.StopAndWait)]
        [InlineData(ZModemProfile.Standard, 1024, ZModemFlowControl.Streaming)]
        [InlineData(ZModemProfile.Standard, 1024, ZModemFlowControl.Windowed)]
        [InlineData(ZModemProfile.ZedZap, 8192, ZModemFlowControl.Windowed)]
        public void TransfersLargePacketAndWindowBranches(ZModemProfile profile, int blockSize, ZModemFlowControl flowControl)
        {
            var options = new ZModemOptions(blockSize: blockSize, profile: profile, flowControl: flowControl);
            var fixture = new Fixture(true, new[] { 0, 8193, 65537 }, options: options);
            fixture.Run(4096);
            fixture.AssertPayload();
        }

        [Fact]
        public void TransfersNegotiatedVariableHeadersAndRunLengthEncoding()
        {
            var options = new ZModemOptions(useVariableHeaders: true, useRunLengthEncoding: true);
            var fixture = new Fixture(true, new[] { 0, 129, 65537 }, options: options);
            fixture.Run(17);
            fixture.AssertPayload();
        }

        [Theory]
        [InlineData(ZModemFlowControl.Streaming)]
        [InlineData(ZModemFlowControl.Windowed)]
        public void StreamingAndWindowBranchesRecoverDamagedData(ZModemFlowControl flowControl)
        {
            var fixture = new Fixture(true, new[] { 100000 }, options: new ZModemOptions(flowControl: flowControl));
            fixture.Link.Faults.Add(WireFault.Corrupt(
                WireDirection.SenderToReceiver, bytes => bytes.Length > 1000, 100, 0x40));
            fixture.Run(97);
            fixture.AssertPayload();
            Assert.Equal(0, fixture.Link.Faults[0].Remaining);
        }

        private static bool IsAck(byte[] bytes)
        {
            return bytes.Length > 6 && bytes[0] == 0x2a && bytes[1] == 0x2a && bytes[4] == '0' && bytes[5] == '3';
        }

        private sealed class Fixture
        {
            private readonly ZModemSender _sender;
            private readonly ZModemReceiver _receiver;
            private readonly ProtocolActionQueue _actions = new ProtocolActionQueue();
            private readonly byte[][] _files;
            private readonly List<List<byte>> _received = new List<List<byte>>();
            private readonly uint _resumeOffset;
            private int _fileIndex = -1;
            private int _completedFiles;

            public Fixture(bool crc32, int[] lengths, uint resumeOffset = 0, ZModemOptions? options = null)
            {
                _sender = new ZModemSender(options ?? new ZModemOptions(useCrc32: crc32));
                _receiver = new ZModemReceiver(options ?? new ZModemOptions(useCrc32: crc32));
                _files = lengths.Select(length => Enumerable.Range(0, length).Select(i => (byte)(i * 17)).ToArray()).ToArray();
                _resumeOffset = resumeOffset;
            }

            public InMemoryDuplexLink Link { get; } = new InMemoryDuplexLink();

            public int Timeouts { get; private set; }

            public uint? FirstRequestedOffset { get; private set; }

            public void Run(int chunkSize)
            {
                SenderEvent(new ZModemEvent.StartRequested());
                ReceiverEvent(new ZModemEvent.StartRequested());
                Link.Run(
                    bytes => SenderEvent(new ZModemEvent.PeerBytesReceived(bytes)),
                    bytes => ReceiverEvent(new ZModemEvent.PeerBytesReceived(bytes)),
                    () => _sender.Snapshot.Phase == ZModemPhase.Completed && _receiver.Snapshot.Phase == ZModemPhase.Completed,
                    () =>
                    {
                        Timeouts++;
                        SenderEvent(new ZModemEvent.TimeoutElapsed());
                        if (Link.PendingBytes == 0)
                        {
                            ReceiverEvent(new ZModemEvent.TimeoutElapsed());
                        }
                    },
                    chunkSize,
                    maxSteps: 200000);
            }

            public void AssertPayload()
            {
                Assert.Equal(_files.Length, _received.Count);
                Assert.Equal(_files.Length, _completedFiles);
                for (var index = 0; index < _files.Length; index++)
                {
                    Assert.Equal(_files[index], _received[index].ToArray());
                }
            }

            private void SenderEvent(ZModemEvent protocolEvent)
            {
                _actions.Post(() =>
                {
                    foreach (var action in _sender.Advance(protocolEvent).Actions)
                    {
                        switch (action)
                        {
                            case ZModemAction.RequestFileHeader:
                                _fileIndex++;
                                SenderEvent(_fileIndex == _files.Length
                                    ? new ZModemEvent.NoMoreFiles()
                                    : new ZModemEvent.FileHeaderReady(new ZModemFileDescriptor("file" + _fileIndex + ".bin", (uint)_files[_fileIndex].Length)));
                                break;
                            case ZModemAction.RequestDataBlock request:
                                FirstRequestedOffset ??= request.Offset;
                                SenderEvent(new ZModemEvent.DataBlockReady(
                                    request.Offset,
                                    _files[_fileIndex].Skip((int)request.Offset).Take(request.MaxLength).ToArray()));
                                break;
                            case ZModemAction.SendBytes send:
                                Link.WriteFromSender(send.Bytes);
                                break;
                            case ZModemAction.Fail fail:
                                Assert.Fail(fail.Reason + "\n" + Link.DescribeTrace());
                                break;
                            case ZModemAction.Cancel cancel:
                                Assert.Fail(cancel.Reason + "\n" + Link.DescribeTrace());
                                break;
                        }
                    }
                });
            }

            private void ReceiverEvent(ZModemEvent protocolEvent)
            {
                _actions.Post(() =>
                {
                    foreach (var action in _receiver.Advance(protocolEvent).Actions)
                    {
                        switch (action)
                        {
                            case ZModemAction.OfferFileHeader:
                                _received.Add(_files[_received.Count].Take((int)_resumeOffset).ToList());
                                ReceiverEvent(new ZModemEvent.FileAccepted(_resumeOffset));
                                break;
                            case ZModemAction.DeliverDataBlock deliver:
                                Assert.Equal((uint)_received[_received.Count - 1].Count, deliver.Offset);
                                _received[_received.Count - 1].AddRange(deliver.Payload);
                                ReceiverEvent(new ZModemEvent.DataBlockAccepted());
                                break;
                            case ZModemAction.FileCompleted completed:
                                Assert.Equal((uint)_received[_received.Count - 1].Count, completed.Length);
                                _completedFiles++;
                                break;
                            case ZModemAction.SendBytes send:
                                Link.WriteFromReceiver(send.Bytes);
                                break;
                            case ZModemAction.Fail fail:
                                Assert.Fail(fail.Reason + "\n" + Link.DescribeTrace());
                                break;
                            case ZModemAction.Cancel cancel:
                                Assert.Fail(cancel.Reason + "\n" + Link.DescribeTrace());
                                break;
                        }
                    }
                });
            }
        }
    }
}
