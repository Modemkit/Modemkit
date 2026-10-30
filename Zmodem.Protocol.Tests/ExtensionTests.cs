using Zmodem.Protocol;

namespace Zmodem.Protocol.Tests;

public sealed class ExtensionTests
{
    [Theory]
    [InlineData(ZModemProfile.Extended4K, 4096)]
    [InlineData(ZModemProfile.Extended8K, 8192)]
    [InlineData(ZModemProfile.ZedZap, 8192)]
    public void LargeProfilesEmitAndReceiveLargeSubpackets(ZModemProfile profile, int blockSize)
    {
        byte[] payload = Payload(20001);
        var fixture = new WireFixture(
            new[] { payload },
            chunkSize: 1,
            options: new ZModemOptions(blockSize: blockSize, profile: profile));
        fixture.Run();
        Assert.Equal(payload, Assert.Single(fixture.Received));
        Assert.Contains(fixture.Trace.OfType<ZModemAction.DeliverDataBlock>(), data => data.Payload.Length == blockSize);
    }

    [Fact]
    public void ZedZipUsesOneKilobytePacketsAndPermitsNullBatches()
    {
        var options = new ZModemOptions(profile: ZModemProfile.ZedZip);
        var files = new WireFixture(new[] { Payload(2201) }, options: options);
        files.Run();
        Assert.Contains(files.Trace.OfType<ZModemAction.DeliverDataBlock>(), data => data.Payload.Length == 1024);
        Assert.All(files.Trace.OfType<ZModemAction.DeliverDataBlock>(), data => Assert.InRange(data.Payload.Length, 0, 1024));
        var empty = new WireFixture(Array.Empty<byte[]>(), options: options);
        empty.Run();
        Assert.Empty(empty.Received);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZModemOptions(blockSize: 4096, profile: ZModemProfile.ZedZip));
    }

    [Theory]
    [InlineData(ZModemFlowControl.Streaming)]
    [InlineData(ZModemFlowControl.Windowed)]
    public void StreamingUsesOneHeaderAcrossManySubpackets(ZModemFlowControl flowControl)
    {
        var options = new ZModemOptions(flowControl: flowControl);
        var fixture = new WireFixture(new[] { Payload(33001) }, chunkSize: 4096, options: options);
        fixture.Run();
        var decoder = new ZModemStreamDecoder();
        var wire = fixture.WireTrace.Where(item => item.Sender).SelectMany(item => decoder.Feed(item.Bytes)).ToArray();
        Assert.Contains(wire.OfType<ZModemWireEvent.DataReceived>(), data => data.End == ZModemDataEnd.Continue);
        Assert.Contains(wire.OfType<ZModemWireEvent.DataReceived>(), data => data.End == ZModemDataEnd.ContinueAndAck);
        Assert.Contains(wire.OfType<ZModemWireEvent.DataReceived>(), data => data.End == ZModemDataEnd.End);
        Assert.True(wire.OfType<ZModemWireEvent.HeaderReceived>().Count(header => header.Header.Type == ZModemHeaderType.Zdata) < 10);
        Assert.Equal(Payload(33001), Assert.Single(fixture.Received));
        if (flowControl == ZModemFlowControl.Windowed)
        {
            Assert.InRange(fixture.MaximumBytesInFlight, 1u, (uint)options.WindowSize);
        }
    }

    [Fact]
    public void WindowStopsAtItsLimitWhenAcknowledgementsAreLost()
    {
        var options = new ZModemOptions(flowControl: ZModemFlowControl.Windowed, windowSize: 4096, acknowledgementInterval: 2048);
        int dropped = 0;
        var fixture = new WireFixture(new[] { Payload(22001) }, chunkSize: 4096, options: options);
        fixture.Mutate = (sender, bytes) =>
        {
            if (!sender && WireFixture.HeaderType(bytes) == ZModemHeaderType.Zack && dropped < 3)
            {
                dropped++;
                return null;
            }

            return bytes;
        };
        fixture.Run();
        Assert.Equal(3, dropped);
        Assert.Equal(Payload(22001), Assert.Single(fixture.Received));
        Assert.InRange(fixture.MaximumBytesInFlight, 1u, 4096u);
    }

    [Theory]
    [InlineData(ZModemFlowControl.Streaming)]
    [InlineData(ZModemFlowControl.Windowed)]
    public void CorruptContinuedSubpacketResumesFromCommittedOffset(ZModemFlowControl mode)
    {
        bool corrupted = false;
        var fixture = new WireFixture(
            new[] { Payload(33001) },
            chunkSize: 4096,
            options: new ZModemOptions(flowControl: mode));
        fixture.Mutate = (sender, bytes) =>
        {
            if (sender && !corrupted && bytes.Length > 100 && WireFixture.HeaderType(bytes) == null)
            {
                bytes[bytes.Length - 1] ^= 1;
                corrupted = true;
            }

            return bytes;
        };
        fixture.Run();
        Assert.True(corrupted);
        Assert.Equal(Payload(33001), Assert.Single(fixture.Received));
    }

    [Fact]
    public void PeerWithoutOverlappedIoForcesStopAndWait()
    {
        var fixture = new WireFixture(
            new[] { Payload(12000) },
            options: new ZModemOptions(flowControl: ZModemFlowControl.Streaming),
            receiverOptions: new ZModemOptions());
        fixture.Run();
        var decoder = new ZModemStreamDecoder();
        var data = fixture.WireTrace.Where(item => item.Sender).SelectMany(item => decoder.Feed(item.Bytes)).OfType<ZModemWireEvent.DataReceived>();
        Assert.All(data, packet => Assert.Equal(ZModemDataEnd.EndAndAck, packet.End));
        Assert.InRange(fixture.MaximumBytesInFlight, 1u, 1024u);
    }

    [Theory]
    [InlineData(ZModemHeaderFormat.VariableHex)]
    [InlineData(ZModemHeaderFormat.VariableBinary16)]
    [InlineData(ZModemHeaderFormat.VariableBinary32)]
    [InlineData(ZModemHeaderFormat.VariableBinaryRle32)]
    public void VariableHeadersPreserveEveryAllowedLengthAcrossEverySplit(ZModemHeaderFormat format)
    {
        for (int length = 0; length <= 16; length++)
        {
            byte[] data = Enumerable.Range(0, length).Select(i => (byte)(i * 17)).ToArray();
            byte[] encoded = ZModemCodec.EncodeHeader(new ZModemHeader(ZModemHeaderType.Zrpos, data, format));
            for (int split = 0; split <= encoded.Length; split++)
            {
                var decoder = new ZModemStreamDecoder(allowLegacyExtensions: true);
                var packets = decoder.Feed(encoded.Take(split).ToArray()).Concat(decoder.Feed(encoded.Skip(split).ToArray())).ToArray();
                var header = Assert.IsType<ZModemWireEvent.HeaderReceived>(Assert.Single(packets)).Header;
                Assert.Equal(data, header.Data);
                Assert.Equal(format, header.Format);
            }
        }
    }

    [Fact]
    public void VariableHeaderMatchesIndependentCrcVector()
    {
        // CRC over type + five data bytes is 0x0b5827be (Python zlib).
        // The variable-length byte precedes the type and is excluded from the CRC.
        byte[] expected = Convert.FromHexString("2a18631845184a184018401840184055be2758184b");
        var header = new ZModemHeader(ZModemHeaderType.Zdata, new byte[] { 0, 0, 0, 0, 0x55 }, ZModemHeaderFormat.VariableBinary32);
        Assert.Equal(expected, ZModemCodec.EncodeHeader(header));
        Assert.IsType<ZModemWireEvent.HeaderReceived>(Assert.Single(new ZModemStreamDecoder(allowLegacyExtensions: true).Feed(expected)));
    }

    [Fact]
    public void RleMatchesIndependentGrammarAndCrcVector()
    {
        // Runs: '~' C A => AAA, '~' space => three spaces, '~' @ => literal '~'.
        // CRC32 over compressed bytes plus ZCRCW is 0x88ba3ba7 (Python zlib).
        byte[] payload = { 65, 65, 65, 32, 32, 32, 126, 66 };
        byte[] expected = Convert.FromHexString("7e43417e207e4042186ba73bba18c811");
        Assert.Equal(expected, ZModemCodec.EncodeData(payload, ZModemDataEnd.EndAndAck, ZModemHeaderFormat.BinaryRle32));
        var decoder = new ZModemStreamDecoder(allowLegacyExtensions: true);
        decoder.Feed(ZModemCodec.EncodeHeader(new ZModemHeader(ZModemHeaderType.Zdata, 0, ZModemHeaderFormat.BinaryRle32)));
        Assert.Equal(payload, Assert.IsType<ZModemWireEvent.DataReceived>(Assert.Single(decoder.Feed(expected))).Payload);
    }

    [Theory]
    [InlineData(ZModemFlowControl.StopAndWait)]
    [InlineData(ZModemFlowControl.Streaming)]
    [InlineData(ZModemFlowControl.Windowed)]
    public void LegacyNegotiationTransfersRunLengthEncodedBinaryData(ZModemFlowControl flowControl)
    {
        var options = new ZModemOptions(useVariableHeaders: true, useRunLengthEncoding: true, flowControl: flowControl);
        byte[] payload = Enumerable.Repeat((byte)0x7e, 4100).Concat(Enumerable.Repeat((byte)' ', 4000)).Concat(Payload(8300)).ToArray();
        var fixture = new WireFixture(new[] { payload }, chunkSize: 1, options: options);
        fixture.Run();
        Assert.Equal(payload, Assert.Single(fixture.Received));
        var decoder = new ZModemStreamDecoder(allowLegacyExtensions: true);
        var headers = fixture.WireTrace.Where(item => item.Sender).SelectMany(item => decoder.Feed(item.Bytes)).OfType<ZModemWireEvent.HeaderReceived>();
        Assert.Contains(headers, header => header.Header.Format == ZModemHeaderFormat.VariableBinaryRle32);
    }

    [Fact]
    public void LegacyOptionsFallBackWhenPeerDoesNotAdvertiseThem()
    {
        var fixture = new WireFixture(
            new[] { Payload(1234) },
            options: new ZModemOptions(useVariableHeaders: true, useRunLengthEncoding: true),
            receiverOptions: new ZModemOptions());
        fixture.Run();
        Assert.Equal(Payload(1234), Assert.Single(fixture.Received));
        var decoder = new ZModemStreamDecoder();
        Assert.DoesNotContain(
            fixture.WireTrace.Where(item => item.Sender).SelectMany(item => decoder.Feed(item.Bytes)),
            item => item is ZModemWireEvent.InvalidFrame);
    }

    [Fact]
    public void TruncatedRleAndExpansionBeyondLimitAreRejected()
    {
        foreach (byte[] encoded in new[]
        {
            new byte[] { 0x7e }, new byte[] { 0x7e, 0x41 },
            Enumerable.Repeat(new byte[] { 0x7e, 0xff, 65 }, 6).SelectMany(x => x).ToArray()
        })
        {
            var decoder = new ZModemStreamDecoder(allowLegacyExtensions: true);
            decoder.Feed(ZModemCodec.EncodeHeader(new ZModemHeader(ZModemHeaderType.Zdata, 0, ZModemHeaderFormat.BinaryRle32)));

            // Ordinary CRC32 encoder transports the intentionally malformed compressed bytes.
            byte[] wire = ZModemCodec.EncodeData(encoded, ZModemDataEnd.EndAndAck);
            Assert.IsType<ZModemWireEvent.InvalidFrame>(Assert.Single(decoder.Feed(wire)));
        }
    }

    [Fact]
    public void CrcComparisonRoundTripPrecedesAcceptance()
    {
        var fixture = new WireFixture(new[] { Payload(4001) }, requestCrc: true);
        fixture.Run();
        Assert.Single(fixture.Trace.OfType<ZModemAction.RequestFileCrc>());
        Assert.Single(fixture.Trace.OfType<ZModemAction.FileCrcReceived>());
        Assert.Equal(Payload(4001), Assert.Single(fixture.Received));
    }

    [Fact]
    public void DroppedCrcReplyIsRetriedWithoutRecomputing()
    {
        bool dropped = false;
        var fixture = new WireFixture(new[] { Payload(4001) }, requestCrc: true);
        fixture.Mutate = (sender, bytes) =>
        {
            if (sender && !dropped && WireFixture.HeaderType(bytes) == ZModemHeaderType.Zcrc)
            {
                dropped = true;
                return null;
            }

            return bytes;
        };
        fixture.Run();
        Assert.True(dropped);
        Assert.Single(fixture.Trace.OfType<ZModemAction.RequestFileCrc>());
        Assert.Equal(Payload(4001), Assert.Single(fixture.Received));
    }

    [Fact]
    public void CorruptCrcReplyRetriesComparisonBeforeAcceptingTheFile()
    {
        bool corrupted = false;
        var fixture = new WireFixture(new[] { Payload(4001) }, requestCrc: true);
        fixture.Mutate = (sender, bytes) =>
        {
            if (sender && !corrupted && WireFixture.HeaderType(bytes) == ZModemHeaderType.Zcrc)
            {
                bytes[bytes.Length - 4] ^= 1;
                corrupted = true;
            }

            return bytes;
        };
        fixture.Run();
        Assert.True(corrupted);
        Assert.Single(fixture.Trace.OfType<ZModemAction.FileCrcReceived>());
        Assert.Equal(Payload(4001), Assert.Single(fixture.Received));
    }

    [Fact]
    public void AmbiguousCanRleBitAloneDoesNotEnableLegacyEncoding()
    {
        var sender = new ZModemSender(new ZModemOptions(useVariableHeaders: true, useRunLengthEncoding: true));
        sender.Advance(new ZModemEvent.PeerBytesReceived(ZModemCodec.EncodeHeader(new ZModemHeader(ZModemHeaderType.Zrinit, 0x6b000000))));
        var offered = sender.Advance(new ZModemEvent.FileHeaderReady(new ZModemFileDescriptor("file", 3)));
        var header = new ZModemStreamDecoder().Feed(Assert.Single(offered.Actions.OfType<ZModemAction.SendBytes>()).Bytes)
            .OfType<ZModemWireEvent.HeaderReceived>().Single();
        Assert.Equal(ZModemHeaderFormat.Binary32, header.Header.Format);
    }

    [Fact]
    public void RecoverySupersedesPendingSourceReadAndSynchronizesFirstFrame()
    {
        var sender = new ZModemSender(new ZModemOptions(flowControl: ZModemFlowControl.Streaming));
        sender.Advance(new ZModemEvent.PeerBytesReceived(ZModemCodec.EncodeHeader(
            new ZModemHeader(ZModemHeaderType.Zrinit, 0x63000000))));
        sender.Advance(new ZModemEvent.FileHeaderReady(new ZModemFileDescriptor("file", 5000)));
        sender.Advance(new ZModemEvent.PeerBytesReceived(ZModemCodec.EncodeHeader(new ZModemHeader(ZModemHeaderType.Zrpos))));
        sender.Advance(new ZModemEvent.DataBlockReady(0, Payload(1024)));
        var restarted = sender.Advance(new ZModemEvent.PeerBytesReceived(ZModemCodec.EncodeHeader(new ZModemHeader(ZModemHeaderType.Zrpos))));
        Assert.Equal(0u, Assert.Single(restarted.Actions.OfType<ZModemAction.RequestDataBlock>()).Offset);
        Assert.Empty(sender.Advance(new ZModemEvent.DataBlockReady(1024, Payload(1024))).Actions);
        var sent = sender.Advance(new ZModemEvent.DataBlockReady(0, Payload(1024)));
        var decoded = new ZModemStreamDecoder().Feed(Assert.Single(sent.Actions.OfType<ZModemAction.SendBytes>()).Bytes);
        Assert.Equal(ZModemDataEnd.EndAndAck, Assert.Single(decoded.OfType<ZModemWireEvent.DataReceived>()).End);
        Assert.Equal(ZModemPhase.WaitingDataAck, sent.Snapshot.Phase);
    }

    [Fact]
    public void SenderCrcHookPreservesRequestedPrefixLength()
    {
        var sender = new ZModemSender();
        sender.Advance(new ZModemEvent.PeerBytesReceived(ZModemCodec.EncodeHeader(
            new ZModemHeader(ZModemHeaderType.Zrinit, 0x23000000))));
        sender.Advance(new ZModemEvent.FileHeaderReady(new ZModemFileDescriptor("file", 1000)));
        var request = sender.Advance(new ZModemEvent.PeerBytesReceived(ZModemCodec.EncodeHeader(
            new ZModemHeader(ZModemHeaderType.Zcrc, 123))));
        Assert.Equal(123u, Assert.Single(request.Actions.OfType<ZModemAction.RequestFileCrc>()).ByteCount);
        var response = sender.Advance(new ZModemEvent.FileCrcReady(0x12345678));
        var header = Assert.Single(new ZModemStreamDecoder().Feed(
            Assert.Single(response.Actions.OfType<ZModemAction.SendBytes>()).Bytes).OfType<ZModemWireEvent.HeaderReceived>());
        Assert.Equal(ZModemHeaderType.Zcrc, header.Header.Type);
        Assert.Equal(0x12345678u, header.Header.Position);
    }

    [Fact]
    public void DeprecatedEsc8BitDoesNotAbortOrdinaryTransfer()
    {
        var sender = new ZModemSender();
        var step = sender.Advance(new ZModemEvent.PeerBytesReceived(ZModemCodec.EncodeHeader(
            new ZModemHeader(ZModemHeaderType.Zrinit, 0xa3000000))));
        Assert.Equal(ZModemPhase.WaitingFile, step.Snapshot.Phase);
        Assert.Single(step.Actions.OfType<ZModemAction.RequestFileHeader>());
    }

    [Fact]
    public void ProfileBoundsAndMalformedVariableLengthsAreEnforced()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZModemOptions(blockSize: 8192));
        Assert.Throws<ArgumentException>(() => new ZModemOptions(useRunLengthEncoding: true));
        Assert.Throws<ArgumentException>(() => new ZModemHeader(ZModemHeaderType.Zdata, new byte[17], ZModemHeaderFormat.VariableBinary32));
        var decoder = new ZModemStreamDecoder(allowLegacyExtensions: true);
        Assert.IsType<ZModemWireEvent.InvalidFrame>(Assert.Single(decoder.Feed(Convert.FromHexString("2a18631851"))));
    }

    [Fact]
    public void CompletedSessionsStillEnforceInputChunkBound()
    {
        var fixture = new WireFixture(Array.Empty<byte[]>(), options: new ZModemOptions(maxBufferedBytes: 4096));
        fixture.Run();
        var oversized = new byte[4097];
        ZModemCodec.EncodeHeader(new ZModemHeader(ZModemHeaderType.Zfin)).CopyTo(oversized, 0);
        Assert.Empty(fixture.Sender.Advance(new ZModemEvent.PeerBytesReceived(oversized)).Actions);
        Assert.Empty(fixture.Receiver.Advance(new ZModemEvent.PeerBytesReceived(oversized)).Actions);
        Assert.Equal(ZModemPhase.Completed, fixture.Sender.Snapshot.Phase);
        Assert.Equal(ZModemPhase.Completed, fixture.Receiver.Snapshot.Phase);
    }

    private static byte[] Payload(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 13)).ToArray();
}
