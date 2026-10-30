using System.Text;
using Zmodem.Protocol;

namespace Zmodem.Protocol.Tests;

public sealed class ReceiverTests
{
    [Fact]
    public void CoalescedStreamingFramesWaitForPersistenceDecisions()
    {
        var receiver = BeginFile(3);
        byte[] wire = Header(ZModemHeaderType.Zdata, 0, ZModemHeaderFormat.Binary32)
            .Concat(ZModemCodec.EncodeData(new byte[] { 1 }, ZModemDataEnd.Continue))
            .Concat(ZModemCodec.EncodeData(new byte[] { 2 }, ZModemDataEnd.ContinueAndAck))
            .Concat(ZModemCodec.EncodeData(new byte[] { 3 }, ZModemDataEnd.End))
            .Concat(Header(ZModemHeaderType.Zeof, 3)).ToArray();

        var step = receiver.Advance(new ZModemEvent.PeerBytesReceived(wire));
        Assert.Equal(new byte[] { 1 }, Assert.Single(step.Actions.OfType<ZModemAction.DeliverDataBlock>()).Payload);
        Assert.Empty(step.Actions.OfType<ZModemAction.SendBytes>());
        Assert.Empty(receiver.Advance(new ZModemEvent.TimeoutElapsed()).Actions);

        step = receiver.Advance(new ZModemEvent.DataBlockAccepted());
        Assert.Equal(new byte[] { 2 }, Assert.Single(step.Actions.OfType<ZModemAction.DeliverDataBlock>()).Payload);
        Assert.Empty(step.Actions.OfType<ZModemAction.SendBytes>());
        step = receiver.Advance(new ZModemEvent.DataBlockAccepted());
        Assert.Equal(new byte[] { 3 }, Assert.Single(step.Actions.OfType<ZModemAction.DeliverDataBlock>()).Payload);
        Assert.Equal(ZModemHeaderType.Zack, WireFixture.HeaderType(Assert.Single(step.Actions.OfType<ZModemAction.SendBytes>()).Bytes));
        step = receiver.Advance(new ZModemEvent.DataBlockAccepted());
        Assert.Equal(3u, Assert.Single(step.Actions.OfType<ZModemAction.FileCompleted>()).Length);
        Assert.Equal(ZModemPhase.WaitingFile, step.Snapshot.Phase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void DeclaredLengthIsAnEstimateAndEofDefinesActualLength(uint estimate)
    {
        var receiver = BeginFile(estimate);
        byte[] wire = Header(ZModemHeaderType.Zdata, 0, ZModemHeaderFormat.Binary32)
            .Concat(ZModemCodec.EncodeData(new byte[] { 1, 2, 3 }, ZModemDataEnd.End)).ToArray();
        receiver.Advance(new ZModemEvent.PeerBytesReceived(wire));
        receiver.Advance(new ZModemEvent.DataBlockAccepted());
        var step = receiver.Advance(new ZModemEvent.PeerBytesReceived(Header(ZModemHeaderType.Zeof, 3)));
        Assert.Equal(3u, Assert.Single(step.Actions.OfType<ZModemAction.FileCompleted>()).Length);
    }

    [Fact]
    public void EofWithMismatchingReceivedPositionIsIgnored()
    {
        var receiver = BeginFile(100);
        var step = receiver.Advance(new ZModemEvent.PeerBytesReceived(Header(ZModemHeaderType.Zeof, 100)));
        Assert.Empty(step.Actions);
        Assert.Equal(ZModemPhase.WaitingData, step.Snapshot.Phase);
        Assert.Equal(0u, step.Snapshot.Position);
    }

    [Fact]
    public void RejectedPersistenceRequestsOriginalOffsetAndDoesNotAcknowledge()
    {
        var receiver = BeginFile(3);
        byte[] wire = Header(ZModemHeaderType.Zdata, 0, ZModemHeaderFormat.Binary32)
            .Concat(ZModemCodec.EncodeData(new byte[] { 1, 2, 3 }, ZModemDataEnd.EndAndAck)).ToArray();
        receiver.Advance(new ZModemEvent.PeerBytesReceived(wire));
        var rejected = receiver.Advance(new ZModemEvent.DataBlockRejected());
        Assert.Equal(0u, rejected.Snapshot.Position);
        var header = Assert.IsType<ZModemWireEvent.HeaderReceived>(
            Assert.Single(new ZModemStreamDecoder().Feed(Assert.Single(rejected.Actions.OfType<ZModemAction.SendBytes>()).Bytes))).Header;
        Assert.Equal(ZModemHeaderType.Zrpos, header.Type);
        Assert.Equal(0u, header.Position);
        var retried = receiver.Advance(new ZModemEvent.PeerBytesReceived(wire));
        Assert.Single(retried.Actions.OfType<ZModemAction.DeliverDataBlock>());
    }

    [Fact]
    public void DuplicateFileHeaderDoesNotPromptAgain()
    {
        var receiver = BeginFile(3);
        var step = receiver.Advance(new ZModemEvent.PeerBytesReceived(FileHeader(3)));
        Assert.Empty(step.Actions.OfType<ZModemAction.OfferFileHeader>());
        Assert.Equal(ZModemHeaderType.Zrpos, WireFixture.HeaderType(Assert.Single(step.Actions.OfType<ZModemAction.SendBytes>()).Bytes));
    }

    [Fact]
    public void MalformedFilenameDoesNotEscapeToApplication()
    {
        var receiver = new ZModemReceiver();
        receiver.Advance(new ZModemEvent.StartRequested());
        byte[] wire = Header(ZModemHeaderType.Zfile, 0, ZModemHeaderFormat.Binary32)
            .Concat(ZModemCodec.EncodeData(Encoding.ASCII.GetBytes("unterminated"), ZModemDataEnd.EndAndAck)).ToArray();
        var step = receiver.Advance(new ZModemEvent.PeerBytesReceived(wire));
        Assert.Empty(step.Actions.OfType<ZModemAction.OfferFileHeader>());
        Assert.Equal(1, step.Snapshot.RetryCount);
    }

    [Fact]
    public void CommandsAreRejectedWithoutExecutingAnything()
    {
        var receiver = new ZModemReceiver();
        receiver.Advance(new ZModemEvent.StartRequested());
        byte[] wire = Header(ZModemHeaderType.Zcommand, 0, ZModemHeaderFormat.Binary32)
            .Concat(ZModemCodec.EncodeData(Encoding.ASCII.GetBytes("echo arbitrary\0"), ZModemDataEnd.EndAndAck)).ToArray();
        var step = receiver.Advance(new ZModemEvent.PeerBytesReceived(wire));
        Assert.Equal(ZModemHeaderType.Zferr, WireFixture.HeaderType(Assert.Single(step.Actions.OfType<ZModemAction.SendBytes>()).Bytes));
    }

    [Fact]
    public void PendingApplicationDataIsBounded()
    {
        var receiver = new ZModemReceiver(new ZModemOptions(maxBufferedBytes: 4096));
        receiver.Advance(new ZModemEvent.StartRequested());
        receiver.Advance(new ZModemEvent.PeerBytesReceived(FileHeader(10000)));
        byte[] frame = Header(ZModemHeaderType.Zdata, 0, ZModemHeaderFormat.Binary32)
            .Concat(ZModemCodec.EncodeData(new byte[1024], ZModemDataEnd.EndAndAck)).ToArray();
        for (int i = 0; i < 4; i++)
        {
            receiver.Advance(new ZModemEvent.PeerBytesReceived(frame));
        }

        Assert.Equal(ZModemPhase.Faulted, receiver.Snapshot.Phase);
    }

    [Fact]
    public void WrongOffsetRequestsResynchronizationWithoutDelivery()
    {
        var receiver = BeginFile(3);
        byte[] wire = Header(ZModemHeaderType.Zdata, 1, ZModemHeaderFormat.Binary32)
            .Concat(ZModemCodec.EncodeData(new byte[] { 2, 3 }, ZModemDataEnd.EndAndAck)).ToArray();
        var step = receiver.Advance(new ZModemEvent.PeerBytesReceived(wire));
        Assert.Empty(step.Actions.OfType<ZModemAction.DeliverDataBlock>());
        Assert.Equal(ZModemHeaderType.Zrpos, WireFixture.HeaderType(Assert.Single(step.Actions.OfType<ZModemAction.SendBytes>()).Bytes));
    }

    [Fact]
    public void CorruptFrameDoesNotDiscardFollowingPartialFrameInSameChunk()
    {
        var receiver = BeginFile(3);
        byte[] good = Header(ZModemHeaderType.Zdata, 0, ZModemHeaderFormat.Binary32)
            .Concat(ZModemCodec.EncodeData(new byte[] { 1, 2, 3 }, ZModemDataEnd.EndAndAck)).ToArray();
        byte[] bad = (byte[])good.Clone();
        bad[bad.Length - 2] ^= 1;
        int split = good.Length - 3;
        receiver.Advance(new ZModemEvent.PeerBytesReceived(bad.Concat(good.Take(split)).ToArray()));
        var step = receiver.Advance(new ZModemEvent.PeerBytesReceived(good.Skip(split).ToArray()));
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(step.Actions.OfType<ZModemAction.DeliverDataBlock>()).Payload);
    }

    [Fact]
    public void EofCannotCompleteAFileDuringCrcComparison()
    {
        var receiver = new ZModemReceiver();
        receiver.Advance(new ZModemEvent.StartRequested());
        receiver.Advance(new ZModemEvent.PeerBytesReceived(FileHeader(3)));
        receiver.Advance(new ZModemEvent.FileCrcRequested());
        var step = receiver.Advance(new ZModemEvent.PeerBytesReceived(Header(ZModemHeaderType.Zeof, 0)));
        Assert.Empty(step.Actions.OfType<ZModemAction.FileCompleted>());
        Assert.Equal(ZModemPhase.WaitingFileCrcResponse, step.Snapshot.Phase);
    }

    private static ZModemReceiver BeginFile(uint size)
    {
        var receiver = new ZModemReceiver();
        receiver.Advance(new ZModemEvent.StartRequested());
        receiver.Advance(new ZModemEvent.PeerBytesReceived(FileHeader(size)));
        receiver.Advance(new ZModemEvent.FileAccepted());
        return receiver;
    }

    private static byte[] FileHeader(uint size)
    {
        return Header(ZModemHeaderType.Zfile, 0, ZModemHeaderFormat.Binary32)
            .Concat(ZModemCodec.EncodeData(Encoding.ASCII.GetBytes("a.bin\0" + size + "\0"), ZModemDataEnd.EndAndAck)).ToArray();
    }

    private static byte[] Header(ZModemHeaderType type, uint position, ZModemHeaderFormat format = ZModemHeaderFormat.Hex)
    {
        return ZModemCodec.EncodeHeader(new ZModemHeader(type, position, format));
    }
}
