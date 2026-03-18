using System;

namespace Ymodem.Protocol
{
    public abstract class YModemEvent
    {
        private YModemEvent()
        {
        }

        public sealed class StartRequested : YModemEvent
        {
        }

        public sealed class PeerByteReceived : YModemEvent
        {
            public PeerByteReceived(byte value)
            {
                Value = value;
            }

            public byte Value
            {
                get;
            }
        }

        public sealed class FileHeaderReady : YModemEvent
        {
            public FileHeaderReady(YModemFileDescriptor file)
            {
                File = file ?? throw new ArgumentNullException(nameof(file));
            }

            public YModemFileDescriptor File
            {
                get;
            }
        }

        public sealed class DataBlockReady : YModemEvent
        {
            public DataBlockReady(int blockNumber, byte[] data, int dataLength, bool isLastBlock)
            {
                if (data == null)
                {
                    throw new ArgumentNullException(nameof(data));
                }

                if (blockNumber <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(blockNumber), "Data block number must be positive.");
                }

                if (dataLength < 0 || dataLength > data.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(dataLength), "Data length must be within the provided data buffer.");
                }

                BlockNumber = blockNumber;
                Data = data;
                DataLength = dataLength;
                IsLastBlock = isLastBlock;
            }

            public int BlockNumber
            {
                get;
            }

            public byte[] Data
            {
                get;
            }

            public int DataLength
            {
                get;
            }

            public bool IsLastBlock
            {
                get;
            }
        }

        public sealed class CancelRequested : YModemEvent
        {
            public CancelRequested(string? reason)
            {
                Reason = reason ?? string.Empty;
            }

            public string Reason
            {
                get;
            }
        }

        public sealed class PacketReceived : YModemEvent
        {
            public PacketReceived(YModemPacket packet)
            {
                Packet = packet ?? throw new ArgumentNullException(nameof(packet));
            }

            public YModemPacket Packet
            {
                get;
            }
        }

        public sealed class FileHeaderAccepted : YModemEvent
        {
        }

        public sealed class FileHeaderRejected : YModemEvent
        {
            public FileHeaderRejected(string? reason)
            {
                Reason = reason ?? string.Empty;
            }

            public string Reason
            {
                get;
            }
        }

        public sealed class DataBlockAccepted : YModemEvent
        {
        }

        public sealed class DataBlockRejected : YModemEvent
        {
        }

        public sealed class NoMoreFiles : YModemEvent
        {
        }
    }
}
