using System.IO;

namespace Xmodem.Protocol.Tests
{
    public sealed class PacketCodecTests
    {
        [Fact]
        public void CrcPacketMatchesIndependentZeroVectorAndNetworkByteOrder()
        {
            var encoder = new XModemPacketEncoder();
            var zero = encoder.Encode(new XModemPacket.Data(1, new byte[128], 128));
            Assert.Equal(133, zero.Length);
            Assert.Equal(new byte[] { 1, 1, 254 }, zero.Take(3));
            Assert.Equal(new byte[] { 0, 0 }, zero.TakeLast(2));

            // CRC-16/XMODEM check value for "123456789" is 0x31C3.
            // Prefixing zero bytes does not change its initial zero register.
            var payload = new byte[128];
            System.Text.Encoding.ASCII.GetBytes("123456789").CopyTo(payload, 119);
            var frame = encoder.Encode(new XModemPacket.Data(1, payload, 128));
            Assert.Equal(new byte[] { 0x31, 0xC3 }, frame.TakeLast(2));
            var decoded = Assert.IsType<XModemPacket.Data>(new XModemPacketDecoder().Decode(frame));
            Assert.Equal(payload, decoded.Payload);
        }

        [Fact]
        public void ChecksumPacketMatchesIndependentModulo256Vector()
        {
            var payload = Enumerable.Range(0, 128).Select(value => (byte)value).ToArray();
            var frame = new XModemPacketEncoder().Encode(
                new XModemPacket.Data(257, payload, payload.Length), XModemIntegrity.Checksum);
            Assert.Equal(132, frame.Length);
            Assert.Equal(1, frame[1]);
            Assert.Equal(254, frame[2]);
            Assert.Equal(192, frame[^1]);
            var decoded = Assert.IsType<XModemPacket.Data>(
                new XModemPacketDecoder().Decode(frame, XModemIntegrity.Checksum));
            Assert.Equal(payload, decoded.Payload);
        }

        [Theory]
        [InlineData(XModemIntegrity.Checksum, 128)]
        [InlineData(XModemIntegrity.Crc16, 128)]
        [InlineData(XModemIntegrity.Crc16, 1024)]
        public void CodecPreservesPayloadAndAddsSubPadding(XModemIntegrity integrity, int blockSize)
        {
            var frame = new XModemPacketEncoder().Encode(
                new XModemPacket.Data(256, new byte[] { 0, 0x1A, 0xFF }, 3, blockSize), integrity);
            var data = Assert.IsType<XModemPacket.Data>(new XModemPacketDecoder().Decode(frame, integrity));
            Assert.Equal(0, data.BlockNumber);
            Assert.Equal(blockSize, data.DataLength);
            Assert.Equal(new byte[] { 0, 0x1A, 0xFF }, data.Payload.Take(3));
            Assert.All(data.Payload.Skip(3), value => Assert.Equal(0x1A, value));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(131)]
        [InlineData(132)]
        public void DecoderRejectsDamagedHeadersPayloadAndCrc(int corruptIndex)
        {
            var frame = new XModemPacketEncoder().Encode(new XModemPacket.Data(1, new byte[128], 128));
            frame[corruptIndex] ^= 1;
            Assert.Throws<InvalidDataException>(() => new XModemPacketDecoder().Decode(frame));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(132)]
        [InlineData(134)]
        public void DecoderRejectsInvalidLengths(int length)
        {
            var frame = new byte[length];
            if (length > 0)
            {
                frame[0] = XModemControlBytes.Soh;
            }

            Assert.Throws<InvalidDataException>(() => new XModemPacketDecoder().Decode(frame));
        }

        [Fact]
        public void ChecksumModeRejectsOneKFrames()
        {
            var packet = new XModemPacket.Data(1, new byte[1024], 1024, 1024);
            Assert.Throws<ArgumentException>(() => new XModemPacketEncoder().Encode(packet, XModemIntegrity.Checksum));
            var frame = new XModemPacketEncoder().Encode(packet);
            Assert.Throws<InvalidDataException>(() => new XModemPacketDecoder().Decode(frame, XModemIntegrity.Checksum));
        }

        [Fact]
        public void PacketAndEventDefensivelyCopyDataForStableRetries()
        {
            var bytes = new byte[] { 1, 2, 3 };
            var packet = new XModemPacket.Data(1, bytes, bytes.Length);
            var protocolEvent = new XModemEvent.DataBlockReady(1, bytes, bytes.Length, true);
            bytes[0] = 9;
            packet.Payload[0] = 8;
            protocolEvent.Data[0] = 7;
            Assert.Equal(1, packet.Payload[0]);
            Assert.Equal(1, protocolEvent.Data[0]);
        }

        [Fact]
        public void StreamDecoderHandlesEverySplitAndCoalescedFrames()
        {
            var frame = new XModemPacketEncoder().Encode(new XModemPacket.Data(1, new byte[128], 128));
            for (var split = 1; split < frame.Length; split++)
            {
                var decoder = new XModemStreamDecoder();
                Assert.Empty(decoder.Feed(frame.Take(split).ToArray()));
                var packet = Assert.IsType<XModemEvent.PacketReceived>(
                    Assert.Single(decoder.Feed(frame.Skip(split).ToArray())));
                Assert.IsType<XModemPacket.Data>(packet.Packet);
                Assert.Equal(0, decoder.BufferedByteCount);
            }

            var combined = frame.Concat(frame).Append(XModemControlBytes.Eot).ToArray();
            Assert.Equal(3, new XModemStreamDecoder().Feed(combined).Count);
        }

        [Fact]
        public void StreamDecoderRecoversAfterCorruptionAndPartialTimeout()
        {
            var good = new XModemPacketEncoder().Encode(new XModemPacket.Data(1, new byte[128], 128));
            var damaged = (byte[])good.Clone();
            damaged[70] = 1;
            var decoder = new XModemStreamDecoder();
            var events = decoder.Feed(damaged.Concat(good).ToArray());
            Assert.IsType<XModemEvent.InvalidFrame>(events[0]);
            Assert.IsType<XModemEvent.PacketReceived>(events[1]);
            Assert.Empty(decoder.Feed(good.Take(17).ToArray()));
            Assert.Throws<InvalidOperationException>(() => decoder.Integrity = XModemIntegrity.Checksum);
            Assert.IsType<XModemEvent.TimeoutElapsed>(decoder.OnTimeout());
            Assert.IsType<XModemEvent.PacketReceived>(Assert.Single(decoder.Feed(good)));
        }

        [Fact]
        public void ValidatePublicArguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new XModemOptions((XModemMode)42));
            Assert.Throws<ArgumentOutOfRangeException>(() => new XModemOptions(maxRetries: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new XModemOptions(crcFallbackAttempts: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new XModemStreamDecoder((XModemIntegrity)42));
            Assert.Throws<ArgumentNullException>(() => new XModemPacketEncoder().Encode(null!));
            Assert.Throws<ArgumentNullException>(() => new XModemPacketDecoder().Decode(null!));
            Assert.Throws<ArgumentNullException>(() => new XModemStreamDecoder().Feed(null!));
            Assert.Throws<ArgumentOutOfRangeException>(() => new XModemPacket.Data(-1, Array.Empty<byte>(), 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new XModemPacket.Data(1, new byte[129], 129));
            Assert.Throws<ArgumentOutOfRangeException>(() => new XModemPacket.Data(1, Array.Empty<byte>(), 0, 512));
        }
    }
}
