namespace Jmodem.Protocol.Tests
{
    public sealed class StateMachineTests
    {
        [Fact]
        public void ReceiverInitiatesNakAndSenderAcknowledgesBeforeRequestingData()
        {
            var receiver = new JModemReceiver();
            var sender = new JModemSender();
            var request = Assert.Single(receiver.Advance(new JModemEvent.StartRequested()).Actions.OfType<JModemAction.SendBytes>());
            Assert.Equal(new byte[] { 0x15 }, request.Bytes);
            var response = sender.Advance(new JModemEvent.PeerBytesReceived(request.Bytes));
            Assert.Equal(new byte[] { 6 }, Assert.Single(response.Actions.OfType<JModemAction.SendBytes>()).Bytes);
            Assert.Equal(512, Assert.Single(response.Actions.OfType<JModemAction.RequestDataBlock>()).BlockSize);
            Assert.Empty(receiver.Advance(new JModemEvent.PeerByteReceived(6)).Actions);
            Assert.Empty(receiver.Advance(new JModemEvent.TimeoutElapsed()).Actions);
            Assert.Equal(JModemPhase.WaitingPacket, receiver.Snapshot.Phase);
        }

        [Fact]
        public void QuietTimeoutBeforeStartDoesNotTransmit()
        {
            var receiver = new JModemReceiver();
            Assert.Empty(receiver.Advance(new JModemEvent.TimeoutElapsed()).Actions);
            Assert.Equal(JModemPhase.Idle, receiver.Snapshot.Phase);
        }

        [Fact]
        public void CleanAcknowledgementsGrowBy512UpTo8192()
        {
            var sender = new JModemSender();
            sender.Advance(new JModemEvent.PeerByteReceived(0x15));
            for (var block = 1; block <= 20; block++)
            {
                var size = sender.Snapshot.BlockSize;
                Assert.Equal(Math.Min(8192, block * 512), size);
                sender.Advance(new JModemEvent.DataBlockReady(block, new byte[size], size, false));
                var next = sender.Advance(new JModemEvent.PeerByteReceived(6));
                Assert.Equal(Math.Min(8192, (block + 1) * 512), Assert.Single(next.Actions.OfType<JModemAction.RequestDataBlock>()).BlockSize);
            }
        }

        [Theory]
        [InlineData(JModemBlockSizingPolicy.Documented, 1, 256)]
        [InlineData(JModemBlockSizingPolicy.Documented, 2, 128)]
        [InlineData(JModemBlockSizingPolicy.Documented, 3, 64)]
        [InlineData(JModemBlockSizingPolicy.Documented, 5, 64)]
        [InlineData(JModemBlockSizingPolicy.SourceCompatible, 1, 512)]
        [InlineData(JModemBlockSizingPolicy.SourceCompatible, 2, 256)]
        [InlineData(JModemBlockSizingPolicy.SourceCompatible, 3, 170)]
        [InlineData(JModemBlockSizingPolicy.SourceCompatible, 9, 64)]
        public void RetriesRetainExactFrameAndOnlyShrinkTheFollowingBlock(
            JModemBlockSizingPolicy policy, int retries, int expectedNextSize)
        {
            var sender = new JModemSender(new JModemOptions(blockSizingPolicy: policy));
            sender.Advance(new JModemEvent.PeerByteReceived(0x15));
            var payload = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();
            var original = Assert.Single(sender.Advance(new JModemEvent.DataBlockReady(1, payload, 512, false))
                .Actions.OfType<JModemAction.SendBytes>()).Bytes;
            for (var retry = 0; retry < retries; retry++)
            {
                var resent = sender.Advance(new JModemEvent.PeerByteReceived(0x15));
                Assert.Equal(original, Assert.Single(resent.Actions.OfType<JModemAction.SendBytes>()).Bytes);
                Assert.Empty(resent.Actions.OfType<JModemAction.RequestDataBlock>());
                Assert.Equal(512, sender.Snapshot.BlockSize);
            }

            var next = sender.Advance(new JModemEvent.PeerByteReceived(6));
            Assert.Equal(expectedNextSize, Assert.Single(next.Actions.OfType<JModemAction.RequestDataBlock>()).BlockSize);
            Assert.Equal(512, sender.Snapshot.BytesTransferred);
        }

