using System;

namespace Xmodem.Protocol
{
    public enum XModemMode
    {
        Checksum,
        Crc,
        OneK,
        G,
        QModemG = G,
        OneKG = G
    }

    public enum XModemIntegrity
    {
        Checksum,
        Crc16
    }

    public sealed class XModemOptions
    {
        public XModemOptions(
            XModemMode mode = XModemMode.Crc,
            int maxRetries = 10,
            bool allowChecksumFallback = false,
            int crcFallbackAttempts = 3,
            int maxBufferedBlocks = 16)
        {
            if (mode != XModemMode.Checksum && mode != XModemMode.Crc && mode != XModemMode.OneK && mode != XModemMode.G)
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            if (maxRetries < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRetries));
            }

            if (crcFallbackAttempts < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(crcFallbackAttempts));
            }

            if (maxBufferedBlocks < 1 || maxBufferedBlocks > 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBufferedBlocks));
            }

            if (mode == XModemMode.G && allowChecksumFallback)
            {
                throw new ArgumentException("XMODEM-G requires CRC and cannot fall back to checksum.", nameof(allowChecksumFallback));
            }

            Mode = mode;
            MaxRetries = maxRetries;
            AllowChecksumFallback = allowChecksumFallback;
            CrcFallbackAttempts = crcFallbackAttempts;
            MaxBufferedBlocks = maxBufferedBlocks;
        }

        public XModemMode Mode { get; }

        public int MaxRetries { get; }

        public bool AllowChecksumFallback { get; }

        public int CrcFallbackAttempts { get; }

        public int MaxBufferedBlocks { get; }
    }

    public static class XModemControlBytes
    {
        public const byte Soh = 0x01;
        public const byte Stx = 0x02;
        public const byte Eot = 0x04;
        public const byte Ack = 0x06;
        public const byte Nak = 0x15;
        public const byte Can = 0x18;
        public const byte Padding = 0x1A;
        public const byte CrcRequest = 0x43;
        public const byte StreamingRequest = 0x47;
    }
}
