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

        [Fact]
        public void DecodeAbsoluteBlock256FrameInDataPhaseReturnsWrappedDataPacket()
        {
            var encoder = new YModemPacketEncoder();
            var decoder = new YModemPacketDecoder();
            var payload = new byte[] { 0x41, 0x42, 0x43 };

            var bytes = encoder.Encode(new YModemPacket.Data(256, payload, payload.Length));
            YModemPacket packet = decoder.Decode(bytes, isDataPhase: true);

            YModemPacket.Data data = Assert.IsType<YModemPacket.Data>(packet);
            Assert.Equal(0, data.BlockNumber);
            Assert.Equal(1024, data.Payload.Length);
            Assert.Equal(0x41, data.Payload[0]);
            Assert.Equal(0x42, data.Payload[1]);
            Assert.Equal(0x43, data.Payload[2]);
        }

        [Fact]
        public void DecodeAbsoluteBlock256FrameWithTamperedCrcInDataPhaseThrowsInvalidOperationException()
        {
            var encoder = new YModemPacketEncoder();
            var decoder = new YModemPacketDecoder();
            var payload = new byte[] { 0x41, 0x42, 0x43 };

            var bytes = encoder.Encode(new YModemPacket.Data(256, payload, payload.Length));
            bytes[^1] ^= 0x01;

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => decoder.Decode(bytes, isDataPhase: true));

            Assert.Equal("Packet CRC is invalid.", exception.Message);
        }

        [Fact]
        public void DecodeBlockZeroInDataPhaseReturnsDataPacketNotHeader()
        {
            var encoder = new YModemPacketEncoder();
            var decoder = new YModemPacketDecoder();
            var file = new YModemFileDescriptor("demo.bin", 123);

            // A header packet has block number 0; in data phase that same frame is a rollover data block
            var bytes = encoder.Encode(new YModemPacket.Header(file));
            YModemPacket packet = decoder.Decode(bytes, isDataPhase: true);

            YModemPacket.Data data = Assert.IsType<YModemPacket.Data>(packet);
            Assert.Equal(0, data.BlockNumber);
        }

        [Fact]
        public void DecodeBlockZeroOutsideDataPhaseReturnsHeader()
        {
            var encoder = new YModemPacketEncoder();
            var decoder = new YModemPacketDecoder();
            var file = new YModemFileDescriptor("demo.bin", 123);

            var bytes = encoder.Encode(new YModemPacket.Header(file));
            YModemPacket packet = decoder.Decode(bytes, isDataPhase: false);

            Assert.IsType<YModemPacket.Header>(packet);
        }
    }
}
