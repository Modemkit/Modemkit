using System;

namespace Jmodem.Protocol
{
    public abstract class JModemAction
    {
        private JModemAction()
        {
        }

        public sealed class SendBytes : JModemAction
        {
            public SendBytes(byte[] bytes)
            {
                Bytes = bytes == null ? throw new ArgumentNullException(nameof(bytes)) : (byte[])bytes.Clone();
            }

            public byte[] Bytes { get; }
        }

        public sealed class RequestDataBlock : JModemAction
        {
            public RequestDataBlock(int blockNumber, int blockSize)
            {
                BlockNumber = blockNumber;
                BlockSize = blockSize;
            }

            public int BlockNumber { get; }

            public int BlockSize { get; }
        }

        public sealed class DeliverDataBlock : JModemAction
        {
            public DeliverDataBlock(int blockNumber, byte[] payload)
            {
                BlockNumber = blockNumber;
                Payload = payload == null ? throw new ArgumentNullException(nameof(payload)) : (byte[])payload.Clone();
            }

            public int BlockNumber { get; }

            public byte[] Payload { get; }
        }

        public sealed class DiscardInput : JModemAction
        {
        }

        public sealed class Complete : JModemAction
        {
        }

        public sealed class Cancel : JModemAction
        {
            public Cancel(string reason)
            {
                Reason = reason;
            }

            public string Reason { get; }
        }

        public sealed class Fail : JModemAction
        {
            public Fail(string reason)
            {
                Reason = reason;
            }

            public string Reason { get; }
        }
    }
}
