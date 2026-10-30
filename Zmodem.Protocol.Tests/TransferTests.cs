using Zmodem.Protocol;

namespace Zmodem.Protocol.Tests;

public sealed class TransferTests
{
    [Theory]
    [InlineData(0, false, 1)]
    [InlineData(1, true, 1)]
    [InlineData(127, false, 7)]
    [InlineData(128, true, 17)]
    [InlineData(129, false, 4096)]
    [InlineData(1023, true, 31)]
    [InlineData(1024, false, 1024)]
    [InlineData(1025, true, 5)]
    [InlineData(32769, true, 4096)]
    public void WireTransferPreservesExactBytes(int length, bool crc32, int chunk)
    {
        byte[] payload = Enumerable.Range(0, length).Select(i => (byte)(i * 13)).ToArray();
        var fixture = new WireFixture(new[] { payload }, crc32, chunk);
        fixture.Run();
        Assert.Equal(payload, Assert.Single(fixture.Received));
    }

    [Fact]
    public void EmptyBatchCompletes()
    {
        var fixture = new WireFixture(Array.Empty<byte[]>());
        fixture.Run();
        Assert.Empty(fixture.Received);
    }

    [Fact]
    public void BatchIncludesEmptyAndBinaryFiles()
    {
        byte[][] files = { new byte[] { 1, 2, 3 }, Array.Empty<byte>(), Enumerable.Range(0, 256).Select(i => (byte)i).ToArray() };
        var fixture = new WireFixture(files);
        fixture.Run();
        Assert.Equal(files.Length, fixture.Received.Count);
        for (int i = 0; i < files.Length; i++)
        {
            Assert.Equal(files[i], fixture.Received[i]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(333)]
    [InlineData(2048)]
    public void ResumeStartsAtApplicationApprovedOffset(uint offset)
    {
        byte[] payload = Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray();
        var fixture = new WireFixture(new[] { payload }, resumeOffset: offset);
        fixture.Run();
        Assert.Equal(payload, Assert.Single(fixture.Received));
        Assert.Equal(payload.Length - offset, fixture.Trace.OfType<ZModemAction.DeliverDataBlock>().Sum(x => x.Payload.Length));
    }

    [Fact]
    public void SkipContinuesToNextFile()
    {
        var fixture = new WireFixture(new[] { new byte[] { 1 }, new byte[] { 2, 3 } }, skipFirst: true);
        fixture.Run();
        Assert.Equal(new byte[] { 2, 3 }, Assert.Single(fixture.Received));
        Assert.Equal(2, fixture.Trace.OfType<ZModemAction.FileSkipped>().Count());
    }

    [Theory]
    [InlineData(ZModemHeaderType.Zack)]
    [InlineData(ZModemHeaderType.Zrpos)]
    [InlineData(ZModemHeaderType.Zfin)]
    public void DroppedReceiverResponseRecovers(ZModemHeaderType dropType)
    {
        bool dropped = false;
        byte[] payload = Enumerable.Range(0, 2301).Select(i => (byte)i).ToArray();
        var fixture = new WireFixture(new[] { payload });
        fixture.Mutate = (sender, wire) =>
        {
            if (!sender && !dropped && WireFixture.HeaderType(wire) == dropType)
            {
                dropped = true;
                return null;
            }

            return wire;
        };
        fixture.Run();
        Assert.True(dropped);
        Assert.Equal(payload, Assert.Single(fixture.Received));
    }

    [Fact]
    public void CorruptDataRetransmitsFromLastAcceptedOffset()
    {
        bool corrupted = false;
        byte[] payload = Enumerable.Range(0, 2500).Select(i => (byte)i).ToArray();
        var fixture = new WireFixture(new[] { payload });
        fixture.Mutate = (sender, wire) =>
        {
            if (sender && !corrupted && WireFixture.HeaderType(wire) == ZModemHeaderType.Zdata)
            {
                corrupted = true;
                wire[wire.Length - 2] ^= 1;
            }

            return wire;
        };
        fixture.Run();
        Assert.True(corrupted);
        Assert.Equal(payload, Assert.Single(fixture.Received));
    }

    [Fact]
    public void LostEofResponseDoesNotDeliverFileTwice()
    {
        int initCount = 0;
        var fixture = new WireFixture(new[] { new byte[] { 1, 2, 3 } });
        fixture.Mutate = (sender, wire) => !sender && WireFixture.HeaderType(wire) == ZModemHeaderType.Zrinit && ++initCount == 2 ? null : wire;
        fixture.Run();
        Assert.Single(fixture.Received);
    }

    [Fact]
    public void RetryExhaustionSendsCancellation()
    {
        var sender = new ZModemSender(new ZModemOptions(maxRetries: 2));
        sender.Advance(new ZModemEvent.StartRequested());
        Assert.Equal(ZModemPhase.WaitingReceiver, sender.Advance(new ZModemEvent.TimeoutElapsed()).Snapshot.Phase);
        Assert.Equal(ZModemPhase.WaitingReceiver, sender.Advance(new ZModemEvent.TimeoutElapsed()).Snapshot.Phase);
        ZModemStepResult failed = sender.Advance(new ZModemEvent.TimeoutElapsed());
        Assert.Equal(ZModemPhase.Faulted, failed.Snapshot.Phase);
        Assert.Single(failed.Actions.OfType<ZModemAction.Fail>());
        Assert.Equal(ZModemCodec.EncodeCancellation(), Assert.Single(failed.Actions.OfType<ZModemAction.SendBytes>()).Bytes);
    }

    [Fact]
    public void PeerCancellationWorksWhileApplicationIsDeciding()
    {
        var receiver = new ZModemReceiver();
        receiver.Advance(new ZModemEvent.StartRequested());
        receiver.Advance(new ZModemEvent.PeerBytesReceived(
            ZModemCodec.EncodeHeader(new ZModemHeader(ZModemHeaderType.Zfile, 0, ZModemHeaderFormat.Binary32))
            .Concat(ZModemCodec.EncodeData(System.Text.Encoding.ASCII.GetBytes("a\0" + "1\0"), ZModemDataEnd.EndAndAck)).ToArray()));
        Assert.Equal(ZModemPhase.WaitingFileDecision, receiver.Snapshot.Phase);
        var cancelled = receiver.Advance(new ZModemEvent.PeerBytesReceived(ZModemCodec.EncodeCancellation()));
        Assert.Equal(ZModemPhase.Cancelled, cancelled.Snapshot.Phase);
        Assert.Single(cancelled.Actions.OfType<ZModemAction.Cancel>());
    }
}
