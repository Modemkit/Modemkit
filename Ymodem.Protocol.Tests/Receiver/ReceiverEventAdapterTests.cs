using Xunit;

namespace Ymodem.Protocol.Tests
{
    public sealed class ReceiverEventAdapterTests
    {
        [Fact]
        public void Decode_HeaderFrame_ReturnsPacketReceivedEvent()
        {
            var adapter = new YModemReceiverEventAdapter();
            var encoder = new YModemPacketEncoder();

            var bytes = encoder.Encode(new YModemPacket.Header(new YModemFileDescriptor("demo.bin", 5)));
            var protocolEvent = adapter.Decode(bytes);

            var packetReceived = Assert.IsType<YModemEvent.PacketReceived>(protocolEvent);
            var header = Assert.IsType<YModemPacket.Header>(packetReceived.Packet);
            Assert.Equal("demo.bin", header.File.FileName);
            Assert.Equal(5, header.File.FileSize);
        }

        [Fact]
        public void Decode_CanByte_ReturnsCancelRequestedEvent()
        {
            var adapter = new YModemReceiverEventAdapter();

            var protocolEvent = adapter.Decode(new byte[] { YModemControlBytes.Can });

            var cancel = Assert.IsType<YModemEvent.CancelRequested>(protocolEvent);
            Assert.Equal("Peer cancelled the transfer.", cancel.Reason);
        }

        [Fact]
        public void Decode_EotByte_ReturnsPacketReceivedEventWithEotPacket()
        {
            var adapter = new YModemReceiverEventAdapter();

            var protocolEvent = adapter.Decode(new byte[] { YModemControlBytes.Eot });

            var packetReceived = Assert.IsType<YModemEvent.PacketReceived>(protocolEvent);
            Assert.IsType<YModemPacket.Eot>(packetReceived.Packet);
        }
    }
}
