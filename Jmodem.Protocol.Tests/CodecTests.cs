using System.IO;

namespace Jmodem.Protocol.Tests
{
    public sealed class CodecTests
    {
        [Theory]
        [InlineData("", "060005012a18")]
        [InlineData("616263", "090005016162631930")]
        public void MatchesIndependentSourceDerivedVectors(string payload, string expected)
        {
            var bytes = Convert.FromHexString(payload);
            var frame = new JModemPacketEncoder().Encode(new JModemPacket(1, bytes, true));
            Assert.Equal(Convert.FromHexString(expected), frame);
            var packet = new JModemPacketDecoder().Decode(frame);
            Assert.Equal(bytes, packet.Payload);
            Assert.True(packet.IsLastBlock);
        }

        [Fact]
        public void MatchesOriginalManualRunExampleWithActualSourceHeaderOrder()
        {
            var bytes = Enumerable.Repeat((byte)0x20, 11).Concat(Convert.FromHexString("343787efff3a23")).ToArray();
            var expected = Convert.FromHexString("1100072abb0b0020343787efff3a23749b");
            var frame = new JModemPacketEncoder().Encode(new JModemPacket(42, bytes, true));
            Assert.Equal(expected, frame);
            Assert.Equal(bytes, new JModemPacketDecoder().Decode(expected).Payload);
            Assert.Equal(0x9B74, JModemCodec.ComputeChecksum(expected.AsSpan(0, expected.Length - 2).ToArray()));
        }

