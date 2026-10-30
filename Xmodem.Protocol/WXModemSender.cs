using System;
using System.Collections.Generic;

namespace Xmodem.Protocol
{
    public sealed class WXModemSender : WXModemSession
    {
        private readonly List<PendingBlock> _window = new List<PendingBlock>(4);
        private readonly WXModemPacketEncoder _encoder = new WXModemPacketEncoder();
        private XModemSender? _fallback;
        private bool _requested;
        private bool _lastBlockProvided;
        private byte? _response;

        public WXModemSender(WXModemOptions? options = null)
            : base(options)
        {
            Phase = XModemPhase.WaitingReceiverRequest;
        }

        protected override int OutstandingBlocks => _window.Count;

        protected override void OnEvent(XModemEvent protocolEvent)
        {
            if (protocolEvent is XModemEvent.PeerBytesReceived bytes)
            {
                foreach (var value in bytes.Bytes)
                {
                    OnByte(value);
                    if (IsTerminal)
                    {
                        break;
                    }
                }

                return;
            }

            if (protocolEvent is XModemEvent.PeerByteReceived peer)
            {
                OnByte(peer.Value);
                return;
            }

            if (_fallback != null)
            {
                ImportFallback(_fallback.Advance(protocolEvent));
                return;
            }

            switch (protocolEvent)
            {
                case XModemEvent.StartRequested _:
                    break;
                case XModemEvent.DataBlockReady data:
                    ProvideData(data);
                    break;
                case XModemEvent.TimeoutElapsed _:
                    OnTimeout();
                    break;
                case XModemEvent.FlowControlTimeoutElapsed _:
                    if (IsPaused)
                    {
                        IsPaused = false;
                        Pump();
                    }

                    break;
                default:
                    Fault("Unsupported WXMODEM sender event.");
                    break;
            }
        }

        private void OnByte(byte value)
        {
            if (_fallback != null)
            {
                ImportFallback(_fallback.Advance(new XModemEvent.PeerByteReceived(value)));
                return;
            }

            if (HandleCancellation(value))
            {
                _response = null;
                return;
            }

            if (Phase == XModemPhase.WaitingReceiverRequest)
            {
                if (value == WXModemControlBytes.WindowRequest)
                {
                    RetryCount = 0;
                    Pump();
                }
                else if (Options.AllowFallback && (value == XModemControlBytes.CrcRequest || value == XModemControlBytes.Nak))
                {
                    Mode = value == XModemControlBytes.CrcRequest ? WXModemMode.Crc : WXModemMode.Checksum;
                    _fallback = new XModemSender(new XModemOptions(
                        Mode == WXModemMode.Crc ? XModemMode.Crc : XModemMode.Checksum, Options.MaxRetries));
                    ImportFallback(_fallback.Advance(new XModemEvent.PeerByteReceived(value)));
                }

                return;
            }

            if (value == WXModemControlBytes.Xoff)
            {
                IsPaused = true;
                return;
            }

            if (value == WXModemControlBytes.Xon)
            {
                IsPaused = false;
                Pump();
                return;
            }

            if (Phase == XModemPhase.WaitingEotAck)
            {
                if (value == XModemControlBytes.Ack)
                {
                    Phase = XModemPhase.Completed;
                    Add(new XModemAction.Complete());
                }
                else if (value == XModemControlBytes.Nak && TryRetry())
                {
                    Send(new[] { XModemControlBytes.Eot });
                }

                return;
            }

            if (value == XModemControlBytes.Ack || value == XModemControlBytes.Nak)
            {
                _response = value;
                return;
            }

            if (!_response.HasValue || value > 3)
            {
                _response = null;
                return;
            }

            var response = _response.Value;
            _response = null;
            var index = _window.FindIndex(block => block.Sent && (block.Number & 3) == value);
            if (index < 0)
            {
                return;
            }

            var acknowledged = response == XModemControlBytes.Ack ? index + 1 : index;
            for (var i = 0; i < acknowledged; i++)
            {
                BytesTransferred += _window[i].DataLength;
            }

            if (acknowledged > 0)
            {
                _window.RemoveRange(0, acknowledged);
                RetryCount = 0;
            }

            if (response == XModemControlBytes.Nak)
            {
                if (!TryRetry())
                {
                    return;
                }

                foreach (var block in _window)
                {
                    block.Sent = false;
                }
            }

            Pump();
        }

        private void ProvideData(XModemEvent.DataBlockReady data)
        {
            if (!_requested || data.BlockNumber != NextBlockNumber
                || data.DataLength > 128 || (!data.IsLastBlock && data.DataLength != 128))
            {
                Fault("WXMODEM data must match the requested block and fill non-final 128-byte blocks.");
                return;
            }

            _requested = false;
            _lastBlockProvided = data.IsLastBlock;
            if (data.DataLength > 0)
            {
                _window.Add(new PendingBlock(
                    NextBlockNumber,
                    data.DataLength,
                    _encoder.Encode(new XModemPacket.Data(NextBlockNumber, data.Data, data.DataLength))));
                NextBlockNumber++;
            }

            Pump();
        }

        private void Pump()
        {
            if (IsTerminal || _fallback != null || Phase == XModemPhase.WaitingEotAck)
            {
                return;
            }

            if (IsPaused)
            {
                Phase = _requested ? XModemPhase.WaitingDataBlock : XModemPhase.WaitingWindowAck;
                return;
            }

            foreach (var block in _window)
            {
                if (!block.Sent)
                {
                    block.Sent = true;
                    Send(block.Wire);
                }
            }

            if (_lastBlockProvided && _window.Count == 0)
            {
                RetryCount = 0;
                Phase = XModemPhase.WaitingEotAck;
                Send(new[] { XModemControlBytes.Eot });
            }
            else if (!_lastBlockProvided && !_requested && _window.Count < 4)
            {
                _requested = true;
                Phase = XModemPhase.WaitingDataBlock;
                Add(new XModemAction.RequestDataBlock(NextBlockNumber, 128));
            }
            else
            {
                Phase = _requested ? XModemPhase.WaitingDataBlock : XModemPhase.WaitingWindowAck;
            }
        }

        private void OnTimeout()
        {
            ClearCancellation();
            _response = null;
            if (IsPaused)
            {
                return;
            }

            if (Phase == XModemPhase.WaitingReceiverRequest)
            {
                TryRetry();
            }
            else if (Phase == XModemPhase.WaitingEotAck)
            {
                if (TryRetry())
                {
                    Send(new[] { XModemControlBytes.Eot });
                }
            }
            else
            {
                var last = _window.FindLast(block => block.Sent);
                if (last != null && TryRetry())
                {
                    // Probe the newest outstanding block: its cumulative ACK can reopen the whole window.
                    Send(last.Wire);
                }
            }
        }

        private sealed class PendingBlock
        {
            public PendingBlock(int number, int dataLength, byte[] wire)
            {
                Number = number;
                DataLength = dataLength;
                Wire = wire;
            }

            public int Number { get; }

            public int DataLength { get; }

            public byte[] Wire { get; }

            public bool Sent { get; set; }
        }
    }
}
