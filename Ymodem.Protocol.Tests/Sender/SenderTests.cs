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
            Assert.Equal(1024, requestData.BlockSize);
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
