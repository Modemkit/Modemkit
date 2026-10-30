using System.Collections.Generic;

namespace Xmodem.Protocol
{
    public sealed class WXModemReceiver : WXModemSession
    {
        private readonly WXModemStreamDecoder _decoder = new WXModemStreamDecoder();
        private readonly XModemStreamDecoder _fallbackDecoder = new XModemStreamDecoder();
        private readonly Queue<XModemEvent> _pending = new Queue<XModemEvent>(4);
        private XModemReceiver? _fallback;
        private XModemPacket.Data? _pendingData;
        private bool _started;
        private bool _recovering;
        private int _attempts;

        public WXModemReceiver(WXModemOptions? options = null)
            : base(options)
        {
        }

        protected override int OutstandingBlocks => _pending.Count + (_pendingData == null ? 0 : 1);

        protected override void OnEvent(XModemEvent protocolEvent)
        {
            if (protocolEvent is XModemEvent.PeerBytesReceived bytes)
            {
                Receive(bytes.Bytes);
                return;
            }

            if (protocolEvent is XModemEvent.PeerByteReceived peer)
            {
                Receive(new[] { peer.Value });
                return;
            }

            if (protocolEvent is XModemEvent.TimeoutElapsed)
            {
                OnTimeout();
                return;
            }

            if (_fallback != null)
            {
                ImportFallback(_fallback.Advance(protocolEvent));
                return;
            }

            switch (protocolEvent)
            {
                case XModemEvent.StartRequested _ when Phase == XModemPhase.Idle:
                    Phase = XModemPhase.WaitingPacket;
                    _attempts = 1;
                    SendRequest();
                    break;
                case XModemEvent.DataBlockAccepted _ when _pendingData != null:
                    BytesTransferred += _pendingData.DataLength;
                    NextBlockNumber++;
                    RetryCount = 0;
                    _recovering = false;
                    _pendingData = null;
                    Phase = XModemPhase.WaitingPacket;
                    SendResponse(XModemControlBytes.Ack, NextBlockNumber - 1);
                    Drain();
                    break;
                case XModemEvent.DataBlockRejected _ when _pendingData != null:
                    _pendingData = null;
                    _pending.Clear();
                    Phase = XModemPhase.WaitingPacket;
                    Reject();
                    break;
                case XModemEvent.PauseRequested _:
                    IsPaused = true;
                    Send(new[] { WXModemControlBytes.Xoff });
                    break;
                case XModemEvent.ResumeRequested _:
                    IsPaused = false;
                    Send(new[] { WXModemControlBytes.Xon });
                    break;
                default:
                    Fault("Unsupported WXMODEM receiver event.");
                    break;
            }
        }

        protected override void OnTerminalEvent(XModemEvent protocolEvent)
        {
            if (Phase != XModemPhase.Completed)
            {
                return;
            }

            if (protocolEvent is XModemEvent.PeerBytesReceived bytes)
            {
                ReceiveTerminal(bytes.Bytes);
            }
            else if (protocolEvent is XModemEvent.PeerByteReceived peer)
            {
                ReceiveTerminal(new[] { peer.Value });
            }
        }

        private void ReceiveTerminal(byte[] bytes)
        {
            foreach (var value in bytes)
            {
                if (value == XModemControlBytes.Eot)
                {
                    Send(new[] { XModemControlBytes.Ack });
                }
            }
        }

        private void Receive(byte[] bytes)
        {
            foreach (var value in bytes)
            {
                if (IsTerminal)
                {
                    break;
                }

                if (Phase == XModemPhase.Idle)
                {
                    continue;
                }

                if (!_started && (value == XModemControlBytes.Eot
                    || value == (Mode == WXModemMode.Windowed ? WXModemControlBytes.Syn : XModemControlBytes.Soh)))
                {
                    _started = true;
                }

                if (_fallback != null)
                {
                    foreach (var decoded in _fallbackDecoder.Feed(new[] { value }))
                    {
                        ImportFallback(_fallback.Advance(decoded));
                    }

                    continue;
                }

                foreach (var decoded in _decoder.Feed(new[] { value }))
                {
                    if (decoded is XModemEvent.PeerByteReceived control)
                    {
                        HandleCancellation(control.Value);
                        continue;
                    }

                    ClearCancellation();
                    if (Phase == XModemPhase.WaitingDataBlockDecision)
                    {
                        if (_pending.Count >= 3)
                        {
                            Fault("WXMODEM peer exceeded the four-block receive window.");
                            break;
                        }

                        _pending.Enqueue(decoded);
                    }
                    else
                    {
                        ProcessDecoded(decoded);
                    }
                }
            }
        }

