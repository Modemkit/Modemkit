namespace Ymodem.Protocol
{
    /// <summary>
    /// Represents the receiver-side phases of a YMODEM batch transfer.
    /// </summary>
    public enum YModemBatchReceiverPhase
    {
        Idle = 0,
        WaitingFileHeaderPacket = 1,
        WaitingFileHeaderDecision = 2,
        WaitingDataPacketOrEot = 3,
        WaitingDataBlockDecision = 4,
        WaitingSecondEot = 5,
        Completed = 6,
        Cancelled = 7,
        Faulted = 8
    }
}
