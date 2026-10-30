using System;
using System.Collections.Generic;
using System.IO;

namespace LegacyModem.Protocol
{
    public sealed class LegacyModemSender
    {
        private readonly LegacyModemContext _state;
        private readonly SortedDictionary<int, byte[]> _outstanding = new SortedDictionary<int, byte[]>();
        private LegacyModemVariant _variant;
        private LegacyModemIntegrity _integrity;
        private LegacyModemFileDescriptor? _file;
        private byte[]? _name;
        private byte[]? _header;
        private int _nameIndex;
        private int _nextBlock = 1;
        private int _lastSent;
        private int _lastAck;
        private int _pendingBlock;
        private int _lastDataBlock;
        private int _cancelCount;
        private int _headerRetries;
        private int _numberedAcks;
        private int _dallyCount;
        private bool _useName;
        private bool _seaLink;
        private bool _overdrive;

        public LegacyModemSender(LegacyModemOptions? options = null)
        {
            _state = new LegacyModemContext(options, LegacyModemPhase.WaitingReceiverRequest);
            _variant = _state.Options.Variant;
            _integrity = _state.Options.Integrity;
        }

        public LegacyModemSnapshot Snapshot => new LegacyModemSnapshot(
            _state.Phase,
            _variant,
            _nextBlock,
            _file?.FileSize is long length ? Math.Min(length, (long)Math.Max(0, _lastAck) * 128) : 0,
            _state.Retries,
            _outstanding.Count,
            _overdrive,
            _state.Failure);

        public LegacyModemStepResult Advance(LegacyModemEvent protocolEvent)
        {
            if (protocolEvent == null)
            {
                throw new ArgumentNullException(nameof(protocolEvent));
            }

            _state.Actions.Clear();
            if (_state.Terminal)
            {
                if (_state.Phase == LegacyModemPhase.Completed && protocolEvent is LegacyModemEvent.BytesReceived polling)
                {
                    foreach (var value in polling.Bytes)
                    {
                        if ((value == LegacyModemControlBytes.Nak || value == LegacyModemControlBytes.CrcRequest) && _dallyCount++ < _state.Options.MaxRetries)
                        {
                            _state.Send(LegacyModemControlBytes.Eot);
                        }
                    }
                }

                return Result();
            }

            switch (protocolEvent)
            {
                case LegacyModemEvent.StartRequested _:
                    break;
                case LegacyModemEvent.BytesReceived input:
                    _state.Append(input.Bytes);
                    break;
                case LegacyModemEvent.FileHeaderReady header:
                    SupplyFile(header.File);
                    break;
                case LegacyModemEvent.NoMoreFiles _:
                    if (_state.Phase != LegacyModemPhase.WaitingFileHeader)
                    {
                        _state.Fail("The file list ended in an invalid state.");
                        break;
                    }

                    _state.Send(LegacyModemControlBytes.Eot);
                    _state.Phase = LegacyModemPhase.Completed;
                    _state.Actions.Add(new LegacyModemAction.Complete());
                    break;
                case LegacyModemEvent.DataBlockReady data:
                    SupplyData(data);
                    break;
                case LegacyModemEvent.TimeoutElapsed _:
                    HandleTimeout();
                    break;
                case LegacyModemEvent.CancelRequested cancel:
                    _state.Cancel(cancel.Reason, true);
                    break;
                default:
                    _state.Fail("Unsupported sender event.");
                    break;
            }

            DrainInput();
            return Result();
        }

