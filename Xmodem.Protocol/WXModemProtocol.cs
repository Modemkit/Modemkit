using System;
using System.Collections.Generic;

namespace Xmodem.Protocol
{
    public enum WXModemMode
    {
        Windowed,
        Crc,
        Checksum
    }

    public static class WXModemControlBytes
    {
        public const byte Dle = 0x10;
        public const byte Xon = 0x11;
        public const byte Xoff = 0x13;
        public const byte Syn = 0x16;
        public const byte WindowRequest = 0x57;
    }

    public sealed class WXModemOptions
    {
        public WXModemOptions(
            int maxRetries = 10,
            bool allowFallback = true,
            int windowedAttempts = 3,
            int crcAttempts = 3,
            int checksumAttempts = 4)
        {
            if (maxRetries < 0 || maxRetries > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRetries));
            }

            if (windowedAttempts < 1 || windowedAttempts > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(windowedAttempts));
            }

            if (crcAttempts < 1 || crcAttempts > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(crcAttempts));
            }

            if (checksumAttempts < 1 || checksumAttempts > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(checksumAttempts));
            }

            MaxRetries = maxRetries;
            AllowFallback = allowFallback;
            WindowedAttempts = windowedAttempts;
            CrcAttempts = crcAttempts;
            ChecksumAttempts = checksumAttempts;
        }

        public int MaxRetries { get; }

        public bool AllowFallback { get; }

        public int WindowedAttempts { get; }

        public int CrcAttempts { get; }

        public int ChecksumAttempts { get; }
    }

    public sealed class WXModemSnapshot
    {
        internal WXModemSnapshot(
            XModemPhase phase,
            WXModemMode mode,
            int nextBlockNumber,
            long bytesTransferred,
            int outstandingBlocks,
            int retryCount,
            bool isPaused,
            string? failureReason)
        {
            Phase = phase;
            Mode = mode;
            NextBlockNumber = nextBlockNumber;
            BytesTransferred = bytesTransferred;
            OutstandingBlocks = outstandingBlocks;
            RetryCount = retryCount;
            IsPaused = isPaused;
            FailureReason = failureReason;
        }

        public XModemPhase Phase { get; }

        public WXModemMode Mode { get; }

        public int NextBlockNumber { get; }

        public long BytesTransferred { get; }

        public int OutstandingBlocks { get; }

        public int RetryCount { get; }

        public bool IsPaused { get; }

        public string? FailureReason { get; }
    }

    public sealed class WXModemStepResult
    {
        internal WXModemStepResult(WXModemSnapshot snapshot, IReadOnlyList<XModemAction> actions)
        {
            Snapshot = snapshot;
            Actions = actions;
        }

        public WXModemSnapshot Snapshot { get; }

        public IReadOnlyList<XModemAction> Actions { get; }
    }
}
