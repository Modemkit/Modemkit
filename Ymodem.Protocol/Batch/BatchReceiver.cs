using System;
using System.Collections.Generic;

namespace Ymodem.Protocol
{
    public sealed class YModemBatchReceiver
    {
        private readonly List<YModemAction> _actions;

        private YModemBatchReceiverPhase _phase;
        private YModemFileDescriptor? _currentFile;
        private int _nextBlockNumber;
        private long _remainingFileBytes;
        private YModemPacket.Data? _pendingDataPacket;
        private string? _failureReason;

        public YModemBatchReceiver()
        {
            _actions = new List<YModemAction>();
            _phase = YModemBatchReceiverPhase.Idle;
            _nextBlockNumber = 1;
        }

        public YModemBatchReceiveStepResult Advance(YModemEvent protocolEvent)
        {
            if (protocolEvent == null)
            {
                throw new ArgumentNullException(nameof(protocolEvent));
            }

            _actions.Clear();

            if (_phase == YModemBatchReceiverPhase.Cancelled || _phase == YModemBatchReceiverPhase.Completed || _phase == YModemBatchReceiverPhase.Faulted)
            {
                return new YModemBatchReceiveStepResult(CreateSnapshot(), _actions.ToArray());
            }

            switch (protocolEvent)
            {
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

            return new YModemBatchReceiveStepResult(CreateSnapshot(), _actions.ToArray());
        }

        private void HandleStartRequested()
        {
            if (_phase != YModemBatchReceiverPhase.Idle)
            {
                Fault("Receive session was started in an invalid state.");
                return;
            }

            _phase = YModemBatchReceiverPhase.WaitingFileHeaderPacket;
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.CrcRequest, "Request file header"));
        }

        private void HandlePacketReceived(YModemPacket packet)
        {
            switch (_phase)
            {
                case YModemBatchReceiverPhase.WaitingFileHeaderPacket:
                    HandleHeaderOrTrailer(packet);
                    return;
                case YModemBatchReceiverPhase.WaitingDataPacketOrEot:
                    HandleDataOrEot(packet);
                    return;
                case YModemBatchReceiverPhase.WaitingSecondEot:
                    HandleSecondEot(packet);
                    return;
                default:
                    Fault("Packet was received in an invalid state.");
                    return;
            }
        }

        private void HandleHeaderOrTrailer(YModemPacket packet)
        {
            if (packet is YModemPacket.BatchTrailer)
            {
                _phase = YModemBatchReceiverPhase.Completed;
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge batch trailer"));
                _actions.Add(new YModemAction.Complete());
                return;
            }

            var header = packet as YModemPacket.Header;
            if (header == null)
            {
                Fault("Expected a file header packet.");
                return;
            }

            _currentFile = header.File;
            _phase = YModemBatchReceiverPhase.WaitingFileHeaderDecision;
            _actions.Add(new YModemAction.OfferFileHeader(header.File));
        }

        private void HandleFileHeaderAccepted()
        {
            if (_phase != YModemBatchReceiverPhase.WaitingFileHeaderDecision || _currentFile == null)
            {
                Fault("File header was accepted in an invalid state.");
                return;
            }

            _remainingFileBytes = _currentFile.FileSize;
            _nextBlockNumber = 1;
            _phase = YModemBatchReceiverPhase.WaitingDataPacketOrEot;
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge file header"));
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.CrcRequest, "Request first data block"));
        }

        private void HandleFileHeaderRejected(string reason)
        {
            if (_phase != YModemBatchReceiverPhase.WaitingFileHeaderDecision)
            {
                Fault("File header was rejected in an invalid state.");
                return;
            }

            _phase = YModemBatchReceiverPhase.Cancelled;
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Reject file header"));
            _actions.Add(new YModemAction.Cancel(reason));
        }

        private void HandleDataOrEot(YModemPacket packet)
        {
            if (packet is YModemPacket.Eot)
            {
                _phase = YModemBatchReceiverPhase.WaitingSecondEot;
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Nak, "Request second EOT"));
                return;
            }

            var data = packet as YModemPacket.Data;
            if (data == null)
            {
                Fault("Expected a data packet or EOT.");
                return;
            }

            if (data.BlockNumber == _nextBlockNumber - 1)
            {
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge repeated data block"));
                return;
            }

            if (data.BlockNumber != _nextBlockNumber)
            {
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Nak, "Reject unexpected data block"));
                return;
            }

            var dataLength = _remainingFileBytes > data.Payload.Length
                ? data.Payload.Length
                : (int)_remainingFileBytes;

            _pendingDataPacket = data;
            _phase = YModemBatchReceiverPhase.WaitingDataBlockDecision;
            _actions.Add(new YModemAction.DeliverDataBlock(data.BlockNumber, data.Payload, dataLength));
        }

        private void HandleDataBlockAccepted()
        {
            if (_phase != YModemBatchReceiverPhase.WaitingDataBlockDecision || _pendingDataPacket == null)
            {
                Fault("Data block was accepted in an invalid state.");
                return;
            }

            var deliveredLength = _remainingFileBytes > _pendingDataPacket.Payload.Length
                ? _pendingDataPacket.Payload.Length
                : (int)_remainingFileBytes;

            _remainingFileBytes -= deliveredLength;
            _nextBlockNumber++;
            _pendingDataPacket = null;
            _phase = YModemBatchReceiverPhase.WaitingDataPacketOrEot;
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge data block"));
        }

        private void HandleDataBlockRejected()
        {
            if (_phase != YModemBatchReceiverPhase.WaitingDataBlockDecision)
            {
                Fault("Data block was rejected in an invalid state.");
                return;
            }

            _pendingDataPacket = null;
            _phase = YModemBatchReceiverPhase.WaitingDataPacketOrEot;
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Nak, "Reject data block"));
        }

        private void HandleSecondEot(YModemPacket packet)
        {
            if (!(packet is YModemPacket.Eot))
            {
                Fault("Expected the second EOT.");
                return;
            }

            _phase = YModemBatchReceiverPhase.WaitingFileHeaderPacket;
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge second EOT"));
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.CrcRequest, "Request next file header"));
        }

        private void Cancel(string reason)
        {
            _phase = YModemBatchReceiverPhase.Cancelled;
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Can, "Cancel receive session"));
            _actions.Add(new YModemAction.Cancel(reason));
        }

        private void Fault(string reason)
        {
            _phase = YModemBatchReceiverPhase.Faulted;
            _failureReason = reason;
            _actions.Add(new YModemAction.Fail(reason));
        }

        private YModemBatchReceiverSnapshot CreateSnapshot()
        {
            return new YModemBatchReceiverSnapshot(_phase, _nextBlockNumber, _remainingFileBytes, _failureReason);
        }
    }
}
