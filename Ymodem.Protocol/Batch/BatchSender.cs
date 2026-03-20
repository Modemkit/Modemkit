using System;
using System.Collections.Generic;

namespace Ymodem.Protocol
{
    public sealed class YModemBatchSender
    {
        private readonly int _dataBlockSize;
        private readonly List<YModemAction> _actions;

        private YModemBatchSenderPhase _phase;
        private YModemPacket? _lastPacket;
        private int _nextBlockNumber;
        private bool _lastDataBlockSent;
        private long _remainingFileBytes;
        private int _lastAcknowledgedDataLength;
        private bool _shortTailBlockEnabled;
        private string? _failureReason;

        public YModemBatchSender(int dataBlockSize = 1024)
        {
            if (dataBlockSize != 128 && dataBlockSize != 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(dataBlockSize), "YMODEM block size must be 128 or 1024 bytes.");
            }

            _dataBlockSize = dataBlockSize;
            _actions = new List<YModemAction>();
            _phase = YModemBatchSenderPhase.WaitingReceiverRequest;
            _nextBlockNumber = 1;
        }

        public YModemBatchStepResult Advance(YModemEvent protocolEvent)
        {
            if (protocolEvent == null)
            {
                throw new ArgumentNullException(nameof(protocolEvent));
            }

            _actions.Clear();

            if (_phase == YModemBatchSenderPhase.Cancelled || _phase == YModemBatchSenderPhase.Completed || _phase == YModemBatchSenderPhase.Faulted)
            {
                return new YModemBatchStepResult(CreateSnapshot(), _actions.ToArray());
            }

            switch (protocolEvent)
            {
                case YModemEvent.CancelRequested cancelRequested:
                    Cancel(cancelRequested.Reason);
                    break;
                case YModemEvent.FileHeaderReady fileHeaderReady:
                    HandleFileHeaderReady(fileHeaderReady);
                    break;
                case YModemEvent.NoMoreFiles _:
                    HandleNoMoreFiles();
                    break;
                case YModemEvent.DataBlockReady dataBlockReady:
                    HandleDataBlockReady(dataBlockReady);
                    break;
                case YModemEvent.PeerByteReceived peerByteReceived:
                    HandlePeerByte(peerByteReceived.Value);
                    break;
                default:
                    Fault("Unsupported protocol event.");
                    break;
            }

            return new YModemBatchStepResult(CreateSnapshot(), _actions.ToArray());
        }

        private void HandlePeerByte(byte value)
        {
            if (value == YModemControlBytes.Can)
            {
                Cancel("Peer cancelled the transfer.");
                return;
            }

            switch (_phase)
            {
                case YModemBatchSenderPhase.WaitingReceiverRequest:
                    if (value != YModemControlBytes.CrcRequest)
                    {
                        Fault("Expected receiver CRC request before starting transfer.");
                        return;
                    }

                    _phase = YModemBatchSenderPhase.WaitingFileHeader;
                    _actions.Add(new YModemAction.RequestFileHeader());
                    return;
                case YModemBatchSenderPhase.WaitingHeaderAck:
                    if (value == YModemControlBytes.Ack)
                    {
                        _phase = YModemBatchSenderPhase.WaitingDataStartRequest;
                        return;
                    }

                    if (value == YModemControlBytes.Nak)
                    {
                        ResendLastPacket("Resend file header");
                        return;
                    }

                    Fault("Expected ACK or NAK after file header.");
                    return;
                case YModemBatchSenderPhase.WaitingDataStartRequest:
                    if (value != YModemControlBytes.CrcRequest)
                    {
                        Fault("Expected receiver CRC request before first data block.");
                        return;
                    }

                    _phase = YModemBatchSenderPhase.WaitingDataBlock;
                    _actions.Add(new YModemAction.RequestDataBlock(_nextBlockNumber, GetNextBlockSize()));
                    return;
                case YModemBatchSenderPhase.WaitingBlockAck:
                    if (value == YModemControlBytes.Ack)
                    {
                        if (_lastDataBlockSent)
                        {
                            SendEot();
                            _phase = YModemBatchSenderPhase.WaitingFirstEotResponse;
                            return;
                        }

                        _remainingFileBytes = Math.Max(0, _remainingFileBytes - _lastAcknowledgedDataLength);
                        _shortTailBlockEnabled = _shortTailBlockEnabled || (_dataBlockSize == 1024 && _lastAcknowledgedDataLength == 1024);
                        _nextBlockNumber++;
                        _phase = YModemBatchSenderPhase.WaitingDataBlock;
                        _actions.Add(new YModemAction.RequestDataBlock(_nextBlockNumber, GetNextBlockSize()));
                        return;
                    }

                    if (value == YModemControlBytes.Nak)
                    {
                        ResendLastPacket("Resend data block");
                        return;
                    }

                    Fault("Expected ACK or NAK after data block.");
                    return;
                case YModemBatchSenderPhase.WaitingFirstEotResponse:
                    if (value == YModemControlBytes.Nak)
                    {
                        SendEot();
                        _phase = YModemBatchSenderPhase.WaitingSecondEotAck;
                        return;
                    }

                    Fault("Expected NAK after first EOT.");
                    return;
                case YModemBatchSenderPhase.WaitingSecondEotAck:
                    if (value == YModemControlBytes.Ack)
                    {
                        _phase = YModemBatchSenderPhase.WaitingNextHeaderRequest;
                        return;
                    }

                    if (value == YModemControlBytes.Nak)
                    {
                        SendEot();
                        return;
                    }

                    Fault("Expected ACK after second EOT.");
                    return;
                case YModemBatchSenderPhase.WaitingNextHeaderRequest:
                    if (value != YModemControlBytes.CrcRequest)
                    {
                        Fault("Expected receiver CRC request before next file header or trailer.");
                        return;
                    }

                    _phase = YModemBatchSenderPhase.WaitingFileHeader;
                    _actions.Add(new YModemAction.RequestFileHeader());
                    return;
                case YModemBatchSenderPhase.WaitingBatchTrailerAck:
                    if (value == YModemControlBytes.Ack)
                    {
                        _phase = YModemBatchSenderPhase.Completed;
                        _actions.Add(new YModemAction.Complete());
                        return;
                    }

                    if (value == YModemControlBytes.Nak)
                    {
                        ResendLastPacket("Resend batch trailer");
                        return;
                    }

                    Fault("Expected ACK or NAK after batch trailer.");
                    return;
                default:
                    Fault("Received an unexpected peer byte.");
                    return;
            }
        }

