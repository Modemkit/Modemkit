using System;
using System.Text;

namespace Ymodem.Protocol
{
    internal static class YModemBlockSizing
    {
        public static int GetBlockSize(long remainingFileBytes)
        {
            if (remainingFileBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(remainingFileBytes), "Remaining file bytes must be non-negative.");
            }

            return remainingFileBytes < 128 ? 128 : 1024;
        }

        public static int GetHeaderBlockSize(YModemFileDescriptor file)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            var headerLength = Encoding.ASCII.GetByteCount(file.FileName + "\0" + file.FileSize + "\0");
            return headerLength <= 128 ? 128 : 1024;
        }
    }
}
