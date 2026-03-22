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
                : this(file, 0)
            {
            }

            public Header(YModemFileDescriptor file, int blockSize)
            {
                File = file ?? throw new ArgumentNullException(nameof(file));

                if (blockSize != 0 && blockSize != 128 && blockSize != 1024)
                {
                    throw new ArgumentOutOfRangeException(nameof(blockSize), "Header block size must be 128 or 1024 bytes when specified.");
                }

                BlockSize = blockSize;
            }

            public YModemFileDescriptor File
            {
                get;
            }

            public int BlockSize
            {
                get;
            }
        }

        public sealed class Data : YModemPacket
        {
            public Data(int blockNumber, byte[] payload, int dataLength)
                : this(blockNumber, payload, dataLength, 0)
            {
            }

            public Data(int blockNumber, byte[] payload, int dataLength, int blockSize)
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

                if (blockSize != 0 && blockSize != 128 && blockSize != 1024)
                {
                    throw new ArgumentOutOfRangeException(nameof(blockSize), "Data block size must be 128 or 1024 bytes when specified.");
                }

                if (blockSize != 0 && dataLength > blockSize)
                {
                    throw new ArgumentOutOfRangeException(nameof(dataLength), "Data length must not exceed the block size.");
                }

                BlockNumber = blockNumber;
                Payload = payload;
                DataLength = dataLength;
                BlockSize = blockSize;
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

            public int BlockSize
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