        private void HandleFileHeaderReady(YModemEvent.FileHeaderReady protocolEvent)
        {
            if (_phase != YModemBatchSenderPhase.WaitingFileHeader)
            {
                Fault("File header was provided in an invalid state.");
                return;
            }

            var packet = new YModemPacket.Header(protocolEvent.File);
            _lastPacket = packet;
            _lastDataBlockSent = false;
            _remainingFileBytes = protocolEvent.File.FileSize;
            _lastAcknowledgedDataLength = 0;
            _shortTailBlockEnabled = false;
            _nextBlockNumber = 1;
            _phase = YModemBatchSenderPhase.WaitingHeaderAck;
            _actions.Add(new YModemAction.SendPacket(packet, "Send file header"));
        }

        private void HandleNoMoreFiles()
        {
            if (_phase != YModemBatchSenderPhase.WaitingFileHeader)
            {
                Fault("Batch completion was provided in an invalid state.");
                return;
            }

            var packet = new YModemPacket.BatchTrailer();
            _lastPacket = packet;
            _phase = YModemBatchSenderPhase.WaitingBatchTrailerAck;
            _actions.Add(new YModemAction.SendPacket(packet, "Send batch trailer"));
        }

        private void HandleDataBlockReady(YModemEvent.DataBlockReady protocolEvent)
        {
            if (_phase != YModemBatchSenderPhase.WaitingDataBlock)
            {
                Fault("Data block was provided in an invalid state.");
                return;
            }

            if (protocolEvent.BlockNumber != _nextBlockNumber)
            {
                Fault("Unexpected data block number.");
                return;
            }

            if (protocolEvent.DataLength > _dataBlockSize)
            {
                Fault("Data block is larger than the configured packet size.");
                return;
            }

            var packet = new YModemPacket.Data(protocolEvent.BlockNumber, protocolEvent.Data, protocolEvent.DataLength);
            _lastPacket = packet;
            _lastDataBlockSent = protocolEvent.IsLastBlock;
            _lastAcknowledgedDataLength = protocolEvent.DataLength;
            _phase = YModemBatchSenderPhase.WaitingBlockAck;
            _actions.Add(new YModemAction.SendPacket(packet, protocolEvent.IsLastBlock ? "Send final data block" : "Send data block"));
        }

        private void SendEot()
        {
            var packet = new YModemPacket.Eot();
            _lastPacket = packet;
            _actions.Add(new YModemAction.SendPacket(packet, "Send EOT"));
        }

        private int GetNextBlockSize()
        {
            if (_dataBlockSize == 1024)
            {
                if (_remainingFileBytes <= 128)
                {
                    return 128;
                }

                if (_shortTailBlockEnabled && _remainingFileBytes < 1024)
                {
                    return 128;
                }
            }

            return _dataBlockSize;
        }

        private void ResendLastPacket(string description)
        {
            if (_lastPacket == null)
            {
                Fault("No packet is available for retransmission.");
                return;
            }

            _actions.Add(new YModemAction.SendPacket(_lastPacket, description));
        }

        private void Cancel(string reason)
        {
            _phase = YModemBatchSenderPhase.Cancelled;
            _actions.Add(new YModemAction.Cancel(reason));
        }

        private void Fault(string reason)
        {
            _phase = YModemBatchSenderPhase.Faulted;
            _failureReason = reason;
            _actions.Add(new YModemAction.Fail(reason));
        }

        private YModemBatchSenderSnapshot CreateSnapshot()
        {
            return new YModemBatchSenderSnapshot(_phase, _nextBlockNumber, _failureReason);
        }
    }
}
