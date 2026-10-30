using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace LegacyModem.Protocol
{
    public sealed class LegacyModemHeader
    {
        internal LegacyModemHeader(LegacyModemVariant variant, LegacyModemFileDescriptor file, string programName, bool overdrive, bool supportsResync)
        {
            Variant = variant;
            File = file;
            ProgramName = programName;
            Overdrive = overdrive;
            SupportsResync = supportsResync;
        }

        public LegacyModemVariant Variant { get; }

        public LegacyModemFileDescriptor File { get; }

        public string ProgramName { get; }

        public bool Overdrive { get; }

        public bool SupportsResync { get; }
    }

    public sealed class LegacyModemDataBlock
    {
        private readonly byte[] _data;

        internal LegacyModemDataBlock(int blockNumber, byte[] data)
        {
            BlockNumber = blockNumber;
            _data = data;
        }

        public int BlockNumber { get; }

        public byte[] Data => (byte[])_data.Clone();
    }

    public static class LegacyModemCodec
    {
        public static byte[] EncodeHeader(
            LegacyModemVariant variant,
            LegacyModemFileDescriptor file,
            LegacyModemIntegrity integrity = LegacyModemIntegrity.Crc16,
            bool overdrive = false,
            bool supportsResync = true,
            string programName = "ModemKit")
        {
            ValidateIntegrity(integrity);
            if (variant != LegacyModemVariant.Telink && variant != LegacyModemVariant.SeaLink)
            {
                throw new ArgumentOutOfRangeException(nameof(variant));
            }

            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            if (!file.FileSize.HasValue)
            {
                throw new ArgumentException("A metadata header requires a file length.", nameof(file));
            }

            ValidateAscii(programName, variant == LegacyModemVariant.Telink ? 16 : 15);
            var payload = new byte[128];
            WriteUInt32(payload, 0, (uint)file.FileSize.Value);
            WriteUInt32(payload, 4, file.Timestamp);
            if (variant == LegacyModemVariant.Telink)
            {
                for (var index = 8; index < 24; index++)
                {
                    payload[index] = 0x20;
                }
            }

            Encoding.ASCII.GetBytes(file.FileName).CopyTo(payload, 8);
            Encoding.ASCII.GetBytes(programName).CopyTo(payload, 25);
            if (variant == LegacyModemVariant.Telink)
            {
                payload[41] = integrity == LegacyModemIntegrity.Crc16 ? (byte)1 : (byte)0;
                return EncodeFrame(LegacyModemControlBytes.Syn, 0, payload, LegacyModemIntegrity.Checksum);
            }

            payload[40] = overdrive ? (byte)1 : (byte)0;
            payload[41] = supportsResync ? (byte)1 : (byte)0;
            return EncodeFrame(LegacyModemControlBytes.Soh, 0, payload, integrity);
        }

        public static LegacyModemHeader DecodeHeader(byte[] frame, LegacyModemIntegrity integrity = LegacyModemIntegrity.Crc16)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            var telink = frame.Length > 0 && frame[0] == LegacyModemControlBytes.Syn;
            var payload = DecodeFrame(frame, telink ? LegacyModemIntegrity.Checksum : integrity, telink);
            if (frame[1] != 0)
            {
                throw new InvalidDataException("A metadata header must be block zero.");
            }

            if (telink && payload[24] != 0)
            {
                throw new InvalidDataException("Unsupported TeLink metadata version.");
            }

            var name = ReadAscii(payload, 8, telink ? 16 : 17).TrimEnd(' ');
            var program = ReadAscii(payload, 25, telink ? 16 : 15);
            try
            {
                return new LegacyModemHeader(
                    telink ? LegacyModemVariant.Telink : LegacyModemVariant.SeaLink,
                    new LegacyModemFileDescriptor(name, ReadUInt32(payload, 0), ReadUInt32(payload, 4)),
                    program,
                    !telink && payload[40] != 0,
                    !telink && payload[41] != 0);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Invalid metadata file name.", exception);
            }
        }

        public static byte[] EncodeData(int blockNumber, byte[] data, LegacyModemIntegrity integrity = LegacyModemIntegrity.Crc16)
        {
            if (blockNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(blockNumber));
            }

            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (data.Length > 128)
            {
                throw new ArgumentException("Data must fit in a 128-byte block.", nameof(data));
            }

            var payload = new byte[128];
            for (var index = 0; index < payload.Length; index++)
            {
                payload[index] = index < data.Length ? data[index] : LegacyModemControlBytes.Sub;
            }

            return EncodeFrame(LegacyModemControlBytes.Soh, blockNumber, payload, integrity);
        }

        public static LegacyModemDataBlock DecodeData(byte[] frame, LegacyModemIntegrity integrity = LegacyModemIntegrity.Crc16)
        {
            var data = DecodeFrame(frame, integrity, false);
            return new LegacyModemDataBlock(frame[1], data);
        }

        public static byte[] EncodeNumberedResponse(byte control, int blockNumber)
        {
            if (control != LegacyModemControlBytes.Ack && control != LegacyModemControlBytes.Nak && control != LegacyModemControlBytes.CrcRequest)
            {
                throw new ArgumentOutOfRangeException(nameof(control));
            }

            if (blockNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(blockNumber));
            }

            return new[] { control, (byte)blockNumber, (byte)(255 - (byte)blockNumber) };
        }

        public static int DecodeNumberedResponse(byte[] response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            if (response.Length != 3 ||
                (response[0] != LegacyModemControlBytes.Ack && response[0] != LegacyModemControlBytes.Nak && response[0] != LegacyModemControlBytes.CrcRequest) ||
                (response[1] ^ response[2]) != 255)
            {
                throw new InvalidDataException("Invalid SEAlink acknowledgement.");
            }

            return response[1];
        }

        public static byte[] EncodeResync(int blockNumber)
        {
            if (blockNumber < 1 || blockNumber > 33554433)
            {
                throw new ArgumentOutOfRangeException(nameof(blockNumber));
            }

            var digits = Encoding.ASCII.GetBytes(blockNumber.ToString(CultureInfo.InvariantCulture));
            var result = new byte[digits.Length + 4];
            result[0] = LegacyModemControlBytes.Syn;
            digits.CopyTo(result, 1);
            result[result.Length - 3] = LegacyModemControlBytes.Etx;
            var crc = Crc16(digits, 0, digits.Length);
            result[result.Length - 2] = (byte)crc;
            result[result.Length - 1] = (byte)(crc >> 8);
            return result;
        }

        public static int DecodeResync(byte[] frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (frame.Length < 5 || frame.Length > 24 || frame[0] != LegacyModemControlBytes.Syn || frame[frame.Length - 3] != LegacyModemControlBytes.Etx)
            {
                throw new InvalidDataException("Invalid RESYNC framing.");
            }

            for (var index = 1; index < frame.Length - 3; index++)
            {
                if (frame[index] < '0' || frame[index] > '9')
                {
                    throw new InvalidDataException("Invalid RESYNC block number.");
                }
            }

            var text = Encoding.ASCII.GetString(frame, 1, frame.Length - 4);
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > 33554433)
            {
                throw new InvalidDataException("RESYNC block number is out of range.");
            }

            if (Crc16(frame, 1, frame.Length - 4) != (frame[frame.Length - 2] | (frame[frame.Length - 1] << 8)))
            {
                throw new InvalidDataException("Invalid RESYNC CRC.");
            }

            return number;
        }

        public static byte[] EncodeModem7Name(string fileName)
        {
            if (fileName == null)
            {
                throw new ArgumentNullException(nameof(fileName));
            }

            var parts = fileName.ToUpperInvariant().Split('.');
            if (parts.Length > 2 || parts[0].Length < 1 || parts[0].Length > 8 || (parts.Length == 2 && (parts[1].Length < 1 || parts[1].Length > 3)))
            {
                throw new ArgumentException("MODEM7 requires a DOS 8.3 file name.", nameof(fileName));
            }

            foreach (var character in fileName)
            {
                if (character < 33 || character > 126 || character == '/' || character == '\\' || character == ':')
                {
                    throw new ArgumentException("Invalid MODEM7 file name.", nameof(fileName));
                }
            }

            return Encoding.ASCII.GetBytes(parts[0].PadRight(8) + (parts.Length == 2 ? parts[1] : string.Empty).PadRight(3));
        }

        public static string DecodeModem7Name(byte[] name)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (name.Length != 11)
            {
                throw new InvalidDataException("MODEM7 names contain exactly eleven characters.");
            }

            foreach (var value in name)
            {
                if (value < 32 || value > 126 || (value >= 'a' && value <= 'z'))
                {
                    throw new InvalidDataException("MODEM7 names use uppercase ASCII.");
                }
            }

            var baseName = Encoding.ASCII.GetString(name, 0, 8).TrimEnd(' ');
            var extension = Encoding.ASCII.GetString(name, 8, 3).TrimEnd(' ');
            var decoded = baseName + (extension.Length > 0 ? "." + extension : string.Empty);
            try
            {
                if (!BytesEqual(EncodeModem7Name(decoded), name))
                {
                    throw new InvalidDataException("Invalid MODEM7 name padding.");
                }
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Invalid MODEM7 name.", exception);
            }

            return decoded;
        }

        public static byte ComputeModem7NameChecksum(byte[] name)
        {
            DecodeModem7Name(name);
            return (byte)(Checksum(name, 0, name.Length) + LegacyModemControlBytes.Sub);
        }

        internal static ushort Crc16(byte[] bytes, int offset, int length)
        {
            ushort crc = 0;
            for (var index = offset; index < offset + length; index++)
            {
                crc ^= (ushort)(bytes[index] << 8);
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (ushort)(((crc & 0x8000) != 0) ? (crc << 1) ^ 0x1021 : crc << 1);
                }
            }

            return crc;
        }

        private static byte[] EncodeFrame(byte start, int blockNumber, byte[] payload, LegacyModemIntegrity integrity)
        {
            ValidateIntegrity(integrity);
            var frame = new byte[integrity == LegacyModemIntegrity.Crc16 ? 133 : 132];
            frame[0] = start;
            frame[1] = (byte)blockNumber;
            frame[2] = (byte)(255 - frame[1]);
            payload.CopyTo(frame, 3);
            if (integrity == LegacyModemIntegrity.Crc16)
            {
                var crc = Crc16(payload, 0, 128);
                frame[131] = (byte)(crc >> 8);
                frame[132] = (byte)crc;
            }
            else
            {
                frame[131] = Checksum(payload, 0, 128);
            }

            return frame;
        }

        private static byte[] DecodeFrame(byte[] frame, LegacyModemIntegrity integrity, bool telink)
        {
            ValidateIntegrity(integrity);
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (frame.Length != (integrity == LegacyModemIntegrity.Crc16 ? 133 : 132) ||
                frame[0] != (telink ? LegacyModemControlBytes.Syn : LegacyModemControlBytes.Soh) ||
                (frame[1] ^ frame[2]) != 255)
            {
                throw new InvalidDataException("Invalid legacy modem frame.");
            }

            var valid = integrity == LegacyModemIntegrity.Crc16
                ? Crc16(frame, 3, 128) == ((frame[131] << 8) | frame[132])
                : Checksum(frame, 3, 128) == frame[131];
            if (!valid)
            {
                throw new InvalidDataException("Invalid legacy modem checksum or CRC.");
            }

            var data = new byte[128];
            Buffer.BlockCopy(frame, 3, data, 0, 128);
            return data;
        }

        private static void ValidateIntegrity(LegacyModemIntegrity integrity)
        {
            if (integrity != LegacyModemIntegrity.Checksum && integrity != LegacyModemIntegrity.Crc16)
            {
                throw new ArgumentOutOfRangeException(nameof(integrity));
            }
        }

        private static byte Checksum(byte[] data, int offset, int count)
        {
            byte result = 0;
            for (var index = offset; index < offset + count; index++)
            {
                result = (byte)(result + data[index]);
            }

            return result;
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        private static void WriteUInt32(byte[] data, int offset, uint value)
        {
            for (var index = 0; index < 4; index++)
            {
                data[offset + index] = (byte)(value >> (index * 8));
            }
        }

        private static string ReadAscii(byte[] data, int offset, int count)
        {
            var length = 0;
            while (length < count && data[offset + length] != 0)
            {
                if (data[offset + length] < 32 || data[offset + length] > 126)
                {
                    throw new InvalidDataException("Metadata contains non-ASCII or control characters.");
                }

                length++;
            }

            return Encoding.ASCII.GetString(data, offset, length);
        }

        private static void ValidateAscii(string text, int maxLength)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            if (text.Length > maxLength)
            {
                throw new ArgumentException("Text is too long.", nameof(text));
            }

            foreach (var character in text)
            {
                if (character < 32 || character > 126)
                {
                    throw new ArgumentException("Printable ASCII is required.", nameof(text));
                }
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (var index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
