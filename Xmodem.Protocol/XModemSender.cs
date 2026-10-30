using System;
using System.Collections.Generic;

namespace Xmodem.Protocol
{
    public sealed class XModemSender
    {
        private readonly XModemOptions _options;
        private readonly List<XModemAction> _actions = new List<XModemAction>();
        private XModemPhase _phase = XModemPhase.WaitingReceiverRequest;
        private XModemIntegrity _integrity;
        private int _nextBlockNumber = 1;
        private int _retryCount;
        private int _cancelCount;
        private int _requestedSize;
        private long _bytesTransferred;
        private bool _lastBlock;
        private XModemPacket? _lastPacket;
        private string? _failureReason;

        public XModemSender(XModemOptions? options = null)
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
            if (IsTerminal())
            {
                return Result();
            }

            switch (protocolEvent)
            {
                case XModemEvent.StartRequested _:
                    break;
                case XModemEvent.CancelRequested cancel:
                    Cancel(cancel.Reason, true);
                    break;
                case XModemEvent.PeerByteReceived peer:
                    HandlePeerByte(peer.Value);
                    break;
                case XModemEvent.DataBlockReady data:
                    HandleData(data);
                    break;
                case XModemEvent.TimeoutElapsed _:
                    HandleTimeout();
                    break;
                case XModemEvent.InvalidFrame _:
                    if (_options.Mode == XModemMode.G)
                    {
                        Fault("XMODEM-G cannot recover from an invalid frame.");
                    }
                    else if (_phase == XModemPhase.WaitingBlockAck || _phase == XModemPhase.WaitingEotAck)
                    {
                        RetryPacket();
                    }

                    break;
                default:
                    Fault("Unsupported sender event.");
                    break;
            }

            return Result();
        }

        private void HandlePeerByte(byte value)
        {
            if (value == XModemControlBytes.Can)
            {
                _cancelCount++;
                if (_cancelCount >= 2)
                {
                    Cancel("Peer cancelled the transfer.", false);
                }

                return;
            }

            _cancelCount = 0;
            if (_phase == XModemPhase.WaitingReceiverRequest)
            {
                if (_options.Mode == XModemMode.G)
                {
                    if (value == XModemControlBytes.StreamingRequest)
                    {
                        _integrity = XModemIntegrity.Crc16;
                        RequestBlock();
                    }
                }
                else if (value == XModemControlBytes.CrcRequest && _options.Mode != XModemMode.Checksum)
                {
                    _integrity = XModemIntegrity.Crc16;
                    RequestBlock();
                }
                else if (value == XModemControlBytes.Nak)
                {
                    _integrity = XModemIntegrity.Checksum;
                    RequestBlock();
                }

                return;
            }

            if (_options.Mode == XModemMode.G && _phase != XModemPhase.WaitingEotAck)
            {
                if (value == XModemControlBytes.Nak)
                {
                    Fault("Peer rejected XMODEM-G data; streaming transfers cannot retransmit.");
                }

                return;
            }

            if (_phase != XModemPhase.WaitingBlockAck && _phase != XModemPhase.WaitingEotAck)
            {
                return;
            }

            if (value == XModemControlBytes.Nak)
            {
                RetryPacket();
                return;
            }

            if (value != XModemControlBytes.Ack)
            {
                return;
            }

            _retryCount = 0;
            if (_phase == XModemPhase.WaitingEotAck)
            {
                _phase = XModemPhase.Completed;
                _actions.Add(new XModemAction.Complete());
                return;
            }

            _bytesTransferred += ((XModemPacket.Data)_lastPacket!).DataLength;
            _nextBlockNumber++;
            if (_lastBlock)
            {
                SendEot();
            }
            else
            {
                RequestBlock();
            }
        }

        private void HandleData(XModemEvent.DataBlockReady data)
        {
            if (_phase != XModemPhase.WaitingDataBlock || data.BlockNumber != _nextBlockNumber)
            {
                Fault("Data was provided for an unexpected block.");
                return;
            }

            if (data.DataLength > _requestedSize || (!data.IsLastBlock && data.DataLength != _requestedSize))
            {
                Fault("A non-final block must fill the requested size; a final block must not exceed it.");
                return;
            }

            _lastBlock = data.IsLastBlock;
            if (data.DataLength == 0)
            {
                SendEot();
                return;
            }

            var blockSize = _requestedSize == 1024 && data.DataLength <= 128 && data.IsLastBlock ? 128 : _requestedSize;
            _lastPacket = new XModemPacket.Data(data.BlockNumber, data.Data, data.DataLength, blockSize);
            _phase = XModemPhase.WaitingBlockAck;
            _actions.Add(new XModemAction.SendPacket(_lastPacket, _integrity));
            if (_options.Mode == XModemMode.G)
            {
                _bytesTransferred += data.DataLength;
                _nextBlockNumber++;
                if (_lastBlock)
                {
                    SendEot();
                }
                else
                {
                    RequestBlock();
                }
            }
        }

        private void RequestBlock()
        {
            _retryCount = 0;
            _requestedSize = (_options.Mode == XModemMode.OneK || _options.Mode == XModemMode.G)
                && _integrity == XModemIntegrity.Crc16 ? 1024 : 128;
            _phase = XModemPhase.WaitingDataBlock;
            _actions.Add(new XModemAction.RequestDataBlock(_nextBlockNumber, _requestedSize));
        }

        private void SendEot()
        {
            _lastPacket = new XModemPacket.Eot();
            _phase = XModemPhase.WaitingEotAck;
            _retryCount = 0;
            _actions.Add(new XModemAction.SendPacket(_lastPacket, _integrity));
        }

        private void HandleTimeout()
        {
            _cancelCount = 0;
            if (_phase == XModemPhase.WaitingReceiverRequest)
            {
                TryRetry();
            }
            else if (_phase == XModemPhase.WaitingBlockAck || _phase == XModemPhase.WaitingEotAck)
            {
                RetryPacket();
            }
        }

        private void RetryPacket()
        {
            if (TryRetry() && _lastPacket != null)
            {
                _actions.Add(new XModemAction.SendPacket(_lastPacket, _integrity));
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
            _actions.Add(new XModemAction.SendControl(XModemControlBytes.Can));
            _actions.Add(new XModemAction.SendControl(XModemControlBytes.Can));
        }

        private bool IsTerminal()
        {
            return _phase == XModemPhase.Completed || _phase == XModemPhase.Cancelled || _phase == XModemPhase.Faulted;
        }

        private XModemStepResult Result()
        {
            return new XModemStepResult(Snapshot, _actions.ToArray());
        }
    }
}
