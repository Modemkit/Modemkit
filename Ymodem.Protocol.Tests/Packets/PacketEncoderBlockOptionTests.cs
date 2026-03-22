using System;

namespace Ymodem.Protocol.Tests
{
    public sealed class PacketEncoderBlockOptionTests
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
