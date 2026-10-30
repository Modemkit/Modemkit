namespace Xmodem.Protocol.Tests
{
    public sealed class StreamingTests
    {
        [Fact]
        public void QModemGAndOneKGAreAliasesForTheSameWireMode()
        {
            Assert.Equal(XModemMode.G, XModemMode.QModemG);
            Assert.Equal(XModemMode.G, XModemMode.OneKG);
            var receiver = new XModemReceiver(new XModemOptions(XModemMode.QModemG));
            var request = Assert.IsType<XModemAction.SendControl>(
                Assert.Single(receiver.Advance(new XModemEvent.StartRequested()).Actions));
            Assert.Equal((byte)'G', request.Value);
        }

        [Fact]
        public void SenderStreamsNextBlockWithoutWaitingForAck()
        {
            var sender = new XModemSender(new XModemOptions(XModemMode.G));
            Assert.Empty(sender.Advance(new XModemEvent.PeerByteReceived(XModemControlBytes.CrcRequest)).Actions);
            Assert.Empty(sender.Advance(new XModemEvent.PeerByteReceived(XModemControlBytes.Nak)).Actions);
            var request = Assert.IsType<XModemAction.RequestDataBlock>(
                Assert.Single(sender.Advance(new XModemEvent.PeerByteReceived((byte)'G')).Actions));
            Assert.Equal(1024, request.BlockSize);
            var next = sender.Advance(new XModemEvent.DataBlockReady(1, new byte[1024], 1024, false));
            Assert.Single(next.Actions.OfType<XModemAction.SendPacket>());
            Assert.Equal(2, Assert.Single(next.Actions.OfType<XModemAction.RequestDataBlock>()).BlockNumber);
            Assert.Equal(1024, sender.Snapshot.BytesTransferred);
            Assert.Single(sender.Advance(new XModemEvent.PeerByteReceived(XModemControlBytes.Nak)).Actions.OfType<XModemAction.Fail>());
        }

        [Fact]
        public void ReceiverDoesNotAcknowledgeDataAndQueuesUntilStorageAccepts()
        {
            var receiver = new XModemReceiver(new XModemOptions(XModemMode.G));
            receiver.Advance(new XModemEvent.StartRequested());
            var first = receiver.Advance(Data(1));
            Assert.Single(first.Actions.OfType<XModemAction.DeliverDataBlock>());
            Assert.Empty(receiver.Advance(Data(2)).Actions);
            Assert.Empty(receiver.Advance(new XModemEvent.PacketReceived(new XModemPacket.Eot())).Actions);
            var accepted = receiver.Advance(new XModemEvent.DataBlockAccepted());
            Assert.Empty(accepted.Actions.OfType<XModemAction.SendControl>());
            Assert.Equal(2, Assert.Single(accepted.Actions.OfType<XModemAction.DeliverDataBlock>()).BlockNumber);
            var finished = receiver.Advance(new XModemEvent.DataBlockAccepted());
            Assert.Single(finished.Actions.OfType<XModemAction.Complete>());
            Assert.Equal(XModemControlBytes.Ack, Assert.Single(finished.Actions.OfType<XModemAction.SendControl>()).Value);
        }

        [Theory]
        [InlineData("crc")]
        [InlineData("sequence")]
        [InlineData("rejection")]
        [InlineData("timeout")]
        public void StreamingDataErrorsAbortInsteadOfRetransmitting(string fault)
        {
            var receiver = new XModemReceiver(new XModemOptions(XModemMode.G));
            receiver.Advance(new XModemEvent.StartRequested());
            receiver.Advance(Data(1));
            XModemStepResult result;
            if (fault == "rejection")
            {
                result = receiver.Advance(new XModemEvent.DataBlockRejected());
            }
            else if (fault == "crc")
            {
                result = receiver.Advance(new XModemEvent.InvalidFrame());
            }
            else
            {
                receiver.Advance(new XModemEvent.DataBlockAccepted());
                result = receiver.Advance(fault == "sequence" ? Data(3) : new XModemEvent.TimeoutElapsed());
            }

            Assert.Equal(XModemPhase.Faulted, result.Snapshot.Phase);
            Assert.Single(result.Actions.OfType<XModemAction.Fail>());
            Assert.Equal(new byte[] { 0x18, 0x18 }, result.Actions.OfType<XModemAction.SendControl>().Select(a => a.Value));
        }

        [Fact]
        public void SlowStorageCannotCauseUnboundedStreamingBufferGrowth()
        {
            var receiver = new XModemReceiver(new XModemOptions(XModemMode.G, maxBufferedBlocks: 1));
            receiver.Advance(new XModemEvent.StartRequested());
            receiver.Advance(Data(1));
            receiver.Advance(Data(2));
            Assert.Single(receiver.Advance(Data(3)).Actions.OfType<XModemAction.Fail>());
            Assert.Equal(XModemPhase.Faulted, receiver.Snapshot.Phase);
        }

        [Fact]
        public void StreamingOptionsRejectIncompatibleFallbackAndInvalidBufferBounds()
        {
            Assert.Throws<ArgumentException>(() => new XModemOptions(XModemMode.G, allowChecksumFallback: true));
            Assert.Throws<ArgumentOutOfRangeException>(() => new XModemOptions(maxBufferedBlocks: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new XModemOptions(maxBufferedBlocks: 1025));
        }

        private static XModemEvent.PacketReceived Data(int number)
        {
            return new XModemEvent.PacketReceived(new XModemPacket.Data(number, new byte[1024], 1024, 1024));
        }
    }
}
