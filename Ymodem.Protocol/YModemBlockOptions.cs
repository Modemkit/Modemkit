using System;

namespace Ymodem.Protocol
{
    public sealed class YModemBlockOptions
    {
        public YModemBlockOptions()
            : this(YModemBlockMode.Dynamic1K)
        {
        }

        public YModemBlockOptions(YModemBlockMode mode)
            : this(mode, true, true)
        {
        }

        public YModemBlockOptions(YModemBlockMode mode, bool use1KBlock0, bool use1KFinalDataBlock)
        {
            switch (mode)
            {
                case YModemBlockMode.Fixed128:
                case YModemBlockMode.Dynamic1K:
                case YModemBlockMode.Fixed1K:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), "Unsupported YMODEM block mode.");
            }

            Mode = mode;
            Use1KBlock0 = use1KBlock0;
            Use1KFinalDataBlock = use1KFinalDataBlock;
        }

        public YModemBlockMode Mode
        {
            get;
        }

        public bool Use1KBlock0
        {
            get;
        }

        public bool Use1KFinalDataBlock
        {
            get;
        }

        public static YModemBlockOptions FromMode(YModemBlockMode blockMode)
        {
            return new YModemBlockOptions(blockMode);
        }
    }
}
