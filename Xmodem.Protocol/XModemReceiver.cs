using System;
using System.Collections.Generic;

namespace Xmodem.Protocol
{
    public sealed class XModemReceiver
    {
        private readonly XModemOptions _options;
        private readonly List<XModemAction> _actions = new List<XModemAction>();
        private readonly Queue<XModemPacket> _streamingPackets = new Queue<XModemPacket>();
        private XModemPhase _phase = XModemPhase.Idle;
        private XModemIntegrity _integrity;
        private int _nextBlockNumber = 1;
        private int _retryCount;
        private int _cancelCount;
        private long _bytesTransferred;
        private bool _receivedFrame;
        private XModemPacket.Data? _pendingPacket;
        private string? _failureReason;

        public XModemReceiver(XModemOptions? options = null)
        {
            _options = options ?? new XModemOptions();
            _integrity = _options.Mode == XModemMode.Checksum ? XModemIntegrity.Checksum : XModemIntegrity.Crc16;
        }

        public XModemSnapshot Snapshot => new XModemSnapshot(
            _phase, _nextBlockNumber, _bytesTransferred, _retryCount, _integrity, _failureReason);

        public XModemStepResult Advance(XModemEvent protocolEvent)
        {
            if (protocolEvent == null)
            {
                throw new ArgumentNullException(nameof(protocolEvent));
            }

            _actions.Clear();
            if (_phase == XModemPhase.Completed)
            {
                if (protocolEvent is XModemEvent.PacketReceived packet && packet.Packet is XModemPacket.Eot)
                {
                    Send(XModemControlBytes.Ack);
                }

                return Result();
            }

            if (_phase == XModemPhase.Cancelled || _phase == XModemPhase.Faulted)
            {
                return Result();
            }

            switch (protocolEvent)
            {
                case XModemEvent.StartRequested _:
                    if (_phase == XModemPhase.Idle)
                    {
                        _phase = XModemPhase.WaitingPacket;
                        RequestStart();
                    }

                    break;
                case XModemEvent.PeerByteReceived peer:
                    HandleControl(peer.Value);
                    break;
                case XModemEvent.PacketReceived packet:
                    _cancelCount = 0;
                    HandlePacket(packet.Packet);
                    break;
                case XModemEvent.DataBlockAccepted _:
                    AcceptBlock();
                    break;
                case XModemEvent.DataBlockRejected _:
                    RejectBlock();
                    break;
                case XModemEvent.TimeoutElapsed _:
                    HandleTimeout();
                    break;
                case XModemEvent.InvalidFrame _:
                    _cancelCount = 0;
                    if (_options.Mode == XModemMode.G)
                    {
                        Fault("XMODEM-G cannot recover from a damaged frame.");
                    }
                    else if (_phase == XModemPhase.WaitingPacket)
                    {
                        // A recognized frame establishes the integrity mode even if its check failed.
                        _receivedFrame = true;
                        RetryNak();
                    }

                    break;
                case XModemEvent.CancelRequested cancel:
                    Cancel(cancel.Reason, true);
                    break;
                default:
                    Fault("Unsupported receiver event.");
                    break;
            }

            while (_phase == XModemPhase.WaitingPacket && _streamingPackets.Count > 0)
            {
                HandlePacket(_streamingPackets.Dequeue());
            }

            return Result();
        }

        private void HandleControl(byte value)
        {
            if (value == XModemControlBytes.Can)
            {
                _cancelCount++;
                if (_cancelCount >= 2)
                {
                    Cancel("Peer cancelled the transfer.", false);
                }
            }
            else
            {
                _cancelCount = 0;
            }
        }

