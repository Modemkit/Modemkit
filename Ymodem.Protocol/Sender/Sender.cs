using System;
using System.Collections.Generic;

namespace Ymodem.Protocol
{
    public sealed class YModemSender
    {
        private readonly int _dataBlockSize;
        private readonly List<YModemAction> _actions;

        private YModemSenderPhase _phase;
        private YModemPacket? _lastPacket;
        private int _nextBlockNumber;
        private bool _lastDataBlockSent;
        private bool _fileHeaderAccepted;
        private long _remainingFileBytes;
        private int _lastAcknowledgedDataLength;
        private bool _shortTailBlockEnabled;
        private string? _failureReason;

        public YModemSender(int dataBlockSize = 1024)
        {
            if (dataBlockSize != 128 && dataBlockSize != 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(dataBlockSize), "YMODEM block size must be 128 or 1024 bytes.");
            }

            _dataBlockSize = dataBlockSize;
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
                case YModemEvent.CancelRequested cancelRequested:
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
            if (value != YModemControlBytes.CrcRequest)
            {
                Fault("Expected receiver CRC request before starting transfer.");
                return;
            }

            _phase = YModemSenderPhase.WaitingFileHeader;
            _actions.Add(new YModemAction.RequestFileHeader());
        }

        private void HandleHeaderAck(byte value)
        {
            switch (value)
            {
                case YModemControlBytes.Ack:
                    _fileHeaderAccepted = true;
                    _phase = YModemSenderPhase.WaitingDataStartRequest;
                    return;
                case YModemControlBytes.Nak:
                    ResendLastPacket("Resend file header");
                    return;
                default:
                    Fault("Expected ACK or NAK after file header.");
                    return;
            }
        }

        private void HandleDataStartRequest(byte value)
        {
            if (value != YModemControlBytes.CrcRequest)
            {
                Fault("Expected receiver CRC request before first data block.");
                return;
            }

            _phase = YModemSenderPhase.WaitingDataBlock;
            _actions.Add(new YModemAction.RequestDataBlock(_nextBlockNumber, GetNextBlockSize()));
        }

        private void HandleBlockAck(byte value)
        {
            switch (value)
            {
                case YModemControlBytes.Ack when _lastDataBlockSent:
                    SendEot();
                    _phase = YModemSenderPhase.WaitingFirstEotResponse;
                    return;
                case YModemControlBytes.Ack:
                    _remainingFileBytes = Math.Max(0, _remainingFileBytes - _lastAcknowledgedDataLength);
                    _shortTailBlockEnabled = _shortTailBlockEnabled || (_dataBlockSize == 1024 && _lastAcknowledgedDataLength == 1024);
                    _nextBlockNumber++;
                    _phase = YModemSenderPhase.WaitingDataBlock;
                    _actions.Add(new YModemAction.RequestDataBlock(_nextBlockNumber, GetNextBlockSize()));
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
            switch (value)
            {
                case YModemControlBytes.Ack:
                    _phase = YModemSenderPhase.WaitingBatchTrailerRequest;
                    return;
                case YModemControlBytes.Nak:
                    SendEot();
                    return;
                default:
                    Fault("Expected ACK after second EOT.");
                    return;
            }
        }

        private void HandleBatchTrailerRequest(byte value)
        {
            if (value != YModemControlBytes.CrcRequest)
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

            var packet = new YModemPacket.Header(protocolEvent.File);
            _lastPacket = packet;
            _remainingFileBytes = protocolEvent.File.FileSize;
            _lastAcknowledgedDataLength = 0;
            _shortTailBlockEnabled = false;
            _phase = YModemSenderPhase.WaitingHeaderAck;
            _actions.Add(new YModemAction.SendPacket(packet, "Send file header"));
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

            if (protocolEvent.DataLength > _dataBlockSize)
            {
                Fault("Data block is larger than the configured packet size.");
                return;
            }

            var packet = new YModemPacket.Data(protocolEvent.BlockNumber, protocolEvent.Data, protocolEvent.DataLength);
            _lastPacket = packet;
            _lastDataBlockSent = protocolEvent.IsLastBlock;
            _lastAcknowledgedDataLength = protocolEvent.DataLength;
            _phase = YModemSenderPhase.WaitingBlockAck;
            _actions.Add(new YModemAction.SendPacket(packet, protocolEvent.IsLastBlock ? "Send final data block" : "Send data block"));
        }

        private void SendEot()
        {
            var packet = new YModemPacket.Eot();
            _lastPacket = packet;
            _actions.Add(new YModemAction.SendPacket(packet, "Send EOT"));
        }

        private void SendBatchTrailer()
        {
            var packet = new YModemPacket.BatchTrailer();
            _lastPacket = packet;
            _actions.Add(new YModemAction.SendPacket(packet, "Send batch trailer"));
        }

        private int GetNextBlockSize()
        {
            if (_shortTailBlockEnabled && _dataBlockSize == 1024 && _remainingFileBytes < 1024)
            {
                return 128;
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
