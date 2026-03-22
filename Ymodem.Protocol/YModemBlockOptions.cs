namespace Ymodem.Protocol
{
    public sealed class YModemBlockOptions
    {
        public YModemBlockOptions()
            : this(YModemBlockMode.Dynamic1K, YModemBlockMode.Dynamic1K)
        {
        }

        public YModemBlockOptions(YModemBlockMode block0Mode, YModemBlockMode dataBlockMode)
        {
            Block0Mode = block0Mode;
            DataBlockMode = dataBlockMode;
        }

        public YModemBlockMode Block0Mode
        {
            get;
        }

        public YModemBlockMode DataBlockMode
        {
            get;
        }

        public static YModemBlockOptions FromMode(YModemBlockMode blockMode)
        {
            return new YModemBlockOptions(blockMode, blockMode);
        }
    }
}
