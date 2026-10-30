using System;
using System.Collections.Generic;

namespace Ymodem.Protocol
{
    public sealed class YModemBatchSender
    {
        private readonly YModemBlockOptions _blockOptions;
        private readonly YModemTransferOptions _transferOptions;
        private readonly List<YModemAction> _actions;
        private int _retryCount;
        private YModemIntegrity _integrity;

        private YModemBatchSenderPhase _phase;
        private YModemPacket? _lastPacket;
        private int _nextBlockNumber;
        private bool _lastDataBlockSent;
        private long _remainingFileBytes;
        private int _lastAcknowledgedDataLength;
        private int _requestedBlockSize;
        private string? _failureReason;

        public YModemBatchSender()
            : this(new YModemBlockOptions())
        {
        }

        public YModemBatchSender(YModemBlockMode blockMode)
            : this(YModemBlockOptions.FromMode(blockMode))
        {
        }

        public YModemBatchSender(YModemBlockOptions blockOptions)
            : this(blockOptions, new YModemTransferOptions())
        {
        }

        public YModemBatchSender(YModemBlockOptions blockOptions, YModemTransferOptions transferOptions)
        {
            _transferOptions = transferOptions ?? throw new ArgumentNullException(nameof(transferOptions));
            _integrity = transferOptions.Mode == YModemTransferMode.Checksum ? YModemIntegrity.Checksum : YModemIntegrity.Crc16;
            _blockOptions = blockOptions ?? throw new ArgumentNullException(nameof(blockOptions));
            _actions = new List<YModemAction>();
            _phase = YModemBatchSenderPhase.WaitingReceiverRequest;
            _nextBlockNumber = 1;
        }

        public YModemBatchSenderSnapshot Snapshot => CreateSnapshot();

        public YModemIntegrity Integrity => _integrity;

        private byte RequestByte => _integrity == YModemIntegrity.Checksum ? YModemControlBytes.Nak : YModemControlBytes.CrcRequest;

        private bool IsStreaming => _transferOptions.Mode == YModemTransferMode.G;

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
                case YModemEvent.TimeoutElapsed _:
                case YModemEvent.InvalidFrameReceived _:
                    HandleTimeout();
                    return new YModemBatchStepResult(CreateSnapshot(), _actions.ToArray());
                case YModemEvent.CancelRequested cancelRequested:
                    _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Cancel send session"));
                    _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Cancel send session"));
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

            if ((value == RequestByte || value == YModemControlBytes.Ack) &&
                (_phase == YModemBatchSenderPhase.WaitingFileHeader || _phase == YModemBatchSenderPhase.WaitingDataBlock))
            {
                return;
            }

            if (IsStreaming)
            {
                HandleStreamingPeerByte(value);
                return;
            }

