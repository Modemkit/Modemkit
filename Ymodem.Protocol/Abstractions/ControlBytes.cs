namespace Ymodem.Protocol
{
    public static class YModemControlBytes
    {
        public const byte Soh = 0x01;
        public const byte Stx = 0x02;
        public const byte Eot = 0x04;
        public const byte Ack = 0x06;
        public const byte Nak = 0x15;
        public const byte Can = 0x18;
        public const byte CrcRequest = 0x43; // 'C'
        public const byte CpmEof = 0x1A;
    }
}
