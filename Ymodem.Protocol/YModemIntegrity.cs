using System;

namespace Ymodem.Protocol
{
    public enum YModemIntegrity
    {
        Crc16 = 0,
        Checksum = 1,
    }

    internal static class YModemIntegrityChecks
    {
        public static void Validate(YModemIntegrity integrity)
        {
            if (integrity != YModemIntegrity.Crc16 && integrity != YModemIntegrity.Checksum)
            {
                throw new ArgumentOutOfRangeException(nameof(integrity));
            }
        }

        public static byte Checksum(byte[] bytes, int offset, int count)
        {
            byte checksum = 0;
            for (var index = offset; index < offset + count; index++)
            {
                checksum = unchecked((byte)(checksum + bytes[index]));
            }

            return checksum;
        }
    }
}
