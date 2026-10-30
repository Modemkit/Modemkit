using System;

namespace Xmodem.Protocol
{
    public abstract class XModemPacket
    {
        private XModemPacket()
        {
        }

        public sealed class Data : XModemPacket
        {
            private readonly byte[] _payload;

            public Data(int blockNumber, byte[] payload, int dataLength, int blockSize = 128)
            {
                if (blockNumber < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(blockNumber));
                }

                if (payload == null)
                {
                    throw new ArgumentNullException(nameof(payload));
                }

                if (blockSize != 128 && blockSize != 1024)
                {
                    throw new ArgumentOutOfRangeException(nameof(blockSize));
                }

                if (dataLength < 0 || dataLength > payload.Length || dataLength > blockSize)
                {
                    throw new ArgumentOutOfRangeException(nameof(dataLength));
                }

                BlockNumber = blockNumber;
                BlockSize = blockSize;
                DataLength = dataLength;
                _payload = new byte[dataLength];
                Buffer.BlockCopy(payload, 0, _payload, 0, dataLength);
            }

            public int BlockNumber { get; }

            public int BlockSize { get; }

            public int DataLength { get; }

            public byte[] Payload => (byte[])_payload.Clone();
        }

        public sealed class Eot : XModemPacket
        {
        }
    }
}
