namespace Ymodem.Protocol
{
    /// <summary>
    /// Represents the sender-side phases of a single-file YMODEM transfer.
    /// </summary>
    public enum YModemSenderPhase
    {
        /// <summary>
        /// Waiting for the receiver to send the initial CRC request.
        /// </summary>
        WaitingReceiverRequest = 0,

        /// <summary>
        /// Waiting for the outer layer to provide the block 0 file header metadata.
        /// </summary>
        WaitingFileHeader = 1,

        /// <summary>
        /// Waiting for the receiver to acknowledge the file header frame.
        /// </summary>
        WaitingHeaderAck = 2,

        /// <summary>
        /// Waiting for the receiver to request the first data block.
        /// </summary>
        WaitingDataStartRequest = 3,

        /// <summary>
        /// Waiting for the outer layer to provide the next data block payload.
        /// </summary>
        WaitingDataBlock = 4,

        /// <summary>
        /// Waiting for the receiver to acknowledge the last transmitted data block.
        /// </summary>
        WaitingBlockAck = 5,

        /// <summary>
        /// Waiting for the receiver response after the first EOT byte.
        /// </summary>
        WaitingFirstEotResponse = 6,

        /// <summary>
        /// Waiting for the receiver response after the second EOT byte.
        /// </summary>
        WaitingSecondEotAck = 7,

        /// <summary>
        /// Waiting for the receiver to request the terminating empty block 0 trailer.
        /// </summary>
        WaitingBatchTrailerRequest = 8,

        /// <summary>
        /// Waiting for the receiver to acknowledge the terminating empty block 0 trailer.
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