        [Fact]
        public void FinalBlockAckCompletesWithoutEotAndEmptyFilesRemainExact()
        {
            var sender = new JModemSender();
            sender.Advance(new JModemEvent.PeerByteReceived(0x15));
            var frame = Assert.Single(sender.Advance(new JModemEvent.DataBlockReady(1, Array.Empty<byte>(), 0, true))
                .Actions.OfType<JModemAction.SendBytes>()).Bytes;
            Assert.Equal(Convert.FromHexString("060005012a18"), frame);
            var complete = sender.Advance(new JModemEvent.PeerByteReceived(6));
            Assert.IsType<JModemAction.Complete>(Assert.Single(complete.Actions));
            Assert.Equal(0, complete.Snapshot.BytesTransferred);
        }

        [Fact]
        public void ReceiverPersistsBeforeAckAndReacksDuplicatesWithoutWritingAgain()
        {
            var receiver = ReadyReceiver();
            var wire = new JModemPacketEncoder().Encode(new JModemPacket(1, new byte[] { 1, 2, 0x1A }, false));
            Assert.Single(receiver.Advance(new JModemEvent.PeerBytesReceived(wire)).Actions.OfType<JModemAction.DeliverDataBlock>());
            Assert.Empty(receiver.Advance(new JModemEvent.PeerBytesReceived(wire)).Actions);
            Assert.Empty(receiver.Advance(new JModemEvent.TimeoutElapsed()).Actions);
            var accepted = receiver.Advance(new JModemEvent.DataBlockAccepted());
            Assert.Equal(new byte[] { 6 }, Assert.Single(accepted.Actions.OfType<JModemAction.SendBytes>()).Bytes);
            var duplicate = receiver.Advance(new JModemEvent.PeerBytesReceived(wire));
            Assert.Empty(duplicate.Actions.OfType<JModemAction.DeliverDataBlock>());
            Assert.Equal(new byte[] { 6 }, Assert.Single(duplicate.Actions.OfType<JModemAction.SendBytes>()).Bytes);
            Assert.Equal(3, receiver.Snapshot.BytesTransferred);
        }

        [Fact]
        public void ReceiverSequenceRolloverPreservesLogicalBlockNumbers()
        {
            var receiver = ReadyReceiver();
            var encoder = new JModemPacketEncoder();
            for (var block = 1; block <= 258; block++)
            {
                var result = receiver.Advance(new JModemEvent.PeerBytesReceived(
                    encoder.Encode(new JModemPacket(block, new[] { (byte)block }, block == 258))));
                Assert.Equal(block, Assert.Single(result.Actions.OfType<JModemAction.DeliverDataBlock>()).BlockNumber);
                receiver.Advance(new JModemEvent.DataBlockAccepted());
            }

            Assert.Equal(JModemPhase.Completed, receiver.Snapshot.Phase);
            Assert.Equal(258, receiver.Snapshot.BytesTransferred);
        }

