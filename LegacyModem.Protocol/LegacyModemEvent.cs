using System;

namespace LegacyModem.Protocol
{
    public abstract class LegacyModemEvent
    {
        private LegacyModemEvent()
        {
        }

        public sealed class StartRequested : LegacyModemEvent
        {
        }

        public sealed class TimeoutElapsed : LegacyModemEvent
        {
        }

        public sealed class BytesReceived : LegacyModemEvent
        {
            private readonly byte[] _bytes;

            public BytesReceived(byte[] bytes)
            {
                _bytes = bytes == null ? throw new ArgumentNullException(nameof(bytes)) : (byte[])bytes.Clone();
            }

            public byte[] Bytes => (byte[])_bytes.Clone();
        }

        public sealed class FileHeaderReady : LegacyModemEvent
        {
            public FileHeaderReady(LegacyModemFileDescriptor file)
            {
                File = file ?? throw new ArgumentNullException(nameof(file));
            }

            public LegacyModemFileDescriptor File { get; }
        }

        public sealed class NoMoreFiles : LegacyModemEvent
        {
        }

        public sealed class DataBlockReady : LegacyModemEvent
        {
            private readonly byte[] _data;

            public DataBlockReady(int blockNumber, byte[] data)
            {
                if (blockNumber < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(blockNumber));
                }

                if (data == null)
                {
                    throw new ArgumentNullException(nameof(data));
                }

                if (data.Length > 128)
                {
                    throw new ArgumentException("Legacy data blocks contain at most 128 bytes.", nameof(data));
                }

                BlockNumber = blockNumber;
                _data = (byte[])data.Clone();
            }

            public int BlockNumber { get; }

            public byte[] Data => (byte[])_data.Clone();
        }

        public sealed class FileHeaderAccepted : LegacyModemEvent
        {
            public FileHeaderAccepted(int restartBlock = 1)
            {
                if (restartBlock < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(restartBlock));
                }

                RestartBlock = restartBlock;
            }

            public int RestartBlock { get; }
        }

        public sealed class FileHeaderRejected : LegacyModemEvent
        {
            public FileHeaderRejected(string reason = "File rejected.")
            {
                Reason = reason;
            }

            public string Reason { get; }
        }

        public sealed class DataBlockAccepted : LegacyModemEvent
        {
        }

        public sealed class DataBlockRejected : LegacyModemEvent
        {
        }

        public sealed class CancelRequested : LegacyModemEvent
        {
            public CancelRequested(string reason = "Transfer cancelled.")
            {
                Reason = reason;
            }

            public string Reason { get; }
        }
    }
}
