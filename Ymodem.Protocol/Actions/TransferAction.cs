using System;

namespace Ymodem.Protocol
{
    public abstract class YModemAction
    {
        private YModemAction()
        {
        }

        public sealed class RequestFileHeader : YModemAction
        {
        }

        public sealed class RequestDataBlock : YModemAction
        {
            public RequestDataBlock(int blockNumber, int blockSize)
            {
                BlockNumber = blockNumber;
                BlockSize = blockSize;
            }

            public int BlockNumber
            {
                get;
            }

            public int BlockSize
            {
                get;
            }
        }

        public sealed class SendPacket : YModemAction
        {
            public SendPacket(YModemPacket packet, string? description)
            {
                Packet = packet ?? throw new ArgumentNullException(nameof(packet));
                Description = description ?? string.Empty;
            }

            public YModemPacket Packet
            {
                get;
            }

            public string Description
            {
                get;
            }
        }

        public sealed class SendControl : YModemAction
        {
            public SendControl(byte value, string? description)
            {
                Value = value;
                Description = description ?? string.Empty;
            }

            public byte Value
            {
                get;
            }

            public string Description
            {
                get;
            }
        }

        public sealed class OfferFileHeader : YModemAction
        {
            public OfferFileHeader(YModemFileDescriptor file)
            {
                File = file ?? throw new ArgumentNullException(nameof(file));
            }

            public YModemFileDescriptor File
            {
                get;
            }
        }

        public sealed class DeliverDataBlock : YModemAction
        {
            public DeliverDataBlock(int blockNumber, byte[] payload, int dataLength)
            {
                Payload = payload ?? throw new ArgumentNullException(nameof(payload));
                BlockNumber = blockNumber;
                DataLength = dataLength;
            }

            public int BlockNumber
            {
                get;
            }

            public byte[] Payload
            {
                get;
            }

            public int DataLength
            {
                get;
            }
        }

        public sealed class Complete : YModemAction
        {
        }

        public sealed class Cancel : YModemAction
        {
            public Cancel(string? reason)
            {
                Reason = reason ?? string.Empty;
            }

            public string Reason
            {
                get;
            }
        }

        public sealed class Fail : YModemAction
        {
            public Fail(string? reason)
            {
                Reason = reason ?? string.Empty;
            }

            public string Reason
            {
                get;
            }
        }
    }
}
