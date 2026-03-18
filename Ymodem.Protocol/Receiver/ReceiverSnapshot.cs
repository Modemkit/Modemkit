namespace Ymodem.Protocol
{
    public sealed class YModemReceiverSnapshot
    {
        public YModemReceiverSnapshot(
            YModemReceiverPhase phase,
            int nextBlockNumber,
            long remainingFileBytes,
            string? failureReason)
        {
            Phase = phase;
            NextBlockNumber = nextBlockNumber;
            RemainingFileBytes = remainingFileBytes;
            FailureReason = failureReason;
        }

        public YModemReceiverPhase Phase
        {
            get;
        }

        public int NextBlockNumber
        {
            get;
        }

        public long RemainingFileBytes
        {
            get;
        }

        public string? FailureReason
        {
            get;
        }
    }
}
