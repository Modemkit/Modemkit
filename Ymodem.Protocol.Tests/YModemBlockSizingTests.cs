using System;
using System.Globalization;

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
        [InlineData(YModemBlockMode.Fixed128, 128)]
        [InlineData(YModemBlockMode.Dynamic1K, 1024)]
        [InlineData(YModemBlockMode.Fixed1K, 1024)]
        public void GetConfiguredBlockSizeReturnsExpectedValue(YModemBlockMode blockMode, int expectedBlockSize)
        {
            var blockSize = YModemBlockSizing.GetConfiguredBlockSize(blockMode);

            Assert.Equal(expectedBlockSize, blockSize);
        }

        [Theory]
        [InlineData(YModemBlockMode.Fixed128, 1, 128)]
        [InlineData(YModemBlockMode.Dynamic1K, 1, 128)]
        [InlineData(YModemBlockMode.Fixed1K, 1, 1024)]
        public void GetDataBlockSizeUsesSelectedBlockMode(YModemBlockMode blockMode, long remainingBytes, int expectedBlockSize)
        {
            var blockSize = YModemBlockSizing.GetDataBlockSize(blockMode, remainingBytes);

            Assert.Equal(expectedBlockSize, blockSize);
        }

        [Theory]
        [InlineData(YModemBlockMode.Fixed128, 119, 128)]
        [InlineData(YModemBlockMode.Dynamic1K, 119, 128)]
        [InlineData(YModemBlockMode.Dynamic1K, 120, 1024)]
        [InlineData(YModemBlockMode.Fixed1K, 119, 1024)]
        public void GetHeaderBlockSizeUsesSelectedBlockMode(YModemBlockMode blockMode, int fileNameLength, int expectedBlockSize)
        {
            var file = new YModemFileDescriptor(new string('a', fileNameLength) + ".bin", 123);

            var blockSize = YModemBlockSizing.GetHeaderBlockSize(blockMode, file);

            Assert.Equal(expectedBlockSize, blockSize);
        }

        [Fact]
        public void BuildHeaderMetadataUsesInvariantCultureForFileSize()
        {
            var originalCulture = CultureInfo.CurrentCulture;
            var originalUiCulture = CultureInfo.CurrentUICulture;

            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
                CultureInfo.CurrentUICulture = new CultureInfo("ar-SA");

                var metadata = YModemBlockSizing.BuildHeaderMetadata(new YModemFileDescriptor("demo.bin", 123));

                Assert.Equal("demo.bin\0123\0", metadata);
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
                CultureInfo.CurrentUICulture = originalUiCulture;
            }
        }
    }
}
