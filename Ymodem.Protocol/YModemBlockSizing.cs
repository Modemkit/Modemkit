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
                case YModemBlockMode.Fixed1K:
                    return StxPayloadSize;
                default:
                    throw new ArgumentOutOfRangeException(nameof(blockMode), "Unsupported YMODEM block mode.");
            }
        }

        public static int GetDataBlockSize(YModemBlockOptions blockOptions, long remainingFileBytes)
        {
            if (blockOptions == null)
            {
                throw new ArgumentNullException(nameof(blockOptions));
            }

            switch (blockOptions.Mode)
            {
                case YModemBlockMode.Fixed128:
                    return SohPayloadSize;
                case YModemBlockMode.Dynamic1K:
                    return GetBlockSizeForPayloadLength(remainingFileBytes);
                case YModemBlockMode.Fixed1K:
                    return blockOptions.Use1KFinalDataBlock || remainingFileBytes > SohPayloadSize
                        ? StxPayloadSize
                        : SohPayloadSize;
                default:
                    throw new ArgumentOutOfRangeException(nameof(blockOptions.Mode), "Unsupported YMODEM block mode.");
            }
        }

        public static int GetHeaderBlockSize(YModemBlockOptions blockOptions, YModemFileDescriptor file)
        {
            if (blockOptions == null)
            {
                throw new ArgumentNullException(nameof(blockOptions));
            }

            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            var headerLength = Encoding.ASCII.GetByteCount(BuildHeaderMetadata(file));
            switch (blockOptions.Mode)
            {
                case YModemBlockMode.Fixed128:
                    return SohPayloadSize;
                case YModemBlockMode.Dynamic1K:
                    return GetBlockSizeForPayloadLength(headerLength);
                case YModemBlockMode.Fixed1K:
                    return blockOptions.Use1KBlock0 ? StxPayloadSize : GetBlockSizeForPayloadLength(headerLength);
                default:
                    throw new ArgumentOutOfRangeException(nameof(blockOptions.Mode), "Unsupported YMODEM block mode.");
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