        private void HandlePacket(XModemPacket packet)
        {
            if (_phase == XModemPhase.WaitingDataBlockDecision)
            {
                if (_options.Mode == XModemMode.G)
                {
                    if (_streamingPackets.Count >= _options.MaxBufferedBlocks)
                    {
                        Fault("XMODEM-G pending storage decisions exceeded the buffering limit.");
                    }
                    else
                    {
                        _streamingPackets.Enqueue(packet);
                    }
                }

                // Do not acknowledge a retransmission before the application has persisted the block.
                return;
            }

            if (_phase != XModemPhase.WaitingPacket)
            {
                return;
            }

            if (packet is XModemPacket.Eot)
            {
                _phase = XModemPhase.Completed;
                Send(XModemControlBytes.Ack);
                _actions.Add(new XModemAction.Complete());
                return;
            }

            var data = (XModemPacket.Data)packet;
            _receivedFrame = true;
            if (_integrity == XModemIntegrity.Checksum && data.BlockSize != 128)
            {
                RetryNak();
                return;
            }

            if (_nextBlockNumber > 1 && (data.BlockNumber & 0xFF) == ((_nextBlockNumber - 1) & 0xFF))
            {
                if (_options.Mode == XModemMode.G)
                {
                    Fault("XMODEM-G received a repeated data block.");
                }
                else
                {
                    Send(XModemControlBytes.Ack);
                }

                return;
            }

            if ((data.BlockNumber & 0xFF) != (_nextBlockNumber & 0xFF) || data.DataLength != data.BlockSize)
            {
                RetryNak();
                return;
            }

            _pendingPacket = data;
            _phase = XModemPhase.WaitingDataBlockDecision;
            _actions.Add(new XModemAction.DeliverDataBlock(_nextBlockNumber, data.Payload));
        }

        private void AcceptBlock()
        {
            if (_phase != XModemPhase.WaitingDataBlockDecision || _pendingPacket == null)
            {
                Fault("No data block is awaiting acceptance.");
                return;
            }

            _bytesTransferred += _pendingPacket.DataLength;
            _nextBlockNumber++;
            _retryCount = 0;
            _pendingPacket = null;
            _phase = XModemPhase.WaitingPacket;
            if (_options.Mode != XModemMode.G)
            {
                Send(XModemControlBytes.Ack);
            }
        }

        private void RejectBlock()
        {
            if (_phase != XModemPhase.WaitingDataBlockDecision)
            {
                Fault("No data block is awaiting rejection.");
                return;
            }

            _pendingPacket = null;
            _phase = XModemPhase.WaitingPacket;
            RetryNak();
        }

        private void HandleTimeout()
        {
            _cancelCount = 0;
            if (_options.Mode == XModemMode.G && _receivedFrame && _phase == XModemPhase.WaitingPacket)
            {
                Fault("XMODEM-G data timed out; streaming transfers cannot recover.");
                return;
            }

            if (_phase != XModemPhase.WaitingPacket || !TryRetry())
            {
                return;
            }

            if (!_receivedFrame)
            {
                if (_options.AllowChecksumFallback && _retryCount >= _options.CrcFallbackAttempts)
                {
                    _integrity = XModemIntegrity.Checksum;
                }

                RequestStart();
            }
            else
            {
                Send(XModemControlBytes.Nak);
            }
        }

        private void RequestStart()
        {
            Send(_options.Mode == XModemMode.G ? XModemControlBytes.StreamingRequest
                : _integrity == XModemIntegrity.Crc16 ? XModemControlBytes.CrcRequest : XModemControlBytes.Nak);
        }

        private void RetryNak()
        {
            if (_options.Mode == XModemMode.G)
            {
                Fault("XMODEM-G rejected a data block; streaming transfers cannot recover.");
                return;
            }

            if (TryRetry())
            {
                Send(XModemControlBytes.Nak);
            }
        }

        private bool TryRetry()
        {
            if (_retryCount >= _options.MaxRetries)
            {
                Fault("XMODEM retry limit exceeded.");
                return false;
            }

            _retryCount++;
            return true;
        }

        private void Cancel(string reason, bool notifyPeer)
        {
            _phase = XModemPhase.Cancelled;
            if (notifyPeer)
            {
                SendCancel();
            }

            _actions.Add(new XModemAction.Cancel(reason));
        }

        private void Fault(string reason)
        {
            _phase = XModemPhase.Faulted;
            _failureReason = reason;
            SendCancel();
            _actions.Add(new XModemAction.Fail(reason));
        }

        private void SendCancel()
        {
            Send(XModemControlBytes.Can);
            Send(XModemControlBytes.Can);
        }

        private void Send(byte value)
        {
            _actions.Add(new XModemAction.SendControl(value));
        }

        private XModemStepResult Result()
        {
            return new XModemStepResult(Snapshot, _actions.ToArray());
        }
    }
}