        private void SupplyFile(LegacyModemFileDescriptor file)
        {
            if (_state.Phase != LegacyModemPhase.WaitingFileHeader || !file.FileSize.HasValue)
            {
                _state.Fail("The sender requires a file with an exact length in response to RequestFileHeader.");
                return;
            }

            _file = file;
            _lastDataBlock = (int)((file.FileSize.Value + 127) / 128);
            _lastSent = 0;
            _lastAck = 0;
            _nextBlock = 1;
            _pendingBlock = 0;
            _headerRetries = 0;
            _numberedAcks = 0;
            _seaLink = false;
            _overdrive = false;
            _state.Retries = 0;
            _outstanding.Clear();
            try
            {
                if (_useName)
                {
                    _name = LegacyModemCodec.EncodeModem7Name(file.FileName);
                    StartName();
                }
                else
                {
                    SendHeader();
                }
            }
            catch (ArgumentException exception)
            {
                _state.Fail(exception.Message);
            }
        }

        private void SupplyData(LegacyModemEvent.DataBlockReady ready)
        {
            if (_file == null || _pendingBlock == 0 || ready.BlockNumber != _pendingBlock)
            {
                _state.Fail("Data was not supplied for the requested block.");
                return;
            }

            var bytes = ready.Data;
            var expectedLength = (int)Math.Min(128, _file.FileSize!.Value - (((long)ready.BlockNumber - 1) * 128));
            if (bytes.Length != expectedLength)
            {
                _state.Fail("The supplied data does not match the requested file range.");
                return;
            }

            var frame = LegacyModemCodec.EncodeData(ready.BlockNumber, bytes, _integrity);
            _state.Send(frame);
            if (!_overdrive)
            {
                _outstanding[ready.BlockNumber] = frame;
            }

            _lastSent = ready.BlockNumber;
            _nextBlock = ready.BlockNumber + 1;
            _pendingBlock = 0;
            Pump();
        }

        private void StartName()
        {
            _nameIndex = 0;
            _state.Phase = LegacyModemPhase.WaitingNameAck;
            _state.Send(LegacyModemControlBytes.Ack, _name![0]);
        }

        private void SendHeader()
        {
            _header = LegacyModemCodec.EncodeHeader(
                _variant,
                _file!,
                _integrity,
                _state.Options.Overdrive,
                _state.Options.EnableResync);
            _state.Send(_header);
            _state.Phase = LegacyModemPhase.WaitingHeaderAck;
        }

        private void DrainInput()
        {
            while (!_state.Terminal && _state.Input.Count > 0)
            {
                if (_state.Phase == LegacyModemPhase.WaitingNameChecksum)
                {
                    var checksum = _state.Read(1)[0];
                    if (checksum == LegacyModemCodec.ComputeModem7NameChecksum(_name!))
                    {
                        _state.Send(LegacyModemControlBytes.Ack);
                        _state.Phase = LegacyModemPhase.WaitingDataRequest;
                        _state.Retries = 0;
                    }
                    else
                    {
                        RetryName();
                    }

                    continue;
                }

                var first = _state.Input[0];
                if (first == LegacyModemControlBytes.Can)
                {
                    _state.Read(1);
                    if (++_cancelCount >= 2)
                    {
                        _state.Cancel("Peer cancelled the transfer.", false);
                    }

                    continue;
                }

                _cancelCount = 0;
                if (first == LegacyModemControlBytes.Syn && _variant == LegacyModemVariant.SeaLink && _file != null)
                {
                    if (!ReadResync())
                    {
                        break;
                    }

                    continue;
                }

                if ((_seaLink || (_variant == LegacyModemVariant.SeaLink && _state.Phase == LegacyModemPhase.WaitingHeaderAck)) &&
                    (first == LegacyModemControlBytes.Ack || first == LegacyModemControlBytes.Nak || first == LegacyModemControlBytes.CrcRequest))
                {
                    if (_state.Input.Count < 3)
                    {
                        break;
                    }

                    if ((_state.Input[1] ^ _state.Input[2]) == 255)
                    {
                        var response = _state.Read(3);
                        HandleNumbered(response[0], response[1]);
                        continue;
                    }

                    if (_state.Phase == LegacyModemPhase.WaitingHeaderAck && _state.Options.AllowFallback)
                    {
                        HandlePlain(_state.Read(1)[0]);
                    }
                    else
                    {
                        _state.Read(1);
                    }

                    continue;
                }

                HandlePlain(_state.Read(1)[0]);
            }
        }

