using System;

namespace Ymodem.Protocol.Tests
{
    public sealed class YModemBlockSizingGuardTests
    {
        [Fact]
        public void GetDataBlockSizeRejectsNullBlockOptions()
        {
            Assert.Throws<ArgumentNullException>(() => YModemBlockSizing.GetDataBlockSize(null!, 1));
        }

        [Fact]
        public void GetHeaderBlockSizeRejectsNullBlockOptions()
        {
            var file = new YModemFileDescriptor("demo.bin", 1);

            Assert.Throws<ArgumentNullException>(() => YModemBlockSizing.GetHeaderBlockSize(null!, file));
        }

        [Fact]
        public void GetHeaderBlockSizeRejectsNullFile()
        {
            var options = new YModemBlockOptions();

            Assert.Throws<ArgumentNullException>(() => YModemBlockSizing.GetHeaderBlockSize(options, null!));
        }
    }
}