        [Fact]
        public void TruncatedOrCorruptFramesDrainUntilQuietBeforeNak()
        {
            var receiver = ReadyReceiver();
            var wire = new JModemPacketEncoder().Encode(new JModemPacket(1, new byte[] { 1, 2, 3 }, true));
            wire[^1] ^= 1;
            var bad = receiver.Advance(new JModemEvent.PeerBytesReceived(wire));
            Assert.Equal(JModemPhase.DrainingInput, bad.Snapshot.Phase);
            Assert.IsType<JModemAction.DiscardInput>(Assert.Single(bad.Actions));
            Assert.Empty(receiver.Advance(new JModemEvent.PeerBytesReceived(new byte[] { 0, 1, 2, 3, 4 })).Actions);
            var quiet = receiver.Advance(new JModemEvent.TimeoutElapsed());
            Assert.Equal(new byte[] { 0x15 }, Assert.Single(quiet.Actions.OfType<JModemAction.SendBytes>()).Bytes);
            wire[^1] ^= 1;
            Assert.Single(receiver.Advance(new JModemEvent.PeerBytesReceived(wire)).Actions.OfType<JModemAction.DeliverDataBlock>());
        }

        [Fact]
        public void RejectedStorageDoesNotAdvanceAndFinalAckCanBeRetried()
        {
            var receiver = ReadyReceiver();
            var wire = new JModemPacketEncoder().Encode(new JModemPacket(1, new byte[] { 7 }, true));
            receiver.Advance(new JModemEvent.PeerBytesReceived(wire));
            var rejected = receiver.Advance(new JModemEvent.DataBlockRejected());
            Assert.Equal(new byte[] { 0x15 }, Assert.Single(rejected.Actions.OfType<JModemAction.SendBytes>()).Bytes);
            Assert.Equal(0, receiver.Snapshot.BytesTransferred);
            receiver.Advance(new JModemEvent.PeerBytesReceived(wire));
            receiver.Advance(new JModemEvent.DataBlockAccepted());
            var repeated = receiver.Advance(new JModemEvent.PeerBytesReceived(wire));
            Assert.Equal(new byte[] { 6 }, Assert.Single(repeated.Actions.OfType<JModemAction.SendBytes>()).Bytes);
            Assert.Empty(repeated.Actions.OfType<JModemAction.Complete>());
            Assert.Equal(1, receiver.Snapshot.BytesTransferred);
        }

        [Fact]
        public void RetryExhaustionSendsSixCanBytes()
        {
            var sender = new JModemSender(new JModemOptions(maxRetries: 1));
            sender.Advance(new JModemEvent.PeerByteReceived(0x15));
            sender.Advance(new JModemEvent.DataBlockReady(1, new byte[] { 1 }, 1, true));
            sender.Advance(new JModemEvent.TimeoutElapsed());
            var failed = sender.Advance(new JModemEvent.TimeoutElapsed());
            Assert.Equal(JModemPhase.Faulted, failed.Snapshot.Phase);
            Assert.Single(failed.Actions.OfType<JModemAction.Fail>());
            Assert.Equal(Enumerable.Repeat((byte)0x18, 6), Assert.Single(failed.Actions.OfType<JModemAction.SendBytes>()).Bytes);
        }

        [Fact]
        public void ReceiverRecognizesSixCanBytesBetweenPackets()
        {
            var receiver = ReadyReceiver();
            var cancelled = receiver.Advance(new JModemEvent.PeerBytesReceived(Enumerable.Repeat((byte)0x18, 6).ToArray()));
            Assert.IsType<JModemAction.Cancel>(Assert.Single(cancelled.Actions));
            Assert.Equal(JModemPhase.Cancelled, receiver.Snapshot.Phase);
        }

