using System;

namespace Ymodem.Protocol
{
    public enum YModemTransferMode
    {
        Crc = 0,
        G = 1,
        Checksum = 2,
    }

    /// <summary>
    /// Selects acknowledged CRC/checksum transfers or streaming YMODEM-G and limits retries.
    /// The host owns the clock and supplies TimeoutElapsed events.
    /// </summary>
    public sealed class YModemTransferOptions
    {
        public YModemTransferOptions(YModemTransferMode mode = YModemTransferMode.Crc, int maxRetries = 10)
        {
            if (mode != YModemTransferMode.Crc && mode != YModemTransferMode.G && mode != YModemTransferMode.Checksum)
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            if (maxRetries < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRetries));
            }

            Mode = mode;
            MaxRetries = maxRetries;
        }

        public YModemTransferMode Mode { get; }

        public int MaxRetries { get; }
    }
}
