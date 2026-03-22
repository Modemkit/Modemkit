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

        // Data blocks follow the sender rule requested by the caller:
        // values below 128 bytes use 128-byte packets, and 128+ bytes use 1K packets.
        public static int GetDataBlockSize(long remainingFileBytes)
        {
            if (remainingFileBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(remainingFileBytes), "Remaining file bytes must be non-negative.");
            }

            return remainingFileBytes < 128 ? 128 : 1024;
        }

        // Block 0 follows header-metadata capacity instead:
        // 128-byte payloads stay on SOH, and values above 128 bytes switch to STX/1K.
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