        private void HandlePlain(byte value)
        {
            var request = value == LegacyModemControlBytes.CrcRequest || value == LegacyModemControlBytes.Nak;
            switch (_state.Phase)
            {
                case LegacyModemPhase.WaitingReceiverRequest:
                    if (!request)
                    {
                        return;
                    }

                    if (_file != null && _name != null)
                    {
                        if (value == LegacyModemControlBytes.Nak)
                        {
                            StartName();
                        }

                        return;
                    }

                    if (_state.Options.Variant == LegacyModemVariant.Modem7 && value != LegacyModemControlBytes.Nak)
                    {
                        return;
                    }

                    _integrity = value == LegacyModemControlBytes.CrcRequest ? LegacyModemIntegrity.Crc16 : LegacyModemIntegrity.Checksum;
                    _variant = _state.Options.Variant;
                    _useName = _variant == LegacyModemVariant.Modem7 || (value == LegacyModemControlBytes.Nak && _state.Options.AllowFallback);
                    if (_useName && _variant == LegacyModemVariant.SeaLink)
                    {
                        _variant = LegacyModemVariant.Telink;
                    }

                    _state.Phase = LegacyModemPhase.WaitingFileHeader;
                    _state.Actions.Add(new LegacyModemAction.RequestFileHeader());
                    return;
                case LegacyModemPhase.WaitingNameAck:
                    if (value != LegacyModemControlBytes.Ack)
                    {
                        RetryName();
                        return;
                    }

                    _nameIndex++;
                    if (_nameIndex == 11)
                    {
                        _state.Send(LegacyModemControlBytes.Sub);
                        _state.Phase = LegacyModemPhase.WaitingNameChecksum;
                    }
                    else
                    {
                        _state.Send(_name![_nameIndex]);
                    }

                    return;
                case LegacyModemPhase.WaitingDataRequest:
                    if (request)
                    {
                        _integrity = value == LegacyModemControlBytes.CrcRequest ? LegacyModemIntegrity.Crc16 : LegacyModemIntegrity.Checksum;
                        if (_variant == LegacyModemVariant.Modem7)
                        {
                            Pump();
                        }
                        else
                        {
                            SendHeader();
                        }
                    }

                    return;
                case LegacyModemPhase.WaitingHeaderAck:
                    if (value == LegacyModemControlBytes.Ack)
                    {
                        _seaLink = false;
                        _overdrive = false;
                        _state.Retries = 0;
                        Pump();
                    }
                    else if (request)
                    {
                        RetryHeader();
                    }

                    return;
                case LegacyModemPhase.WaitingEotAck:
                    if (value == LegacyModemControlBytes.Ack)
                    {
                        FinishFile();
                    }
                    else if (value == LegacyModemControlBytes.CrcRequest)
                    {
                        if (_seaLink)
                        {
                            FinishFile();
                            HandlePlain(value);
                        }
                        else if (_state.Retry("EOT was not acknowledged."))
                        {
                            _state.Send(LegacyModemControlBytes.Eot);
                        }
                    }
                    else if (value == LegacyModemControlBytes.Nak && _state.Retry("EOT was not acknowledged."))
                    {
                        _state.Send(LegacyModemControlBytes.Eot);
                    }

                    return;
                case LegacyModemPhase.WaitingDataBlock:
                case LegacyModemPhase.WaitingAcknowledgement:
                    if (!_seaLink && value == LegacyModemControlBytes.Ack && _outstanding.Count > 0)
                    {
                        _lastAck = _lastSent;
                        _outstanding.Clear();
                        _state.Retries = 0;
                        Pump();
                    }
                    else if (request && _outstanding.Count > 0)
                    {
                        RetryWindow();
                    }

                    return;
            }
        }

