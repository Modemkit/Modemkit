using System;

namespace Ymodem.Protocol.Tests
{
    public sealed class YModemBlockSizingTests
    {
        [Theory]
        [InlineData(0, 128)]
        [InlineData(1, 128)]
        [InlineData(127, 128)]
        [InlineData(128, 128)]
        [InlineData(129, 1024)]
        [InlineData(1024, 1024)]
        public void GetBlockSizeForPayloadLengthUsesPayloadCapacityRule(long payloadLength, int expectedBlockSize)
        {
            var blockSize = YModemBlockSizing.GetBlockSizeForPayloadLength(payloadLength);

            Assert.Equal(expectedBlockSize, blockSize);
        }

        [Fact]
        public void GetBlockSizeForPayloadLengthRejectsNegativeLengths()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => YModemBlockSizing.GetBlockSizeForPayloadLength(-1));
        }

        [Theory]
        [InlineData(YModemBlockMode.Fixed128, 119, 123, 128)]
        [InlineData(YModemBlockMode.Fixed128, 120, 123, 128)]
        [InlineData(YModemBlockMode.Dynamic1K, 119, 123, 128)]
        [InlineData(YModemBlockMode.Dynamic1K, 120, 123, 1024)]
        public void GetHeaderBlockSizeUsesConfiguredModeAndPayloadCapacityRule(YModemBlockMode blockMode, int fileNameLength, long fileSize, int expectedBlockSize)
        {
            var configuredDataBlockSize = YModemBlockSizing.GetConfiguredDataBlockSize(blockMode);
            var file = new YModemFileDescriptor(new string('a', fileNameLength) + ".bin", fileSize);

            var blockSize = YModemBlockSizing.GetHeaderBlockSize(configuredDataBlockSize, file);

            Assert.Equal(expectedBlockSize, blockSize);
        }

        [Theory]
        [InlineData(0, 128)]
        [InlineData(128, 128)]
        [InlineData(129, 1024)]
        public void GetDataBlockSizeDelegatesToPayloadCapacityRule(long remainingFileBytes, int expectedBlockSize)
        {
            var blockSize = YModemBlockSizing.GetDataBlockSize(remainingFileBytes);

            Assert.Equal(expectedBlockSize, blockSize);
        }
    }
}
