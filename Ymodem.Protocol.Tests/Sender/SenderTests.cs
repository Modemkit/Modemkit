namespace Ymodem.Protocol.Tests
{
    public sealed class SenderTests
    {
        [Fact]
        public void SenderCompletesSingleBlockTransferThroughAbstractPackets()
        {
            var sender = new YModemSender();

            YModemStepResult step1 = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            YModemAction requestHeader = Assert.Single(step1.Actions);
            Assert.IsType<YModemAction.RequestFileHeader>(requestHeader);
            Assert.Equal(YModemSenderPhase.WaitingFileHeader, step1.Snapshot.Phase);

            var file = new YModemFileDescriptor("demo.bin", 3);
            YModemStepResult step2 = sender.Advance(new YModemEvent.FileHeaderReady(file));
            YModemAction.SendPacket sendHeader = Assert.IsType<YModemAction.SendPacket>(Assert.Single(step2.Actions));
            YModemPacket.Header headerPacket = Assert.IsType<YModemPacket.Header>(sendHeader.Packet);
            Assert.Same(file, headerPacket.File);
            Assert.Equal(YModemSenderPhase.WaitingHeaderAck, step2.Snapshot.Phase);

            YModemStepResult step3 = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            Assert.Empty(step3.Actions);
            Assert.Equal(YModemSenderPhase.WaitingDataStartRequest, step3.Snapshot.Phase);

            YModemStepResult step4 = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            YModemAction.RequestDataBlock requestData = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(step4.Actions));
            Assert.Equal(1, requestData.BlockNumber);
            Assert.Equal(128, requestData.BlockSize);
            Assert.Equal(YModemSenderPhase.WaitingDataBlock, step4.Snapshot.Phase);

            var payload = new byte[] { 0x41, 0x42, 0x43 };
            YModemStepResult step5 = sender.Advance(new YModemEvent.DataBlockReady(1, payload, 3, true));
            YModemAction.SendPacket sendData = Assert.IsType<YModemAction.SendPacket>(Assert.Single(step5.Actions));
            YModemPacket.Data dataPacket = Assert.IsType<YModemPacket.Data>(sendData.Packet);
            Assert.Equal(1, dataPacket.BlockNumber);
            Assert.Same(payload, dataPacket.Payload);
            Assert.Equal(3, dataPacket.DataLength);
            Assert.Equal(YModemSenderPhase.WaitingBlockAck, step5.Snapshot.Phase);

            YModemStepResult step6 = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            YModemAction.SendPacket sendFirstEot = Assert.IsType<YModemAction.SendPacket>(Assert.Single(step6.Actions));
            Assert.IsType<YModemPacket.Eot>(sendFirstEot.Packet);
            Assert.Equal(YModemSenderPhase.WaitingFirstEotResponse, step6.Snapshot.Phase);

            YModemStepResult step7 = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Nak));
            YModemAction.SendPacket sendSecondEot = Assert.IsType<YModemAction.SendPacket>(Assert.Single(step7.Actions));
            Assert.IsType<YModemPacket.Eot>(sendSecondEot.Packet);
            Assert.Equal(YModemSenderPhase.WaitingSecondEotAck, step7.Snapshot.Phase);

            YModemStepResult step8 = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            Assert.Empty(step8.Actions);
            Assert.Equal(YModemSenderPhase.WaitingBatchTrailerRequest, step8.Snapshot.Phase);

            YModemStepResult step9 = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            YModemAction.SendPacket sendTrailer = Assert.IsType<YModemAction.SendPacket>(Assert.Single(step9.Actions));
            Assert.IsType<YModemPacket.BatchTrailer>(sendTrailer.Packet);
            Assert.Equal(YModemSenderPhase.WaitingBatchTrailerAck, step9.Snapshot.Phase);

            YModemStepResult step10 = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            Assert.Contains(step10.Actions, action => action is YModemAction.Complete);
            Assert.Equal(YModemSenderPhase.Completed, step10.Snapshot.Phase);
        }

        [Fact]
        public void SenderKeepsLogicalBlockNumberAfterBlock255()
        {
            var sender = new YModemSender();
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
        public void SenderRequests128ByteTailBlockAfterFull1KBlock()
        {
            var sender = new YModemSender();
            var fullPayload = new byte[1024];
            var tailPayload = new byte[] { 0x41 };

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("tail.bin", 1025)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));

            YModemAction.RequestDataBlock firstRequest = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest)).Actions));
            Assert.Equal(1024, firstRequest.BlockSize);

            sender.Advance(new YModemEvent.DataBlockReady(1, fullPayload, fullPayload.Length, false));

            YModemAction.RequestDataBlock tailRequest = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack)).Actions));
            Assert.Equal(2, tailRequest.BlockNumber);
            Assert.Equal(128, tailRequest.BlockSize);

            YModemAction.SendPacket sendTail = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.DataBlockReady(2, tailPayload, tailPayload.Length, true)).Actions));
            YModemPacket.Data tailPacket = Assert.IsType<YModemPacket.Data>(sendTail.Packet);
            Assert.Equal(1, tailPacket.DataLength);
            Assert.Equal(128 + 5, new YModemPacketEncoder(128).Encode(tailPacket).Length);
        }


        [Fact]
        public void SenderUses1KPacketForTailLargerThan128Bytes()
        {
            var sender = new YModemSender();
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
            var encodedBytes = new YModemPacketEncoder(1024).Encode(tailPacket);

            Assert.Equal(1024, tailPacket.BlockSize);
            Assert.Equal(513, tailPacket.DataLength);
            Assert.Equal(YModemControlBytes.Stx, encodedBytes[0]);
            Assert.Equal(1024 + 5, encodedBytes.Length);
        }

        [Fact]
        public void SenderRequests128ByteFirstDataBlockForSmallFiles()
        {
            var sender = new YModemSender();

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("small.bin", 3)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));

            YModemAction.RequestDataBlock requestData = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest)).Actions));
            Assert.Equal(1, requestData.BlockNumber);
            Assert.Equal(128, requestData.BlockSize);
        }

        [Theory]
        [InlineData(0, 128)]
        [InlineData(1, 128)]
        [InlineData(127, 128)]
        [InlineData(128, 1024)]
        [InlineData(129, 1024)]
        [InlineData(1024, 1024)]
        [InlineData(1025, 1024)]
        public void SenderRequestsExpectedFirstDataBlockSizeForFileSize(long fileSize, int expectedBlockSize)
        {
            var sender = new YModemSender();

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
        [InlineData(128, 1024)]
        [InlineData(129, 1024)]
        [InlineData(1024, 1024)]
        public void SenderRequestsExpectedTailBlockSizeAfterFull1KBlock(int tailSize, int expectedBlockSize)
        {
            var sender = new YModemSender();
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
        public void SenderHeaderUses128BytePacket()
        {
            var sender = new YModemSender();
            var file = new YModemFileDescriptor("header.bin", 123);

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));

            YModemAction.SendPacket sendHeader = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.FileHeaderReady(file)).Actions));
            YModemPacket.Header header = Assert.IsType<YModemPacket.Header>(sendHeader.Packet);
            var bytes = new YModemPacketEncoder().Encode(header);

            Assert.Equal(YModemControlBytes.Soh, bytes[0]);
            Assert.Equal(128 + 5, bytes.Length);
        }

        [Fact]
        public void SenderUses1KHeaderPacketWhenMetadataExceeds128Bytes()
        {
            var sender = new YModemSender();
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
        public void SenderHeaderWithNonAsciiFileNameFailsWhenEncoded()
        {
            var sender = new YModemSender();
            var file = new YModemFileDescriptor("文件.bin", 123);

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));

            YModemAction.SendPacket sendHeader = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.FileHeaderReady(file)).Actions));
            YModemPacket.Header header = Assert.IsType<YModemPacket.Header>(sendHeader.Packet);
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => new YModemPacketEncoder().Encode(header));

            Assert.Contains("non-ASCII", exception.Message);
        }

        [Fact]
        public void SenderRejectsDataLargerThanRequested128ByteBlock()
        {
            var sender = new YModemSender();
            var oversizedPayload = new byte[129];

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("small.bin", 1)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));

            YModemAction.RequestDataBlock requestData = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest)).Actions));
            Assert.Equal(128, requestData.BlockSize);

            YModemStepResult step = sender.Advance(new YModemEvent.DataBlockReady(1, oversizedPayload, oversizedPayload.Length, true));
            YModemAction.Fail failure = Assert.IsType<YModemAction.Fail>(Assert.Single(step.Actions));
            Assert.Equal("Data block is larger than the requested packet size.", failure.Reason);
        }

        [Fact]
        public void SenderEncodesTailBlockUsingPacketBlockSizeEvenWith1KEncoder()
        {
            var sender = new YModemSender();
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
            var encodedBytes = new YModemPacketEncoder(1024).Encode(tailPacket);

            Assert.Equal(128, tailPacket.BlockSize);
            Assert.Equal(YModemControlBytes.Soh, encodedBytes[0]);
            Assert.Equal(128 + 5, encodedBytes.Length);
        }

        [Fact]
        public void SenderNakAfterHeaderResendsSameHeaderPacket()
        {
            var sender = new YModemSender();
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));

            YModemStepResult originalStep = sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("demo.bin", 1)));
            YModemAction.SendPacket originalSend = Assert.IsType<YModemAction.SendPacket>(Assert.Single(originalStep.Actions));

            YModemStepResult resendStep = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Nak));
            YModemAction.SendPacket resend = Assert.IsType<YModemAction.SendPacket>(Assert.Single(resendStep.Actions));

            Assert.Same(originalSend.Packet, resend.Packet);
            Assert.Equal("Resend file header", resend.Description);
        }

        [Fact]
        public void SenderUnexpectedInitialByteFaults()
        {
            var sender = new YModemSender();

            YModemStepResult step = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));

            YModemAction.Fail failure = Assert.IsType<YModemAction.Fail>(Assert.Single(step.Actions));
            Assert.Equal("Expected receiver CRC request before starting transfer.", failure.Reason);
            Assert.Equal(YModemSenderPhase.Faulted, step.Snapshot.Phase);
        }

        [Fact]
        public void SenderCancelRequestedCancelsAndEmitsCancelAction()
        {
            var sender = new YModemSender();

            YModemStepResult step = sender.Advance(new YModemEvent.CancelRequested("stop"));

            Assert.Collection(
                step.Actions,
                action =>
                {
                    YModemAction.Cancel cancel = Assert.IsType<YModemAction.Cancel>(action);
                    Assert.Equal("stop", cancel.Reason);
                });
            Assert.Equal(YModemSenderPhase.Cancelled, step.Snapshot.Phase);
        }
    }
}
