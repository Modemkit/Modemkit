using System.Collections.Generic;

namespace Xmodem.Protocol
{
    public enum XModemPhase
    {
        Idle,
        WaitingReceiverRequest,
        WaitingDataBlock,
        WaitingBlockAck,
        WaitingEotAck,
        WaitingPacket,
        WaitingDataBlockDecision,
        Completed,
        Cancelled,
        Faulted,
        WaitingWindowAck
    }

    public sealed class XModemSnapshot
    {
        internal XModemSnapshot(
            XModemPhase phase,
            int nextBlockNumber,
            long bytesTransferred,
            int retryCount,
            XModemIntegrity integrity,
            string? failureReason)
        {
            Phase = phase;
            NextBlockNumber = nextBlockNumber;
            BytesTransferred = bytesTransferred;
            RetryCount = retryCount;
            Integrity = integrity;
            FailureReason = failureReason;
        }

        public XModemPhase Phase { get; }

        public int NextBlockNumber { get; }

        public long BytesTransferred { get; }

        public int RetryCount { get; }

        public XModemIntegrity Integrity { get; }

        public string? FailureReason { get; }
    }

    public sealed class XModemStepResult
    {
        internal XModemStepResult(XModemSnapshot snapshot, IReadOnlyList<XModemAction> actions)
        {
            Snapshot = snapshot;
            Actions = actions;
        }

        public XModemSnapshot Snapshot { get; }

        public IReadOnlyList<XModemAction> Actions { get; }
    }
}
