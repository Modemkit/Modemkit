using System;

namespace Ymodem.Protocol.Tests
{
    public sealed class PacketConstructionTests
    {

        [Theory]
        [InlineData(-1)]
        [InlineData(3)]
        public void BlockOptionsRejectUnsupportedMode(int mode)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new YModemBlockOptions((YModemBlockMode)mode));
        }
        [Theory]
        [InlineData(1)]
        [InlineData(127)]
        [InlineData(255)]
        [InlineData(1023)]
        public void HeaderRejectsUnsupportedExplicitBlockSize(int blockSize)
        {
            var file = new YModemFileDescriptor("demo.bin", 1);

            Assert.Throws<ArgumentOutOfRangeException>(() => new YModemPacket.Header(file, blockSize));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(127)]
        [InlineData(255)]
        [InlineData(2048)]
        public void DataRejectsUnsupportedExplicitBlockSize(int blockSize)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new YModemPacket.Data(1, new byte[128], 1, blockSize));
        }

        [Fact]
        public void DataRejectsLengthLargerThanExplicitBlockSize()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new YModemPacket.Data(1, new byte[1024], 129, 128));
        }
    }
}
