using System.Collections.Generic;

namespace LegacyModem.Protocol
{
    public enum LegacyModemPhase
    {
        Idle,
        WaitingReceiverRequest,
        WaitingFileHeader,
        WaitingNameStart,
        WaitingNameCharacter,
        WaitingNameAck,
        WaitingNameChecksum,
        WaitingNameChecksumAck,
        WaitingDataRequest,
        WaitingHeader,
        WaitingHeaderAck,
        WaitingFileDecision,
        WaitingData,
        WaitingDataBlock,
        WaitingDataDecision,
        WaitingAcknowledgement,
        WaitingResyncAck,
        WaitingEotAck,
        Completed,
        Cancelled,
        Faulted,
    }

    public sealed class LegacyModemSnapshot
    {
        internal LegacyModemSnapshot(
            LegacyModemPhase phase,
            LegacyModemVariant variant,
            int nextBlockNumber,
            long bytesTransferred,
            int retryCount,
            int outstandingBlocks,
            bool overdrive,
            string? failureReason)
        {
            Phase = phase;
            Variant = variant;
            NextBlockNumber = nextBlockNumber;
            BytesTransferred = bytesTransferred;
            RetryCount = retryCount;
            OutstandingBlocks = outstandingBlocks;
            Overdrive = overdrive;
            FailureReason = failureReason;
        }

        public LegacyModemPhase Phase { get; }

        public LegacyModemVariant Variant { get; }

        public int NextBlockNumber { get; }

        public long BytesTransferred { get; }

        public int RetryCount { get; }

        public int OutstandingBlocks { get; }

        public bool Overdrive { get; }

        public string? FailureReason { get; }
    }

    public sealed class LegacyModemStepResult
    {
        internal LegacyModemStepResult(LegacyModemSnapshot snapshot, IReadOnlyList<LegacyModemAction> actions)
        {
            Snapshot = snapshot;
            Actions = actions;
        }

        public LegacyModemSnapshot Snapshot { get; }

        public IReadOnlyList<LegacyModemAction> Actions { get; }
    }
}