        [Fact]
        public void NormalCompressedVectorUsesSourceTypeBits()
        {
            var frame = new JModemPacketEncoder().Encode(new JModemPacket(1, new byte[512], false));
            Assert.Equal(Convert.FromHexString("0a000301bb000200740c"), frame);
            Assert.False(new JModemPacketDecoder().Decode(frame).IsLastBlock);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(255)]
        [InlineData(256)]
        [InlineData(257)]
        public void SequenceWrapUsesOneByte(int blockNumber)
        {
            var frame = new JModemPacketEncoder().Encode(new JModemPacket(blockNumber, new byte[] { 7 }, false));
            Assert.Equal(blockNumber & 0xFF, frame[3]);
            Assert.Equal(blockNumber & 0xFF, new JModemPacketDecoder().Decode(frame).BlockNumber);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AllByteValuesAndMaximumPayloadRoundTrip(bool compress)
        {
            var bytes = Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray();
            var frame = new JModemPacketEncoder(compress).Encode(new JModemPacket(1, bytes, true));
            Assert.Equal(8198, frame.Length);
            Assert.Equal(5, frame[2]);
            Assert.Equal(bytes, new JModemPacketDecoder().Decode(frame).Payload);
        }

        [Fact]
        public void IncompressibleSentinelsUseRawEncodingWithoutEscaping()
        {
            var bytes = new byte[] { 0xBB, 0, 0xBB, 0xAF, 0xEF, 0xBB, 0, 0xAE, 0xEF };
            var frame = new JModemPacketEncoder().Encode(new JModemPacket(1, bytes, true));
            Assert.Equal(5, frame[2]);
            Assert.Equal(bytes, frame.Skip(4).Take(bytes.Length));
            Assert.Equal(bytes, new JModemPacketDecoder().Decode(frame).Payload);
            Assert.Equal(18, JModemCodec.EncodeRunLength(bytes).Length);
        }

        [Fact]
        public void RunCountsUseLittleEndianAndCanCoverEightKilobytes()
        {
            var data = Enumerable.Repeat((byte)0xBB, 8192).ToArray();
            var encoded = JModemCodec.EncodeRunLength(data);
            Assert.Equal(new byte[] { 0xBB, 0, 0x20, 0xBB }, encoded);
            Assert.Equal(data, JModemCodec.DecodeRunLength(encoded));
        }

        [Theory]
        [InlineData("bb")]
        [InlineData("bb01")]
        [InlineData("bb0100")]
        [InlineData("bb000041")]
        [InlineData("bb012041")]
        [InlineData("bbffff41")]
        public void RejectsTruncatedZeroAndOversizedRuns(string encoded)
        {
            Assert.Throws<InvalidDataException>(() => JModemCodec.DecodeRunLength(Convert.FromHexString(encoded)));
        }

        [Fact]
        public void CorruptionAnywhereInHeaderPayloadOrCheckIsDetected()
        {
            var frame = new JModemPacketEncoder(false).Encode(new JModemPacket(1, new byte[] { 1, 2, 3, 4, 5 }, true));
            for (var index = 0; index < frame.Length; index++)
            {
                var bad = (byte[])frame.Clone();
                bad[index] ^= 0x80;
                Assert.Throws<InvalidDataException>(() => new JModemPacketDecoder().Decode(bad));
            }
        }

        [Fact]
        public void DecoderAcceptsOriginalThreePointZeroThreeFinalFlagsWithoutNormalBit()
        {
            var frame = new JModemPacketEncoder(false).Encode(new JModemPacket(1, new byte[] { 7 }, true));
            frame[2] = JModemControlBytes.EndOfFile;
            RepairChecksum(frame);
            Assert.True(new JModemPacketDecoder().Decode(frame).IsLastBlock);
            frame[2] = 0x80;
            RepairChecksum(frame);
            Assert.Throws<InvalidDataException>(() => new JModemPacketDecoder().Decode(frame));
        }

        [Fact]
        public void FragmentationAtEverySplitAndCoalescingPreservePacketBoundaries()
        {
            var frame = new JModemPacketEncoder().Encode(new JModemPacket(1, Enumerable.Range(0, 512).Select(i => (byte)i).ToArray(), false));
            for (var split = 1; split < frame.Length; split++)
            {
                var decoder = new JModemStreamDecoder();
                Assert.Empty(decoder.Feed(frame.Take(split).ToArray()));
                Assert.IsType<JModemEvent.PacketReceived>(Assert.Single(decoder.Feed(frame.Skip(split).ToArray())));
                Assert.Equal(0, decoder.BufferedByteCount);
            }

            Assert.Equal(2, new JModemStreamDecoder().Feed(frame.Concat(frame).ToArray()).Count);
        }

        [Fact]
        public void BadLengthDiscardsUntilQuietAndNeverAllocatesItsAdvertisedLength()
        {
            var decoder = new JModemStreamDecoder();
            var invalid = decoder.Feed(new byte[] { 0xFF, 0xFF }.Concat(new byte[100000]).ToArray());
            Assert.IsType<JModemEvent.InvalidFrame>(Assert.Single(invalid));
            Assert.True(decoder.IsDiscarding);
            Assert.Equal(0, decoder.BufferedByteCount);
            decoder.OnTimeout();
            Assert.False(decoder.IsDiscarding);
            var frame = new JModemPacketEncoder().Encode(new JModemPacket(1, new byte[] { 0x55 }, true));
            Assert.IsType<JModemEvent.PacketReceived>(Assert.Single(decoder.Feed(frame)));
        }

        [Fact]
        public void CancellationDoesNotConfuseBinaryCanBytesOrLength1818()
        {
            var decoder = new JModemStreamDecoder();
            Assert.Empty(decoder.Feed(new byte[] { 24, 24, 24 }));
            Assert.IsType<JModemEvent.CancelRequested>(Assert.Single(decoder.Feed(new byte[] { 24, 24, 24 })));
            var payload = Enumerable.Repeat((byte)24, 0x1818 - 6).ToArray();
            var frame = new JModemPacketEncoder(false).Encode(new JModemPacket(1, payload, true));
            Assert.Equal(new byte[] { 24, 24 }, frame.Take(2));
            var decoded = Assert.IsType<JModemEvent.PacketReceived>(Assert.Single(decoder.Feed(frame)));
            Assert.Equal(payload, decoded.Packet.Payload);
        }

        [Fact]
        public void CallbackExceptionsPropagateInsteadOfBecomingWireErrors()
        {
            var frame = new JModemPacketEncoder().Encode(new JModemPacket(1, new byte[] { 1 }, true));
            Assert.Throws<InvalidDataException>(() => new JModemStreamDecoder().Feed(frame, _ => throw new InvalidDataException("storage")));
        }

        [Fact]
        public void PacketPayloadAndReadyEventDefensivelyCopyBuffers()
        {
            var bytes = new byte[] { 1 };
            var packet = new JModemPacket(1, bytes, true);
            var ready = new JModemEvent.DataBlockReady(1, bytes, 1, true);
            bytes[0] = 2;
            packet.Payload[0] = 3;
            ready.Data[0] = 4;
            Assert.Equal(1, packet.Payload[0]);
            Assert.Equal(1, ready.Data[0]);
        }

        [Fact]
        public void InvalidPublicArgumentsAreRejected()
        {
            Assert.Throws<ArgumentNullException>(() => new JModemPacketEncoder().Encode(null!));
            Assert.Throws<ArgumentNullException>(() => new JModemPacketDecoder().Decode(null!));
            Assert.Throws<ArgumentOutOfRangeException>(() => new JModemOptions(initialBlockSize: 63));
            Assert.Throws<ArgumentOutOfRangeException>(() => new JModemOptions(initialBlockSize: 8193));
            Assert.Throws<ArgumentOutOfRangeException>(() => new JModemOptions(maxRetries: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new JModemOptions(blockSizingPolicy: (JModemBlockSizingPolicy)2));
            Assert.Throws<ArgumentOutOfRangeException>(() => new JModemOptions(maxInputBytes: 8197));
            Assert.Throws<ArgumentException>(() => new JModemPacket(1, new byte[8193], true));
            Assert.Throws<ArgumentException>(() => new JModemPacket(1, Array.Empty<byte>(), false));
        }

        private static void RepairChecksum(byte[] frame)
        {
            var checksum = JModemCodec.ComputeChecksum(frame.AsSpan(0, frame.Length - 2).ToArray());
            frame[^2] = (byte)checksum;
            frame[^1] = (byte)(checksum >> 8);
        }
    }
}
