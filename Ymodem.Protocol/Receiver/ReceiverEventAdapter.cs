using System;

namespace Ymodem.Protocol
{
    public sealed class YModemReceiverEventAdapter
    {
        private readonly YModemPacketDecoder _packetDecoder;

        public YModemReceiverEventAdapter()
            : this(new YModemPacketDecoder())
        {
        }

        public YModemReceiverEventAdapter(YModemPacketDecoder packetDecoder)
        {
            _packetDecoder = packetDecoder ?? throw new ArgumentNullException(nameof(packetDecoder));
        }

        /// <summary>
        /// Decodes a raw byte buffer received during the header phase.
        /// A block-zero frame is interpreted as a header or batch-trailer packet.
        /// </summary>
        public YModemEvent Decode(byte[] bytes)
        {
            return Decode(bytes, false);
        }

        /// <summary>
        /// Decodes a raw byte buffer.
        /// </summary>
        /// <param name="bytes">The raw bytes received from the transport layer.</param>
        /// <param name="isDataPhase">
        /// <c>true</c> when the receiver is in the data-transfer phase (i.e. at least one
        /// data block has already been accepted for the current file). When <c>true</c>, a
        /// block-zero frame is decoded as <see cref="YModemPacket.Data"/> rather than
        /// <see cref="YModemPacket.Header"/>, supporting the 255→0 block-number rollover.
        /// Pass <c>false</c> (or use the single-parameter overload) during the header phase.
        /// </param>
        public YModemEvent Decode(byte[] bytes, bool isDataPhase)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length == 1 && bytes[0] == YModemControlBytes.Can)
            {
                return new YModemEvent.CancelRequested("Peer cancelled the transfer.");
            }

            var packet = _packetDecoder.Decode(bytes, isDataPhase);
            return new YModemEvent.PacketReceived(packet);
        }
    }
}