        private void ProcessDecoded(XModemEvent protocolEvent)
        {
            if (protocolEvent is XModemEvent.InvalidFrame)
            {
                if (!_recovering)
                {
                    Reject();
                }

                return;
            }

            if (!(protocolEvent is XModemEvent.PacketReceived packet))
            {
                return;
            }

            if (packet.Packet is XModemPacket.Eot)
            {
                if (_recovering)
                {
                    Reject();
                    return;
                }

                Send(new[] { XModemControlBytes.Ack });
                Phase = XModemPhase.Completed;
                Add(new XModemAction.Complete());
                return;
            }

            var data = (XModemPacket.Data)packet.Packet;
            var distance = ((NextBlockNumber & 0xFF) - (data.BlockNumber & 0xFF)) & 0xFF;
            if (distance >= 1 && distance <= 4 && NextBlockNumber > distance)
            {
                SendResponse(XModemControlBytes.Ack, NextBlockNumber - 1);
            }
            else if (distance != 0)
            {
                if (!_recovering)
                {
                    Reject();
                }
            }
            else
            {
                _pendingData = data;
                Phase = XModemPhase.WaitingDataBlockDecision;
                Add(new XModemAction.DeliverDataBlock(NextBlockNumber, data.Payload));
            }
        }

        private void Drain()
        {
            while (Phase == XModemPhase.WaitingPacket && _pending.Count > 0)
            {
                ProcessDecoded(_pending.Dequeue());
            }
        }

        private void OnTimeout()
        {
            if (Phase == XModemPhase.Idle)
            {
                return;
            }

            ClearCancellation();
            if (Phase == XModemPhase.WaitingDataBlockDecision || IsPaused)
            {
                return;
            }

            if (!_started)
            {
                var limit = Mode == WXModemMode.Windowed ? Options.WindowedAttempts
                    : Mode == WXModemMode.Crc ? Options.CrcAttempts : Options.ChecksumAttempts;
                if (_attempts < limit)
                {
                    _attempts++;
                    SendRequest();
                }
                else if (Options.AllowFallback && Mode != WXModemMode.Checksum)
                {
                    Mode = Mode == WXModemMode.Windowed ? WXModemMode.Crc : WXModemMode.Checksum;
                    _attempts = 1;
                    _fallback = new XModemReceiver(new XModemOptions(
                        Mode == WXModemMode.Crc ? XModemMode.Crc : XModemMode.Checksum, Options.MaxRetries));
                    _fallbackDecoder.Reset();
                    _fallbackDecoder.Integrity = Mode == WXModemMode.Crc ? XModemIntegrity.Crc16 : XModemIntegrity.Checksum;
                    ImportFallback(_fallback.Advance(new XModemEvent.StartRequested()));
                }
                else
                {
                    Fault("WXMODEM handshake attempts exhausted.");
                }

                return;
            }

            if (_fallback != null)
            {
                ImportFallback(_fallback.Advance(_fallbackDecoder.OnTimeout()));
            }
            else
            {
                _decoder.Reset();
                Reject();
            }
        }

        private void SendRequest()
        {
            Send(new[]
            {
                Mode == WXModemMode.Windowed ? WXModemControlBytes.WindowRequest
                    : Mode == WXModemMode.Crc ? XModemControlBytes.CrcRequest : XModemControlBytes.Nak
            });
        }

        private void Reject()
        {
            _recovering = true;
            if (TryRetry())
            {
                SendResponse(XModemControlBytes.Nak, NextBlockNumber);
            }
        }

        private void SendResponse(byte response, int blockNumber)
        {
            Send(new[] { response, (byte)(blockNumber & 3) });
        }
    }
}
