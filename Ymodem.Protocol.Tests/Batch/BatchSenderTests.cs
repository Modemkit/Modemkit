using Xunit;

namespace Ymodem.Protocol.Tests
{
    public sealed partial class BatchSenderTests
    {
        [Fact]
        public void BatchSenderKeepsLogicalBlockNumberAfterBlock255()
        {
            var sender = new YModemBatchSender();
            var payload = new byte[1024];

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("large.bin", (256L * 1024) + 1)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));

            YModemAction.RequestDataBlock requestData = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest)).Actions));
            Assert.Equal(1, requestData.BlockNumber);

            for (var i = 1; i <= 255; i++)
            {
                Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.DataBlockReady(i, payload, payload.Length, false)).Actions));

                if (i < 255)
                {
                    requestData = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack)).Actions));
                    Assert.Equal(i + 1, requestData.BlockNumber);
                }
            }

            requestData = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack)).Actions));
            Assert.Equal(256, requestData.BlockNumber);

            YModemAction.SendPacket sendWrappedData = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.DataBlockReady(256, payload, 1, true)).Actions));
            YModemPacket.Data packet = Assert.IsType<YModemPacket.Data>(sendWrappedData.Packet);
            var encodedBytes = new YModemPacketEncoder().Encode(packet);

            Assert.Equal(256, packet.BlockNumber);
            Assert.Equal(1, packet.DataLength);
            Assert.Equal(0, encodedBytes[1]);
        }

        [Fact]
        public void BatchSenderRequests128ByteTailBlockAfterFull1KBlock()
        {
            var sender = new YModemBatchSender();
            var fullPayload = new byte[1024];

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("tail.bin", 1025)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));

            YModemAction.RequestDataBlock firstRequest = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest)).Actions));
            Assert.Equal(1024, firstRequest.BlockSize);

            sender.Advance(new YModemEvent.DataBlockReady(1, fullPayload, fullPayload.Length, false));

            YModemAction.RequestDataBlock tailRequest = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack)).Actions));
            Assert.Equal(2, tailRequest.BlockNumber);
            Assert.Equal(128, tailRequest.BlockSize);
        }

        [Fact]
        public void BatchSenderUses1KPacketForTailLargerThan128Bytes()
        {
            var sender = new YModemBatchSender();
            var fullPayload = new byte[1024];
            var tailPayload = new byte[1024];

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("tail.bin", 1537)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));

            YModemAction.RequestDataBlock firstRequest = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest)).Actions));
            Assert.Equal(1024, firstRequest.BlockSize);

            sender.Advance(new YModemEvent.DataBlockReady(1, fullPayload, fullPayload.Length, false));

            YModemAction.RequestDataBlock tailRequest = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack)).Actions));
            Assert.Equal(2, tailRequest.BlockNumber);
            Assert.Equal(1024, tailRequest.BlockSize);

            YModemAction.SendPacket sendTail = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.DataBlockReady(2, tailPayload, 513, true)).Actions));
            YModemPacket.Data tailPacket = Assert.IsType<YModemPacket.Data>(sendTail.Packet);
            var encodedBytes = new YModemPacketEncoder().Encode(tailPacket);

            Assert.Equal(1024, tailPacket.BlockSize);
            Assert.Equal(513, tailPacket.DataLength);
            Assert.Equal(YModemControlBytes.Stx, encodedBytes[0]);
            Assert.Equal(1024 + 5, encodedBytes.Length);
        }

        [Fact]
        public void BatchSenderRequests128ByteFirstDataBlockForSmallFiles()
        {
            var sender = new YModemBatchSender();

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("small.bin", 3)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));

            YModemAction.RequestDataBlock requestData = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest)).Actions));
            Assert.Equal(1, requestData.BlockNumber);
            Assert.Equal(128, requestData.BlockSize);
        }

        [Fact]
        public void BatchSenderFixed128ModeRequests128ByteBlocksForLargeFiles()
        {
            var sender = new YModemBatchSender(YModemBlockMode.Fixed128);

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("large.bin", 2048)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));

            YModemAction.RequestDataBlock requestData = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest)).Actions));

            Assert.Equal(1, requestData.BlockNumber);
            Assert.Equal(128, requestData.BlockSize);
        }

        [Fact]
        public void BatchSenderFixed128ModeKeepsRequesting128ByteBlocksAfterAcknowledgedData()
        {
            var sender = new YModemBatchSender(YModemBlockMode.Fixed128);
            var payload = new byte[128];

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("large.bin", 2048)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.DataBlockReady(1, payload, payload.Length, false));

            YModemAction.RequestDataBlock nextRequest = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack)).Actions));

            Assert.Equal(2, nextRequest.BlockNumber);
            Assert.Equal(128, nextRequest.BlockSize);
        }

        [Theory]
        [InlineData(0, 128)]
        [InlineData(1, 128)]
        [InlineData(127, 128)]
        [InlineData(128, 128)]
        [InlineData(129, 1024)]
        [InlineData(1024, 1024)]
        [InlineData(1025, 1024)]
        public void BatchSenderRequestsExpectedFirstDataBlockSizeForFileSize(long fileSize, int expectedBlockSize)
        {
            var sender = new YModemBatchSender();

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("boundary.bin", fileSize)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));

            YModemAction.RequestDataBlock requestData = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest)).Actions));
            Assert.Equal(1, requestData.BlockNumber);
            Assert.Equal(expectedBlockSize, requestData.BlockSize);
        }

        [Theory]
        [InlineData(1, 128)]
        [InlineData(127, 128)]
        [InlineData(128, 128)]
        [InlineData(129, 1024)]
        [InlineData(1024, 1024)]
        public void BatchSenderRequestsExpectedTailBlockSizeAfterFull1KBlock(int tailSize, int expectedBlockSize)
        {
            var sender = new YModemBatchSender();
            var firstBlock = new byte[1024];

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("tail.bin", 1024 + tailSize)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.DataBlockReady(1, firstBlock, firstBlock.Length, false));

            YModemAction.RequestDataBlock tailRequest = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack)).Actions));

            Assert.Equal(2, tailRequest.BlockNumber);
            Assert.Equal(expectedBlockSize, tailRequest.BlockSize);
        }

        [Fact]
        public void BatchSenderHeaderUses128BytePacket()
        {
            var sender = new YModemBatchSender();
            var file = new YModemFileDescriptor("header.bin", 123);

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));

            YModemAction.SendPacket sendHeader = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.FileHeaderReady(file)).Actions));
            YModemPacket.Header header = Assert.IsType<YModemPacket.Header>(sendHeader.Packet);
            var bytes = new YModemPacketEncoder().Encode(header);

            Assert.Equal(YModemControlBytes.Soh, bytes[0]);
            Assert.Equal(128 + 5, bytes.Length);
        }

        [Fact]
        public void BatchSenderUses128ByteHeaderPacketWhenMetadataIsExactly128Bytes()
        {
            var sender = new YModemBatchSender();
            var file = new YModemFileDescriptor(new string('a', 119) + ".bin", 123);

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));

            YModemAction.SendPacket sendHeader = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.FileHeaderReady(file)).Actions));
            YModemPacket.Header header = Assert.IsType<YModemPacket.Header>(sendHeader.Packet);
            var bytes = new YModemPacketEncoder().Encode(header);

            Assert.Equal(128, header.BlockSize);
            Assert.Equal(YModemControlBytes.Soh, bytes[0]);
            Assert.Equal(128 + 5, bytes.Length);
        }

        [Fact]
        public void BatchSenderUses1KHeaderPacketWhenMetadataExceeds128Bytes()
        {
            var sender = new YModemBatchSender();
            var file = new YModemFileDescriptor(new string('a', 120) + ".bin", 123);

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));

            YModemAction.SendPacket sendHeader = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.FileHeaderReady(file)).Actions));
            YModemPacket.Header header = Assert.IsType<YModemPacket.Header>(sendHeader.Packet);
            var bytes = new YModemPacketEncoder().Encode(header);

            Assert.Equal(1024, header.BlockSize);
            Assert.Equal(YModemControlBytes.Stx, bytes[0]);
            Assert.Equal(1024 + 5, bytes.Length);
        }

        [Fact]
        public void BatchSenderHeaderWithNonAsciiFileNameFailsWhenEncoded()
        {
            var sender = new YModemBatchSender();
            var file = new YModemFileDescriptor("文件.bin", 123);

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));

            YModemAction.SendPacket sendHeader = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.FileHeaderReady(file)).Actions));
            YModemPacket.Header header = Assert.IsType<YModemPacket.Header>(sendHeader.Packet);
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => new YModemPacketEncoder().Encode(header));

            Assert.Contains("non-ASCII", exception.Message);
        }

        [Fact]
        public void BatchSenderRejectsDataLargerThanRequested128ByteBlock()
        {
            var sender = new YModemBatchSender();
            var oversizedPayload = new byte[129];

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("small.bin", 1)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));

            YModemAction.RequestDataBlock requestData = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest)).Actions));
            Assert.Equal(128, requestData.BlockSize);

            YModemBatchStepResult step = sender.Advance(new YModemEvent.DataBlockReady(1, oversizedPayload, oversizedPayload.Length, true));
            YModemAction.Fail failure = Assert.IsType<YModemAction.Fail>(Assert.Single(step.Actions));
            Assert.Equal("Data block is larger than the requested packet size.", failure.Reason);
        }

        [Fact]
        public void BatchSenderEncodesTailBlockUsingPacketBlockSizeEvenWith1KEncoder()
        {
            var sender = new YModemBatchSender();
            var fullPayload = new byte[1024];
            var tailPayload = new byte[] { 0x41 };

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("tail.bin", 1025)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.DataBlockReady(1, fullPayload, fullPayload.Length, false));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));

            YModemAction.SendPacket sendTail = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.DataBlockReady(2, tailPayload, tailPayload.Length, true)).Actions));
            YModemPacket.Data tailPacket = Assert.IsType<YModemPacket.Data>(sendTail.Packet);
            var encodedBytes = new YModemPacketEncoder().Encode(tailPacket);

            Assert.Equal(128, tailPacket.BlockSize);
            Assert.Equal(YModemControlBytes.Soh, encodedBytes[0]);
            Assert.Equal(128 + 5, encodedBytes.Length);
        }

        [Fact]
        public void BatchSenderCompletesTwoFileBatchAndTrailer()
        {
            var sender = new YModemBatchSender();

            YModemBatchStepResult step1 = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            Assert.IsType<YModemAction.RequestFileHeader>(Assert.Single(step1.Actions));

            var file1 = new YModemFileDescriptor("one.bin", 1);
            YModemBatchStepResult step2 = sender.Advance(new YModemEvent.FileHeaderReady(file1));
            Assert.IsType<YModemPacket.Header>(Assert.IsType<YModemAction.SendPacket>(Assert.Single(step2.Actions)).Packet);

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.DataBlockReady(1, new byte[] { 0x41 }, 1, true));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Nak));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));

            YModemBatchStepResult step8 = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            Assert.IsType<YModemAction.RequestFileHeader>(Assert.Single(step8.Actions));

            var file2 = new YModemFileDescriptor("two.bin", 1);
            YModemBatchStepResult step9 = sender.Advance(new YModemEvent.FileHeaderReady(file2));
            YModemAction.SendPacket sendSecondHeader = Assert.IsType<YModemAction.SendPacket>(Assert.Single(step9.Actions));
            Assert.Equal("two.bin", Assert.IsType<YModemPacket.Header>(sendSecondHeader.Packet).File.FileName);

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.DataBlockReady(1, new byte[] { 0x42 }, 1, true));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Nak));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));

            YModemBatchStepResult trailerStep = sender.Advance(new YModemEvent.NoMoreFiles());
            Assert.IsType<YModemPacket.BatchTrailer>(Assert.IsType<YModemAction.SendPacket>(Assert.Single(trailerStep.Actions)).Packet);

            YModemBatchStepResult completeStep = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            Assert.Contains(completeStep.Actions, action => action is YModemAction.Complete);
        }
    }
}

