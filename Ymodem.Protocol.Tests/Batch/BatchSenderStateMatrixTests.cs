namespace Ymodem.Protocol.Tests
{
    public sealed class BatchSenderStateMatrixTests
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
