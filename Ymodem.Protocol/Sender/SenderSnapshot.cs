namespace Ymodem.Protocol
{
    public sealed class YModemSenderSnapshot
    {
        public YModemSenderSnapshot(
            YModemSenderPhase phase,
            int nextBlockNumber,
            bool fileHeaderAccepted,
            bool lastDataBlockSent,
            string? failureReason)
        {
            Phase = phase;
            NextBlockNumber = nextBlockNumber;
            FileHeaderAccepted = fileHeaderAccepted;
            LastDataBlockSent = lastDataBlockSent;
            FailureReason = failureReason;
        }

        public YModemSenderPhase Phase
        {
            get;
        }

        public int NextBlockNumber
        {
            get;
        }

        public bool FileHeaderAccepted
        {
            get;
        }

        public bool LastDataBlockSent
        {
            get;
        }

        public string? FailureReason
        {
            get;
        }
    }
}
