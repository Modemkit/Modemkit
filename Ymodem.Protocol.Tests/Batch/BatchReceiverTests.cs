using Xunit;

namespace Ymodem.Protocol.Tests
{
    public sealed class BatchReceiverTests
    {
        [Fact]
        public void BatchReceiverAcksRepeatedDataBlockWithoutRedelivery()
        {
            var receiver = new YModemBatchReceiver();

            receiver.Advance(new YModemEvent.StartRequested());
            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Header(new YModemFileDescriptor("demo.bin", 1))));
            receiver.Advance(new YModemEvent.FileHeaderAccepted());

            var payload = new byte[1024];
            payload[0] = 0x41;

            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(1, payload, payload.Length)));
            receiver.Advance(new YModemEvent.DataBlockAccepted());

            YModemBatchReceiveStepResult duplicateStep = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(1, payload, payload.Length)));
            YModemAction.SendControl ack = Assert.IsType<YModemAction.SendControl>(Assert.Single(duplicateStep.Actions));
            Assert.Equal(YModemControlBytes.Ack, ack.Value);
            Assert.Equal(YModemBatchReceiverPhase.WaitingDataPacketOrEot, duplicateStep.Snapshot.Phase);
        }

        [Fact]
        public void BatchReceiverCompletesTwoFileBatch()
        {
            var receiver = new YModemBatchReceiver();

            receiver.Advance(new YModemEvent.StartRequested());
            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Header(new YModemFileDescriptor("one.bin", 1))));
            receiver.Advance(new YModemEvent.FileHeaderAccepted());

            var payload1 = new byte[1024];
            payload1[0] = 0x41;
            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(1, payload1, payload1.Length)));
            receiver.Advance(new YModemEvent.DataBlockAccepted());
            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Eot()));
            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Eot()));

            YModemBatchReceiveStepResult secondHeaderStep = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Header(new YModemFileDescriptor("two.bin", 1))));
            Assert.IsType<YModemAction.OfferFileHeader>(Assert.Single(secondHeaderStep.Actions));

            receiver.Advance(new YModemEvent.FileHeaderAccepted());
            var payload2 = new byte[1024];
            payload2[0] = 0x42;
            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(1, payload2, payload2.Length)));
            receiver.Advance(new YModemEvent.DataBlockAccepted());
            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Eot()));
            receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Eot()));

            YModemBatchReceiveStepResult completeStep = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.BatchTrailer()));
            Assert.Contains(completeStep.Actions, action => action is YModemAction.Complete);
            Assert.Equal(YModemBatchReceiverPhase.Completed, completeStep.Snapshot.Phase);
        }

        [Fact]
        public void BatchReceiverAcceptsSenderPacketWithLogicalBlock256AfterWireWrapAround()
        {
            const long fileSize = (255L * 1024) + 1;
            var sender = new YModemBatchSender();
            var receiver = new YModemBatchReceiver();
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

            YModemBatchReceiveStepResult step = receiver.Advance(new YModemEvent.PacketReceived(packet));

            YModemAction.DeliverDataBlock deliver = Assert.IsType<YModemAction.DeliverDataBlock>(Assert.Single(step.Actions));
            Assert.Equal(256, deliver.BlockNumber);
            Assert.Equal(1, deliver.DataLength);
            Assert.Equal(YModemBatchReceiverPhase.WaitingDataBlockDecision, step.Snapshot.Phase);
        }

        [Fact]
        public void BatchReceiverAcceptsWireBlockZeroAsLogicalBlock256AfterBlockNumberWrapsAround()
        {
            const long fileSize = (255L * 1024) + 1;
            var receiver = new YModemBatchReceiver();
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

            YModemBatchReceiveStepResult step = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(0, payload, payload.Length)));

            YModemAction.DeliverDataBlock deliver = Assert.IsType<YModemAction.DeliverDataBlock>(Assert.Single(step.Actions));
            Assert.Equal(256, deliver.BlockNumber);
            Assert.Equal(1, deliver.DataLength);
            Assert.Equal(YModemBatchReceiverPhase.WaitingDataBlockDecision, step.Snapshot.Phase);
        }

        [Fact]
        public void BatchReceiverAcksRepeatedBlockZeroAfterRollover()
        {
            const long fileSize = (256L * 1024) + 1024;
            var receiver = new YModemBatchReceiver();
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
            YModemBatchReceiveStepResult step = receiver.Advance(new YModemEvent.PacketReceived(new YModemPacket.Data(0, payload, payload.Length)));

            YModemAction.SendControl ack = Assert.IsType<YModemAction.SendControl>(Assert.Single(step.Actions));
            Assert.Equal(YModemControlBytes.Ack, ack.Value);
            Assert.Equal(YModemBatchReceiverPhase.WaitingDataPacketOrEot, step.Snapshot.Phase);
        }
    }
}
