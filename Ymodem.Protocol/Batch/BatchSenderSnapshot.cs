namespace Ymodem.Protocol
{
    public sealed class YModemBatchSenderSnapshot
    {
        public YModemBatchSenderSnapshot(
            YModemBatchSenderPhase phase,
            int nextBlockNumber,
            string? failureReason)
        {
            Phase = phase;
            NextBlockNumber = nextBlockNumber;
            FailureReason = failureReason;
        }

        public YModemBatchSenderPhase Phase { get; }

        public int NextBlockNumber { get; }

        public string? FailureReason { get; }
    }
}
