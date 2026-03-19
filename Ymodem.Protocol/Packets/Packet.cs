using System;

namespace Ymodem.Protocol
{
    public abstract class YModemPacket
    {
        private YModemPacket()
        {
        }

        public sealed class Header : YModemPacket
        {
            public Header(YModemFileDescriptor file)
            {
                File = file ?? throw new ArgumentNullException(nameof(file));
            }

            public YModemFileDescriptor File
            {
                get;
            }
        }

        public sealed class Data : YModemPacket
        {
            public Data(int blockNumber, byte[] payload, int dataLength)
            {
                if (payload == null)
                {
                    throw new ArgumentNullException(nameof(payload));
                }

                if (blockNumber < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(blockNumber), "Data block number must be non-negative.");
                }

                if (dataLength < 0 || dataLength > payload.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(dataLength), "Data length must be within the provided payload buffer.");
                }

                BlockNumber = blockNumber;
                Payload = payload;
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

        public sealed class Eot : YModemPacket
        {
        }

        public sealed class BatchTrailer : YModemPacket
        {
        }
    }
}
