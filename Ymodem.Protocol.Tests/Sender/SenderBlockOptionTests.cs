namespace Ymodem.Protocol.Tests
{
    public sealed class SenderBlockOptionTests
    {
        [Fact]
        public void SenderBlockOptionsCanForce1KHeaderAndDataBlocks()
        {
            var sender = new YModemSender(new YModemBlockOptions(YModemBlockMode.Fixed1K, true, true));
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
