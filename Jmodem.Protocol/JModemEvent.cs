using System;

namespace Jmodem.Protocol
{
    public abstract class JModemEvent
    {
        private JModemEvent()
        {
        }

        public sealed class StartRequested : JModemEvent
        {
        }

        public sealed class TimeoutElapsed : JModemEvent
        {
        }

        public sealed class DataBlockAccepted : JModemEvent
        {
        }

        public sealed class DataBlockRejected : JModemEvent
        {
        }

        public sealed class PeerBytesReceived : JModemEvent
        {
            public PeerBytesReceived(byte[] bytes)
            {
                Bytes = bytes == null ? throw new ArgumentNullException(nameof(bytes)) : (byte[])bytes.Clone();
            }

            public byte[] Bytes { get; }
        }

        public sealed class PeerByteReceived : JModemEvent
        {
            public PeerByteReceived(byte value)
            {
                Value = value;
            }

            public byte Value { get; }
        }

        public sealed class DataBlockReady : JModemEvent
        {
            private readonly byte[] _data;

            public DataBlockReady(int blockNumber, byte[] data, int dataLength, bool isLastBlock)
            {
                if (blockNumber < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(blockNumber));
                }

                if (data == null)
                {
                    throw new ArgumentNullException(nameof(data));
                }

                if (dataLength < 0 || dataLength > data.Length || dataLength > 8192)
                {
                    throw new ArgumentOutOfRangeException(nameof(dataLength));
                }

                BlockNumber = blockNumber;
                IsLastBlock = isLastBlock;
                _data = new byte[dataLength];
                Buffer.BlockCopy(data, 0, _data, 0, dataLength);
            }

            public int BlockNumber { get; }

            public byte[] Data => (byte[])_data.Clone();

            public int DataLength => _data.Length;

            public bool IsLastBlock { get; }
        }

        public sealed class PacketReceived : JModemEvent
        {
            public PacketReceived(JModemPacket packet)
            {
                Packet = packet ?? throw new ArgumentNullException(nameof(packet));
            }

            public JModemPacket Packet { get; }
        }

        public sealed class InvalidFrame : JModemEvent
        {
            public InvalidFrame(string? reason = null)
            {
                Reason = reason ?? "Invalid JMODEM frame.";
            }

            public string Reason { get; }
        }

        public sealed class CancelRequested : JModemEvent
        {
            public CancelRequested(string? reason = null)
            {
                Reason = reason ?? "Transfer cancelled.";
            }

            public string Reason { get; }
        }
    }
}
