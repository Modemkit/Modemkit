using System;

namespace LegacyModem.Protocol
{
    public sealed class LegacyModemFileDescriptor
    {
        /// <summary>
        /// Timestamp is the raw historical wire value: DOS packed time/date in
        /// TeLink, or the peer's four-byte SEAlink time value. No epoch conversion
        /// is applied because deployed SEAlink implementations disagree on it.
        /// </summary>
        public LegacyModemFileDescriptor(string fileName, long? fileSize = null, uint timestamp = 0)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("A file name is required.", nameof(fileName));
            }

            if (fileName.Length > 16)
            {
                throw new ArgumentException("Legacy header names are limited to 16 ASCII characters.", nameof(fileName));
            }

            foreach (var character in fileName)
            {
                if (character < 32 || character > 126 || character == '/' || character == '\\' || character == ':')
                {
                    throw new ArgumentException("A printable ASCII base file name is required.", nameof(fileName));
                }
            }

            if (fileName == "." || fileName == ".." || fileName.Trim() != fileName)
            {
                throw new ArgumentException("Invalid file name.", nameof(fileName));
            }

            if (fileSize < 0 || fileSize > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(fileSize));
            }

            FileName = fileName;
            FileSize = fileSize;
            Timestamp = timestamp;
        }

        public string FileName { get; }

        public long? FileSize { get; }

        public uint Timestamp { get; }
    }
}
