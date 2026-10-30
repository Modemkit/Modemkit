using System;
using System.Collections.Generic;

namespace Ymodem.Protocol
{
    public sealed class YModemReceiver
    {
        private readonly List<YModemAction> _actions;
        private readonly YModemTransferOptions _transferOptions;
        private int _retryCount;
        private bool _fileEnded;

        private YModemReceiverPhase _phase;
        private YModemFileDescriptor? _currentFile;
        private int _nextBlockNumber;
        private long _remainingFileBytes;
        private YModemPacket.Data? _pendingDataPacket;
        private string? _failureReason;

        public YModemReceiver()
            : this(new YModemTransferOptions())
        {
        }

        public YModemReceiver(YModemTransferOptions transferOptions)
        {
            _transferOptions = transferOptions ?? throw new ArgumentNullException(nameof(transferOptions));
            _actions = new List<YModemAction>();
            _phase = YModemReceiverPhase.Idle;
            _nextBlockNumber = 1;
        }

        public YModemReceiverSnapshot Snapshot
        {
            get
            {
                return CreateSnapshot();
            }
        }

        public YModemIntegrity Integrity => _transferOptions.Mode == YModemTransferMode.Checksum ? YModemIntegrity.Checksum : YModemIntegrity.Crc16;

        private bool IsStreaming => _transferOptions.Mode == YModemTransferMode.G;

        private byte RequestByte => IsStreaming ? YModemControlBytes.StreamingRequest :
            (Integrity == YModemIntegrity.Checksum ? YModemControlBytes.Nak : YModemControlBytes.CrcRequest);

        public YModemReceiveStepResult Advance(YModemEvent protocolEvent)
        {
            if (protocolEvent == null)
            {
                throw new ArgumentNullException(nameof(protocolEvent));
            }

            _actions.Clear();

            if (!IsStreaming && _phase == YModemReceiverPhase.Completed && protocolEvent is YModemEvent.PacketReceived repeatedTrailer && repeatedTrailer.Packet is YModemPacket.BatchTrailer)
            {
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge repeated batch trailer"));
            }

            if (_phase == YModemReceiverPhase.Cancelled || _phase == YModemReceiverPhase.Completed || _phase == YModemReceiverPhase.Faulted)
            {
                return new YModemReceiveStepResult(CreateSnapshot(), _actions.ToArray());
            }

            switch (protocolEvent)
            {
                case YModemEvent.TimeoutElapsed _:
                    HandleTimeout();
                    break;
                case YModemEvent.InvalidFrameReceived invalidFrame:
                    RejectFrame(invalidFrame.Reason);
                    break;
                case YModemEvent.StartRequested _:
                    HandleStartRequested();
                    break;
                case YModemEvent.PacketReceived packetReceived:
                    HandlePacketReceived(packetReceived.Packet);
                    break;
                case YModemEvent.FileHeaderAccepted _:
                    HandleFileHeaderAccepted();
                    break;
                case YModemEvent.FileHeaderRejected fileHeaderRejected:
                    HandleFileHeaderRejected(fileHeaderRejected.Reason);
                    break;
                case YModemEvent.DataBlockAccepted _:
                    HandleDataBlockAccepted();
                    break;
                case YModemEvent.DataBlockRejected _:
                    HandleDataBlockRejected();
                    break;
                case YModemEvent.CancelRequested cancelRequested:
                    Cancel(cancelRequested.Reason);
                    break;
                default:
                    Fault("Unsupported protocol event.");
                    break;
            }

            return new YModemReceiveStepResult(CreateSnapshot(), _actions.ToArray());
        }

        private void HandleStartRequested()
        {
            if (_phase != YModemReceiverPhase.Idle)
            {
                Fault("Receive session was started in an invalid state.");
                return;
            }

            _phase = YModemReceiverPhase.WaitingFileHeaderPacket;
            _actions.Add(new YModemAction.SendControl(RequestByte, "Request file header"));
        }

