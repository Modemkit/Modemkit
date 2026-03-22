using System;
using System.Globalization;
using System.Text;

namespace Ymodem.Protocol
{
    internal static class YModemBlockSizing
    {
        private const int SohPayloadSize = 128;
        private const int StxPayloadSize = 1024;

        public static int GetConfiguredBlockSize(YModemBlockMode blockMode)
        {
            switch (blockMode)
            {
                case YModemBlockMode.Fixed128:
                    return SohPayloadSize;
                case YModemBlockMode.Dynamic1K:
                    return StxPayloadSize;
                case YModemBlockMode.Fixed1K:
                    return StxPayloadSize;
                default:
                    throw new ArgumentOutOfRangeException(nameof(blockMode), "Unsupported YMODEM block mode.");
            }
        }

        public static int GetDataBlockSize(YModemBlockMode blockMode, long remainingFileBytes)
        {
            switch (blockMode)
            {
                case YModemBlockMode.Fixed128:
                    return SohPayloadSize;
                case YModemBlockMode.Dynamic1K:
                    return GetBlockSizeForPayloadLength(remainingFileBytes);
                case YModemBlockMode.Fixed1K:
                    return StxPayloadSize;
                default:
                    throw new ArgumentOutOfRangeException(nameof(blockMode), "Unsupported YMODEM block mode.");
            }
        }

        // Block 0 uses the selected block mode and dynamic capacity rule when requested.
        public static int GetHeaderBlockSize(YModemBlockMode blockMode, YModemFileDescriptor file)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            var headerLength = Encoding.ASCII.GetByteCount(BuildHeaderMetadata(file));
            switch (blockMode)
            {
                case YModemBlockMode.Fixed128:
                    return SohPayloadSize;
                case YModemBlockMode.Dynamic1K:
                    return GetBlockSizeForPayloadLength(headerLength);
                case YModemBlockMode.Fixed1K:
                    return StxPayloadSize;
                default:
                    throw new ArgumentOutOfRangeException(nameof(blockMode), "Unsupported YMODEM block mode.");
            }
        }

        internal static string BuildHeaderMetadata(YModemFileDescriptor file)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            return file.FileName + "\0" + file.FileSize.ToString(CultureInfo.InvariantCulture) + "\0";
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
