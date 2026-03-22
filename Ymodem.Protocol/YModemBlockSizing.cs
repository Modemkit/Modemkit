using System;
using System.Text;

namespace Ymodem.Protocol
{
    internal static class YModemBlockSizing
    {
        public static int GetConfiguredDataBlockSize(YModemBlockMode blockMode)
        {
            switch (blockMode)
            {
                case YModemBlockMode.Fixed128:
                    return 128;
                case YModemBlockMode.Dynamic1K:
                    return 1024;
                default:
                    throw new ArgumentOutOfRangeException(nameof(blockMode), "Unsupported YMODEM block mode.");
            }
        }

        // Dynamic 1K mode keeps 128-byte packets for values that still fit in 128 bytes.
        // Only values above 128 bytes switch to STX/1K packets.
        public static int GetDataBlockSize(long remainingFileBytes)
        {
            if (remainingFileBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(remainingFileBytes), "Remaining file bytes must be non-negative.");
            }

            return remainingFileBytes <= 128 ? 128 : 1024;
        }

        // Block 0 follows the same capacity rule in dynamic mode:
        // values that fit in 128 bytes stay on SOH, and only larger headers switch to STX/1K.
        public static int GetHeaderBlockSize(int configuredDataBlockSize, YModemFileDescriptor file)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            var headerLength = Encoding.ASCII.GetByteCount(file.FileName + "\0" + file.FileSize + "\0");
            return configuredDataBlockSize == 128
                ? 128
                : GetDataBlockSize(headerLength);
        }
    }
}
