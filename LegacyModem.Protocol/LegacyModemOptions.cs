using System;

namespace LegacyModem.Protocol
{
    public enum LegacyModemVariant
    {
        Modem7,
        Telink,
        SeaLink,
    }

    public enum LegacyModemIntegrity
    {
        Checksum,
        Crc16,
    }

    public sealed class LegacyModemOptions
    {
        public LegacyModemOptions(
            LegacyModemVariant variant = LegacyModemVariant.SeaLink,
            LegacyModemIntegrity integrity = LegacyModemIntegrity.Crc16,
            int maxRetries = 10,
            int windowSize = 6,
            bool overdrive = false,
            bool enableResync = true,
            bool allowFallback = true,
            int maxBufferedBytes = 1048576)
        {
            if (variant != LegacyModemVariant.Modem7 && variant != LegacyModemVariant.Telink && variant != LegacyModemVariant.SeaLink)
            {
                throw new ArgumentOutOfRangeException(nameof(variant));
            }

            if (integrity != LegacyModemIntegrity.Checksum && integrity != LegacyModemIntegrity.Crc16)
            {
                throw new ArgumentOutOfRangeException(nameof(integrity));
            }

            if (maxRetries < 0 || maxRetries > 1000000)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRetries));
            }

            if (windowSize < 1 || windowSize > 127)
            {
                throw new ArgumentOutOfRangeException(nameof(windowSize));
            }

            if (maxBufferedBytes < 133)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBufferedBytes));
            }

            Variant = variant;
            Integrity = integrity;
            MaxRetries = maxRetries;
            WindowSize = windowSize;
            Overdrive = overdrive;
            EnableResync = enableResync;
            AllowFallback = allowFallback;
            MaxBufferedBytes = maxBufferedBytes;
        }

        public LegacyModemVariant Variant { get; }

        public LegacyModemIntegrity Integrity { get; }

        public int MaxRetries { get; }

        public int WindowSize { get; }

        public bool Overdrive { get; }

        public bool EnableResync { get; }

        public bool AllowFallback { get; }

        public int MaxBufferedBytes { get; }
    }

    public static class LegacyModemControlBytes
    {
        public const byte Soh = 0x01;
        public const byte Etx = 0x03;
        public const byte Eot = 0x04;
        public const byte Ack = 0x06;
        public const byte Nak = 0x15;
        public const byte Syn = 0x16;
        public const byte Can = 0x18;
        public const byte Sub = 0x1a;
        public const byte CrcRequest = 0x43;
        public const byte NameRetry = 0x75;
    }
}
