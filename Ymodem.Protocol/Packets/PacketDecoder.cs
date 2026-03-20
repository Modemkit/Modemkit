using System;
using System.Text;

namespace Ymodem.Protocol
{
    public sealed class YModemPacketDecoder
    {
        public YModemPacket Decode(byte[] bytes)
        {
            return Decode(bytes, false);
        }

        public YModemPacket Decode(byte[] bytes, bool isDataPhase)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length == 1 && bytes[0] == YModemControlBytes.Eot)
            {
                return new YModemPacket.Eot();
            }

            if (bytes.Length < 5)
            {
                throw new InvalidOperationException("Packet bytes are too short.");
            }

            var blockSize = GetBlockSize(bytes[0], bytes.Length);
            var blockNumber = bytes[1];
            var blockNumberComplement = bytes[2];
            if (blockNumberComplement != unchecked((byte)(255 - blockNumber)))
            {
                throw new InvalidOperationException("Packet block number complement is invalid.");
            }

            var payload = new byte[blockSize];
            Buffer.BlockCopy(bytes, 3, payload, 0, blockSize);

            var expectedCrc = (ushort)((bytes[blockSize + 3] << 8) | bytes[blockSize + 4]);
            var actualCrc = ComputeCrc16(payload, 0, blockSize);
            if (expectedCrc != actualCrc)
            {
                throw new InvalidOperationException("Packet CRC is invalid.");
            }

            if (blockNumber == 0 && !isDataPhase)
            {
                return DecodeBlockZero(payload);
            }

            return new YModemPacket.Data(blockNumber, payload, payload.Length);
        }

        private static int GetBlockSize(byte startByte, int totalLength)
        {
            if (startByte == YModemControlBytes.Soh && totalLength == 133)
            {
                return 128;
            }

            if (startByte == YModemControlBytes.Stx && totalLength == 1029)
            {
                return 1024;
            }

            throw new InvalidOperationException("Unsupported packet framing.");
        }

        private static YModemPacket DecodeBlockZero(byte[] payload)
        {
            var isBatchTrailer = true;
            for (var index = 0; index < payload.Length; index++)
            {
                if (payload[index] != 0)
                {
                    isBatchTrailer = false;
                    break;
                }
            }

            if (isBatchTrailer)
            {
                return new YModemPacket.BatchTrailer();
            }

            var nameEnd = Array.IndexOf(payload, (byte)0);
            if (nameEnd < 0)
            {
                throw new InvalidOperationException("Header packet does not contain a file name terminator.");
            }

            var fileName = Encoding.ASCII.GetString(payload, 0, nameEnd);
            var sizeStart = nameEnd + 1;
            var sizeEnd = sizeStart;
            while (sizeEnd < payload.Length && payload[sizeEnd] != 0 && payload[sizeEnd] != 0x20)
            {
                sizeEnd++;
            }

            var sizeText = Encoding.ASCII.GetString(payload, sizeStart, sizeEnd - sizeStart);
            if (!long.TryParse(sizeText, out var fileSize))
            {
                throw new InvalidOperationException("Header packet contains an invalid file size.");
            }

            return new YModemPacket.Header(new YModemFileDescriptor(fileName, fileSize));
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