        private void HandlePacketReceived(YModemPacket packet)
        {
            if (_fileEnded && _phase == YModemReceiverPhase.WaitingBatchTrailer && packet is YModemPacket.Eot)
            {
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge repeated EOT"));
                _actions.Add(new YModemAction.SendControl(RequestByte, "Request next header"));
                return;
            }

            if ((_phase == YModemReceiverPhase.WaitingFileHeaderDecision && packet is YModemPacket.Header) ||
                (_phase == YModemReceiverPhase.WaitingDataBlockDecision && packet is YModemPacket.Data pending &&
                _pendingDataPacket != null && MatchesBlockNumber(pending.BlockNumber, _nextBlockNumber)))
            {
                return;
            }

            switch (_phase)
            {
                case YModemReceiverPhase.WaitingFileHeaderPacket:
                    HandleHeaderPacket(packet);
                    return;
                case YModemReceiverPhase.WaitingDataPacketOrEot:
                    HandleDataOrEot(packet);
                    return;
                case YModemReceiverPhase.WaitingSecondEot:
                    HandleSecondEot(packet);
                    return;
                case YModemReceiverPhase.WaitingBatchTrailer:
                    HandleBatchTrailer(packet);
                    return;
                default:
                    Fault("Packet was received in an invalid state.");
                    return;
            }
        }

        private void HandleHeaderPacket(YModemPacket packet)
        {
            var header = packet as YModemPacket.Header;
            if (header == null)
            {
                Fault("Expected a file header packet.");
                return;
            }

            _retryCount = 0;
            _fileEnded = false;
            _currentFile = header.File;
            _phase = YModemReceiverPhase.WaitingFileHeaderDecision;
            _actions.Add(new YModemAction.OfferFileHeader(header.File));
        }

        private void HandleFileHeaderAccepted()
        {
            if (_phase != YModemReceiverPhase.WaitingFileHeaderDecision || _currentFile == null)
            {
                Fault("File header was accepted in an invalid state.");
                return;
            }

            _remainingFileBytes = _currentFile.HasFileSize ? _currentFile.FileSize : -1;
            _nextBlockNumber = 1;
            _phase = YModemReceiverPhase.WaitingDataPacketOrEot;
            if (!IsStreaming)
            {
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge file header"));
            }

            _actions.Add(new YModemAction.SendControl(RequestByte, "Request first data block"));
        }

        private void HandleFileHeaderRejected(string reason)
        {
            if (_phase != YModemReceiverPhase.WaitingFileHeaderDecision)
            {
                Fault("File header was rejected in an invalid state.");
                return;
            }

            _phase = YModemReceiverPhase.Cancelled;
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Reject file header"));
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Reject file header"));
            _actions.Add(new YModemAction.Cancel(reason));
        }

        private void HandleDataOrEot(YModemPacket packet)
        {
            if (packet is YModemPacket.Header repeatedHeader && _nextBlockNumber == 1 &&
                _currentFile != null && repeatedHeader.File.FileName == _currentFile.FileName &&
                repeatedHeader.File.FileSize == _currentFile.FileSize)
            {
                if (!IsStreaming)
                {
                    _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge repeated file header"));
                }

                _actions.Add(new YModemAction.SendControl(RequestByte, "Request first data block"));
                return;
            }

            if (packet is YModemPacket.Eot)
            {
                if (_remainingFileBytes > 0)
                {
                    RejectFrame("EOT arrived before the advertised file length.");
                    return;
                }

                if (IsStreaming)
                {
                    FinishFile();
                    return;
                }

                _retryCount = 0;
                _phase = YModemReceiverPhase.WaitingSecondEot;
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Nak, "Request second EOT"));
                return;
            }

            var data = packet as YModemPacket.Data;
            if (data == null)
            {
                Fault("Expected a data packet or EOT.");
                return;
            }

            var previousBlockNumber = _nextBlockNumber - 1;

            if (_nextBlockNumber > 1 && MatchesBlockNumber(data.BlockNumber, previousBlockNumber))
            {
                if (IsStreaming)
                {
                    RejectFrame("Repeated data block during YMODEM-G transfer.");
                }
                else
                {
                    _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge repeated data block"));
                }

                return;
            }

            if (!MatchesBlockNumber(data.BlockNumber, _nextBlockNumber))
            {
                RejectFrame("Unexpected data block number.");
                return;
            }

            var dataLength = _remainingFileBytes < 0 || _remainingFileBytes > data.DataLength
                ? data.DataLength
                : (int)_remainingFileBytes;

            if (_remainingFileBytes == 0 || data.DataLength == 0)
            {
                RejectFrame("Unexpected data beyond the advertised file length.");
                return;
            }

