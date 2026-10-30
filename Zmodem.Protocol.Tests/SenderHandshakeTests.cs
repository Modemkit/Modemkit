using Zmodem.Protocol;

namespace Zmodem.Protocol.Tests;

public sealed class SenderHandshakeTests
{
    [Theory]
    [InlineData(ZModemFlowControl.Streaming)]
    [InlineData(ZModemFlowControl.Windowed)]
    public void LateInitConfirmsOriginalEofAfterDuplicateResumeWasRetransmitted(ZModemFlowControl mode)
    {
        var sender = CreateSenderAtEof(mode);
        var resumed = Peer(sender, ZModemHeaderType.Zrpos);
        Assert.Equal(0u, Assert.Single(resumed.Actions.OfType<ZModemAction.RequestDataBlock>()).Offset);
        var resent = sender.Advance(new ZModemEvent.DataBlockReady(0, new byte[] { 0 }));
        Assert.Equal(ZModemPhase.WaitingDataAck, resent.Snapshot.Phase);

        var completed = Peer(sender, ZModemHeaderType.Zrinit, 0x23000000);

        Assert.Equal(1u, Assert.Single(completed.Actions.OfType<ZModemAction.FileCompleted>()).Length);
        Assert.Single(completed.Actions.OfType<ZModemAction.RequestFileHeader>());
        Assert.Equal(1u, sender.AcknowledgedOffset);
        Assert.Equal(ZModemPhase.WaitingFile, sender.Snapshot.Phase);
        FinishBatch(sender);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateInitWhileSourceReadIsPendingIgnoresReadBeforeOfferingNextFile(bool coalesced)
    {
        var sender = CreateSenderAtEof(ZModemFlowControl.Streaming);
        ZModemStepResult completed;
        if (coalesced)
        {
            completed = sender.Advance(new ZModemEvent.PeerBytesReceived(
                Header(ZModemHeaderType.Zrpos).Concat(Header(ZModemHeaderType.Zrinit, 0x23000000)).ToArray()));
            Assert.Single(completed.Actions.OfType<ZModemAction.RequestDataBlock>());
        }
        else
        {
            Peer(sender, ZModemHeaderType.Zrpos);
            completed = Peer(sender, ZModemHeaderType.Zrinit, 0x23000000);
        }

        Assert.Equal(1u, Assert.Single(completed.Actions.OfType<ZModemAction.FileCompleted>()).Length);
        Assert.Empty(completed.Actions.OfType<ZModemAction.RequestFileHeader>());
        Assert.Empty(Peer(sender, ZModemHeaderType.Zrinit, 0x23000000).Actions);
        var stale = sender.Advance(new ZModemEvent.DataBlockReady(0, new byte[] { 99 }));
        Assert.Empty(stale.Actions.OfType<ZModemAction.SendBytes>());
        Assert.Empty(stale.Actions.OfType<ZModemAction.FileCompleted>());
        Assert.Single(stale.Actions.OfType<ZModemAction.RequestFileHeader>());
        FinishBatch(sender);
    }

    [Fact]
    public void InitBeforeAnyEofCannotCompleteFile()
    {
        var sender = CreateSenderWaitingForData(ZModemFlowControl.Streaming, 2048);
        var ignored = Peer(sender, ZModemHeaderType.Zrinit, 0x23000000);
        Assert.Empty(ignored.Actions.OfType<ZModemAction.FileCompleted>());
        Assert.Equal(ZModemPhase.WaitingData, ignored.Snapshot.Phase);
        sender.Advance(new ZModemEvent.DataBlockReady(0, new byte[1024]));
        ignored = Peer(sender, ZModemHeaderType.Zrinit, 0x23000000);
        Assert.Empty(ignored.Actions.OfType<ZModemAction.FileCompleted>());
        Assert.Equal(ZModemPhase.WaitingData, ignored.Snapshot.Phase);
    }

    [Fact]
    public void LegitimateResumeAfterFinalDataErrorStillRetransmitsAndRequiresCompletion()
    {
        var sender = CreateSenderAtEof(ZModemFlowControl.Streaming);
        var resumed = Peer(sender, ZModemHeaderType.Zrpos);
        Assert.Single(resumed.Actions.OfType<ZModemAction.RequestDataBlock>());
        var resent = sender.Advance(new ZModemEvent.DataBlockReady(0, new byte[] { 0 }));
        var decoder = new ZModemStreamDecoder();
        var wire = decoder.Feed(Assert.Single(resent.Actions.OfType<ZModemAction.SendBytes>()).Bytes);
        Assert.Equal(ZModemDataEnd.EndAndAck, Assert.Single(wire.OfType<ZModemWireEvent.DataReceived>()).End);
        Assert.Empty(resent.Actions.OfType<ZModemAction.FileCompleted>());
        var ack = Peer(sender, ZModemHeaderType.Zack, 1);
        Assert.Equal(ZModemPhase.WaitingEofResponse, ack.Snapshot.Phase);
        Assert.Equal(ZModemHeaderType.Zeof, DecodeHeader(Assert.Single(ack.Actions.OfType<ZModemAction.SendBytes>()).Bytes).Type);
        var completed = Peer(sender, ZModemHeaderType.Zrinit, 0x23000000);
        Assert.Equal(1u, Assert.Single(completed.Actions.OfType<ZModemAction.FileCompleted>()).Length);
        FinishBatch(sender);
    }

    [Fact]
    public void CompletionTrackingResetsBeforeNextFileAndStaleDataCannotLeakAcrossFiles()
    {
        var sender = CreateSenderAtEof(ZModemFlowControl.Streaming);
        Peer(sender, ZModemHeaderType.Zrpos);
        Peer(sender, ZModemHeaderType.Zrinit, 0x23000000);
        var stale = sender.Advance(new ZModemEvent.DataBlockReady(0, new byte[] { 99 }));
        Assert.Single(stale.Actions.OfType<ZModemAction.RequestFileHeader>());

        sender.Advance(new ZModemEvent.FileHeaderReady(new ZModemFileDescriptor("second.bin", 1)));
        Peer(sender, ZModemHeaderType.Zrpos);
        var premature = Peer(sender, ZModemHeaderType.Zrinit, 0x23000000);
        Assert.Empty(premature.Actions.OfType<ZModemAction.FileCompleted>());
        var sent = sender.Advance(new ZModemEvent.DataBlockReady(0, new byte[] { 42 }));
        var decoder = new ZModemStreamDecoder();
        var data = sent.Actions.OfType<ZModemAction.SendBytes>()
            .SelectMany(action => decoder.Feed(action.Bytes)).OfType<ZModemWireEvent.DataReceived>().Single();
        Assert.Equal(new byte[] { 42 }, data.Payload);
        var completed = Peer(sender, ZModemHeaderType.Zrinit, 0x23000000);
        Assert.Equal("second.bin", Assert.Single(completed.Actions.OfType<ZModemAction.FileCompleted>()).File.FileName);
        FinishBatch(sender);
    }

    [Fact]
    public void CompletionDrainsEveryOutstandingReadWhenOffsetsWereRevisited()
    {
        var sender = CreateSenderWaitingForData(ZModemFlowControl.Streaming, 4);
        sender.Advance(new ZModemEvent.DataBlockReady(0, new byte[4]));
        Peer(sender, ZModemHeaderType.Zrpos, 0);
        Peer(sender, ZModemHeaderType.Zrpos, 1);
        Peer(sender, ZModemHeaderType.Zrpos, 0);
        var completed = Peer(sender, ZModemHeaderType.Zrinit, 0x23000000);
        Assert.Equal(4u, Assert.Single(completed.Actions.OfType<ZModemAction.FileCompleted>()).Length);
        Assert.Empty(completed.Actions.OfType<ZModemAction.RequestFileHeader>());

        Assert.Empty(sender.Advance(new ZModemEvent.DataBlockReady(0, new byte[4])).Actions);
        Assert.Empty(sender.Advance(new ZModemEvent.DataBlockReady(1, new byte[3])).Actions);
        Assert.Empty(sender.Advance(new ZModemEvent.TimeoutElapsed()).Actions);
        Assert.Empty(Peer(sender, ZModemHeaderType.Zrinit, 0x23000000).Actions);
        var drained = sender.Advance(new ZModemEvent.DataBlockReady(0, new byte[4]));
        Assert.Single(drained.Actions.OfType<ZModemAction.RequestFileHeader>());
        Assert.Empty(drained.Actions.OfType<ZModemAction.FileCompleted>());
        Assert.Empty(drained.Actions.OfType<ZModemAction.SendBytes>());
        FinishBatch(sender);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CancellationStillWorksWhileCompletedFileReadsAreDraining(int cancellation)
    {
        var sender = CreateSenderAtEof(ZModemFlowControl.Streaming);
        Peer(sender, ZModemHeaderType.Zrpos);
        Peer(sender, ZModemHeaderType.Zrinit, 0x23000000);
        ZModemEvent protocolEvent = cancellation switch
        {
            0 => new ZModemEvent.CancelRequested(),
            1 => new ZModemEvent.PeerBytesReceived(ZModemCodec.EncodeCancellation()),
            _ => new ZModemEvent.PeerBytesReceived(Header(ZModemHeaderType.Zabort)),
        };
        var cancelled = sender.Advance(protocolEvent);
        Assert.Equal(ZModemPhase.Cancelled, cancelled.Snapshot.Phase);
        Assert.Single(cancelled.Actions.OfType<ZModemAction.Cancel>());
        Assert.Empty(cancelled.Actions.OfType<ZModemAction.RequestFileHeader>());
    }

    [Fact]
    public void InitBeforeEofCannotCompleteAcknowledgedModeFile()
    {
        var sender = CreateSenderWaitingForData(ZModemFlowControl.StopAndWait, 1);
        sender.Advance(new ZModemEvent.DataBlockReady(0, new byte[] { 0 }));
        var ignored = Peer(sender, ZModemHeaderType.Zrinit, 0x23000000);
        Assert.Empty(ignored.Actions.OfType<ZModemAction.FileCompleted>());
        Assert.Equal(ZModemPhase.WaitingDataAck, sender.Snapshot.Phase);
    }

    private static ZModemSender CreateSenderAtEof(ZModemFlowControl mode)
    {
        var sender = CreateSenderWaitingForData(mode, 1);
        var sent = sender.Advance(new ZModemEvent.DataBlockReady(0, new byte[] { 0 }));
        Assert.Equal(ZModemPhase.WaitingEofResponse, sent.Snapshot.Phase);
        Assert.Contains(
            sent.Actions.OfType<ZModemAction.SendBytes>(),
            action => new ZModemStreamDecoder().Feed(action.Bytes).OfType<ZModemWireEvent.HeaderReceived>().Any(header => header.Header.Type == ZModemHeaderType.Zeof));
        return sender;
    }

    private static ZModemSender CreateSenderWaitingForData(ZModemFlowControl mode, uint size)
    {
        var sender = new ZModemSender(new ZModemOptions(flowControl: mode));
        sender.Advance(new ZModemEvent.StartRequested());
        Peer(sender, ZModemHeaderType.Zrinit, 0x23000000);
        sender.Advance(new ZModemEvent.FileHeaderReady(new ZModemFileDescriptor("first.bin", size)));
        Peer(sender, ZModemHeaderType.Zrpos);
        return sender;
    }

    private static void FinishBatch(ZModemSender sender)
    {
        var finish = sender.Advance(new ZModemEvent.NoMoreFiles());
        Assert.Equal(ZModemPhase.WaitingFinish, finish.Snapshot.Phase);
        Assert.Single(Peer(sender, ZModemHeaderType.Zfin).Actions.OfType<ZModemAction.Complete>());
    }

    private static ZModemStepResult Peer(ZModemSender sender, ZModemHeaderType type, uint position = 0) =>
        sender.Advance(new ZModemEvent.PeerBytesReceived(Header(type, position)));

    private static byte[] Header(ZModemHeaderType type, uint position = 0) =>
        ZModemCodec.EncodeHeader(new ZModemHeader(type, position));

    private static ZModemHeader DecodeHeader(byte[] wire) =>
        Assert.Single(new ZModemStreamDecoder().Feed(wire).OfType<ZModemWireEvent.HeaderReceived>()).Header;
}
