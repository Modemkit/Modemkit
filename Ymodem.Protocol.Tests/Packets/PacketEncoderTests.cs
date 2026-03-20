using System.Text;

namespace Ymodem.Protocol.Tests
{
    public sealed class PacketEncoderTests
    {
        [Fact]
        public void EncodeEotReturnsSingleEotByte()
        {
            var encoder = new YModemPacketEncoder();

            var bytes = encoder.Encode(new YModemPacket.Eot());

            Assert.Equal([YModemControlBytes.Eot], bytes);
        }

        [Fact]
        public void EncodeHeaderUsesBlockZeroSohAndAsciiMetadata()
        {
            var encoder = new YModemPacketEncoder();
            var packet = new YModemPacket.Header(new YModemFileDescriptor("demo.bin", 123));

            var bytes = encoder.Encode(packet);

            Assert.Equal(133, bytes.Length);
            Assert.Equal(YModemControlBytes.Soh, bytes[0]);
            Assert.Equal(0, bytes[1]);
            Assert.Equal(255, bytes[2]);

            var payload = new byte[128];
            Buffer.BlockCopy(bytes, 3, payload, 0, payload.Length);
            var prefix = Encoding.ASCII.GetString(payload, 0, "demo.bin\0123\0".Length);
            Assert.Equal("demo.bin\0123\0", prefix);
        }

        [Fact]
        public void EncodeDataUsesConfiguredBlockSizeAndPadsWithCpmEof()
        {
            var encoder = new YModemPacketEncoder(128);
            var packet = new YModemPacket.Data(1, [0x41, 0x42, 0x43], 3);

            var bytes = encoder.Encode(packet);

            Assert.Equal(133, bytes.Length);
            Assert.Equal(YModemControlBytes.Soh, bytes[0]);
            Assert.Equal(1, bytes[1]);
            Assert.Equal(254, bytes[2]);
            Assert.Equal(0x41, bytes[3]);
            Assert.Equal(0x42, bytes[4]);
            Assert.Equal(0x43, bytes[5]);
            Assert.Equal(YModemControlBytes.CpmEof, bytes[6]);
            Assert.Equal(YModemControlBytes.CpmEof, bytes[130]);
        }

        [Fact]
        public void EncoderUsesPacketBlockSizeWhenEncodingDataFrames()
        {
            var encoder = new YModemPacketEncoder(1024);
            var packet = new YModemPacket.Data(1, new byte[128], 3, 128);

            var bytes = encoder.Encode(packet);

            Assert.Equal(YModemControlBytes.Soh, bytes[0]);
            Assert.Equal(128 + 5, bytes.Length);
        }

        [Fact]
        public void EncodeHeaderWithNonAsciiFileNameThrowsInvalidOperationException()
        {
            var encoder = new YModemPacketEncoder();
            var packet = new YModemPacket.Header(new YModemFileDescriptor("文件.bin", 100));

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => encoder.Encode(packet));

            Assert.Contains("non-ASCII", exception.Message);
        }
    }
}
