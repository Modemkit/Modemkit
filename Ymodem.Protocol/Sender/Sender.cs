using System;
using System.Collections.Generic;

namespace Ymodem.Protocol
{
    public sealed class YModemSender
    {
        private readonly YModemBlockOptions _blockOptions;
        private readonly YModemTransferOptions _transferOptions;
        private readonly List<YModemAction> _actions;
        private int _retryCount;
        private YModemIntegrity _integrity;

        private YModemSenderPhase _phase;
        private YModemPacket? _lastPacket;
        private int _nextBlockNumber;
        private bool _lastDataBlockSent;
        private bool _fileHeaderAccepted;
        private long _remainingFileBytes;
        private int _lastAcknowledgedDataLength;
        private int _requestedBlockSize;
        private string? _failureReason;

        public YModemSender()
            : this(new YModemBlockOptions())
        {
        }

        public YModemSender(YModemBlockMode blockMode)
            : this(YModemBlockOptions.FromMode(blockMode))
        {
        }

        public YModemSender(YModemBlockOptions blockOptions)
            : this(blockOptions, new YModemTransferOptions())
        {
        }

        public YModemSender(YModemBlockOptions blockOptions, YModemTransferOptions transferOptions)
        {
            _transferOptions = transferOptions ?? throw new ArgumentNullException(nameof(transferOptions));
            _integrity = transferOptions.Mode == YModemTransferMode.Checksum ? YModemIntegrity.Checksum : YModemIntegrity.Crc16;
            _blockOptions = blockOptions ?? throw new ArgumentNullException(nameof(blockOptions));
            _actions = new List<YModemAction>();
            _phase = YModemSenderPhase.WaitingReceiverRequest;
            _nextBlockNumber = 1;
        }

        public YModemSenderSnapshot Snapshot
        {
            get
            {
                return CreateSnapshot();
            }
        }

        public YModemIntegrity Integrity => _integrity;

        private bool IsStreaming => _transferOptions.Mode == YModemTransferMode.G;

        private byte RequestByte => _integrity == YModemIntegrity.Checksum ? YModemControlBytes.Nak : YModemControlBytes.CrcRequest;

        public YModemStepResult Advance(YModemEvent protocolEvent)
        {
            if (protocolEvent == null)
            {
                throw new ArgumentNullException(nameof(protocolEvent));
            }

            _actions.Clear();

            if (_phase == YModemSenderPhase.Cancelled || _phase == YModemSenderPhase.Completed || _phase == YModemSenderPhase.Faulted)
            {
                return new YModemStepResult(CreateSnapshot(), _actions.ToArray());
            }

            switch (protocolEvent)
            {
                case YModemEvent.TimeoutElapsed _:
                case YModemEvent.InvalidFrameReceived _:
                    HandleTimeout();
                    return new YModemStepResult(CreateSnapshot(), _actions.ToArray());
                case YModemEvent.CancelRequested cancelRequested:
                    _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Cancel send session"));
                    _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Cancel send session"));
                    Cancel(cancelRequested.Reason);
                    return new YModemStepResult(CreateSnapshot(), _actions.ToArray());
                case YModemEvent.FileHeaderReady fileHeaderReady:
                    HandleFileHeaderReady(fileHeaderReady);
                    return new YModemStepResult(CreateSnapshot(), _actions.ToArray());
                case YModemEvent.DataBlockReady dataBlockReady:
                    HandleDataBlockReady(dataBlockReady);
                    return new YModemStepResult(CreateSnapshot(), _actions.ToArray());
                case YModemEvent.PeerByteReceived peerByteReceived:
                    HandlePeerByte(peerByteReceived.Value);
                    return new YModemStepResult(CreateSnapshot(), _actions.ToArray());
                default:
                    Fault("Unsupported protocol event.");
                    return new YModemStepResult(CreateSnapshot(), _actions.ToArray());
            }
        }

