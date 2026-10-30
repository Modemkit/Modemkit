using System;

namespace Xmodem.Protocol
{
    public abstract class XModemEvent
    {
        private XModemEvent()
        {
        }

        public sealed class StartRequested : XModemEvent
        {
        }

        public sealed class TimeoutElapsed : XModemEvent
        {
        }

        public sealed class FlowControlTimeoutElapsed : XModemEvent
        {
        }

        public sealed class PauseRequested : XModemEvent
        {
        }

        public sealed class ResumeRequested : XModemEvent
        {
        }

        public sealed class PeerBytesReceived : XModemEvent
        {
            public PeerBytesReceived(byte[] bytes)
            {
                Bytes = bytes == null ? throw new ArgumentNullException(nameof(bytes)) : (byte[])bytes.Clone();
            }

            public byte[] Bytes { get; }
        }

        public sealed class PeerByteReceived : XModemEvent
        {
            public PeerByteReceived(byte value)
            {
                Value = value;
            }

            public byte Value { get; }
        }

        public sealed class PacketReceived : XModemEvent
        {
            public PacketReceived(XModemPacket packet)
            {
                Packet = packet ?? throw new ArgumentNullException(nameof(packet));
            }

            public XModemPacket Packet { get; }
        }

        public sealed class DataBlockReady : XModemEvent
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

                if (dataLength < 0 || dataLength > data.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(dataLength));
                }

                BlockNumber = blockNumber;
                DataLength = dataLength;
                IsLastBlock = isLastBlock;
                _data = new byte[dataLength];
                Buffer.BlockCopy(data, 0, _data, 0, dataLength);
            }

            public int BlockNumber { get; }

            public byte[] Data => (byte[])_data.Clone();

            public int DataLength { get; }

            public bool IsLastBlock { get; }
        }

        public sealed class DataBlockAccepted : XModemEvent
        {
        }

        public sealed class DataBlockRejected : XModemEvent
        {
        }

        public sealed class InvalidFrame : XModemEvent
        {
            public InvalidFrame(string? reason = null)
            {
                Reason = reason ?? "Invalid XMODEM frame.";
            }

            public string Reason { get; }
        }

        public sealed class CancelRequested : XModemEvent
        {
            public CancelRequested(string? reason = null)
            {
                Reason = reason ?? "Transfer cancelled.";
            }

            public string Reason { get; }
        }
    }
}
