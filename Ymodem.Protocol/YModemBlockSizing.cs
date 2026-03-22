using System;
using System.Text;

namespace Ymodem.Protocol
{
    internal static class YModemBlockSizing
    {
        private const int SohPayloadSize = 128;
        private const int StxPayloadSize = 1024;

        public static int GetConfiguredDataBlockSize(YModemBlockMode blockMode)
        {
            switch (blockMode)
            {
                case YModemBlockMode.Fixed128:
                    return SohPayloadSize;
                case YModemBlockMode.Dynamic1K:
                    return StxPayloadSize;
                default:
                    throw new ArgumentOutOfRangeException(nameof(blockMode), "Unsupported YMODEM block mode.");
            }
        }

        public static int GetDataBlockSize(long remainingFileBytes)
        {
            return GetBlockSizeForPayloadLength(remainingFileBytes);
        }

        // Block 0 follows the same payload-capacity rule in dynamic mode.
        public static int GetHeaderBlockSize(int configuredDataBlockSize, YModemFileDescriptor file)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            var headerLength = Encoding.ASCII.GetByteCount(file.FileName + "\0" + file.FileSize + "\0");
            return configuredDataBlockSize == SohPayloadSize
                ? SohPayloadSize
                : GetBlockSizeForPayloadLength(headerLength);
        }

        // Dynamic 1K mode uses a single payload-capacity rule:
        // payloads that fit in SOH remain 128-byte packets, and only larger payloads switch to STX/1K.
        internal static int GetBlockSizeForPayloadLength(long payloadLength)
        {
            if (payloadLength < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(payloadLength), "Payload length must be non-negative.");
            }

            return payloadLength <= SohPayloadSize ? SohPayloadSize : StxPayloadSize;
        }
    }
}
