using Xunit;

namespace Ymodem.Protocol.Tests
{
    public sealed class BatchSenderTests
    {
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
