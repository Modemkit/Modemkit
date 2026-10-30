using System;
using System.Globalization;
using System.Text;

namespace Ymodem.Protocol
{
    public sealed class YModemPacketDecoder
    {
        /// <summary>
        /// Decodes a raw packet buffer received during the header phase.
        /// A block-zero frame is interpreted as a <see cref="YModemPacket.Header"/> or
        /// <see cref="YModemPacket.BatchTrailer"/>.
        /// </summary>
        public YModemPacket Decode(byte[] bytes)
        {
            return Decode(bytes, false);
        }

        /// <summary>
        /// Decodes a raw packet buffer.
        /// </summary>
        /// <param name="bytes">The raw bytes received from the transport layer.</param>
        /// <param name="isDataPhase">
        /// <c>true</c> when the receiver is in the data-transfer phase (i.e. at least one
        /// data block has already been accepted for the current file). When <c>true</c>, a
        /// block-zero frame is decoded as <see cref="YModemPacket.Data"/> rather than
        /// <see cref="YModemPacket.Header"/>, supporting the 255→0 block-number rollover
        /// that occurs in files larger than 255 × block-size bytes.
        /// Pass <c>false</c> (or use the single-parameter overload) during the header phase.
        /// </param>
        public YModemPacket Decode(byte[] bytes, bool isDataPhase)
        {
            return Decode(bytes, YModemIntegrity.Crc16, isDataPhase);
        }

        public YModemPacket Decode(byte[] bytes, YModemIntegrity integrity, bool isDataPhase = false)
        {
            YModemIntegrityChecks.Validate(integrity);
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

            var blockSize = GetBlockSize(bytes[0], bytes.Length, integrity);
            var blockNumber = bytes[1];
            var blockNumberComplement = bytes[2];
            if (blockNumberComplement != unchecked((byte)(255 - blockNumber)))
            {
                throw new InvalidOperationException("Packet block number complement is invalid.");
            }

            var payload = new byte[blockSize];
            Buffer.BlockCopy(bytes, 3, payload, 0, blockSize);

            if (integrity == YModemIntegrity.Checksum)
            {
                if (bytes[blockSize + 3] != YModemIntegrityChecks.Checksum(payload, 0, blockSize))
                {
                    throw new InvalidOperationException("Packet checksum is invalid.");
                }
            }
            else
            {
                var expectedCrc = (ushort)((bytes[blockSize + 3] << 8) | bytes[blockSize + 4]);
                var actualCrc = ComputeCrc16(payload, 0, blockSize);
                if (expectedCrc != actualCrc)
                {
                    throw new InvalidOperationException("Packet CRC is invalid.");
                }
            }

            if (blockNumber == 0 && !isDataPhase)
            {
                return DecodeBlockZero(payload);
            }

            return new YModemPacket.Data(blockNumber, payload, payload.Length, blockSize);
        }

        private static int GetBlockSize(byte startByte, int totalLength, YModemIntegrity integrity)
        {
            var overhead = integrity == YModemIntegrity.Crc16 ? 5 : 4;
            if (startByte == YModemControlBytes.Soh && totalLength == 128 + overhead)
            {
                return 128;
            }

            if (startByte == YModemControlBytes.Stx && totalLength == 1024 + overhead)
            {
                return 1024;
            }

            throw new InvalidOperationException("Unsupported packet framing.");
        }

        private static YModemPacket DecodeBlockZero(byte[] payload)
        {
            if (payload[0] == 0)
            {
                return new YModemPacket.BatchTrailer();
            }

            var nameEnd = Array.IndexOf(payload, (byte)0);
            if (nameEnd < 0)
            {
                throw new InvalidOperationException("Header packet does not contain a file name terminator.");
            }

            var fileName = Encoding.ASCII.GetString(payload, 0, nameEnd);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new InvalidOperationException("Header packet contains an empty file name.");
            }

            for (var index = 0; index < nameEnd; index++)
            {
                if (payload[index] >= 127 || payload[index] < 32)
                {
                    throw new InvalidOperationException("Header packet contains an invalid file name.");
                }
            }

            var sizeStart = nameEnd + 1;
            var sizeEnd = sizeStart;
            while (sizeEnd < payload.Length && payload[sizeEnd] != 0 && payload[sizeEnd] != 0x20)
            {
                sizeEnd++;
            }

            var sizeText = Encoding.ASCII.GetString(payload, sizeStart, sizeEnd - sizeStart);
            if (sizeText.Length == 0)
            {
                return new YModemPacket.Header(new YModemFileDescriptor(fileName));
            }

            if (!long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var fileSize))
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