namespace Ymodem.Protocol.Tests
{
    public sealed partial class BatchSenderTests
    {

        [Fact]
        public void BatchSenderNakAfterHeaderResendsSameHeaderPacket()
        {
            var sender = new YModemBatchSender();
            var file = new YModemFileDescriptor("demo.bin", 3);

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));

            YModemAction.SendPacket sendHeader = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.FileHeaderReady(file)).Actions));
            YModemBatchStepResult resendStep = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Nak));
            YModemAction.SendPacket resend = Assert.IsType<YModemAction.SendPacket>(Assert.Single(resendStep.Actions));

            Assert.Same(sendHeader.Packet, resend.Packet);
            Assert.Equal("Resend file header", resend.Description);
            Assert.Equal(YModemBatchSenderPhase.WaitingHeaderAck, resendStep.Snapshot.Phase);
        }

        [Fact]
        public void BatchSenderNakAfterDataBlockResendsSameDataPacket()
        {
            var sender = new YModemBatchSender();
            var payload = new byte[] { 0x41, 0x42, 0x43 };

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("demo.bin", payload.Length)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));

            YModemAction.SendPacket sendData = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.DataBlockReady(1, payload, payload.Length, true)).Actions));
            YModemBatchStepResult resendStep = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Nak));
            YModemAction.SendPacket resend = Assert.IsType<YModemAction.SendPacket>(Assert.Single(resendStep.Actions));

            Assert.Same(sendData.Packet, resend.Packet);
            Assert.Equal("Resend data block", resend.Description);
            Assert.Equal(YModemBatchSenderPhase.WaitingBlockAck, resendStep.Snapshot.Phase);
        }

        [Fact]
        public void BatchSenderNakAfterBatchTrailerResendsSameTrailerPacket()
        {
            var sender = new YModemBatchSender();

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));

            YModemAction.SendPacket sendTrailer = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.NoMoreFiles()).Actions));
            YModemBatchStepResult resendStep = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Nak));
            YModemAction.SendPacket resend = Assert.IsType<YModemAction.SendPacket>(Assert.Single(resendStep.Actions));

            Assert.Same(sendTrailer.Packet, resend.Packet);
            Assert.Equal("Resend batch trailer", resend.Description);
            Assert.Equal(YModemBatchSenderPhase.WaitingBatchTrailerAck, resendStep.Snapshot.Phase);
        }

        [Fact]
        public void BatchSenderCancelRequestedCancelsAndEmitsCancelAction()
        {
            var sender = new YModemBatchSender();

            YModemBatchStepResult step = sender.Advance(new YModemEvent.CancelRequested("stop"));

            YModemAction.Cancel cancel = Assert.IsType<YModemAction.Cancel>(Assert.Single(step.Actions));
            Assert.Equal("stop", cancel.Reason);
            Assert.Equal(YModemBatchSenderPhase.Cancelled, step.Snapshot.Phase);
        }

        [Fact]
        public void BatchSenderPeerCancelCancelsAndEmitsCancelAction()
        {
            var sender = new YModemBatchSender();

            YModemBatchStepResult step = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Can));

            YModemAction.Cancel cancel = Assert.IsType<YModemAction.Cancel>(Assert.Single(step.Actions));
            Assert.Equal("Peer cancelled the transfer.", cancel.Reason);
            Assert.Equal(YModemBatchSenderPhase.Cancelled, step.Snapshot.Phase);
        }

        [Fact]
        public void BatchSenderBlockOptionsCanForce1KHeaderAndDataBlocks()
        {
            var sender = new YModemBatchSender(new YModemBlockOptions(YModemBlockMode.Fixed1K, true, true));
            var file = new YModemFileDescriptor("demo.bin", 3);

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));

            YModemAction.SendPacket sendHeader = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.FileHeaderReady(file)).Actions));
            YModemPacket.Header header = Assert.IsType<YModemPacket.Header>(sendHeader.Packet);
            Assert.Equal(1024, header.BlockSize);

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            YModemAction.RequestDataBlock request = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest)).Actions));
            Assert.Equal(1024, request.BlockSize);
        }
    }
}


