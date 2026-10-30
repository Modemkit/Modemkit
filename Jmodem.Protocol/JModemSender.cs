using System;

namespace Jmodem.Protocol
{
    public sealed class JModemSender : JModemSession
    {
        private readonly JModemPacketEncoder _encoder;
        private JModemPacket? _lastPacket;
        private byte[]? _lastFrame;

        public JModemSender(JModemOptions? options = null)
            : base(options)
        {
            _encoder = new JModemPacketEncoder(Options.EnableCompression);
            Phase = JModemPhase.WaitingHandshake;
        }

        protected override void OnEvent(JModemEvent protocolEvent)
        {
            switch (protocolEvent)
            {
                case JModemEvent.StartRequested _:
                    break;
                case JModemEvent.PeerBytesReceived bytes:
                    foreach (var value in bytes.Bytes)
                    {
                        HandlePeer(value);
                        if (IsTerminal)
                        {
                            break;
                        }
                    }

                    break;
                case JModemEvent.PeerByteReceived peer:
                    HandlePeer(peer.Value);
                    break;
                case JModemEvent.DataBlockReady data:
                    HandleData(data);
                    break;
                case JModemEvent.TimeoutElapsed _:
                    if (Phase == JModemPhase.WaitingHandshake && TryRetry())
                    {
                        Send(JModemControlBytes.Nak);
                    }
                    else if (Phase == JModemPhase.WaitingBlockAck)
                    {
                        Retransmit();
                    }

                    break;
                default:
                    Fault("Unsupported JMODEM sender event.");
                    break;
            }
        }

        private void HandlePeer(byte value)
        {
            if (value == JModemControlBytes.Can)
            {
                Cancel("Peer cancelled the transfer.", false);
                return;
            }

            if (Phase == JModemPhase.WaitingHandshake)
            {
                if (value == JModemControlBytes.Nak || value == JModemControlBytes.Ack)
                {
                    if (value == JModemControlBytes.Nak)
                    {
                        Send(JModemControlBytes.Ack);
                    }

                    RetryCount = 0;
                    RequestData();
                }

                return;
            }

            if (Phase != JModemPhase.WaitingBlockAck || _lastPacket == null)
            {
                return;
            }

            if (value == JModemControlBytes.Nak)
            {
                Retransmit();
                return;
            }

            if (value != JModemControlBytes.Ack)
            {
                return;
            }

            BytesTransferred += _lastPacket.DataLength;
            NextBlockNumber++;
            if (_lastPacket.IsLastBlock)
            {
                RetryCount = 0;
                Phase = JModemPhase.Completed;
                Add(new JModemAction.Complete());
                return;
            }

            if (RetryCount == 0)
            {
                BlockSize = Math.Min(8192, BlockSize + 512);
            }
            else if (Options.BlockSizingPolicy == JModemBlockSizingPolicy.SourceCompatible)
            {
                BlockSize = Math.Max(64, BlockSize / RetryCount);
            }
            else
            {
                for (var retry = 0; retry < RetryCount && BlockSize > 64; retry++)
                {
                    BlockSize = Math.Max(64, BlockSize / 2);
                }
            }

            RetryCount = 0;
            RequestData();
        }

        private void HandleData(JModemEvent.DataBlockReady data)
        {
            if (Phase != JModemPhase.WaitingDataBlock || data.BlockNumber != NextBlockNumber
                || data.DataLength > BlockSize || (data.DataLength == 0 && !data.IsLastBlock))
            {
                Fault("JMODEM data does not match the outstanding request.");
                return;
            }

            _lastPacket = new JModemPacket(data.BlockNumber, data.Data, data.IsLastBlock);
            _lastFrame = _encoder.Encode(_lastPacket);
            Phase = JModemPhase.WaitingBlockAck;
            Add(new JModemAction.SendBytes(_lastFrame));
        }

        private void RequestData()
        {
            Phase = JModemPhase.WaitingDataBlock;
            Add(new JModemAction.RequestDataBlock(NextBlockNumber, BlockSize));
        }

        private void Retransmit()
        {
            if (TryRetry() && _lastFrame != null)
            {
                Add(new JModemAction.SendBytes(_lastFrame));
            }
        }
    }
}
