using System;
using System.Collections.Generic;
using System.IO;

namespace Jmodem.Protocol
{
    public sealed class JModemPacket
    {
        private readonly byte[] _payload;
        private readonly byte[]? _wireFrame;

        public JModemPacket(int blockNumber, byte[] payload, bool isLastBlock)
            : this(blockNumber, payload, isLastBlock, null)
        {
        }

        internal JModemPacket(int blockNumber, byte[] payload, bool isLastBlock, byte[]? wireFrame)
        {
            if (blockNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(blockNumber));
            }

            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (payload.Length > 8192 || (payload.Length == 0 && !isLastBlock))
            {
                throw new ArgumentException("JMODEM payloads allow 1..8192 bytes, or an empty final block.", nameof(payload));
            }

            BlockNumber = blockNumber;
            IsLastBlock = isLastBlock;
            _payload = (byte[])payload.Clone();
            _wireFrame = wireFrame == null ? null : (byte[])wireFrame.Clone();
        }

        public int BlockNumber { get; }

        public bool IsLastBlock { get; }

        public byte[] Payload => (byte[])_payload.Clone();

        public int DataLength => _payload.Length;

        internal byte[]? WireFrame => _wireFrame == null ? null : (byte[])_wireFrame.Clone();
    }

    public sealed class JModemPacketEncoder
    {
        private readonly bool _enableCompression;

        public JModemPacketEncoder(bool enableCompression = true)
        {
            _enableCompression = enableCompression;
        }

        public byte[] Encode(JModemPacket packet)
        {
            if (packet == null)
            {
                throw new ArgumentNullException(nameof(packet));
            }

            var payload = packet.Payload;
            var type = JModemControlBytes.Normal;
            if (_enableCompression && payload.Length > 0)
            {
                var encoded = JModemCodec.EncodeRunLength(payload);
                if (encoded.Length < payload.Length)
                {
                    payload = encoded;
                    type |= JModemControlBytes.Compressed;
                }
            }

            if (packet.IsLastBlock)
            {
                type |= JModemControlBytes.EndOfFile;
            }

            var frame = new byte[payload.Length + 6];
            frame[0] = (byte)frame.Length;
            frame[1] = (byte)(frame.Length >> 8);
            frame[2] = type;
            frame[3] = (byte)(packet.BlockNumber & 0xFF);
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            var check = JModemCodec.ComputeChecksum(frame, frame.Length - 2);
            frame[frame.Length - 2] = (byte)check;
            frame[frame.Length - 1] = (byte)(check >> 8);
            return frame;
        }
    }

    public sealed class JModemPacketDecoder
    {
        public JModemPacket Decode(byte[] frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (frame.Length < 6 || frame.Length > 8198 || (frame[0] | (frame[1] << 8)) != frame.Length)
            {
                throw new InvalidDataException("Invalid JMODEM total frame length.");
            }

            if ((frame[2] & ~7) != 0)
            {
                throw new InvalidDataException("Unknown JMODEM block type bits.");
            }

            var expected = frame[frame.Length - 2] | (frame[frame.Length - 1] << 8);
            if (expected != JModemCodec.ComputeChecksum(frame, frame.Length - 2))
            {
                throw new InvalidDataException("Invalid JMODEM rotate/add checksum.");
            }

            var payload = new byte[frame.Length - 6];
            Buffer.BlockCopy(frame, 4, payload, 0, payload.Length);
            if ((frame[2] & JModemControlBytes.Compressed) != 0)
            {
                payload = JModemCodec.DecodeRunLength(payload);
            }

            var final = (frame[2] & JModemControlBytes.EndOfFile) != 0;
            if (payload.Length == 0 && !final)
            {
                throw new InvalidDataException("A non-final JMODEM block cannot be empty.");
            }

            return new JModemPacket(frame[3], payload, final, frame);
        }
    }

    public static class JModemCodec
    {
        public static ushort ComputeChecksum(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            return ComputeChecksum(bytes, bytes.Length);
        }

        public static byte[] EncodeRunLength(byte[] bytes)
        {
            ValidateLength(bytes);
            var encoded = new List<byte>();
            for (var offset = 0; offset < bytes.Length;)
            {
                var value = bytes[offset];
                var count = 1;
                while (offset + count < bytes.Length && bytes[offset + count] == value)
                {
                    count++;
                }

                if (value == JModemControlBytes.RunLengthEscape || count > 1)
                {
                    encoded.Add(JModemControlBytes.RunLengthEscape);
                    encoded.Add((byte)count);
                    encoded.Add((byte)(count >> 8));
                    encoded.Add(value);
                }
                else
                {
                    encoded.Add(value);
                }

                offset += count;
            }

            return encoded.ToArray();
        }

        public static byte[] DecodeRunLength(byte[] bytes)
        {
            ValidateLength(bytes);
            var decoded = new List<byte>();
            for (var offset = 0; offset < bytes.Length; offset++)
            {
                var value = bytes[offset];
                var count = 1;
                if (value == JModemControlBytes.RunLengthEscape)
                {
                    if (bytes.Length - offset < 4)
                    {
                        throw new InvalidDataException("Truncated JMODEM run.");
                    }

                    count = bytes[++offset];
                    count |= bytes[++offset] << 8;
                    value = bytes[++offset];
                    if (count == 0)
                    {
                        throw new InvalidDataException("A JMODEM run must contain at least one byte.");
                    }
                }

                if (count > 8192 - decoded.Count)
                {
                    throw new InvalidDataException("Expanded JMODEM data exceeds 8192 bytes.");
                }

                for (var repeat = 0; repeat < count; repeat++)
                {
                    decoded.Add(value);
                }
            }

            return decoded.ToArray();
        }

        internal static ushort ComputeChecksum(byte[] bytes, int count)
        {
            var checksum = 0;
            for (var offset = 0; offset < count; offset++)
            {
                checksum = (checksum + bytes[offset]) & 0xFFFF;
                var shift = (count - offset) & 7;
                checksum = ((checksum << shift) | (checksum >> (16 - shift))) & 0xFFFF;
            }

            return (ushort)checksum;
        }

        private static void ValidateLength(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length > 8192)
            {
                throw new ArgumentOutOfRangeException(nameof(bytes));
            }
        }
    }
}
