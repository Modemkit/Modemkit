using Xunit;

namespace Ymodem.Protocol.Tests
{
    public sealed class ReceiverTests
    {
        [Fact]
        public void ReceiverCompletesSingleBlockTransferThroughAbstractPackets()
        {
            var receiver = new YModemReceiver();

            YModemReceiveStepResult step1 = receiver.Advance(new YModemEvent.StartRequested());
            YModemAction.SendControl requestHeader = Assert.IsType<YModemAction.SendControl>(Assert.Single(step1.Actions));
            Assert.Equal(YModemControlBytes.CrcRequest, requestHeader.Value);
            Assert.Equal(YModemReceiverPhase.WaitingFileHeaderPacket, step1.Snapshot.Phase);

            var file = new YModemFileDescriptor("demo.bin", 3);
            YModemReceiveStepResult step2 = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Header(file)));
            YModemAction.OfferFileHeader offer = Assert.IsType<YModemAction.OfferFileHeader>(Assert.Single(step2.Actions));
            Assert.Same(file, offer.File);
            Assert.Equal(YModemReceiverPhase.WaitingFileHeaderDecision, step2.Snapshot.Phase);

            YModemReceiveStepResult step3 = receiver.Advance(new YModemEvent.FileHeaderAccepted());
            Assert.Collection(
                step3.Actions,
                action =>
                {
                    YModemAction.SendControl control = Assert.IsType<YModemAction.SendControl>(action);
                    Assert.Equal(YModemControlBytes.Ack, control.Value);
                },
                action =>
                {
                    YModemAction.SendControl control = Assert.IsType<YModemAction.SendControl>(action);
                    Assert.Equal(YModemControlBytes.CrcRequest, control.Value);
                });
            Assert.Equal(YModemReceiverPhase.WaitingDataPacketOrEot, step3.Snapshot.Phase);

            var payload = new byte[1024];
            payload[0] = 0x41;
            payload[1] = 0x42;
            payload[2] = 0x43;

