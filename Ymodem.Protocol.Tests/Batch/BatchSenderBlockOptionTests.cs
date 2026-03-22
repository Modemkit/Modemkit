namespace Ymodem.Protocol.Tests
{
    public sealed class BatchSenderBlockOptionTests
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