            switch (_phase)
            {
                case YModemBatchSenderPhase.WaitingReceiverRequest:
                    if (value != YModemControlBytes.CrcRequest && value != YModemControlBytes.Nak)
                    {
                        Fault("Expected receiver CRC request before starting transfer.");
                        return;
                    }

                    _integrity = value == YModemControlBytes.Nak ? YModemIntegrity.Checksum : YModemIntegrity.Crc16;
                    _phase = YModemBatchSenderPhase.WaitingFileHeader;
                    _actions.Add(new YModemAction.RequestFileHeader());
                    return;
                case YModemBatchSenderPhase.WaitingHeaderAck:
                    if (value == YModemControlBytes.Ack)
                    {
                        _retryCount = 0;
                        _phase = YModemBatchSenderPhase.WaitingDataStartRequest;
                        return;
                    }

                    if (value == YModemControlBytes.Nak || value == YModemControlBytes.CrcRequest)
                    {
                        ResendLastPacket("Resend file header");
                        return;
                    }

                    Fault("Expected ACK or NAK after file header.");
                    return;
                case YModemBatchSenderPhase.WaitingDataStartRequest:
                    if (value == YModemControlBytes.Ack)
                    {
                        return;
                    }

                    if (value != RequestByte)
                    {
                        Fault("Expected receiver CRC request before first data block.");
                        return;
                    }

                    _phase = YModemBatchSenderPhase.WaitingDataBlock;
                    RequestDataBlock();
                    return;
                case YModemBatchSenderPhase.WaitingBlockAck:
                    if (value == YModemControlBytes.CrcRequest && _nextBlockNumber == 1)
                    {
                        ResendLastPacket("Resend first data block");
                        return;
                    }

                    if (value == YModemControlBytes.Ack)
                    {
                        if (_lastDataBlockSent)
                        {
                            SendEot();
                            _phase = YModemBatchSenderPhase.WaitingFirstEotResponse;
                            return;
                        }

                        _remainingFileBytes = _remainingFileBytes < 0 ? -1 : Math.Max(0, _remainingFileBytes - _lastAcknowledgedDataLength);
                        _nextBlockNumber++;
                        _phase = YModemBatchSenderPhase.WaitingDataBlock;
                        RequestDataBlock();
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
                    if (value == YModemControlBytes.CrcRequest)
                    {
                        return;
                    }

                    if (value == YModemControlBytes.Ack)
                    {
                        _retryCount = 0;
                        _phase = YModemBatchSenderPhase.WaitingNextHeaderRequest;
                        return;
                    }

                    if (value == YModemControlBytes.Nak)
                    {
                        SendEot();
                        _phase = YModemBatchSenderPhase.WaitingSecondEotAck;
                        return;
                    }

                    Fault("Expected NAK after first EOT.");
                    return;
                case YModemBatchSenderPhase.WaitingSecondEotAck:
                    if (value == YModemControlBytes.CrcRequest)
                    {
                        return;
                    }

                    if (value == YModemControlBytes.Ack)
                    {
                        _retryCount = 0;
                        _phase = YModemBatchSenderPhase.WaitingNextHeaderRequest;
                        return;
                    }

                    if (value == YModemControlBytes.Nak)
                    {
                        ResendLastPacket("Resend EOT");
                        return;
                    }

                    Fault("Expected ACK after second EOT.");
                    return;
                case YModemBatchSenderPhase.WaitingNextHeaderRequest:
                    if (value == YModemControlBytes.Ack)
                    {
                        return;
                    }

                    if (value != RequestByte)
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

                    if (value == YModemControlBytes.Nak || value == YModemControlBytes.CrcRequest)
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

            var packet = new YModemPacket.Header(protocolEvent.File, YModemBlockSizing.GetHeaderBlockSize(_blockOptions, protocolEvent.File));
            _retryCount = 0;
            _lastPacket = packet;
            _lastDataBlockSent = false;
            _remainingFileBytes = protocolEvent.File.HasFileSize ? protocolEvent.File.FileSize : -1;
            _lastAcknowledgedDataLength = 0;
            _requestedBlockSize = 0;
            _nextBlockNumber = 1;
            _phase = YModemBatchSenderPhase.WaitingHeaderAck;
            _actions.Add(new YModemAction.SendPacket(packet, "Send file header", _integrity));
        }

        private void HandleNoMoreFiles()
        {
            if (_phase != YModemBatchSenderPhase.WaitingFileHeader)
            {
                Fault("Batch completion was provided in an invalid state.");
                return;
            }

            var packet = new YModemPacket.BatchTrailer();
            _retryCount = 0;
            _lastPacket = packet;
            _phase = YModemBatchSenderPhase.WaitingBatchTrailerAck;
            _actions.Add(new YModemAction.SendPacket(packet, "Send batch trailer", _integrity));
            if (IsStreaming)
            {
                _phase = YModemBatchSenderPhase.Completed;
                _actions.Add(new YModemAction.Complete());
            }
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

            if (protocolEvent.DataLength > _requestedBlockSize)
            {
                Fault("Data block is larger than the requested packet size.");
                return;
            }

            if ((_remainingFileBytes >= 0 && (protocolEvent.DataLength > _remainingFileBytes ||
                protocolEvent.IsLastBlock != (protocolEvent.DataLength == _remainingFileBytes))) ||
                (!protocolEvent.IsLastBlock && protocolEvent.DataLength != _requestedBlockSize))
            {
                Fault("Data block does not match the advertised file length.");
                return;
            }

            if (_remainingFileBytes == 0 || (protocolEvent.DataLength == 0 && protocolEvent.IsLastBlock))
            {
                SendEot();
                _phase = YModemBatchSenderPhase.WaitingFirstEotResponse;
                return;
            }

            var packet = new YModemPacket.Data(protocolEvent.BlockNumber, protocolEvent.Data, protocolEvent.DataLength, _requestedBlockSize);
            _retryCount = 0;
            _lastPacket = packet;
            _lastDataBlockSent = protocolEvent.IsLastBlock;
            _lastAcknowledgedDataLength = protocolEvent.DataLength;
            _phase = YModemBatchSenderPhase.WaitingBlockAck;
            _actions.Add(new YModemAction.SendPacket(packet, protocolEvent.IsLastBlock ? "Send final data block" : "Send data block", _integrity));
            if (IsStreaming)
            {
                _remainingFileBytes = _remainingFileBytes < 0 ? -1 : _remainingFileBytes - protocolEvent.DataLength;
                if (_lastDataBlockSent)
                {
                    SendEot();
                    _phase = YModemBatchSenderPhase.WaitingFirstEotResponse;
                }
                else
                {
                    _nextBlockNumber++;
                    _phase = YModemBatchSenderPhase.WaitingDataBlock;
                    RequestDataBlock();
                }
            }
        }

        private void SendEot()
        {
            var packet = new YModemPacket.Eot();
            _retryCount = 0;
            _lastPacket = packet;
            _actions.Add(new YModemAction.SendPacket(packet, "Send EOT", _integrity));
        }

        private int GetNextBlockSize()
        {
            return YModemBlockSizing.GetDataBlockSize(_blockOptions, _remainingFileBytes < 0 ? long.MaxValue : _remainingFileBytes);
        }

        private void RequestDataBlock()
        {
            _requestedBlockSize = GetNextBlockSize();
            _actions.Add(new YModemAction.RequestDataBlock(_nextBlockNumber, _requestedBlockSize));
        }

        private void ResendLastPacket(string description)
        {
            if (!BeginRetry())
            {
                return;
            }

            if (_lastPacket == null)
            {
                Fault("No packet is available for retransmission.");
                return;
            }

            _actions.Add(new YModemAction.SendPacket(_lastPacket, description, _integrity));
        }

        private bool BeginRetry()
        {
            if (_retryCount++ >= _transferOptions.MaxRetries)
            {
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Retry limit reached"));
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Retry limit reached"));
                Fault("Retry limit exceeded.");
                return false;
            }

            return true;
        }

        private void HandleTimeout()
        {
            if (_phase == YModemBatchSenderPhase.WaitingFileHeader || _phase == YModemBatchSenderPhase.WaitingDataBlock)
            {
                return;
            }

            if (IsStreaming && _phase != YModemBatchSenderPhase.WaitingReceiverRequest &&
                _phase != YModemBatchSenderPhase.WaitingFirstEotResponse && _phase != YModemBatchSenderPhase.WaitingBatchTrailerAck &&
                _phase != YModemBatchSenderPhase.WaitingHeaderAck && _phase != YModemBatchSenderPhase.WaitingSecondEotAck &&
                _phase != YModemBatchSenderPhase.WaitingNextHeaderRequest)
            {
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Streaming transfer interrupted"));
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Streaming transfer interrupted"));
                Fault("YMODEM-G cannot retransmit streamed data.");
                return;
            }

            if (_phase == YModemBatchSenderPhase.WaitingReceiverRequest)
            {
                BeginRetry();
                return;
            }

            if (_phase == YModemBatchSenderPhase.WaitingDataStartRequest)
            {
                _phase = YModemBatchSenderPhase.WaitingHeaderAck;
            }
            else if (_phase == YModemBatchSenderPhase.WaitingNextHeaderRequest)
            {
                _phase = YModemBatchSenderPhase.WaitingSecondEotAck;
            }

            ResendLastPacket("Resend after timeout");
        }

        private void HandleStreamingPeerByte(byte value)
        {
            if (value == YModemControlBytes.StreamingRequest &&
                (_phase == YModemBatchSenderPhase.WaitingFileHeader || _phase == YModemBatchSenderPhase.WaitingDataBlock ||
                _phase == YModemBatchSenderPhase.WaitingFirstEotResponse || _phase == YModemBatchSenderPhase.WaitingSecondEotAck))
            {
                return;
            }

            switch (_phase)
            {
                case YModemBatchSenderPhase.WaitingReceiverRequest:
                    if (value == YModemControlBytes.StreamingRequest)
                    {
                        _retryCount = 0;
                        _phase = YModemBatchSenderPhase.WaitingFileHeader;
                        _actions.Add(new YModemAction.RequestFileHeader());
                        return;
                    }

                    break;
                case YModemBatchSenderPhase.WaitingHeaderAck:
                    if (value == YModemControlBytes.StreamingRequest)
                    {
                        _retryCount = 0;

                        _phase = YModemBatchSenderPhase.WaitingDataBlock;
                        RequestDataBlock();
                        return;
                    }

                    break;
                case YModemBatchSenderPhase.WaitingFirstEotResponse:
                case YModemBatchSenderPhase.WaitingSecondEotAck:
                    if (value == YModemControlBytes.Ack)
                    {
                        _retryCount = 0;
                        _phase = YModemBatchSenderPhase.WaitingNextHeaderRequest;
                        return;
                    }

                    if (value == YModemControlBytes.Nak)
                    {
                        ResendLastPacket("Resend EOT");
                        return;
                    }

                    break;
                case YModemBatchSenderPhase.WaitingNextHeaderRequest:
                    if (value == YModemControlBytes.StreamingRequest)
                    {
                        _retryCount = 0;
                        _phase = YModemBatchSenderPhase.WaitingFileHeader;
                        _actions.Add(new YModemAction.RequestFileHeader());
                        return;
                    }

                    break;
                case YModemBatchSenderPhase.WaitingBatchTrailerAck:
                    if (value == YModemControlBytes.Ack)
                    {
                        _phase = YModemBatchSenderPhase.Completed;
                        _actions.Add(new YModemAction.Complete());
                        return;
                    }

                    break;
            }

            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Streaming transfer interrupted"));
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Streaming transfer interrupted"));
            Fault("Unexpected response during YMODEM-G transfer.");
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
