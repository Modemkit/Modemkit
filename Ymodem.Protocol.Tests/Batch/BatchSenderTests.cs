using Xunit;

namespace Ymodem.Protocol.Tests
{
    public sealed class BatchSenderTests
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
