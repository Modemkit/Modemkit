namespace Ymodem.Protocol
{
    /// <summary>
    /// Represents the receiver-side phases of a single-file YMODEM transfer.
    /// </summary>
    public enum YModemReceiverPhase
    {
        /// <summary>
        /// Waiting for the outer layer to start the receive session.
        /// </summary>
        Idle = 0,

        /// <summary>
        /// Waiting for the sender block 0 header packet.
        /// </summary>
        WaitingFileHeaderPacket = 1,

        /// <summary>
        /// Waiting for the outer layer to accept or reject the file header.
        /// </summary>
        WaitingFileHeaderDecision = 2,

        /// <summary>
        /// Waiting for data packets or the first EOT byte.
        /// </summary>
        WaitingDataPacketOrEot = 3,

        /// <summary>
        /// Waiting for the outer layer to accept or reject the delivered data block.
        /// </summary>
        WaitingDataBlockDecision = 4,

        /// <summary>
        /// Waiting for the second EOT byte after sending NAK to the first one.
        /// </summary>
        WaitingSecondEot = 5,

        /// <summary>
        /// Waiting for the terminating empty block 0 trailer.
        /// </summary>
        WaitingBatchTrailer = 6,

        /// <summary>
        /// Transfer completed successfully.
        /// </summary>
        Completed = 7,

        /// <summary>
        /// Transfer was cancelled locally or by protocol decision.
        /// </summary>
        Cancelled = 8,

        /// <summary>
        /// Transfer failed due to an invalid protocol event or state transition.
        /// </summary>
        Faulted = 9
    }
}
