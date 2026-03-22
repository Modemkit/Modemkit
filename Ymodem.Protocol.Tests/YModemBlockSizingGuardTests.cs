using System;
using System.Reflection;
using System.Runtime.Serialization;

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
            var options = CreateBlockOptionsWithUnsupportedMode();

            var exception = Assert.Throws<ArgumentOutOfRangeException>(() => YModemBlockSizing.GetDataBlockSize(options, 1));

            Assert.Equal("Mode", exception.ParamName);
        }

        [Fact]
        public void GetHeaderBlockSizeReportsModeWhenBlockOptionContainsUnsupportedMode()
        {
            var file = new YModemFileDescriptor("demo.bin", 1);
            var options = CreateBlockOptionsWithUnsupportedMode();

            var exception = Assert.Throws<ArgumentOutOfRangeException>(() => YModemBlockSizing.GetHeaderBlockSize(options, file));

            Assert.Equal("Mode", exception.ParamName);
        }

        private static YModemBlockOptions CreateBlockOptionsWithUnsupportedMode()
        {
            var options = (YModemBlockOptions)FormatterServices.GetUninitializedObject(typeof(YModemBlockOptions));
            var modeField = typeof(YModemBlockOptions).GetField("<Mode>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(modeField);
            modeField!.SetValue(options, (YModemBlockMode)99);
            return options;
        }
    }
}