        private void HandlePeerByte(byte value)
        {
            if (value == YModemControlBytes.Can)
            {
                Cancel("Peer cancelled the transfer.");
                return;
            }

            if ((value == RequestByte || value == YModemControlBytes.Ack) &&
                (_phase == YModemSenderPhase.WaitingFileHeader || _phase == YModemSenderPhase.WaitingDataBlock))
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
                case YModemSenderPhase.WaitingReceiverRequest:
                    HandleReceiverRequest(value);
                    return;
                case YModemSenderPhase.WaitingHeaderAck:
                    HandleHeaderAck(value);
                    return;
                case YModemSenderPhase.WaitingDataStartRequest:
                    HandleDataStartRequest(value);
                    return;
                case YModemSenderPhase.WaitingBlockAck:
                    HandleBlockAck(value);
                    return;
                case YModemSenderPhase.WaitingFirstEotResponse:
                    HandleFirstEotResponse(value);
                    return;
                case YModemSenderPhase.WaitingSecondEotAck:
                    HandleSecondEotAck(value);
                    return;
                case YModemSenderPhase.WaitingBatchTrailerRequest:
                    HandleBatchTrailerRequest(value);
                    return;
                case YModemSenderPhase.WaitingBatchTrailerAck:
                    HandleBatchTrailerAck(value);
                    return;
                case YModemSenderPhase.WaitingFileHeader:
                case YModemSenderPhase.WaitingDataBlock:
                case YModemSenderPhase.Completed:
                case YModemSenderPhase.Cancelled:
                case YModemSenderPhase.Faulted:
                    break;
                default:
                    throw new InvalidOperationException("Unhandled sender phase: " + _phase + ".");
            }

            Fault("Received an unexpected peer byte.");
        }

        private void HandleReceiverRequest(byte value)
        {
            if (value != YModemControlBytes.CrcRequest && value != YModemControlBytes.Nak)
            {
                Fault("Expected receiver CRC request before starting transfer.");
                return;
            }

            _integrity = value == YModemControlBytes.Nak ? YModemIntegrity.Checksum : YModemIntegrity.Crc16;
            _phase = YModemSenderPhase.WaitingFileHeader;
            _actions.Add(new YModemAction.RequestFileHeader());
        }

        private void HandleHeaderAck(byte value)
        {
            switch (value)
            {
                case YModemControlBytes.Ack:
                    _retryCount = 0;
                    _fileHeaderAccepted = true;
                    _phase = YModemSenderPhase.WaitingDataStartRequest;
                    return;
                case YModemControlBytes.Nak:
                case YModemControlBytes.CrcRequest:
                    ResendLastPacket("Resend file header");
                    return;
                default:
                    Fault("Expected ACK or NAK after file header.");
                    return;
            }
        }

        private void HandleDataStartRequest(byte value)
        {
            if (value == YModemControlBytes.Ack)
            {
                return;
            }

            if (value != RequestByte)
            {
                Fault("Expected receiver CRC request before first data block.");
                return;
            }

            _phase = YModemSenderPhase.WaitingDataBlock;
            _retryCount = 0;
            RequestDataBlock();
        }

        private void HandleBlockAck(byte value)
        {
            if (value == YModemControlBytes.CrcRequest && _nextBlockNumber == 1)
            {
                ResendLastPacket("Resend first data block");
                return;
            }

            switch (value)
            {
                case YModemControlBytes.Ack when _lastDataBlockSent:
                    SendEot();
                    _phase = YModemSenderPhase.WaitingFirstEotResponse;
                    return;
                case YModemControlBytes.Ack:
                    _remainingFileBytes = _remainingFileBytes < 0 ? -1 : Math.Max(0, _remainingFileBytes - _lastAcknowledgedDataLength);
                    _nextBlockNumber++;
                    _phase = YModemSenderPhase.WaitingDataBlock;
                    RequestDataBlock();
                    return;
                case YModemControlBytes.Nak:
                    ResendLastPacket("Resend data block");
                    return;
                default:
                    Fault("Expected ACK or NAK after data block.");
                    return;
            }
        }