            _pendingDataPacket = data;
            _phase = YModemReceiverPhase.WaitingDataBlockDecision;
            _actions.Add(new YModemAction.DeliverDataBlock(_nextBlockNumber, data.Payload, dataLength));
        }

        private void HandleDataBlockAccepted()
        {
            if (_phase != YModemReceiverPhase.WaitingDataBlockDecision || _pendingDataPacket == null)
            {
                Fault("Data block was accepted in an invalid state.");
                return;
            }

            var deliveredLength = _remainingFileBytes < 0 || _remainingFileBytes > _pendingDataPacket.DataLength
                ? _pendingDataPacket.DataLength
                : (int)_remainingFileBytes;

            _retryCount = 0;
            _remainingFileBytes = _remainingFileBytes < 0 ? -1 : _remainingFileBytes - deliveredLength;
            _nextBlockNumber++;
            _pendingDataPacket = null;
            _phase = YModemReceiverPhase.WaitingDataPacketOrEot;
            if (!IsStreaming)
            {
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge data block"));
            }
        }

        private void HandleDataBlockRejected()
        {
            if (_phase != YModemReceiverPhase.WaitingDataBlockDecision)
            {
                Fault("Data block was rejected in an invalid state.");
                return;
            }

            _pendingDataPacket = null;
            _phase = YModemReceiverPhase.WaitingDataPacketOrEot;
            RejectFrame("Data block rejected by the application.");
        }

        private static bool MatchesBlockNumber(int actualBlockNumber, int expectedBlockNumber)
        {
            return actualBlockNumber == expectedBlockNumber || actualBlockNumber == (expectedBlockNumber & 0xFF);
        }

        private void HandleSecondEot(YModemPacket packet)
        {
            if (!(packet is YModemPacket.Eot))
            {
                Fault("Expected the second EOT.");
                return;
            }

            FinishFile();
        }

        private void HandleBatchTrailer(YModemPacket packet)
        {
            if (!(packet is YModemPacket.BatchTrailer))
            {
                Fault("Expected batch trailer packet.");
                return;
            }

            _phase = YModemReceiverPhase.Completed;
            if (!IsStreaming)
            {
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge batch trailer"));
            }

            _actions.Add(new YModemAction.Complete());
        }

        private void FinishFile()
        {
            _retryCount = 0;
            _fileEnded = true;
            _phase = YModemReceiverPhase.WaitingBatchTrailer;
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge EOT"));
            _actions.Add(new YModemAction.SendControl(RequestByte, "Request next header"));
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

        private void RejectFrame(string reason)
        {
            if (IsStreaming)
            {
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Abort streaming transfer"));
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Abort streaming transfer"));
                Fault(reason);
                return;
            }

            if (BeginRetry())
            {
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Nak, reason));
            }
        }

        private void HandleTimeout()
        {
            if (_phase == YModemReceiverPhase.Idle || _phase == YModemReceiverPhase.WaitingFileHeaderDecision ||
                _phase == YModemReceiverPhase.WaitingDataBlockDecision)
            {
                return;
            }

            if (IsStreaming && _phase == YModemReceiverPhase.WaitingDataPacketOrEot)
            {
                RejectFrame("YMODEM-G data stream timed out.");
                return;
            }

            if (!BeginRetry())
            {
                return;
            }

            var request = _phase == YModemReceiverPhase.WaitingFileHeaderPacket || _phase == YModemReceiverPhase.WaitingBatchTrailer ||
                (_phase == YModemReceiverPhase.WaitingDataPacketOrEot && _nextBlockNumber == 1)
                ? RequestByte : YModemControlBytes.Nak;
            _actions.Add(new YModemAction.SendControl(request, "Retry after timeout"));
        }

        private void Cancel(string reason)
        {
            _phase = YModemReceiverPhase.Cancelled;
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Cancel receive session"));
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Cancel receive session"));
            _actions.Add(new YModemAction.Cancel(reason));
        }

        private void Fault(string reason)
        {
            _phase = YModemReceiverPhase.Faulted;
            _failureReason = reason;
            _actions.Add(new YModemAction.Fail(reason));
        }

        private YModemReceiverSnapshot CreateSnapshot()
        {
            return new YModemReceiverSnapshot(_phase, _nextBlockNumber, _remainingFileBytes, _failureReason);
        }
    }
}
