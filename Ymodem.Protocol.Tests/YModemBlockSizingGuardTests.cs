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

        [Fact]
        public void GetDataBlockSizeReportsModeWhenBlockOptionContainsUnsupportedMode()
        {
            var exception = Assert.Throws<ArgumentOutOfRangeException>(() => YModemBlockSizing.GetDataBlockSize((YModemBlockOptions)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(YModemBlockOptions)), 1));

            Assert.Equal("Mode", exception.ParamName);
        }

        [Fact]
        public void GetHeaderBlockSizeReportsModeWhenBlockOptionContainsUnsupportedMode()
        {
            var file = new YModemFileDescriptor("demo.bin", 1);
            var options = (YModemBlockOptions)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(YModemBlockOptions));

            var exception = Assert.Throws<ArgumentOutOfRangeException>(() => YModemBlockSizing.GetHeaderBlockSize(options, file));

            Assert.Equal("Mode", exception.ParamName);
        }
    }
}
