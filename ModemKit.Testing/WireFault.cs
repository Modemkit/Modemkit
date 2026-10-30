using System;

namespace ModemKit.Testing
{
    /// <summary>A bounded fault applied to matching writes before stream fragmentation.</summary>
    public sealed class WireFault
    {
        private readonly Func<byte[], bool> _matches;
        private readonly Func<byte[], byte[]> _transform;

        public WireFault(
            WireDirection direction,
            Func<byte[], bool> matches,
            Func<byte[], byte[]> transform,
            int occurrences = 1)
        {
            if (!Enum.IsDefined(typeof(WireDirection), direction))
            {
                throw new ArgumentOutOfRangeException(nameof(direction));
            }

            if (occurrences < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(occurrences));
            }

            Direction = direction;
            _matches = matches ?? throw new ArgumentNullException(nameof(matches));
            _transform = transform ?? throw new ArgumentNullException(nameof(transform));
            Remaining = occurrences;
        }

        public WireDirection Direction { get; }

        public int Remaining { get; private set; }

        public static WireFault Drop(WireDirection direction, Func<byte[], bool> matches, int occurrences = 1)
        {
            return new WireFault(direction, matches, _ => Array.Empty<byte>(), occurrences);
        }

        public static WireFault Corrupt(
            WireDirection direction,
            Func<byte[], bool> matches,
            int byteOffset,
            byte xorMask = 1,
            int occurrences = 1)
        {
            if (byteOffset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(byteOffset));
            }

            if (xorMask == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(xorMask));
            }

            return new WireFault(
                direction,
                matches,
                bytes =>
                {
                    if (byteOffset >= bytes.Length)
                    {
                        throw new InvalidOperationException("Fault offset is outside the matching write.");
                    }

                    bytes[byteOffset] ^= xorMask;
                    return bytes;
                },
                occurrences);
        }

        public static WireFault Duplicate(WireDirection direction, Func<byte[], bool> matches, int occurrences = 1)
        {
            return new WireFault(
                direction,
                matches,
                bytes =>
                {
                    var duplicate = new byte[checked(bytes.Length * 2)];
                    Buffer.BlockCopy(bytes, 0, duplicate, 0, bytes.Length);
                    Buffer.BlockCopy(bytes, 0, duplicate, bytes.Length, bytes.Length);
                    return duplicate;
                },
                occurrences);
        }

        public static WireFault Truncate(
            WireDirection direction,
            Func<byte[], bool> matches,
            int retainedLength,
            int occurrences = 1)
        {
            if (retainedLength < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(retainedLength));
            }

            return new WireFault(
                direction,
                matches,
                bytes =>
                {
                    var truncated = new byte[Math.Min(retainedLength, bytes.Length)];
                    Buffer.BlockCopy(bytes, 0, truncated, 0, truncated.Length);
                    return truncated;
                },
                occurrences);
        }

        internal byte[] Apply(WireDirection direction, byte[] bytes)
        {
            if (Remaining == 0 || direction != Direction || !_matches((byte[])bytes.Clone()))
            {
                return bytes;
            }

            Remaining--;
            return _transform((byte[])bytes.Clone()) ?? throw new InvalidOperationException("A wire fault returned null.");
        }
    }
}
