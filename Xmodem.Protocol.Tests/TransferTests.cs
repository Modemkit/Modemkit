namespace Xmodem.Protocol.Tests
{
    public sealed class TransferTests
    {
        public static IEnumerable<object[]> BoundaryCases()
        {
            foreach (var mode in Enum.GetValues<XModemMode>().Distinct())
            {
                foreach (var length in new[] { 0, 1, 127, 128, 129, 1023, 1024, 1025, 32769, 262145 })
                {
                    yield return new object[] { mode, length };
                }
            }
        }

        [Theory]
        [MemberData(nameof(BoundaryCases))]
        public void TransfersBoundaryLengthsAndRollover(XModemMode mode, int length)
        {
            var source = Enumerable.Range(0, length).Select(value => (byte)value).ToArray();
            var fixture = new XModemWireFixture(source, mode);
            fixture.Run();
            AssertCompleted(fixture, source);
            Assert.Equal(length, fixture.Sender.Snapshot.BytesTransferred);
            Assert.Equal(fixture.Received.Count, fixture.Receiver.Snapshot.BytesTransferred);
        }

        [Theory]
        [InlineData(XModemMode.Checksum)]
        [InlineData(XModemMode.Crc)]
        [InlineData(XModemMode.OneK)]
        public void CorruptFrameIsRetriedWithoutDuplicateDelivery(XModemMode mode)
        {
            var source = Enumerable.Range(0, 3000).Select(value => (byte)value).ToArray();
            var fixture = new XModemWireFixture(source, mode, 1);
            var corrupted = false;
            fixture.MutateWire = (toReceiver, bytes) =>
            {
                if (toReceiver && bytes.Length > 3 && !corrupted)
                {
                    bytes[17] ^= 0x80;
                    corrupted = true;
                }

                return bytes;
            };
            fixture.Run();
            Assert.True(corrupted);
            AssertCompleted(fixture, source);
            var firstFrames = fixture.Transcript.Where(item => item.ToReceiver && item.Bytes.Length > 3 && item.Bytes[1] == 1).ToArray();
            Assert.Equal(2, firstFrames.Length);
            Assert.Equal(firstFrames[0].Bytes, firstFrames[1].Bytes);
        }

        [Fact]
        public void DroppedDataAckRetransmitsIdenticalBlockWithoutDuplicateDelivery()
        {
            var source = Enumerable.Range(0, 300).Select(value => (byte)value).ToArray();
            var fixture = new XModemWireFixture(source, XModemMode.Crc);
            var dropped = false;
            fixture.MutateWire = (toReceiver, bytes) =>
            {
                if (!toReceiver && bytes[0] == XModemControlBytes.Ack && !dropped)
                {
                    dropped = true;
                    return null;
                }

                return bytes;
            };
            fixture.Run();
            AssertCompleted(fixture, source);
            Assert.True(dropped);
            Assert.Equal(2, fixture.Transcript.Count(item => item.ToReceiver && item.Bytes.Length > 3 && item.Bytes[1] == 1));
        }

        [Fact]
        public void DroppedEotAckIsRecoveredByCompletedReceiver()
        {
            var source = new byte[] { 7 };
            var fixture = new XModemWireFixture(source, XModemMode.Crc);
            var dropped = false;
            fixture.MutateWire = (toReceiver, bytes) =>
            {
                if (!toReceiver && bytes[0] == XModemControlBytes.Ack
                    && fixture.Receiver.Snapshot.Phase == XModemPhase.Completed && !dropped)
                {
                    dropped = true;
                    return null;
                }

                return bytes;
            };
            fixture.Run();
            AssertCompleted(fixture, source);
            Assert.True(dropped);
            Assert.Equal(2, fixture.Transcript.Count(item => item.ToReceiver && item.Bytes.Length == 1 && item.Bytes[0] == 4));
        }

        [Fact]
        public void TruncatedFrameIsDiscardedOnTimeoutBeforeRetransmission()
        {
            var source = new byte[1025];
            var fixture = new XModemWireFixture(source, XModemMode.OneK);
            var truncated = false;
            fixture.MutateWire = (toReceiver, bytes) =>
            {
                if (toReceiver && bytes.Length > 3 && !truncated)
                {
                    truncated = true;
                    return bytes.Take(17).ToArray();
                }

                return bytes;
            };
            fixture.Run();
            AssertCompleted(fixture, source);
        }

        [Fact]
        public void SenderRetryLimitCancelsPeerAndTerminates()
        {
            var fixture = new XModemWireFixture(new byte[129], XModemMode.Crc, maxRetries: 2);
            fixture.MutateWire = (toReceiver, bytes) =>
                !toReceiver && bytes[0] == XModemControlBytes.Ack ? null : bytes;
            fixture.Run();
            Assert.Equal(XModemPhase.Faulted, fixture.Sender.Snapshot.Phase);
            Assert.Equal(XModemPhase.Cancelled, fixture.Receiver.Snapshot.Phase);
            Assert.Equal(3, fixture.Transcript.Count(item => item.ToReceiver && item.Bytes.Length > 3));
        }

        [Fact]
        public void OneKSenderUsesSohForSmallFinalBlock()
        {
            var fixture = new XModemWireFixture(new byte[1025], XModemMode.OneK);
            fixture.Run();
            var frames = fixture.Transcript.Where(item => item.ToReceiver && item.Bytes.Length > 3).ToArray();
            Assert.Equal(new byte[] { XModemControlBytes.Stx, XModemControlBytes.Soh }, frames.Select(item => item.Bytes[0]));
        }

        private static void AssertCompleted(XModemWireFixture fixture, byte[] source)
        {
            Assert.Equal(XModemPhase.Completed, fixture.Sender.Snapshot.Phase);
            Assert.Equal(XModemPhase.Completed, fixture.Receiver.Snapshot.Phase);
            Assert.Equal(source, fixture.Received.Take(source.Length));
            Assert.All(fixture.Received.Skip(source.Length), value => Assert.Equal(0x1A, value));
        }
    }
}