        [Fact]
        public void InputChunkAndPacketSequenceAreBounded()
        {
            var receiver = ReadyReceiver();
            var wrong = new JModemPacketEncoder().Encode(new JModemPacket(0, new byte[] { 1 }, true));
            var rejected = receiver.Advance(new JModemEvent.PeerBytesReceived(wrong));
            Assert.Equal(new byte[] { 0x15 }, Assert.Single(rejected.Actions.OfType<JModemAction.SendBytes>()).Bytes);
            Assert.Single(receiver.Advance(new JModemEvent.PeerBytesReceived(new byte[65537])).Actions.OfType<JModemAction.Fail>());
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(15, false)]
        [InlineData(18, false)]
        [InlineData(512, false)]
        [InlineData(512, true)]
        public void LostStartupAckIsRecoveredWithoutConfusingControlsAndFrameBytes(int length, bool compressed)
        {
            var receiver = new JModemReceiver();
            receiver.Advance(new JModemEvent.StartRequested());
            var payload = Enumerable.Repeat((byte)0x18, length).ToArray();
            var wire = new JModemPacketEncoder(compressed).Encode(new JModemPacket(1, payload, true));
            var actions = new List<JModemAction>();
            foreach (var value in wire)
            {
                actions.AddRange(receiver.Advance(new JModemEvent.PeerByteReceived(value)).Actions);
            }

            Assert.Empty(actions.OfType<JModemAction.Cancel>());
            Assert.Empty(actions.OfType<JModemAction.Fail>());
            Assert.Equal(payload, Assert.Single(actions.OfType<JModemAction.DeliverDataBlock>()).Payload);
            Assert.Single(receiver.Advance(new JModemEvent.DataBlockAccepted()).Actions.OfType<JModemAction.Complete>());
        }

        [Fact]
        public void HandshakeAckAndCompressedFirstFrameCanBeCoalesced()
        {
            var receiver = new JModemReceiver();
            receiver.Advance(new JModemEvent.StartRequested());
            var frame = new JModemPacketEncoder().Encode(new JModemPacket(1, new byte[512], true));
            var result = receiver.Advance(new JModemEvent.PeerBytesReceived(new byte[] { 6 }.Concat(frame).ToArray()));
            Assert.Equal(new byte[512], Assert.Single(result.Actions.OfType<JModemAction.DeliverDataBlock>()).Payload);
        }

        [Fact]
        public void StartupCancellationAfterAnUncommittedAckIsRecognized()
        {
            var receiver = new JModemReceiver();
            receiver.Advance(new JModemEvent.StartRequested());
            var result = receiver.Advance(new JModemEvent.PeerBytesReceived(
                new byte[] { 6 }.Concat(Enumerable.Repeat((byte)24, 6)).ToArray()));
            Assert.Equal(JModemPhase.Cancelled, result.Snapshot.Phase);
            Assert.IsType<JModemAction.Cancel>(Assert.Single(result.Actions));
        }

        [Fact]
        public void DamagedFinalRetryDoesNotPreventLaterIdenticalRetryFromBeingAcknowledged()
        {
            var receiver = ReadyReceiver();
            var payload = Enumerable.Range(0, 129).Select(i => (byte)i).ToArray();
            var frame = new JModemPacketEncoder().Encode(new JModemPacket(1, payload, true));
            receiver.Advance(new JModemEvent.PeerBytesReceived(frame));
            receiver.Advance(new JModemEvent.DataBlockAccepted());
            var damaged = (byte[])frame.Clone();
            damaged[^1] ^= 1;
            Assert.Empty(receiver.Advance(new JModemEvent.PeerBytesReceived(damaged)).Actions);
            var actions = new List<JModemAction>();
            foreach (var value in frame)
            {
                actions.AddRange(receiver.Advance(new JModemEvent.PeerByteReceived(value)).Actions);
            }

            Assert.Equal(new byte[] { 6 }, Assert.Single(actions.OfType<JModemAction.SendBytes>()).Bytes);
            Assert.Empty(actions.OfType<JModemAction.Complete>());
            Assert.Equal(JModemPhase.Completed, receiver.Snapshot.Phase);
            Assert.Equal(payload.Length, receiver.Snapshot.BytesTransferred);
        }

        private static JModemReceiver ReadyReceiver()
        {
            var receiver = new JModemReceiver();
            receiver.Advance(new JModemEvent.StartRequested());
            receiver.Advance(new JModemEvent.PeerByteReceived(6));
            receiver.Advance(new JModemEvent.TimeoutElapsed());
            return receiver;
        }
    }
}
