using Xunit;

namespace Ymodem.Protocol.Tests
{
    public sealed class PacketDecoderTests
    {
        [Fact]
        public void DecodeHeaderFrameRoundTripsToHeaderPacket()
        {
            var encoder = new YModemPacketEncoder();
            var decoder = new YModemPacketDecoder();
            var file = new YModemFileDescriptor("demo.bin", 123);

            var bytes = encoder.Encode(new YModemPacket.Header(file));
            YModemPacket packet = decoder.Decode(bytes);

            YModemPacket.Header header = Assert.IsType<YModemPacket.Header>(packet);
            Assert.Equal("demo.bin", header.File.FileName);
            Assert.Equal(123, header.File.FileSize);
        }

        [Fact]
        public void DecodeEmptyBlockZeroReturnsBatchTrailer()
        {
            var encoder = new YModemPacketEncoder();
            var decoder = new YModemPacketDecoder();

            var bytes = encoder.Encode(new YModemPacket.BatchTrailer());
            YModemPacket packet = decoder.Decode(bytes);

            Assert.IsType<YModemPacket.BatchTrailer>(packet);
        }

        [Fact]
        public void DecodeTamperedCrcThrowsInvalidOperationException()
        {
            var encoder = new YModemPacketEncoder();
            var decoder = new YModemPacketDecoder();

            var bytes = encoder.Encode(new YModemPacket.BatchTrailer());
            bytes[^1] ^= 0x01;

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => decoder.Decode(bytes));

            Assert.Equal("Packet CRC is invalid.", exception.Message);
        }

        [Fact]
        public void DecodeInvalidComplementThrowsInvalidOperationException()
        {
            var encoder = new YModemPacketEncoder();
            var decoder = new YModemPacketDecoder();

            var bytes = encoder.Encode(new YModemPacket.BatchTrailer());
            bytes[2] ^= 0x01;

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => decoder.Decode(bytes));

            Assert.Equal("Packet block number complement is invalid.", exception.Message);
        }
    }
}
