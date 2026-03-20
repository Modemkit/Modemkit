using System;
using System.Collections.Generic;

namespace Ymodem.Protocol
{
    public sealed class YModemReceiver
    {
        private readonly List<YModemAction> _actions;

        private YModemReceiverPhase _phase;
        private YModemFileDescriptor? _currentFile;
        private int _nextBlockNumber;
        private long _remainingFileBytes;
        private YModemPacket.Data? _pendingDataPacket;
        private string? _failureReason;

        public YModemReceiver()
        {
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

        public YModemReceiveStepResult Advance(YModemEvent protocolEvent)
        {
            if (protocolEvent == null)
            {
                throw new ArgumentNullException(nameof(protocolEvent));
            }

            _actions.Clear();

            if (_phase == YModemReceiverPhase.Cancelled || _phase == YModemReceiverPhase.Completed || _phase == YModemReceiverPhase.Faulted)
            {
                return new YModemReceiveStepResult(CreateSnapshot(), _actions.ToArray());
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
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.CrcRequest, "Request file header"));
        }

        private void HandlePacketReceived(YModemPacket packet)
        {
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

            _remainingFileBytes = _currentFile.FileSize;
            _nextBlockNumber = 1;
            _phase = YModemReceiverPhase.WaitingDataPacketOrEot;
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge file header"));
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.CrcRequest, "Request first data block"));
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
            _actions.Add(new YModemAction.Cancel(reason));
        }

        private void HandleDataOrEot(YModemPacket packet)
        {
            if (packet is YModemPacket.Eot)
            {
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

            if (MatchesBlockNumber(data.BlockNumber, previousBlockNumber))
            {
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge repeated data block"));
                return;
            }

            if (!MatchesBlockNumber(data.BlockNumber, _nextBlockNumber))
            {
                _actions.Add(new YModemAction.SendControl(YModemControlBytes.Nak, "Reject unexpected data block"));
                return;
            }

            var dataLength = _remainingFileBytes > data.Payload.Length
                ? data.Payload.Length
                : (int)_remainingFileBytes;

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

            var deliveredLength = _remainingFileBytes > _pendingDataPacket.Payload.Length
                ? _pendingDataPacket.Payload.Length
                : (int)_remainingFileBytes;

            _remainingFileBytes -= deliveredLength;
            _nextBlockNumber++;
            _pendingDataPacket = null;
            _phase = YModemReceiverPhase.WaitingDataPacketOrEot;
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge data block"));
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
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Nak, "Reject data block"));
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

            _phase = YModemReceiverPhase.WaitingBatchTrailer;
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge second EOT"));
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.CrcRequest, "Request batch trailer"));
        }

        private void HandleBatchTrailer(YModemPacket packet)
        {
            if (!(packet is YModemPacket.BatchTrailer))
            {
                Fault("Expected batch trailer packet.");
                return;
            }

            _phase = YModemReceiverPhase.Completed;
            _actions.Add(new YModemAction.SendControl(YModemControlBytes.Ack, "Acknowledge batch trailer"));
            _actions.Add(new YModemAction.Complete());
        }

        private void Cancel(string reason)
        {
            _phase = YModemReceiverPhase.Cancelled;
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
