using System;

namespace Xmodem.Protocol
{
    public abstract class XModemAction
    {
        private XModemAction()
        {
        }

        public sealed class RequestDataBlock : XModemAction
        {
            public RequestDataBlock(int blockNumber, int blockSize)
            {
                BlockNumber = blockNumber;
                BlockSize = blockSize;
            }

            public int BlockNumber { get; }

            public int BlockSize { get; }
        }

        public sealed class SendPacket : XModemAction
        {
            public SendPacket(XModemPacket packet, XModemIntegrity integrity)
            {
                Packet = packet ?? throw new ArgumentNullException(nameof(packet));
                XModemChecks.ValidateIntegrity(integrity);
                Integrity = integrity;
            }

            public XModemPacket Packet { get; }

            public XModemIntegrity Integrity { get; }
        }

        public sealed class SendControl : XModemAction
        {
            public SendControl(byte value)
            {
                Value = value;
            }

            public byte Value { get; }
        }

        public sealed class SendBytes : XModemAction
        {
            public SendBytes(byte[] bytes)
            {
                Bytes = bytes == null ? throw new ArgumentNullException(nameof(bytes)) : (byte[])bytes.Clone();
            }

            public byte[] Bytes { get; }
        }

        public sealed class DeliverDataBlock : XModemAction
        {
            public DeliverDataBlock(int blockNumber, byte[] payload)
            {
                BlockNumber = blockNumber;
                Payload = payload ?? throw new ArgumentNullException(nameof(payload));
            }

            public int BlockNumber { get; }

            public byte[] Payload { get; }
        }

        public sealed class Complete : XModemAction
        {
        }

        public sealed class Cancel : XModemAction
        {
            public Cancel(string reason)
            {
                Reason = reason;
            }

            public string Reason { get; }
        }

        public sealed class Fail : XModemAction
        {
            public Fail(string reason)
            {
                Reason = reason;
            }

            public string Reason { get; }
        }
    }
}
