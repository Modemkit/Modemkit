using System;
using System.IO;

namespace Xmodem.Protocol
{
    public sealed class XModemPacketEncoder
    {
        public byte[] Encode(XModemPacket packet, XModemIntegrity integrity = XModemIntegrity.Crc16)
        {
            XModemChecks.ValidateIntegrity(integrity);
            if (packet == null)
            {
                throw new ArgumentNullException(nameof(packet));
            }

            if (packet is XModemPacket.Eot)
            {
                return new[] { XModemControlBytes.Eot };
            }

            var data = (XModemPacket.Data)packet;
            if (integrity == XModemIntegrity.Checksum && data.BlockSize != 128)
            {
                throw new ArgumentException("XMODEM checksum packets must contain 128 bytes.", nameof(packet));
            }

            var frame = new byte[3 + data.BlockSize + (integrity == XModemIntegrity.Crc16 ? 2 : 1)];
            frame[0] = data.BlockSize == 128 ? XModemControlBytes.Soh : XModemControlBytes.Stx;
            frame[1] = (byte)(data.BlockNumber & 0xFF);
            frame[2] = (byte)(0xFF - frame[1]);
            Buffer.BlockCopy(data.Payload, 0, frame, 3, data.DataLength);
            for (var i = data.DataLength; i < data.BlockSize; i++)
            {
                frame[3 + i] = XModemControlBytes.Padding;
            }

            if (integrity == XModemIntegrity.Crc16)
            {
                var crc = XModemChecks.Crc16(frame, 3, data.BlockSize);
                frame[frame.Length - 2] = (byte)(crc >> 8);
                frame[frame.Length - 1] = (byte)(crc & 0xFF);
            }
            else
            {
                frame[frame.Length - 1] = XModemChecks.Checksum(frame, 3, data.BlockSize);
            }

            return frame;
        }
    }

    public sealed class XModemPacketDecoder
    {
        public XModemPacket Decode(byte[] frame, XModemIntegrity integrity = XModemIntegrity.Crc16)
        {
            XModemChecks.ValidateIntegrity(integrity);
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (frame.Length == 1 && frame[0] == XModemControlBytes.Eot)
            {
                return new XModemPacket.Eot();
            }

            if (frame.Length < 3 || (frame[0] != XModemControlBytes.Soh && frame[0] != XModemControlBytes.Stx))
            {
                throw new InvalidDataException("Expected SOH, STX or EOT.");
            }

            var size = frame[0] == XModemControlBytes.Soh ? 128 : 1024;
            if (integrity == XModemIntegrity.Checksum && size != 128)
            {
                throw new InvalidDataException("XMODEM checksum does not support 1K packets.");
            }

            if (frame.Length != size + 3 + (integrity == XModemIntegrity.Crc16 ? 2 : 1))
            {
                throw new InvalidDataException("Incorrect XMODEM frame length.");
            }

            if ((frame[1] ^ frame[2]) != 0xFF)
            {
                throw new InvalidDataException("Invalid block number complement.");
            }

            if (integrity == XModemIntegrity.Crc16)
            {
                var actual = (frame[frame.Length - 2] << 8) | frame[frame.Length - 1];
                if (actual != XModemChecks.Crc16(frame, 3, size))
                {
                    throw new InvalidDataException("Invalid CRC16.");
                }
            }
            else if (frame[frame.Length - 1] != XModemChecks.Checksum(frame, 3, size))
            {
                throw new InvalidDataException("Invalid checksum.");
            }

            var payload = new byte[size];
            Buffer.BlockCopy(frame, 3, payload, 0, size);
            return new XModemPacket.Data(frame[1], payload, size, size);
        }
    }

    internal static class XModemChecks
    {
        internal static void ValidateIntegrity(XModemIntegrity integrity)
        {
            if (integrity != XModemIntegrity.Checksum && integrity != XModemIntegrity.Crc16)
            {
                throw new ArgumentOutOfRangeException(nameof(integrity));
            }
        }

        internal static ushort Crc16(byte[] bytes, int offset, int count)
        {
            var crc = 0;
            for (var i = offset; i < offset + count; i++)
            {
                crc ^= bytes[i] << 8;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
                    crc &= 0xFFFF;
                }
            }

            return (ushort)crc;
        }

        internal static byte Checksum(byte[] bytes, int offset, int count)
        {
            var sum = 0;
            for (var i = offset; i < offset + count; i++)
            {
                sum += bytes[i];
            }

            return (byte)(sum & 0xFF);
        }
    }
}
