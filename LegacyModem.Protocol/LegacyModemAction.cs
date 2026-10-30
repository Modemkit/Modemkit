using System;

namespace LegacyModem.Protocol
{
    public abstract class LegacyModemAction
    {
        private LegacyModemAction()
        {
        }

        public sealed class SendBytes : LegacyModemAction
        {
            private readonly byte[] _bytes;

            public SendBytes(byte[] bytes)
            {
                _bytes = bytes == null ? throw new ArgumentNullException(nameof(bytes)) : (byte[])bytes.Clone();
            }

            public byte[] Bytes => (byte[])_bytes.Clone();
        }

        /// <summary>Discard unsent transport output when resynchronizing a window.</summary>
        public sealed class PurgeOutput : LegacyModemAction
        {
        }

        public sealed class RequestFileHeader : LegacyModemAction
        {
        }

        public sealed class RequestDataBlock : LegacyModemAction
        {
            public RequestDataBlock(int blockNumber, int dataLength)
            {
                BlockNumber = blockNumber;
                DataLength = dataLength;
            }

            public int BlockNumber { get; }

            public long Offset => ((long)BlockNumber - 1) * 128;

            public int DataLength { get; }

            public int BlockSize => 128;
        }

        public sealed class OfferFileHeader : LegacyModemAction
        {
            public OfferFileHeader(LegacyModemFileDescriptor file, bool canRestart)
            {
                File = file;
                CanRestart = canRestart;
            }

            public LegacyModemFileDescriptor File { get; }

            public bool CanRestart { get; }
        }

        public sealed class DeliverDataBlock : LegacyModemAction
        {
            private readonly byte[] _data;

            public DeliverDataBlock(int blockNumber, byte[] data)
            {
                BlockNumber = blockNumber;
                _data = data == null ? throw new ArgumentNullException(nameof(data)) : (byte[])data.Clone();
            }

            public int BlockNumber { get; }

            public long Offset => ((long)BlockNumber - 1) * 128;

            public byte[] Data => (byte[])_data.Clone();
        }

        public sealed class FileCompleted : LegacyModemAction
        {
            public FileCompleted(LegacyModemFileDescriptor file, long bytesTransferred)
            {
                File = file;
                BytesTransferred = bytesTransferred;
            }

            public LegacyModemFileDescriptor File { get; }

            public long BytesTransferred { get; }
        }

        public sealed class Complete : LegacyModemAction
        {
        }

        public sealed class Cancel : LegacyModemAction
        {
            public Cancel(string reason)
            {
                Reason = reason;
            }

            public string Reason { get; }
        }

        public sealed class Fail : LegacyModemAction
        {
            public Fail(string reason)
            {
                Reason = reason;
            }

            public string Reason { get; }
        }
    }
}
