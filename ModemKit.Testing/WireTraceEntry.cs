using System;

namespace ModemKit.Testing
{
    public sealed class WireTraceEntry
    {
        internal WireTraceEntry(int sequence, WireDirection direction, byte[] original, byte[] transmitted)
        {
            Sequence = sequence;
            Direction = direction;
            Original = (byte[])original.Clone();
            Transmitted = (byte[])transmitted.Clone();
        }

        public int Sequence { get; }

        public WireDirection Direction { get; }

        public byte[] Original { get; }

        public byte[] Transmitted { get; }

        public override string ToString()
        {
            return Sequence + " " + Direction + " " + Preview(Original) + " -> " + Preview(Transmitted);
        }

        private static string Preview(byte[] bytes)
        {
            return bytes.Length + " bytes [" + BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, 16)) + "]";
        }
    }
}
