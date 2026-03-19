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

        public YModemEvent Decode(byte[] bytes, bool isDataPhase = false)
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
