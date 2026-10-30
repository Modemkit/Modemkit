using ModemKit.Testing;

namespace ModemKit.IntegrationTests
{
    public sealed class DuplexLinkTests
    {
        [Fact]
        public void WindowResynchronizationPurgesOnlyUndeliveredBytesInOneDirection()
        {
            var link = new InMemoryDuplexLink();
            link.WriteFromSender(new byte[] { 1, 2, 3 });
            link.WriteFromReceiver(new byte[] { 6 });
            Assert.Equal(new byte[] { 1 }, link.ReadAtReceiver(1));
            Assert.Equal(2, link.DiscardPending(WireDirection.SenderToReceiver));
            Assert.Empty(link.ReadAtReceiver(10));
            Assert.Equal(new byte[] { 6 }, link.ReadAtSender(10));
            Assert.Equal(2, link.Trace.Count);
        }

        [Fact]
        public void ReadsSplitAndCoalesceWritesWithoutSharingBuffers()
        {
            var link = new InMemoryDuplexLink();
            var source = new byte[] { 1, 2, 3 };
            link.WriteFromSender(source);
            link.WriteFromSender(new byte[] { 4, 5 });
            source[0] = 99;
            Assert.Equal(new byte[] { 1, 2 }, link.ReadAtReceiver(2));
            Assert.Equal(new byte[] { 3, 4, 5 }, link.ReadAtReceiver(10));
            Assert.Empty(link.ReadAtSender(10));
        }

        [Fact]
        public void FaultsApplyOnlyToSelectedDirectionAndOccurrence()
        {
            var link = new InMemoryDuplexLink();
            var fault = WireFault.Drop(WireDirection.ReceiverToSender, b => b.Length == 1 && b[0] == 6);
            link.Faults.Add(fault);
            link.WriteFromSender(new byte[] { 6 });
            link.WriteFromReceiver(new byte[] { 6 });
            link.WriteFromReceiver(new byte[] { 6 });
            Assert.Equal(new byte[] { 6 }, link.ReadAtReceiver(10));
            Assert.Equal(new byte[] { 6 }, link.ReadAtSender(10));
            Assert.Equal(0, fault.Remaining);
            Assert.Empty(link.Trace[1].Transmitted);
        }

        [Fact]
        public void CorruptionDuplicationAndTruncationComposeInOrder()
        {
            var link = new InMemoryDuplexLink();
            link.Faults.Add(WireFault.Corrupt(WireDirection.SenderToReceiver, _ => true, 1, 0x80));
            link.Faults.Add(WireFault.Duplicate(WireDirection.SenderToReceiver, _ => true));
            link.Faults.Add(WireFault.Truncate(WireDirection.SenderToReceiver, _ => true, 5));
            link.WriteFromSender(new byte[] { 1, 2, 3 });
            Assert.Equal(new byte[] { 1, 0x82, 3, 1, 0x82 }, link.ReadAtReceiver(10));
        }

        [Fact]
        public void StalledTransferIsBoundedAndIncludesWireEvidence()
        {
            var link = new InMemoryDuplexLink();
            link.WriteFromSender(new byte[] { 0x43 });
            var timeouts = 0;
            var error = Assert.Throws<InvalidOperationException>(() =>
                link.Run(_ => { }, _ => { }, () => false, () => timeouts++, maxSteps: 4));
            Assert.Equal(3, timeouts);
            Assert.Contains("SenderToReceiver", error.Message);
            Assert.Contains("43", error.Message);
        }

        [Fact]
        public void TranscriptAndBufferedBytesAreBounded()
        {
            var link = new InMemoryDuplexLink(maxBufferedBytes: 3, maxTraceEntries: 2);
            link.WriteFromSender(new byte[] { 1 });
            link.WriteFromSender(new byte[] { 2 });
            link.WriteFromReceiver(new byte[] { 3 });
            Assert.Equal(2, link.Trace.Count);
            Assert.Equal(2, link.Trace[0].Sequence);
            Assert.Throws<InvalidOperationException>(() => link.WriteFromSender(new byte[] { 4 }));
            Assert.Equal(3, link.PendingBytes);
        }

        [Fact]
        public void RunDrainsLastAcknowledgementAfterCompletion()
        {
            var link = new InMemoryDuplexLink();
            link.WriteFromReceiver(new byte[] { 6 });
            var delivered = false;
            link.Run(
                bytes => delivered = bytes.SequenceEqual(new byte[] { 6 }),
                _ => { },
                () => true,
                () => throw new InvalidOperationException("Unexpected timeout."));
            Assert.True(delivered);
            Assert.Equal(0, link.PendingBytes);
        }
    }
}
