using System;
using System.Collections.Generic;

namespace Zmodem.Protocol
{
    public static class ZModemCodec
    {
        public const byte Zdle = 0x18;

        public static ushort ComputeCrc16(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            ushort crc = 0;
            foreach (byte value in bytes)
            {
                crc ^= (ushort)(value << 8);
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0));
                }
            }

            return crc;
        }

        public static uint ComputeCrc32(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            uint crc = uint.MaxValue;
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
                }
            }

            return ~crc;
        }

        public static byte[] EncodeHeader(ZModemHeader header)
        {
            if (header == null)
            {
                throw new ArgumentNullException(nameof(header));
            }

            var data = new List<byte> { (byte)header.Type };
            data.AddRange(header.Data);
            AddCrc(data, ZModemHeader.IsCrc32Format(header.Format));
            var wire = new List<byte>();
            byte indicator = HeaderIndicator(header.Format);
            bool variable = ZModemHeader.IsVariableFormat(header.Format);
            if (header.Format == ZModemHeaderFormat.Hex || header.Format == ZModemHeaderFormat.VariableHex)
            {
                wire.AddRange(new byte[] { 0x2a, 0x2a, Zdle, indicator });
                const string digits = "0123456789abcdef";
                if (variable)
                {
                    wire.Add((byte)digits[header.Data.Length >> 4]);
                    wire.Add((byte)digits[header.Data.Length & 15]);
                }

                foreach (byte value in data)
                {
                    wire.Add((byte)digits[value >> 4]);
                    wire.Add((byte)digits[value & 15]);
                }

                wire.Add(13);
                wire.Add(10);
                if (header.Type != ZModemHeaderType.Zack && header.Type != ZModemHeaderType.Zfin)
                {
                    wire.Add(0x11);
                }
            }
            else
            {
                wire.AddRange(new byte[] { 0x2a, Zdle, indicator });
                if (variable)
                {
                    Escape(wire, (byte)header.Data.Length);
                }

                foreach (byte value in data)
                {
                    Escape(wire, value);
                }
            }

            return wire.ToArray();
        }

        public static byte[] EncodeData(byte[] payload, ZModemDataEnd end, bool crc32 = true)
        {
            return EncodeData(payload, end, crc32 ? ZModemHeaderFormat.Binary32 : ZModemHeaderFormat.Binary16);
        }

        public static byte[] EncodeData(byte[] payload, ZModemDataEnd end, ZModemHeaderFormat format, ZModemProfile profile = ZModemProfile.Standard)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            int maximum = MaximumSubpacketSize(profile);
            if (payload.Length > maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(payload));
            }

            if (end < ZModemDataEnd.End || end > ZModemDataEnd.EndAndAck)
            {
                throw new ArgumentOutOfRangeException(nameof(end));
            }

            HeaderIndicator(format);
            bool crc32 = ZModemHeader.IsCrc32Format(format);
            byte[] encoded = ZModemHeader.IsRleFormat(format) ? EncodeRunLength(payload) : payload;
            var data = new List<byte>(encoded) { (byte)end };
            AddCrc(data, crc32);
            var wire = new List<byte>();
            foreach (byte value in encoded)
            {
                Escape(wire, value);
            }

            wire.Add(Zdle);
            wire.Add((byte)end);
            for (int i = encoded.Length + 1; i < data.Count; i++)
            {
                Escape(wire, data[i]);
            }

            if (end == ZModemDataEnd.EndAndAck)
            {
                wire.Add(0x11);
            }

            return wire.ToArray();
        }

        public static byte[] EncodeCancellation()
        {
            return new byte[] { Zdle, Zdle, Zdle, Zdle, Zdle, Zdle, Zdle, Zdle, 8, 8, 8, 8, 8, 8, 8, 8 };
        }

        internal static int MaximumSubpacketSize(ZModemProfile profile)
        {
            switch (profile)
            {
                case ZModemProfile.Standard:
                case ZModemProfile.ZedZip:
                    return 1024;
                case ZModemProfile.Extended4K:
                    return 4096;
                case ZModemProfile.Extended8K:
                case ZModemProfile.ZedZap:
                    return 8192;
                default:
                    throw new ArgumentOutOfRangeException(nameof(profile));
            }
        }

        internal static byte HeaderIndicator(ZModemHeaderFormat format)
        {
            switch (format)
            {
                case ZModemHeaderFormat.Binary16:
                    return 0x41;
                case ZModemHeaderFormat.Hex:
                    return 0x42;
                case ZModemHeaderFormat.Binary32:
                    return 0x43;
                case ZModemHeaderFormat.BinaryRle32:
                    return 0x44;
                case ZModemHeaderFormat.VariableBinary16:
                    return 0x61;
                case ZModemHeaderFormat.VariableHex:
                    return 0x62;
                case ZModemHeaderFormat.VariableBinary32:
                    return 0x63;
                case ZModemHeaderFormat.VariableBinaryRle32:
                    return 0x64;
                default:
                    throw new ArgumentOutOfRangeException(nameof(format));
            }
        }

        internal static byte[] DecodeRunLength(byte[] encoded, int maximum)
        {
            var decoded = new List<byte>();
            for (int index = 0; index < encoded.Length; index++)
            {
                byte value = encoded[index];
                int count = 1;
                if (value == 0x7e)
                {
                    if (++index >= encoded.Length)
                    {
                        throw new FormatException("Truncated RLE escape.");
                    }

                    byte code = encoded[index];
                    if (code >= 0x20 && code < 0x40)
                    {
                        count = code - 0x1d;
                        value = 0x20;
                    }
                    else if (code == 0x40)
                    {
                        value = 0x7e;
                    }
                    else
                    {
                        if (code < 0x41 || ++index >= encoded.Length)
                        {
                            throw new FormatException("Invalid RLE run.");
                        }

                        count = code - 0x40;
                        value = encoded[index];
                    }
                }

                if (decoded.Count + count > maximum)
                {
                    throw new FormatException("Expanded RLE subpacket exceeds the configured limit.");
                }

                for (int repeat = 0; repeat < count; repeat++)
                {
                    decoded.Add(value);
                }
            }

            return decoded.ToArray();
        }

        internal static bool CheckCrc(byte[] data, byte[] expected, bool crc32)
        {
            uint crc = crc32 ? ComputeCrc32(data) : ComputeCrc16(data);
            for (int i = 0; i < expected.Length; i++)
            {
                int shift = crc32 ? i * 8 : (1 - i) * 8;
                if (expected[i] != (byte)(crc >> shift))
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] EncodeRunLength(byte[] payload)
        {
            var encoded = new List<byte>();
            for (int index = 0; index < payload.Length;)
            {
                byte value = payload[index];
                int count = 1;
                while (index + count < payload.Length && payload[index + count] == value && count < 127)
                {
                    count++;
                }

                index += count;
                if (value == 0x20 && count >= 3 && count <= 34)
                {
                    encoded.Add(0x7e);
                    encoded.Add((byte)(count + 0x1d));
                }
                else if (count >= 3 || (value == 0x7e && count > 1))
                {
                    encoded.Add(0x7e);
                    encoded.Add((byte)(count + 0x40));
                    encoded.Add(value);
                }
                else
                {
                    for (int repeat = 0; repeat < count; repeat++)
                    {
                        encoded.Add(value);
                        if (value == 0x7e)
                        {
                            encoded.Add(0x40);
                        }
                    }
                }
            }

            return encoded.ToArray();
        }

        private static void AddCrc(List<byte> data, bool crc32)
        {
            uint crc = crc32 ? ComputeCrc32(data.ToArray()) : ComputeCrc16(data.ToArray());
            if (crc32)
            {
                for (int i = 0; i < 4; i++)
                {
                    data.Add((byte)(crc >> (i * 8)));
                }
            }
            else
            {
                data.Add((byte)(crc >> 8));
                data.Add((byte)crc);
            }
        }

        private static void Escape(List<byte> wire, byte value)
        {
            // Escaping every control byte also protects CR after '@' and software flow control.
            if ((value & 0x60) == 0)
            {
                wire.Add(Zdle);
                wire.Add((byte)(value ^ 0x40));
            }
            else if (value == 0x7f || value == 0xff)
            {
                wire.Add(Zdle);
                wire.Add(value == 0x7f ? (byte)0x6c : (byte)0x6d);
            }
            else
            {
                wire.Add(value);
            }
        }
    }
}
