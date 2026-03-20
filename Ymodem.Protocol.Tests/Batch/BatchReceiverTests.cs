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
    }
}
