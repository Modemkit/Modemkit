namespace Xmodem.Protocol.Tests
{
    public sealed class StateMachineTests
    {
        [Fact]
        public void ReceiverFallsBackOnlyBeforeAnyFrameIsReceived()
        {
            var receiver = new XModemReceiver(new XModemOptions(
                allowChecksumFallback: true, crcFallbackAttempts: 2));
            AssertControl(receiver.Advance(new XModemEvent.StartRequested()), XModemControlBytes.CrcRequest);
            AssertControl(receiver.Advance(new XModemEvent.TimeoutElapsed()), XModemControlBytes.CrcRequest);
            AssertControl(receiver.Advance(new XModemEvent.TimeoutElapsed()), XModemControlBytes.Nak);
            Assert.Equal(XModemIntegrity.Checksum, receiver.Snapshot.Integrity);

            var active = new XModemReceiver(new XModemOptions(
                allowChecksumFallback: true, crcFallbackAttempts: 1));
            active.Advance(new XModemEvent.StartRequested());
            active.Advance(new XModemEvent.PacketReceived(new XModemPacket.Data(1, new byte[128], 128)));
            active.Advance(new XModemEvent.DataBlockAccepted());
            AssertControl(active.Advance(new XModemEvent.TimeoutElapsed()), XModemControlBytes.Nak);
            Assert.Equal(XModemIntegrity.Crc16, active.Snapshot.Integrity);
        }

        [Fact]
        public void SenderNegotiatesChecksumFromNakAndUses128ByteFrames()
        {
            var sender = new XModemSender(new XModemOptions(XModemMode.OneK));
            var request = Assert.IsType<XModemAction.RequestDataBlock>(
                Assert.Single(sender.Advance(new XModemEvent.PeerByteReceived(XModemControlBytes.Nak)).Actions));
            Assert.Equal(128, request.BlockSize);
            Assert.Equal(XModemIntegrity.Checksum, sender.Snapshot.Integrity);
        }

        [Fact]
        public void ChecksumOnlySenderWaitsForNak()
        {
            var sender = new XModemSender(new XModemOptions(XModemMode.Checksum));
            Assert.Empty(sender.Advance(new XModemEvent.PeerByteReceived(XModemControlBytes.CrcRequest)).Actions);
            Assert.Equal(XModemPhase.WaitingReceiverRequest, sender.Snapshot.Phase);
            Assert.IsType<XModemAction.RequestDataBlock>(
                Assert.Single(sender.Advance(new XModemEvent.PeerByteReceived(XModemControlBytes.Nak)).Actions));
        }

        [Fact]
        public void ReceiverDoesNotTreatInitialBlockZeroAsDuplicate()
        {
            var receiver = new XModemReceiver();
            receiver.Advance(new XModemEvent.StartRequested());
            var result = receiver.Advance(
                new XModemEvent.PacketReceived(new XModemPacket.Data(0, new byte[128], 128)));
            AssertControl(result, XModemControlBytes.Nak);
            Assert.Equal(0, receiver.Snapshot.BytesTransferred);
        }

        [Fact]
        public void PendingApplicationWriteDoesNotAckOrRedeliverDuplicate()
        {
            var receiver = new XModemReceiver();
            receiver.Advance(new XModemEvent.StartRequested());
            var data = new XModemEvent.PacketReceived(new XModemPacket.Data(1, new byte[128], 128));
            Assert.IsType<XModemAction.DeliverDataBlock>(Assert.Single(receiver.Advance(data).Actions));
            Assert.Empty(receiver.Advance(data).Actions);
            Assert.Empty(receiver.Advance(new XModemEvent.TimeoutElapsed()).Actions);
            AssertControl(receiver.Advance(new XModemEvent.DataBlockAccepted()), XModemControlBytes.Ack);
            AssertControl(receiver.Advance(data), XModemControlBytes.Ack);
            Assert.Equal(128, receiver.Snapshot.BytesTransferred);
        }

        [Fact]
        public void RejectedBlockMayBeRetransmittedAndPersisted()
        {
            var receiver = new XModemReceiver();
            receiver.Advance(new XModemEvent.StartRequested());
            var data = new XModemEvent.PacketReceived(new XModemPacket.Data(1, new byte[128], 128));
            receiver.Advance(data);
            AssertControl(receiver.Advance(new XModemEvent.DataBlockRejected()), XModemControlBytes.Nak);
            Assert.Equal(0, receiver.Snapshot.BytesTransferred);
            Assert.IsType<XModemAction.DeliverDataBlock>(Assert.Single(receiver.Advance(data).Actions));
            AssertControl(receiver.Advance(new XModemEvent.DataBlockAccepted()), XModemControlBytes.Ack);
        }

        [Fact]
        public void TwoConsecutiveCancelBytesAreRequiredAndTimeoutClearsFirst()
        {
            var sender = new XModemSender();
            Assert.Empty(sender.Advance(new XModemEvent.PeerByteReceived(XModemControlBytes.Can)).Actions);
            sender.Advance(new XModemEvent.TimeoutElapsed());
            Assert.Empty(sender.Advance(new XModemEvent.PeerByteReceived(XModemControlBytes.Can)).Actions);
            Assert.IsType<XModemAction.Cancel>(
                Assert.Single(sender.Advance(new XModemEvent.PeerByteReceived(XModemControlBytes.Can)).Actions));
            Assert.Equal(XModemPhase.Cancelled, sender.Snapshot.Phase);

            var receiver = new XModemReceiver();
            receiver.Advance(new XModemEvent.StartRequested());
            Assert.Empty(receiver.Advance(new XModemEvent.PeerByteReceived(XModemControlBytes.Can)).Actions);
            receiver.Advance(new XModemEvent.PeerByteReceived(0));
            Assert.Empty(receiver.Advance(new XModemEvent.PeerByteReceived(XModemControlBytes.Can)).Actions);
            Assert.IsType<XModemAction.Cancel>(
                Assert.Single(receiver.Advance(new XModemEvent.PeerByteReceived(XModemControlBytes.Can)).Actions));
        }

        [Fact]
        public void LocalCancelSendsTwoCanBytesAndTerminalStateIsStable()
        {
            var sender = new XModemSender();
            var result = sender.Advance(new XModemEvent.CancelRequested("Stopped"));
            Assert.Equal(new byte[] { 0x18, 0x18 }, result.Actions.OfType<XModemAction.SendControl>().Select(a => a.Value));
            Assert.Equal("Stopped", Assert.Single(result.Actions.OfType<XModemAction.Cancel>()).Reason);
            Assert.Empty(sender.Advance(new XModemEvent.PeerByteReceived(XModemControlBytes.CrcRequest)).Actions);
            Assert.Equal(XModemPhase.Cancelled, sender.Snapshot.Phase);
        }

        [Fact]
        public void TimeoutBudgetsAreBoundedForBothRoles()
        {
            var sender = new XModemSender(new XModemOptions(maxRetries: 1));
            Assert.Empty(sender.Advance(new XModemEvent.TimeoutElapsed()).Actions);
            Assert.Single(sender.Advance(new XModemEvent.TimeoutElapsed()).Actions.OfType<XModemAction.Fail>());
            Assert.Equal(XModemPhase.Faulted, sender.Snapshot.Phase);

            var receiver = new XModemReceiver(new XModemOptions(maxRetries: 1));
            receiver.Advance(new XModemEvent.StartRequested());
            AssertControl(receiver.Advance(new XModemEvent.TimeoutElapsed()), XModemControlBytes.CrcRequest);
            Assert.Single(receiver.Advance(new XModemEvent.TimeoutElapsed()).Actions.OfType<XModemAction.Fail>());
            Assert.Equal(XModemPhase.Faulted, receiver.Snapshot.Phase);
        }

        [Fact]
        public void SenderRejectsShortIntermediateBlockAndWrongBlockNumber()
        {
            foreach (var block in new[]
            {
                new XModemEvent.DataBlockReady(1, new byte[3], 3, false),
                new XModemEvent.DataBlockReady(2, new byte[128], 128, true),
                new XModemEvent.DataBlockReady(1, new byte[129], 129, true)
            })
            {
                var sender = new XModemSender();
                sender.Advance(new XModemEvent.PeerByteReceived(XModemControlBytes.CrcRequest));
                Assert.Single(sender.Advance(block).Actions.OfType<XModemAction.Fail>());
            }
        }

        [Fact]
        public void SenderSupportsPeerRequestingSecondEot()
        {
            var sender = new XModemSender();
            sender.Advance(new XModemEvent.PeerByteReceived(XModemControlBytes.CrcRequest));
            var first = sender.Advance(new XModemEvent.DataBlockReady(1, Array.Empty<byte>(), 0, true));
            Assert.IsType<XModemPacket.Eot>(Assert.IsType<XModemAction.SendPacket>(Assert.Single(first.Actions)).Packet);
            var second = sender.Advance(new XModemEvent.PeerByteReceived(XModemControlBytes.Nak));
            Assert.IsType<XModemPacket.Eot>(Assert.IsType<XModemAction.SendPacket>(Assert.Single(second.Actions)).Packet);
            Assert.IsType<XModemAction.Complete>(
                Assert.Single(sender.Advance(new XModemEvent.PeerByteReceived(XModemControlBytes.Ack)).Actions));
        }

        [Fact]
        public void InvalidFirstFrameLocksIntegritySoTimeoutCannotSwitchMidTransfer()
        {
            var receiver = new XModemReceiver(new XModemOptions(
                allowChecksumFallback: true, crcFallbackAttempts: 1));
            receiver.Advance(new XModemEvent.StartRequested());
            AssertControl(receiver.Advance(new XModemEvent.InvalidFrame()), XModemControlBytes.Nak);
            AssertControl(receiver.Advance(new XModemEvent.TimeoutElapsed()), XModemControlBytes.Nak);
            Assert.Equal(XModemIntegrity.Crc16, receiver.Snapshot.Integrity);
        }

        private static void AssertControl(XModemStepResult result, byte value)
        {
            Assert.Equal(value, Assert.IsType<XModemAction.SendControl>(Assert.Single(result.Actions)).Value);
        }
    }
}