        private void HandleNumbered(byte value, int sequence)
        {
            if (_state.Phase == LegacyModemPhase.WaitingHeaderAck)
            {
                if (sequence != 0)
                {
                    return;
                }

                if (value == LegacyModemControlBytes.Ack)
                {
                    _seaLink = true;
                    _overdrive = _state.Options.Overdrive;
                    _numberedAcks = 1;
                    _state.Retries = 0;
                    Pump();
                }
                else
                {
                    RetryHeader();
                }

                return;
            }

            if (value != LegacyModemControlBytes.Ack && _overdrive && _lastSent - _lastAck >= 128)
            {
                _state.Fail("An eight-bit NAK cannot safely identify this Overdrive restart position; RESYNC is required.");
                return;
            }

            var reference = _state.Phase == LegacyModemPhase.WaitingEotAck ? _lastDataBlock + 1 : _lastSent + 1;
            var logical = reference - ((reference - sequence) & 255);
            if (logical < 1 || logical <= reference - 128 || logical > reference)
            {
                return;
            }

            if (value == LegacyModemControlBytes.Ack)
            {
                if (_state.Phase == LegacyModemPhase.WaitingEotAck && logical == _lastDataBlock + 1)
                {
                    FinishFile();
                    return;
                }

                if (logical > _lastSent)
                {
                    return;
                }

                if (_overdrive && ++_numberedAcks >= 10)
                {
                    _overdrive = false;
                }

                if (logical > _lastAck)
                {
                    _lastAck = logical;
                    var remove = new List<int>();
                    foreach (var pair in _outstanding)
                    {
                        if (pair.Key <= logical)
                        {
                            remove.Add(pair.Key);
                        }
                    }

                    foreach (var number in remove)
                    {
                        _outstanding.Remove(number);
                    }

                    _state.Retries = 0;
                    if (_state.Phase != LegacyModemPhase.WaitingEotAck)
                    {
                        Pump();
                    }
                }
            }
            else if (_state.Retry("A numbered NAK requested retransmission."))
            {
                RestartAt(logical);
            }
        }

        private bool ReadResync()
        {
            var end = _state.Input.IndexOf(LegacyModemControlBytes.Etx, 1);
            if (end < 0)
            {
                if (_state.Input.Count > 22)
                {
                    _state.Input.Clear();
                    if (_state.Retry("Malformed RESYNC request."))
                    {
                        _state.Send(LegacyModemControlBytes.Nak);
                    }
                }

                return false;
            }

            if (_state.Input.Count < end + 3)
            {
                return false;
            }

            var frame = _state.Read(end + 3);
            try
            {
                var block = LegacyModemCodec.DecodeResync(frame);
                if (!_state.Options.EnableResync || block > _lastDataBlock + 1)
                {
                    throw new InvalidDataException("RESYNC is unavailable for that file position.");
                }

                _seaLink = true;
                _overdrive = _state.Options.Overdrive;
                _state.Actions.Add(new LegacyModemAction.PurgeOutput());
                _state.Send(LegacyModemControlBytes.Ack);
                RestartAt(block, false);
            }
            catch (InvalidDataException)
            {
                if (_state.Retry("Invalid RESYNC request."))
                {
                    _state.Send(LegacyModemControlBytes.Nak);
                }
            }

            return true;
        }

        private void RestartAt(int block, bool purge = true)
        {
            if (purge)
            {
                _state.Actions.Add(new LegacyModemAction.PurgeOutput());
            }

            _outstanding.Clear();
            _pendingBlock = 0;
            _lastAck = block - 1;
            _lastSent = block - 1;
            _nextBlock = block;
            Pump();
        }

