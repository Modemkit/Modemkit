using System;
using System.Text;

namespace Ymodem.Protocol
{
    public sealed class YModemPacketEncoder
    {
        private readonly int _dataBlockSize;

        public YModemPacketEncoder(int dataBlockSize = 1024)
        {
            if (dataBlockSize != 128 && dataBlockSize != 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(dataBlockSize), "YMODEM block size must be 128 or 1024 bytes.");
            }

            _dataBlockSize = dataBlockSize;
        }

        public byte[] Encode(YModemPacket packet)
        {
            if (packet == null)
            {
                throw new ArgumentNullException(nameof(packet));
            }

            switch (packet)
            {
                case YModemPacket.Header header:
                    return BuildHeaderFrame(header.File);
                case YModemPacket.Data data:
                    return BuildDataFrame(data.BlockNumber, data.Payload, data.DataLength, _dataBlockSize);
                case YModemPacket.Eot _:
                    return new[] { YModemControlBytes.Eot };
                case YModemPacket.BatchTrailer _:
                    return BuildEmptyHeaderFrame();
                default:
                    throw new InvalidOperationException("Unsupported packet type: " + packet.GetType().FullName + ".");
            }
        }

        private static byte[] BuildHeaderFrame(YModemFileDescriptor file)
        {
            var payload = new byte[128];
            var headerText = file.FileName + "\0" + file.FileSize + "\0";
            var headerBytes = Encoding.ASCII.GetBytes(headerText);

            if (headerBytes.Length > payload.Length)
            {
                throw new InvalidOperationException("File header metadata exceeds 128 bytes.");
            }

            Buffer.BlockCopy(headerBytes, 0, payload, 0, headerBytes.Length);
            return BuildFrame(0, payload, 128);
        }

        private static byte[] BuildEmptyHeaderFrame()
        {
            return BuildFrame(0, new byte[128], 128);
        }

        private static byte[] BuildDataFrame(int blockNumber, byte[] data, int dataLength, int blockSize)
        {
            var payload = new byte[blockSize];
            if (dataLength > 0)
            {
                Buffer.BlockCopy(data, 0, payload, 0, dataLength);
            }

            for (var index = dataLength; index < payload.Length; index++)
            {
                payload[index] = YModemControlBytes.CpmEof;
            }

            return BuildFrame(blockNumber, payload, blockSize);
        }

        private static byte[] BuildFrame(int blockNumber, byte[] payload, int blockSize)
        {
            var frame = new byte[blockSize + 5];
            frame[0] = blockSize == 128 ? YModemControlBytes.Soh : YModemControlBytes.Stx;
            frame[1] = unchecked((byte)blockNumber);
            frame[2] = unchecked((byte)(255 - frame[1]));

            Buffer.BlockCopy(payload, 0, frame, 3, blockSize);

            var crc = ComputeCrc16(payload, 0, blockSize);
            frame[blockSize + 3] = (byte)(crc >> 8);
            frame[blockSize + 4] = (byte)(crc & 0xFF);
            return frame;
        }

        private static ushort ComputeCrc16(byte[] buffer, int offset, int count)
        {
            ushort crc = 0;

            for (var i = 0; i < count; i++)
            {
                crc ^= (ushort)(buffer[offset + i] << 8);

                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x8000) != 0
                        ? (ushort)((crc << 1) ^ 0x1021)
                        : (ushort)(crc << 1);
                }
            }

            return crc;
        }
    }
}