namespace Ymodem.Protocol.Tests
{
    public sealed partial class BatchSenderTests
    {
        [Theory]
        [InlineData("header", "Resend file header", YModemBatchSenderPhase.WaitingHeaderAck)]
        [InlineData("data", "Resend data block", YModemBatchSenderPhase.WaitingBlockAck)]
        [InlineData("trailer", "Resend batch trailer", YModemBatchSenderPhase.WaitingBatchTrailerAck)]
        public void BatchSenderNakResendsLastPacketAcrossStates(string scenario, string expectedDescription, YModemBatchSenderPhase expectedPhase)
        {
            var sender = CreateBatchSenderForResendScenario(scenario, out YModemPacket expectedPacket);

            YModemBatchStepResult step = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Nak));
            YModemAction.SendPacket resend = Assert.IsType<YModemAction.SendPacket>(Assert.Single(step.Actions));

            Assert.Same(expectedPacket, resend.Packet);
            Assert.Equal(expectedDescription, resend.Description);
            Assert.Equal(expectedPhase, step.Snapshot.Phase);
        }

        [Theory]
        [InlineData("header", (byte)0x43, "Expected ACK or NAK after file header.")]
        [InlineData("data_start", (byte)0x06, "Expected receiver CRC request before first data block.")]
        [InlineData("first_eot", (byte)0x06, "Expected NAK after first EOT.")]
        [InlineData("second_eot", (byte)0x43, "Expected ACK after second EOT.")]
        [InlineData("next_header", (byte)0x06, "Expected receiver CRC request before next file header or trailer.")]
        [InlineData("trailer", (byte)0x18, "Peer cancelled the transfer.")]
        public void BatchSenderStateMachineHandlesUnexpectedPeerInputMatrix(string scenario, byte peerByte, string expectedReason)
        {
            var sender = CreateBatchSenderForUnexpectedPeerScenario(scenario);

            YModemBatchStepResult step = sender.Advance(new YModemEvent.PeerByteReceived(peerByte));

            if (peerByte == YModemControlBytes.Can)
            {
                YModemAction.Cancel cancel = Assert.IsType<YModemAction.Cancel>(Assert.Single(step.Actions));
                Assert.Equal(expectedReason, cancel.Reason);
                Assert.Equal(YModemBatchSenderPhase.Cancelled, step.Snapshot.Phase);
                return;
            }

            YModemAction.Fail failure = Assert.IsType<YModemAction.Fail>(Assert.Single(step.Actions));
            Assert.Equal(expectedReason, failure.Reason);
            Assert.Equal(YModemBatchSenderPhase.Faulted, step.Snapshot.Phase);
        }

        private static YModemBatchSender CreateBatchSenderForResendScenario(string scenario, out YModemPacket expectedPacket)
        {
            var sender = new YModemBatchSender();
            var payload = new byte[] { 0x41 };

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));

            if (scenario == "trailer")
            {
                YModemAction.SendPacket sendTrailer = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.NoMoreFiles()).Actions));
                expectedPacket = sendTrailer.Packet;
                return sender;
            }

            if (scenario == "header")
            {
                YModemAction.SendPacket sendHeader = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("demo.bin", payload.Length))).Actions));
                expectedPacket = sendHeader.Packet;
                return sender;
            }

            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("demo.bin", payload.Length)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            YModemAction.SendPacket sendData = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.DataBlockReady(1, payload, payload.Length, true)).Actions));
            expectedPacket = sendData.Packet;
            return sender;
        }

        private static YModemBatchSender CreateBatchSenderForUnexpectedPeerScenario(string scenario)
        {
            var sender = new YModemBatchSender();
            var payload = new byte[] { 0x41 };

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));

            if (scenario == "trailer")
            {
                sender.Advance(new YModemEvent.NoMoreFiles());
                return sender;
            }

            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("demo.bin", payload.Length)));

            if (scenario == "header")
            {
                return sender;
            }

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));

            if (scenario == "data_start")
            {
                return sender;
            }

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.DataBlockReady(1, payload, payload.Length, true));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));

            if (scenario == "first_eot")
            {
                return sender;
            }

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Nak));

            if (scenario == "second_eot")
            {
                return sender;
            }

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));

            if (scenario == "next_header")
            {
                return sender;
            }

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.NoMoreFiles());
            return sender;
        }
    }
}
