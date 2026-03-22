namespace Ymodem.Protocol.Tests
{
    public sealed class SenderStateMatrixTests
    {
        [Theory]
        [InlineData("header", "Resend file header", YModemSenderPhase.WaitingHeaderAck)]
        [InlineData("data", "Resend data block", YModemSenderPhase.WaitingBlockAck)]
        [InlineData("trailer", "Resend batch trailer", YModemSenderPhase.WaitingBatchTrailerAck)]
        public void SenderNakResendsLastPacketAcrossStates(string scenario, string expectedDescription, YModemSenderPhase expectedPhase)
        {
            var sender = CreateSenderForResendScenario(scenario, out YModemPacket expectedPacket);

            YModemStepResult step = sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Nak));
            YModemAction.SendPacket resend = Assert.IsType<YModemAction.SendPacket>(Assert.Single(step.Actions));

            Assert.Same(expectedPacket, resend.Packet);
            Assert.Equal(expectedDescription, resend.Description);
            Assert.Equal(expectedPhase, step.Snapshot.Phase);
        }

        [Theory]
        [InlineData("header", (byte)0x43, "Expected ACK or NAK after file header.")]
        [InlineData("data_start", (byte)0x06, "Expected receiver CRC request before first data block.")]
        [InlineData("first_eot", (byte)0x06, "Expected NAK after first EOT.")]
        [InlineData("second_eot", (byte)0x18, "Peer cancelled the transfer.")]
        [InlineData("trailer_request", (byte)0x06, "Expected receiver CRC request before batch trailer.")]
        public void SenderStateMachineHandlesUnexpectedPeerInputMatrix(string scenario, byte peerByte, string expectedReason)
        {
            var sender = CreateSenderForUnexpectedPeerScenario(scenario);

            YModemStepResult step = sender.Advance(new YModemEvent.PeerByteReceived(peerByte));

            if (peerByte == YModemControlBytes.Can)
            {
                YModemAction.Cancel cancel = Assert.IsType<YModemAction.Cancel>(Assert.Single(step.Actions));
                Assert.Equal(expectedReason, cancel.Reason);
                Assert.Equal(YModemSenderPhase.Cancelled, step.Snapshot.Phase);
                return;
            }

            YModemAction.Fail failure = Assert.IsType<YModemAction.Fail>(Assert.Single(step.Actions));
            Assert.Equal(expectedReason, failure.Reason);
            Assert.Equal(YModemSenderPhase.Faulted, step.Snapshot.Phase);
        }

        private static YModemSender CreateSenderForResendScenario(string scenario, out YModemPacket expectedPacket)
        {
            var sender = new YModemSender();
            var payload = new byte[] { 0x41 };

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));

            if (scenario == "header")
            {
                YModemAction.SendPacket sendHeader = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("demo.bin", payload.Length))).Actions));
                expectedPacket = sendHeader.Packet;
                return sender;
            }

            sender.Advance(new YModemEvent.FileHeaderReady(new YModemFileDescriptor("demo.bin", payload.Length)));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));

            if (scenario == "data")
            {
                YModemAction.SendPacket sendData = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.DataBlockReady(1, payload, payload.Length, true)).Actions));
                expectedPacket = sendData.Packet;
                return sender;
            }

            sender.Advance(new YModemEvent.DataBlockReady(1, payload, payload.Length, true));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Nak));
            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));

            YModemAction.SendPacket sendTrailer = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest)).Actions));
            expectedPacket = sendTrailer.Packet;
            return sender;
        }

        private static YModemSender CreateSenderForUnexpectedPeerScenario(string scenario)
        {
            var sender = new YModemSender();
            var payload = new byte[] { 0x41 };

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
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
            return sender;
        }
    }
}
