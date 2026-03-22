using System.Globalization;
using System.Text;

namespace Ymodem.Protocol.Tests
{
    public sealed partial class PacketEncoderTests
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
        public void EncodeHeaderUsesBlockZeroSohWhenMetadataFits128Bytes()
        {
            var encoder = new YModemPacketEncoder();
            var packet = new YModemPacket.Header(new YModemFileDescriptor(new string('a', 118) + ".bin", 123));

            var bytes = encoder.Encode(packet);

            Assert.Equal(133, bytes.Length);
            Assert.Equal(YModemControlBytes.Soh, bytes[0]);
            Assert.Equal(0, bytes[1]);
            Assert.Equal(255, bytes[2]);
        }

        [Fact]
        public void EncodeHeaderUsesBlockZeroSohWhenMetadataIsExactly128BytesInDynamic1KMode()
        {
            var encoder = new YModemPacketEncoder();
            var packet = new YModemPacket.Header(new YModemFileDescriptor(new string('a', 119) + ".bin", 123));

            var bytes = encoder.Encode(packet);

            Assert.Equal(133, bytes.Length);
            Assert.Equal(YModemControlBytes.Soh, bytes[0]);
            Assert.Equal(0, bytes[1]);
            Assert.Equal(255, bytes[2]);
        }

        [Fact]
        public void EncodeHeaderUsesBlockZeroStxWhenMetadataExceeds128Bytes()
        {
            var encoder = new YModemPacketEncoder();
            var packet = new YModemPacket.Header(new YModemFileDescriptor(new string('a', 120) + ".bin", 123));

            var bytes = encoder.Encode(packet);

            Assert.Equal(1029, bytes.Length);
            Assert.Equal(YModemControlBytes.Stx, bytes[0]);
            Assert.Equal(0, bytes[1]);
            Assert.Equal(255, bytes[2]);
        }

        [Fact]
        public void Fixed128ModeRejectsHeaderMetadataLargerThan128Bytes()
        {
            var encoder = new YModemPacketEncoder(YModemBlockMode.Fixed128);
            var packet = new YModemPacket.Header(new YModemFileDescriptor(new string('a', 120) + ".bin", 123));

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => encoder.Encode(packet));

            Assert.Contains("selected header block size of 128 bytes", exception.Message);
        }

        [Fact]
        public void EncodeBatchTrailerUses128ByteBlockByDefault()
        {
            var encoder = new YModemPacketEncoder();

            var bytes = encoder.Encode(new YModemPacket.BatchTrailer());

            Assert.Equal(133, bytes.Length);
            Assert.Equal(YModemControlBytes.Soh, bytes[0]);
        }

        [Fact]
        public void EncodeDataUsesConfiguredBlockSizeAndPadsWithCpmEof()
        {
            var encoder = new YModemPacketEncoder(YModemBlockMode.Fixed128);
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
        public void EncodeDataUsesFixed128Mode()
        {
            var encoder = new YModemPacketEncoder(YModemBlockMode.Fixed128);
            var packet = new YModemPacket.Data(1, [0x41, 0x42, 0x43], 3);

            var bytes = encoder.Encode(packet);

            Assert.Equal(YModemControlBytes.Soh, bytes[0]);
            Assert.Equal(128 + 5, bytes.Length);
        }

        [Fact]
        public void EncoderUsesPacketBlockSizeWhenEncodingDataFrames()
        {
            var encoder = new YModemPacketEncoder();
            var packet = new YModemPacket.Data(1, new byte[128], 3, 128);

            var bytes = encoder.Encode(packet);

            Assert.Equal(YModemControlBytes.Soh, bytes[0]);
            Assert.Equal(128 + 5, bytes.Length);
        }

        [Fact]
        public void ExplicitBlockSizeAllowsShortPayloadBuffers()
        {
            var encoder = new YModemPacketEncoder();
            var packet = new YModemPacket.Data(1, [0x41, 0x42, 0x43], 3, 128);

            var bytes = encoder.Encode(packet);

            Assert.Equal(YModemControlBytes.Soh, bytes[0]);
            Assert.Equal(128 + 5, bytes.Length);
            Assert.Equal(0x41, bytes[3]);
            Assert.Equal(0x42, bytes[4]);
            Assert.Equal(0x43, bytes[5]);
        }

        [Fact]
        public void EncodeHeaderWithNonAsciiFileNameThrowsInvalidOperationException()
        {
            var encoder = new YModemPacketEncoder();
            var packet = new YModemPacket.Header(new YModemFileDescriptor("文件.bin", 100));

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => encoder.Encode(packet));

            Assert.Contains("non-ASCII", exception.Message);
        }

        [Fact]
        public void EncodeOversizedAsciiHeaderThrowsInvalidOperationException()
        {
            var encoder = new YModemPacketEncoder();
            var packet = new YModemPacket.Header(new YModemFileDescriptor(new string('a', 1020) + ".bin", 100));

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => encoder.Encode(packet));

            Assert.Contains("selected header block size of 1024 bytes", exception.Message);
        }

        [Fact]
        public void EncodeHeaderUsesInvariantCultureForFileSize()
        {
            var originalCulture = CultureInfo.CurrentCulture;
            var originalUiCulture = CultureInfo.CurrentUICulture;

            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
                CultureInfo.CurrentUICulture = new CultureInfo("ar-SA");

                var encoder = new YModemPacketEncoder();
                var packet = new YModemPacket.Header(new YModemFileDescriptor("demo.bin", 123));

                var bytes = encoder.Encode(packet);
                var payload = new byte[128];
                Buffer.BlockCopy(bytes, 3, payload, 0, payload.Length);
                var prefix = Encoding.ASCII.GetString(payload, 0, "demo.bin\0123\0".Length);

                Assert.Equal("demo.bin\0123\0", prefix);
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
                CultureInfo.CurrentUICulture = originalUiCulture;
            }
        }

    }
}

namespace Ymodem.Protocol.Tests
{
    public sealed partial class PacketEncoderTests
    {
        [Fact]
        public void EncodeHeaderCanForce1KBlockZeroIndependently()
        {
            var encoder = new YModemPacketEncoder(new YModemBlockOptions(YModemBlockMode.Fixed1K, true, true));
            var bytes = encoder.Encode(new YModemPacket.Header(new YModemFileDescriptor("demo.bin", 123)));

            Assert.Equal(YModemControlBytes.Stx, bytes[0]);
            Assert.Equal(1024 + 5, bytes.Length);
        }

        [Fact]
        public void EncodeDataCanForce1KBlocksIndependently()
        {
            var encoder = new YModemPacketEncoder(new YModemBlockOptions(YModemBlockMode.Fixed1K, false, true));
            var bytes = encoder.Encode(new YModemPacket.Data(1, new byte[] { 0x41 }, 1));

            Assert.Equal(YModemControlBytes.Stx, bytes[0]);
            Assert.Equal(1024 + 5, bytes.Length);
        }
    }
}