        private void HandleFirstEotResponse(byte value)
        {
            if (value == YModemControlBytes.CrcRequest)
            {
                return;
            }

            if (value == YModemControlBytes.Ack)
            {
                _retryCount = 0;
                _phase = YModemSenderPhase.WaitingBatchTrailerRequest;
                return;
            }

            if (value == YModemControlBytes.Nak)
            {
                SendEot();
                _phase = YModemSenderPhase.WaitingSecondEotAck;
                return;
            }

            Fault("Expected NAK after first EOT.");
        }

        private void HandleSecondEotAck(byte value)
        {
            if (value == YModemControlBytes.CrcRequest)
            {
                return;
            }

            switch (value)
            {
                case YModemControlBytes.Ack:
                    _retryCount = 0;
                    _phase = YModemSenderPhase.WaitingBatchTrailerRequest;
                    return;
                case YModemControlBytes.Nak:
                    ResendLastPacket("Resend EOT");
                    return;
                default:
                    Fault("Expected ACK after second EOT.");
                    return;
            }
        }

        private void HandleBatchTrailerRequest(byte value)
        {
            if (value == YModemControlBytes.Ack)
            {
                return;
            }

            if (value != RequestByte)
            {
                Fault("Expected receiver CRC request before batch trailer.");
                return;
            }

            SendBatchTrailer();
            _phase = YModemSenderPhase.WaitingBatchTrailerAck;
        }

        private void HandleBatchTrailerAck(byte value)
        {
            switch (value)
            {
                case YModemControlBytes.Ack:
                    _phase = YModemSenderPhase.Completed;
                    _actions.Add(new YModemAction.Complete());
                    return;
                case YModemControlBytes.Nak:
                case YModemControlBytes.CrcRequest:
                    ResendLastPacket("Resend batch trailer");
                    return;
                default:
                    Fault("Expected ACK or NAK after batch trailer.");
                    return;
            }
        }

        private void HandleFileHeaderReady(YModemEvent.FileHeaderReady protocolEvent)
        {
            if (_phase != YModemSenderPhase.WaitingFileHeader)
            {
                Fault("File header was provided in an invalid state.");
                return;
            }

            var packet = new YModemPacket.Header(protocolEvent.File, YModemBlockSizing.GetHeaderBlockSize(_blockOptions, protocolEvent.File));
            _retryCount = 0;
            _lastPacket = packet;
            _remainingFileBytes = protocolEvent.File.HasFileSize ? protocolEvent.File.FileSize : -1;
            _lastAcknowledgedDataLength = 0;
            _requestedBlockSize = 0;
            _phase = YModemSenderPhase.WaitingHeaderAck;
            _actions.Add(new YModemAction.SendPacket(packet, "Send file header", _integrity));
        }

        private void HandleDataBlockReady(YModemEvent.DataBlockReady protocolEvent)
        {
            if (_phase != YModemSenderPhase.WaitingDataBlock)
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
                _phase = YModemSenderPhase.WaitingFirstEotResponse;
                return;
            }

