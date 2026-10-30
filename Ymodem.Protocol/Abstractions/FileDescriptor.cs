using System;

namespace Ymodem.Protocol
{
    public sealed class YModemFileDescriptor
    {
        public YModemFileDescriptor(string fileName)
            : this(fileName, 0)
        {
            HasFileSize = false;
        }

        public YModemFileDescriptor(string fileName, long fileSize)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("File name is required.", nameof(fileName));
            }

            if (fileSize < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(fileSize), "File size cannot be negative.");
            }

            foreach (var character in fileName)
            {
                if (character < 32 || character == 127)
                {
                    throw new ArgumentException("File name cannot contain control characters.", nameof(fileName));
                }
            }

            FileName = fileName;
            FileSize = fileSize;
            HasFileSize = true;
        }

        public bool HasFileSize { get; }

        public string FileName
        {
            get;
        }

        public long FileSize
        {
            get;
        }
    }
}