            YModemReceiveStepResult step4 = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(1, payload, payload.Length)));
            YModemAction.DeliverDataBlock deliver = Assert.IsType<YModemAction.DeliverDataBlock>(Assert.Single(step4.Actions));
            Assert.Equal(1, deliver.BlockNumber);
            Assert.Equal(3, deliver.DataLength);
            Assert.Equal(YModemReceiverPhase.WaitingDataBlockDecision, step4.Snapshot.Phase);

            YModemReceiveStepResult step5 = receiver.Advance(new YModemEvent.DataBlockAccepted());
            YModemAction.SendControl ackData = Assert.IsType<YModemAction.SendControl>(Assert.Single(step5.Actions));
            Assert.Equal(YModemControlBytes.Ack, ackData.Value);
            Assert.Equal(0, step5.Snapshot.RemainingFileBytes);

            YModemReceiveStepResult step6 = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Eot()));
            YModemAction.SendControl nak = Assert.IsType<YModemAction.SendControl>(Assert.Single(step6.Actions));
            Assert.Equal(YModemControlBytes.Nak, nak.Value);
            Assert.Equal(YModemReceiverPhase.WaitingSecondEot, step6.Snapshot.Phase);

            YModemReceiveStepResult step7 = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Eot()));
            Assert.Collection(
                step7.Actions,
                action =>
                {
                    YModemAction.SendControl control = Assert.IsType<YModemAction.SendControl>(action);
                    Assert.Equal(YModemControlBytes.Ack, control.Value);
                },
                action =>
                {
                    YModemAction.SendControl control = Assert.IsType<YModemAction.SendControl>(action);
                    Assert.Equal(YModemControlBytes.CrcRequest, control.Value);
                });
            Assert.Equal(YModemReceiverPhase.WaitingBatchTrailer, step7.Snapshot.Phase);

            YModemReceiveStepResult step8 = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.BatchTrailer()));
            Assert.Collection(
                step8.Actions,
                action =>
                {
                    YModemAction.SendControl control = Assert.IsType<YModemAction.SendControl>(action);
                    Assert.Equal(YModemControlBytes.Ack, control.Value);
                },
                action => Assert.IsType<YModemAction.Complete>(action));
            Assert.Equal(YModemReceiverPhase.Completed, step8.Snapshot.Phase);
        }

        [Fact]
        public void ReceiverUnexpectedBlockNumberSendsNakAndKeepsWaiting()
        {
            var receiver = new YModemReceiver();
            receiver.Advance(new YModemEvent.StartRequested());
            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Header(new YModemFileDescriptor("demo.bin", 3))));
            receiver.Advance(new YModemEvent.FileHeaderAccepted());

            var payload = new byte[1024];
            YModemReceiveStepResult step = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(2, payload, payload.Length)));

            YModemAction.SendControl nak = Assert.IsType<YModemAction.SendControl>(Assert.Single(step.Actions));
            Assert.Equal(YModemControlBytes.Nak, nak.Value);
            Assert.Equal(YModemReceiverPhase.WaitingDataPacketOrEot, step.Snapshot.Phase);
        }

        [Fact]
        public void ReceiverDataBlockRejectedSendsNak()
        {
            var receiver = new YModemReceiver();
            receiver.Advance(new YModemEvent.StartRequested());
            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Header(new YModemFileDescriptor("demo.bin", 3))));
            receiver.Advance(new YModemEvent.FileHeaderAccepted());

            var payload = new byte[1024];
            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(1, payload, payload.Length)));

            YModemReceiveStepResult step = receiver.Advance(new YModemEvent.DataBlockRejected());

            YModemAction.SendControl nak = Assert.IsType<YModemAction.SendControl>(Assert.Single(step.Actions));
            Assert.Equal(YModemControlBytes.Nak, nak.Value);
            Assert.Equal(YModemReceiverPhase.WaitingDataPacketOrEot, step.Snapshot.Phase);
        }

        [Fact]
        public void ReceiverFileHeaderRejectedCancelsAndSendsCan()
        {
            var receiver = new YModemReceiver();
            receiver.Advance(new YModemEvent.StartRequested());
            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Header(new YModemFileDescriptor("demo.bin", 3))));

            YModemReceiveStepResult step = receiver.Advance(new YModemEvent.FileHeaderRejected("no space"));

            Assert.Collection(
                step.Actions,
                action =>
                {
                    YModemAction.SendControl can = Assert.IsType<YModemAction.SendControl>(action);
                    Assert.Equal(YModemControlBytes.Can, can.Value);
                },
                action =>
                {
                    YModemAction.Cancel cancel = Assert.IsType<YModemAction.Cancel>(action);
                    Assert.Equal("no space", cancel.Reason);
                });
            Assert.Equal(YModemReceiverPhase.Cancelled, step.Snapshot.Phase);
        }

        [Fact]
        public void ReceiverCancelRequestedCancelsAndSendsCan()
        {
            var receiver = new YModemReceiver();
            receiver.Advance(new YModemEvent.StartRequested());

            YModemReceiveStepResult step = receiver.Advance(new YModemEvent.CancelRequested("user"));

            Assert.Collection(
                step.Actions,
                action =>
                {
                    YModemAction.SendControl can = Assert.IsType<YModemAction.SendControl>(action);
                    Assert.Equal(YModemControlBytes.Can, can.Value);
                },
                action =>
                {
                    YModemAction.Cancel cancel = Assert.IsType<YModemAction.Cancel>(action);
                    Assert.Equal("user", cancel.Reason);
                });
            Assert.Equal(YModemReceiverPhase.Cancelled, step.Snapshot.Phase);
        }

        [Fact]
        public void ReceiverRepeatedPreviousBlockAcksWithoutDeliveringAgain()
        {
            var receiver = new YModemReceiver();
            receiver.Advance(new YModemEvent.StartRequested());
            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Header(new YModemFileDescriptor("demo.bin", 1))));
            receiver.Advance(new YModemEvent.FileHeaderAccepted());

            var payload = new byte[1024];
            payload[0] = 0x41;

            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(1, payload, payload.Length)));
            receiver.Advance(new YModemEvent.DataBlockAccepted());

            YModemReceiveStepResult step = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(1, payload, payload.Length)));

            YModemAction.SendControl ack = Assert.IsType<YModemAction.SendControl>(Assert.Single(step.Actions));
            Assert.Equal(YModemControlBytes.Ack, ack.Value);
            Assert.Equal(YModemReceiverPhase.WaitingDataPacketOrEot, step.Snapshot.Phase);
        }

        [Fact]
        public void ReceiverAcceptsSenderPacketLevelDataPacketAfterBlockNumberWrapsAround()
        {
            const long fileSize = (255L * 1024) + 1;
            var sender = new YModemSender();
            var receiver = new YModemReceiver();
            var payload = new byte[1024];

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest));
            receiver.Advance(new YModemEvent.StartRequested());

            var file = new YModemFileDescriptor("large.bin", fileSize);
            YModemAction.SendPacket sendHeader = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.FileHeaderReady(file)).Actions));
            receiver.Advance(new YModemEvent.PacketReceived(sendHeader.Packet));
            receiver.Advance(new YModemEvent.FileHeaderAccepted());

            sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack));
            YModemAction.RequestDataBlock requestData = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.CrcRequest)).Actions));
            Assert.Equal(1, requestData.BlockNumber);

            for (var i = 1; i <= 255; i++)
            {
                YModemAction.SendPacket sendData = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.DataBlockReady(i, payload, payload.Length, false)).Actions));
                receiver.Advance(new YModemEvent.PacketReceived(sendData.Packet));
                receiver.Advance(new YModemEvent.DataBlockAccepted());

                if (i < 255)
                {
                    requestData = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack)).Actions));
                    Assert.Equal(i + 1, requestData.BlockNumber);
                }
            }

            YModemAction.RequestDataBlock requestWrappedBlock = Assert.IsType<YModemAction.RequestDataBlock>(Assert.Single(sender.Advance(new YModemEvent.PeerByteReceived(YModemControlBytes.Ack)).Actions));
            Assert.Equal(256, requestWrappedBlock.BlockNumber);

            YModemAction.SendPacket sendWrappedData = Assert.IsType<YModemAction.SendPacket>(Assert.Single(sender.Advance(new YModemEvent.DataBlockReady(256, payload, 1, true)).Actions));
            YModemPacket.Data packet = Assert.IsType<YModemPacket.Data>(sendWrappedData.Packet);
            Assert.Equal(256, packet.BlockNumber);

            YModemReceiveStepResult step = receiver.Advance(new YModemEvent.PacketReceived(packet));

            YModemAction.DeliverDataBlock deliver = Assert.IsType<YModemAction.DeliverDataBlock>(Assert.Single(step.Actions));
            Assert.Equal(256, deliver.BlockNumber);
            Assert.Equal(1, deliver.DataLength);
            Assert.Equal(YModemReceiverPhase.WaitingDataBlockDecision, step.Snapshot.Phase);
        }

        [Fact]
        public void ReceiverAcceptsAbsoluteBlockNumberAfterBlockNumberWrapsAround()
        {
            const long fileSize = (256L * 1024) + 1;
            var receiver = new YModemReceiver();
            receiver.Advance(new YModemEvent.StartRequested());
            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Header(new YModemFileDescriptor("large.bin", fileSize))));
            receiver.Advance(new YModemEvent.FileHeaderAccepted());

            var payload = new byte[1024];

            for (var i = 1; i <= 255; i++)
            {
                receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(i, payload, payload.Length)));
                receiver.Advance(new YModemEvent.DataBlockAccepted());
            }

            YModemReceiveStepResult step = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(256, payload, payload.Length)));

            YModemAction.DeliverDataBlock deliver = Assert.IsType<YModemAction.DeliverDataBlock>(Assert.Single(step.Actions));
            Assert.Equal(256, deliver.BlockNumber);
            Assert.Equal(YModemReceiverPhase.WaitingDataBlockDecision, step.Snapshot.Phase);
        }

        [Fact]
        public void ReceiverAcceptsDataBlockZeroAfterBlockNumberWrapsAround()
        {
            // A file large enough to require more than 255 data blocks ((256 * 1024) + 1 bytes)
            const long fileSize = (256L * 1024) + 1;
            var receiver = new YModemReceiver();
            receiver.Advance(new YModemEvent.StartRequested());
            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Header(new YModemFileDescriptor("large.bin", fileSize))));
            receiver.Advance(new YModemEvent.FileHeaderAccepted());

            var payload = new byte[1024];

            // Accept blocks 1 through 255 so _nextBlockNumber reaches 256
            for (var i = 1; i <= 255; i++)
            {
                receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(i, payload, payload.Length)));
                receiver.Advance(new YModemEvent.DataBlockAccepted());
            }

            // Block 256 wraps to 0 on the wire; the decoder passes blockNumber=0 in data phase
            YModemReceiveStepResult step = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(0, payload, payload.Length)));

            // Receiver must deliver the block rather than NAK-ing it
            Assert.IsType<YModemAction.DeliverDataBlock>(Assert.Single(step.Actions));
            Assert.Equal(YModemReceiverPhase.WaitingDataBlockDecision, step.Snapshot.Phase);
        }

        [Fact]
        public void ReceiverAcksRepeatedBlockZeroAfterRollover()
        {
            const long fileSize = (256L * 1024) + 1024;
            var receiver = new YModemReceiver();
            receiver.Advance(new YModemEvent.StartRequested());
            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Header(new YModemFileDescriptor("large.bin", fileSize))));
            receiver.Advance(new YModemEvent.FileHeaderAccepted());

            var payload = new byte[1024];

            for (var i = 1; i <= 255; i++)
            {
                receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(i, payload, payload.Length)));
                receiver.Advance(new YModemEvent.DataBlockAccepted());
            }

            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(0, payload, payload.Length)));
            receiver.Advance(new YModemEvent.DataBlockAccepted());

            // Sender retransmits block 0 (duplicate); receiver should ACK without re-delivering
            YModemReceiveStepResult step = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(0, payload, payload.Length)));

            YModemAction.SendControl ack = Assert.IsType<YModemAction.SendControl>(Assert.Single(step.Actions));
            Assert.Equal(YModemControlBytes.Ack, ack.Value);
            Assert.Equal(YModemReceiverPhase.WaitingDataPacketOrEot, step.Snapshot.Phase);
        }
    }
}
