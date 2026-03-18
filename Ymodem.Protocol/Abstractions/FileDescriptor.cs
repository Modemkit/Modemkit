using System;

namespace Ymodem.Protocol
{
    public sealed class YModemFileDescriptor
    {
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

            FileName = fileName;
            FileSize = fileSize;
        }

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