        private void Pump()
        {
            if (_state.Terminal || _pendingBlock != 0)
            {
                return;
            }

            if (_nextBlock > _lastDataBlock)
            {
                if (_overdrive || _lastAck >= _lastDataBlock)
                {
                    _state.Send(LegacyModemControlBytes.Eot);
                    _state.Phase = LegacyModemPhase.WaitingEotAck;
                }
                else
                {
                    _state.Phase = LegacyModemPhase.WaitingAcknowledgement;
                }

                return;
            }

            var window = _seaLink ? _state.Options.WindowSize : 1;
            if (!_overdrive && _nextBlock > _lastAck + window)
            {
                _state.Phase = LegacyModemPhase.WaitingAcknowledgement;
                return;
            }

            _pendingBlock = _nextBlock;
            _state.Phase = LegacyModemPhase.WaitingDataBlock;
            _state.Actions.Add(new LegacyModemAction.RequestDataBlock(
                _pendingBlock,
                (int)Math.Min(128, _file!.FileSize!.Value - (((long)_pendingBlock - 1) * 128))));
        }

        private void FinishFile()
        {
            _lastAck = _lastDataBlock;
            _state.Actions.Add(new LegacyModemAction.FileCompleted(_file!, _file!.FileSize!.Value));
            _file = null;
            _name = null;
            _header = null;
            _outstanding.Clear();
            _pendingBlock = 0;
            _seaLink = false;
            _overdrive = false;
            _state.Retries = 0;
            _state.Phase = LegacyModemPhase.WaitingReceiverRequest;
        }

        private void RetryName()
        {
            if (_state.Retry("MODEM7 filename handshake failed."))
            {
                _state.Send(LegacyModemControlBytes.NameRetry);
                _state.Phase = LegacyModemPhase.WaitingReceiverRequest;
            }
        }

        private void RetryHeader()
        {
            if (!_state.Retry("Metadata was not acknowledged."))
            {
                return;
            }

            if (++_headerRetries > 3 && _state.Options.AllowFallback)
            {
                _headerRetries = 0;
                if (_variant == LegacyModemVariant.SeaLink)
                {
                    _variant = LegacyModemVariant.Telink;
                    SendHeader();
                }
                else
                {
                    _variant = LegacyModemVariant.Modem7;
                    Pump();
                }

                return;
            }

            _state.Send(_header!);
        }

        private void RetryWindow()
        {
            if (!_state.Retry("Data was not acknowledged."))
            {
                return;
            }

            foreach (var pair in _outstanding)
            {
                _state.Send(pair.Value);
            }
        }

        private void HandleTimeout()
        {
            if (_state.Phase == LegacyModemPhase.WaitingFileHeader || _pendingBlock != 0)
            {
                return;
            }

            if (_state.Phase == LegacyModemPhase.WaitingHeaderAck && _state.Input.Count == 1 &&
                _state.Input[0] == LegacyModemControlBytes.Ack && _state.Options.AllowFallback)
            {
                _state.Input.Clear();
                HandlePlain(LegacyModemControlBytes.Ack);
                return;
            }

            if (_state.Phase == LegacyModemPhase.WaitingEotAck && _state.Input.Count == 1 &&
                _state.Input[0] == LegacyModemControlBytes.CrcRequest)
            {
                _state.Input.Clear();
                HandlePlain(LegacyModemControlBytes.CrcRequest);
                return;
            }

            _state.Input.Clear();
            switch (_state.Phase)
            {
                case LegacyModemPhase.WaitingNameAck:
                case LegacyModemPhase.WaitingNameChecksum:
                    RetryName();
                    return;
                case LegacyModemPhase.WaitingHeaderAck:
                    RetryHeader();
                    return;
                case LegacyModemPhase.WaitingAcknowledgement:
                    RetryWindow();
                    return;
                case LegacyModemPhase.WaitingDataRequest:
                    if (_state.Retry("The data start request was lost."))
                    {
                        _state.Send(LegacyModemControlBytes.Ack);
                    }

                    return;
                default:
                    // FTS-0007 retries EOT when the receiver polls. Blind EOT
                    // retransmission at a batch boundary can mean "end batch".
                    _state.Retry("The receiver stopped responding.");
                    return;
            }
        }

        private LegacyModemStepResult Result()
        {
            return new LegacyModemStepResult(Snapshot, _state.Actions.ToArray());
        }
    }
}
