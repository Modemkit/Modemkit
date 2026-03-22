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
        public void BatchSenderBlockOptionsCanForce1KHeaderAndDataBlocks()
        {
            var sender = new YModemBatchSender(new YModemBlockOptions(YModemBlockMode.Fixed1K, YModemBlockMode.Fixed1K));
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
