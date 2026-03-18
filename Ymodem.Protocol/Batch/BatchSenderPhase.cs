namespace Ymodem.Protocol
{
    /// <summary>
    /// Represents the sender-side phases of a YMODEM batch transfer.
    /// </summary>
    public enum YModemBatchSenderPhase
    {
        /// <summary>
        /// Waiting for the receiver to start the session.
        /// </summary>
        WaitingReceiverRequest = 0,

        /// <summary>
        /// Waiting for the outer layer to provide the next file header or finish the batch.
        /// </summary>
        WaitingFileHeader = 1,

        /// <summary>
        /// Waiting for the receiver to acknowledge the current file header.
        /// </summary>
        WaitingHeaderAck = 2,

        /// <summary>
        /// Waiting for the receiver to request the first data block of the current file.
        /// </summary>
        WaitingDataStartRequest = 3,

        /// <summary>
        /// Waiting for the outer layer to provide the next data block of the current file.
        /// </summary>
        WaitingDataBlock = 4,

        /// <summary>
        /// Waiting for the receiver to acknowledge the current data block.
        /// </summary>
        WaitingBlockAck = 5,

        /// <summary>
        /// Waiting for the receiver response to the first EOT.
        /// </summary>
        WaitingFirstEotResponse = 6,

        /// <summary>
        /// Waiting for the receiver response to the second EOT.
        /// </summary>
        WaitingSecondEotAck = 7,

        /// <summary>
        /// Waiting for the receiver CRC request before the next file header or final trailer.
        /// </summary>
        WaitingNextHeaderRequest = 8,

        /// <summary>
        /// Waiting for the receiver to acknowledge the final empty block 0 trailer.
        /// </summary>
        WaitingBatchTrailerAck = 9,

        /// <summary>
        /// Transfer completed successfully.
        /// </summary>
        Completed = 10,

        /// <summary>
        /// Transfer was cancelled locally or by the peer.
        /// </summary>
        Cancelled = 11,

        /// <summary>
        /// Transfer failed due to an invalid protocol event or state transition.
        /// </summary>
        Faulted = 12
    }
}
