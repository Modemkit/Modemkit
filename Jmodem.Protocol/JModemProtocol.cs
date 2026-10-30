using System;
using System.Collections.Generic;

namespace Jmodem.Protocol
{
    public enum JModemBlockSizingPolicy
    {
        Documented,
        SourceCompatible
    }

    public enum JModemPhase
    {
        Idle,
        WaitingHandshake,
        WaitingDataBlock,
        WaitingBlockAck,
        WaitingPacket,
        WaitingDataBlockDecision,
        DrainingInput,
        Completed,
        Cancelled,
        Faulted
    }

    public static class JModemControlBytes
    {
        public const byte Ack = 0x06;
        public const byte Nak = 0x15;
        public const byte Can = 0x18;
        public const byte RunLengthEscape = 0xBB;
        public const byte Normal = 0x01;
        public const byte Compressed = 0x02;
        public const byte EndOfFile = 0x04;
    }

    public sealed class JModemOptions
    {
        public JModemOptions(
            int maxRetries = 10,
            bool enableCompression = true,
            int initialBlockSize = 512,
            JModemBlockSizingPolicy blockSizingPolicy = JModemBlockSizingPolicy.Documented,
            int maxInputBytes = 65536)
        {
            if (maxRetries < 0 || maxRetries > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRetries));
            }

            if (initialBlockSize < 64 || initialBlockSize > 8192)
            {
                throw new ArgumentOutOfRangeException(nameof(initialBlockSize));
            }

            if (blockSizingPolicy != JModemBlockSizingPolicy.Documented && blockSizingPolicy != JModemBlockSizingPolicy.SourceCompatible)
            {
                throw new ArgumentOutOfRangeException(nameof(blockSizingPolicy));
            }

            if (maxInputBytes < 8198 || maxInputBytes > 1048576)
            {
                throw new ArgumentOutOfRangeException(nameof(maxInputBytes));
            }

            MaxRetries = maxRetries;
            EnableCompression = enableCompression;
            InitialBlockSize = initialBlockSize;
            BlockSizingPolicy = blockSizingPolicy;
            MaxInputBytes = maxInputBytes;
        }

        public int MaxRetries { get; }

        public bool EnableCompression { get; }

        public int InitialBlockSize { get; }

        public JModemBlockSizingPolicy BlockSizingPolicy { get; }

        public int MaxInputBytes { get; }
    }

    public sealed class JModemSnapshot
    {
        internal JModemSnapshot(
            JModemPhase phase,
            int nextBlockNumber,
            long bytesTransferred,
            int blockSize,
            int retryCount,
            string? failureReason)
        {
            Phase = phase;
            NextBlockNumber = nextBlockNumber;
            BytesTransferred = bytesTransferred;
            BlockSize = blockSize;
            RetryCount = retryCount;
            FailureReason = failureReason;
        }

        public JModemPhase Phase { get; }

        public int NextBlockNumber { get; }

        public long BytesTransferred { get; }

        public int BlockSize { get; }

        public int RetryCount { get; }

        public string? FailureReason { get; }
    }

    public sealed class JModemStepResult
    {
        internal JModemStepResult(JModemSnapshot snapshot, IReadOnlyList<JModemAction> actions)
        {
            Snapshot = snapshot;
            Actions = actions;
        }

        public JModemSnapshot Snapshot { get; }

        public IReadOnlyList<JModemAction> Actions { get; }
    }
}