            var packet = new YModemPacket.Data(protocolEvent.BlockNumber, protocolEvent.Data, protocolEvent.DataLength, _requestedBlockSize);
            _retryCount = 0;
            _lastPacket = packet;
            _lastDataBlockSent = protocolEvent.IsLastBlock;
            _lastAcknowledgedDataLength = protocolEvent.DataLength;
            _phase = YModemSenderPhase.WaitingBlockAck;
            _actions.Add(new YModemAction.SendPacket(packet, protocolEvent.IsLastBlock ? "Send final data block" : "Send data block", _integrity));
            if (IsStreaming)
            {
                _remainingFileBytes = _remainingFileBytes < 0 ? -1 : _remainingFileBytes - protocolEvent.DataLength;
                if (_lastDataBlockSent)
                {
                    SendEot();
                    _phase = YModemSenderPhase.WaitingFirstEotResponse;
                }
                else
                {
                    _nextBlockNumber++;
                    _phase = YModemSenderPhase.WaitingDataBlock;
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

        private void SendBatchTrailer()
        {
            var packet = new YModemPacket.BatchTrailer();
            _retryCount = 0;
            _lastPacket = packet;
            _actions.Add(new YModemAction.SendPacket(packet, "Send batch trailer", _integrity));
            if (IsStreaming)
            {
                _phase = YModemSenderPhase.Completed;
                _actions.Add(new YModemAction.Complete());
            }
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
            if (_phase == YModemSenderPhase.WaitingFileHeader || _phase == YModemSenderPhase.WaitingDataBlock)
            {
                return;
            }

            if (IsStreaming && _phase != YModemSenderPhase.WaitingReceiverRequest &&
                _phase != YModemSenderPhase.WaitingFirstEotResponse && _phase != YModemSenderPhase.WaitingBatchTrailerAck &&
                _phase != YModemSenderPhase.WaitingHeaderAck && _phase != YModemSenderPhase.WaitingSecondEotAck &&
                _phase != YModemSenderPhase.WaitingBatchTrailerRequest)
            {
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Streaming transfer interrupted"));
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Streaming transfer interrupted"));
                Fault("YMODEM-G cannot retransmit streamed data.");
                return;
            }

            if (_phase == YModemSenderPhase.WaitingReceiverRequest)
            {
                BeginRetry();
                return;
            }

            if (_phase == YModemSenderPhase.WaitingDataStartRequest)
            {
                _phase = YModemSenderPhase.WaitingHeaderAck;
            }
            else if (_phase == YModemSenderPhase.WaitingBatchTrailerRequest)
            {
                _phase = YModemSenderPhase.WaitingSecondEotAck;
            }

            ResendLastPacket("Resend after timeout");
        }

        private void HandleStreamingPeerByte(byte value)
        {
            if (value == YModemControlBytes.StreamingRequest &&
                (_phase == YModemSenderPhase.WaitingFileHeader || _phase == YModemSenderPhase.WaitingDataBlock ||
                _phase == YModemSenderPhase.WaitingFirstEotResponse || _phase == YModemSenderPhase.WaitingSecondEotAck))
            {
                return;
            }

            switch (_phase)
            {
                case YModemSenderPhase.WaitingReceiverRequest:
                    if (value == YModemControlBytes.StreamingRequest)
                    {
                        _retryCount = 0;
                        _phase = YModemSenderPhase.WaitingFileHeader;
                        _actions.Add(new YModemAction.RequestFileHeader());
                        return;
                    }

                    break;
                case YModemSenderPhase.WaitingHeaderAck:
                    if (value == YModemControlBytes.StreamingRequest)
                    {
                        _retryCount = 0;
                        _fileHeaderAccepted = true;
                        _phase = YModemSenderPhase.WaitingDataBlock;
                        RequestDataBlock();
                        return;
                    }

                    break;
                case YModemSenderPhase.WaitingFirstEotResponse:
                case YModemSenderPhase.WaitingSecondEotAck:
                    if (value == YModemControlBytes.Ack)
                    {
                        _retryCount = 0;
                        _phase = YModemSenderPhase.WaitingBatchTrailerRequest;
                        return;
                    }

                    if (value == YModemControlBytes.Nak)
                    {
                        ResendLastPacket("Resend EOT");
                        return;
                    }

                    break;
                case YModemSenderPhase.WaitingBatchTrailerRequest:
                    if (value == YModemControlBytes.StreamingRequest)
                    {
                        _retryCount = 0;
                        SendBatchTrailer();
                        return;
                    }

                    break;
                case YModemSenderPhase.WaitingBatchTrailerAck:
                    if (value == YModemControlBytes.Ack)
                    {
                        _phase = YModemSenderPhase.Completed;
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
            _phase = YModemSenderPhase.Cancelled;
            _actions.Add(new YModemAction.Cancel(reason));
        }

        private void Fault(string reason)
        {
            _phase = YModemSenderPhase.Faulted;
            _failureReason = reason;
            _actions.Add(new YModemAction.Fail(reason));
        }

        private YModemSenderSnapshot CreateSnapshot()
        {
            return new YModemSenderSnapshot(_phase, _nextBlockNumber, _fileHeaderAccepted, _lastDataBlockSent, _failureReason);
        }
    }
}
