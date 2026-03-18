namespace Ymodem.Protocol
{
    public sealed class YModemBatchReceiverSnapshot
    {
        public YModemBatchReceiverSnapshot(
            YModemBatchReceiverPhase phase,
            int nextBlockNumber,
            long remainingFileBytes,
            string? failureReason)
        {
            Phase = phase;
            NextBlockNumber = nextBlockNumber;
            RemainingFileBytes = remainingFileBytes;
            FailureReason = failureReason;
        }

        public YModemBatchReceiverPhase Phase { get; }

        public int NextBlockNumber { get; }

        public long RemainingFileBytes { get; }

        public string? FailureReason { get; }
    }
}
