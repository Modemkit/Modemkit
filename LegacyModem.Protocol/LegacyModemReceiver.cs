using System;
using System.Collections.Generic;
using System.IO;

namespace LegacyModem.Protocol
{
    public sealed class LegacyModemReceiver
    {
        private readonly LegacyModemContext _state;
        private readonly List<byte> _nameBytes = new List<byte>(11);
        private LegacyModemVariant _variant;
        private LegacyModemIntegrity _integrity;
        private LegacyModemFileDescriptor? _file;
        private LegacyModemFileDescriptor? _nameFile;
        private LegacyModemHeader? _header;
        private byte[]? _deferredFrame;
        private byte[]? _resyncFrame;
        private int _pendingLength;
        private int _nextBlock = 1;
        private int _cancelCount;
        private int _outOfOrderCount;
        private long _bytesTransferred;
        private bool _seaLink;
        private bool _overdrive;
        private bool _canResync;
        private bool _unknownEotSeen;
        private bool _deferredEot;
        private bool _lastResponseWasNak;

        public LegacyModemReceiver(LegacyModemOptions? options = null)
        {
            _state = new LegacyModemContext(options, LegacyModemPhase.Idle);
            _variant = _state.Options.Variant;
            _integrity = _state.Options.Integrity;
        }

        public LegacyModemSnapshot Snapshot => new LegacyModemSnapshot(
            _state.Phase,
            _variant,
            _nextBlock,
            _bytesTransferred,
            _state.Retries,
            0,
            _overdrive,
            _state.Failure);

        private byte Poll => _integrity == LegacyModemIntegrity.Crc16 ? LegacyModemControlBytes.CrcRequest : LegacyModemControlBytes.Nak;

        public LegacyModemStepResult Advance(LegacyModemEvent protocolEvent)
        {
            if (protocolEvent == null)
            {
                throw new ArgumentNullException(nameof(protocolEvent));
            }

            _state.Actions.Clear();
            if (_state.Terminal)
            {
                return Result();
            }

            switch (protocolEvent)
            {
                case LegacyModemEvent.StartRequested _:
                    if (_state.Phase != LegacyModemPhase.Idle)
                    {
                        _state.Fail("The receive session has already started.");
                    }
                    else
                    {
                        StartBatch();
                    }

                    break;
                case LegacyModemEvent.BytesReceived input:
                    _state.Append(input.Bytes);
                    break;
                case LegacyModemEvent.FileHeaderAccepted accepted:
                    AcceptFile(accepted.RestartBlock);
                    break;
                case LegacyModemEvent.FileHeaderRejected rejected:
                    if (_state.Phase != LegacyModemPhase.WaitingFileDecision)
                    {
                        _state.Fail("A file decision arrived in an invalid state.");
                    }
                    else
                    {
                        _state.Cancel(rejected.Reason, true);
                    }

                    break;
                case LegacyModemEvent.DataBlockAccepted _:
                    AcceptData();
                    break;
                case LegacyModemEvent.DataBlockRejected _:
                    if (_state.Phase != LegacyModemPhase.WaitingDataDecision)
                    {
                        _state.Fail("A data decision arrived in an invalid state.");
                    }
                    else
                    {
                        _state.Phase = LegacyModemPhase.WaitingData;
                        RejectData("The application rejected a data block.");
                    }

                    break;
                case LegacyModemEvent.TimeoutElapsed _:
                    HandleTimeout();
                    break;
                case LegacyModemEvent.CancelRequested cancel:
                    _state.Cancel(cancel.Reason, true);
                    break;
                default:
                    _state.Fail("Unsupported receiver event.");
                    break;
            }

            DrainInput();
            return Result();
        }

        private void StartBatch()
        {
            _file = null;
            _header = null;
            _nameFile = null;
            _deferredFrame = null;
            _deferredEot = false;
            _resyncFrame = null;
            _seaLink = false;
            _overdrive = false;
            _canResync = false;
            _unknownEotSeen = false;
            _lastResponseWasNak = false;
            _outOfOrderCount = 0;
            _nextBlock = 1;
            _bytesTransferred = 0;
            _variant = _state.Options.Variant;
            _integrity = _state.Options.Integrity;
            _state.Retries = 0;
            _nameBytes.Clear();
            _state.Phase = _variant == LegacyModemVariant.Modem7
                ? LegacyModemPhase.WaitingNameStart
                : LegacyModemPhase.WaitingHeader;
            _state.Send(_variant == LegacyModemVariant.Modem7 ? LegacyModemControlBytes.Nak : Poll);
        }

        private void DrainInput()
        {
            while (!_state.Terminal && _state.Input.Count > 0)
            {
                if (_state.Phase == LegacyModemPhase.WaitingFileDecision || _state.Phase == LegacyModemPhase.WaitingDataDecision)
                {
                    return;
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
                if (_state.Phase == LegacyModemPhase.WaitingNameStart ||
                    _state.Phase == LegacyModemPhase.WaitingNameCharacter ||
                    _state.Phase == LegacyModemPhase.WaitingNameChecksumAck)
                {
                    HandleNameByte(_state.Read(1)[0]);
                    continue;
                }

                if (_state.Phase == LegacyModemPhase.WaitingResyncAck)
                {
                    if (first == LegacyModemControlBytes.Soh || first == LegacyModemControlBytes.Syn)
                    {
                        var ignoredLength = first == LegacyModemControlBytes.Syn || _integrity == LegacyModemIntegrity.Checksum ? 132 : 133;
                        if (_state.Input.Count < ignoredLength)
                        {
                            return;
                        }

                        _state.Read(ignoredLength);
                        continue;
                    }

                    _state.Read(1);
                    if (first == LegacyModemControlBytes.Ack)
                    {
                        _state.Phase = LegacyModemPhase.WaitingData;
                    }
                    else if (first == LegacyModemControlBytes.Nak)
                    {
                        RetryResync();
                    }

                    continue;
                }

                if (first == LegacyModemControlBytes.Eot || (first == LegacyModemControlBytes.Sub && _state.Phase == LegacyModemPhase.WaitingHeader))
                {
                    _state.Read(1);
                    HandleEot();
                    continue;
                }

                if (_state.Phase == LegacyModemPhase.WaitingHeader && first == LegacyModemControlBytes.Ack)
                {
                    _state.Read(1);
                    if (_nameFile != null)
                    {
                        _state.Send(Poll);
                    }
                    else if (_state.Options.AllowFallback)
                    {
                        _nameBytes.Clear();
                        _state.Phase = LegacyModemPhase.WaitingNameCharacter;
                    }

                    continue;
                }

                if (first != LegacyModemControlBytes.Soh && first != LegacyModemControlBytes.Syn)
                {
                    _state.Read(1);
                    if (_state.Phase == LegacyModemPhase.WaitingData)
                    {
                        RejectData("Unexpected byte between data frames.");
                    }

                    continue;
                }

                if (_state.Input.Count < 3)
                {
                    return;
                }

                if ((_state.Input[1] ^ _state.Input[2]) != 255 && _seaLink)
                {
                    // FTS-0007 requires a sliding SOH/sequence/complement search
                    // during SEAlink resynchronization.
                    _state.Read(1);
                    continue;
                }

                var frameLength = first == LegacyModemControlBytes.Syn || _integrity == LegacyModemIntegrity.Checksum ? 132 : 133;
                if (_state.Input.Count < frameLength)
                {
                    return;
                }

                HandleFrame(_state.Read(frameLength));
            }
        }

        private void HandleNameByte(byte value)
        {
            switch (_state.Phase)
            {
                case LegacyModemPhase.WaitingNameStart:
                    if (value == LegacyModemControlBytes.Ack)
                    {
                        _nameBytes.Clear();
                        _state.Phase = LegacyModemPhase.WaitingNameCharacter;
                    }
                    else if (value == LegacyModemControlBytes.Eot)
                    {
                        CompleteBatch();
                    }

                    return;
                case LegacyModemPhase.WaitingNameCharacter:
                    if (value == LegacyModemControlBytes.Sub)
                    {
                        try
                        {
                            var name = _nameBytes.ToArray();
                            _nameFile = new LegacyModemFileDescriptor(LegacyModemCodec.DecodeModem7Name(name));
                            _state.Send(LegacyModemCodec.ComputeModem7NameChecksum(name));
                            _state.Phase = LegacyModemPhase.WaitingNameChecksumAck;
                        }
                        catch (InvalidDataException)
                        {
                            RetryName();
                        }

                        return;
                    }

                    if (value == LegacyModemControlBytes.NameRetry || value < 32 || value > 126 ||
                        (value >= 'a' && value <= 'z') || _nameBytes.Count >= 11)
                    {
                        RetryName();
                        return;
                    }

                    _nameBytes.Add(value);
                    _state.Send(LegacyModemControlBytes.Ack);
                    return;
                case LegacyModemPhase.WaitingNameChecksumAck:
                    if (value == LegacyModemControlBytes.Ack)
                    {
                        _state.Retries = 0;
                        _state.Phase = LegacyModemPhase.WaitingHeader;
                        _state.Send(Poll);
                    }
                    else
                    {
                        RetryName();
                    }

                    return;
            }
        }

        private void HandleFrame(byte[] frame)
        {
            try
            {
                var metadata = frame[1] == 0 &&
                    (_state.Phase == LegacyModemPhase.WaitingHeader || (_nextBlock == 1 && _header != null));
                if (metadata)
                {
                    var header = LegacyModemCodec.DecodeHeader(frame, _integrity);
                    if (_header != null)
                    {
                        if (_header.File.FileName != header.File.FileName || _header.File.FileSize != header.File.FileSize)
                        {
                            throw new InvalidDataException("The retransmitted header changed.");
                        }

                        SendAck(0, true);
                        return;
                    }

                    if (!_state.Options.AllowFallback && header.Variant != _state.Options.Variant)
                    {
                        throw new InvalidDataException("The peer selected an incompatible legacy variant.");
                    }

                    _header = header;
                    _file = header.File;
                    _variant = header.Variant;
                    _seaLink = header.Variant == LegacyModemVariant.SeaLink;
                    _canResync = _seaLink && header.SupportsResync && _state.Options.EnableResync;
                    _state.Retries = 0;
                    OfferFile();
                    return;
                }

                var block = LegacyModemCodec.DecodeData(frame, _integrity);
                if (_state.Phase == LegacyModemPhase.WaitingHeader)
                {
                    if (_nameFile == null || block.BlockNumber != 1)
                    {
                        throw new InvalidDataException("Data arrived before a file name.");
                    }

                    _file = _nameFile;
                    _variant = LegacyModemVariant.Modem7;
                    _deferredFrame = frame;
                    OfferFile();
                    return;
                }

                HandleData(block);
            }
            catch (InvalidDataException exception)
            {
                RejectData(exception.Message);
            }
        }

        private void OfferFile()
        {
            _state.Phase = LegacyModemPhase.WaitingFileDecision;
            _state.Actions.Add(new LegacyModemAction.OfferFileHeader(_file!, _canResync));
        }

        private void AcceptFile(int restartBlock)
        {
            if (_state.Phase != LegacyModemPhase.WaitingFileDecision || _file == null)
            {
                _state.Fail("A file was accepted in an invalid state.");
                return;
            }

            var lastBlock = _file.FileSize.HasValue ? (int)((_file.FileSize.Value + 127) / 128) : 0;
            if (restartBlock != 1 && (!_canResync || restartBlock > lastBlock + 1))
            {
                _state.Fail("The requested restart position is not available.");
                return;
            }

            _nextBlock = restartBlock;
            _bytesTransferred = _file.FileSize.HasValue ? Math.Min(_file.FileSize.Value, ((long)restartBlock - 1) * 128) : 0;
            _state.Phase = LegacyModemPhase.WaitingData;
            if (_header != null && restartBlock == 1)
            {
                SendAck(0, true);
            }

            _overdrive = _seaLink && _header!.Overdrive && _state.Options.Overdrive;
            if (restartBlock != 1)
            {
                SendResync();
                return;
            }

            if (_deferredFrame != null)
            {
                var frame = _deferredFrame;
                _deferredFrame = null;
                HandleFrame(frame);
            }
            else if (_deferredEot)
            {
                _deferredEot = false;
                HandleEot();
            }
        }

        private void HandleData(LegacyModemDataBlock block)
        {
            if (_file == null || _state.Phase != LegacyModemPhase.WaitingData)
            {
                throw new InvalidDataException("Data arrived outside a receive transfer.");
            }

            if (_nextBlock > 1 && block.BlockNumber == ((_nextBlock - 1) & 255))
            {
                SendAck(_nextBlock - 1);
                return;
            }

            if (block.BlockNumber != (_nextBlock & 255))
            {
                if (!_lastResponseWasNak || ++_outOfOrderCount % 32 == 0)
                {
                    RejectData("Unexpected data sequence.");
                }

                return;
            }

            var remaining = _file.FileSize.HasValue ? _file.FileSize.Value - _bytesTransferred : 128;
            if (remaining <= 0)
            {
                throw new InvalidDataException("Data exceeds the advertised file length.");
            }

            _pendingLength = (int)Math.Min(128, remaining);
            var data = new byte[_pendingLength];
            Buffer.BlockCopy(block.Data, 0, data, 0, data.Length);
            _unknownEotSeen = false;
            _state.Phase = LegacyModemPhase.WaitingDataDecision;
            _state.Actions.Add(new LegacyModemAction.DeliverDataBlock(_nextBlock, data));
        }

        private void AcceptData()
        {
            if (_state.Phase != LegacyModemPhase.WaitingDataDecision)
            {
                _state.Fail("A data block was accepted in an invalid state.");
                return;
            }

            _bytesTransferred += _pendingLength;
            _state.Retries = 0;
            _lastResponseWasNak = false;
            _outOfOrderCount = 0;
            SendAck(_nextBlock);
            _nextBlock++;
            _state.Phase = LegacyModemPhase.WaitingData;
        }

        private void HandleEot()
        {
            if (_state.Phase == LegacyModemPhase.WaitingHeader && _nameFile == null)
            {
                CompleteBatch();
                return;
            }

            if (_state.Phase == LegacyModemPhase.WaitingHeader && _nameFile != null)
            {
                _file = _nameFile;
                _variant = LegacyModemVariant.Modem7;
                _deferredEot = true;
                OfferFile();
                return;
            }

            if (_file == null || _state.Phase != LegacyModemPhase.WaitingData)
            {
                RejectData("EOT arrived outside a transfer.");
                return;
            }

            if (_file.FileSize.HasValue && _bytesTransferred != _file.FileSize.Value)
            {
                RejectData("EOT arrived before the advertised file length.");
                return;
            }

            if (!_file.FileSize.HasValue && !_unknownEotSeen)
            {
                _unknownEotSeen = true;
                _state.Send(LegacyModemControlBytes.Nak);
                return;
            }

            _overdrive = false;
            SendAck(_nextBlock, true);
            _state.Actions.Add(new LegacyModemAction.FileCompleted(_file, _bytesTransferred));
            StartBatch();
        }

        private void SendAck(int block, bool force = false)
        {
            if (_overdrive && !force)
            {
                return;
            }

            _state.Send(_seaLink
                ? LegacyModemCodec.EncodeNumberedResponse(LegacyModemControlBytes.Ack, block)
                : new[] { LegacyModemControlBytes.Ack });
        }

        private void RejectData(string reason)
        {
            if (!_state.Retry(reason))
            {
                return;
            }

            _lastResponseWasNak = true;
            _outOfOrderCount = 0;
            if (_canResync)
            {
                SendResync();
            }
            else
            {
                _state.Send(_seaLink
                    ? LegacyModemCodec.EncodeNumberedResponse(Poll, _nextBlock)
                    : new[] { Poll });
            }
        }

        private void SendResync()
        {
            // Bytes already delivered in this transport read belong to the old
            // window. Keeping a partial frame would consume the RESYNC ACK as
            // payload when the sender purges its remaining output.
            _state.Input.Clear();
            _resyncFrame = LegacyModemCodec.EncodeResync(_nextBlock);
            _state.Phase = LegacyModemPhase.WaitingResyncAck;
            _state.Send(_resyncFrame);
        }

        private void RetryResync()
        {
            if (_state.Retry("RESYNC was not acknowledged."))
            {
                _state.Send(_resyncFrame!);
            }
        }

        private void RetryName()
        {
            if (_state.Retry("MODEM7 filename handshake failed."))
            {
                _nameBytes.Clear();
                _nameFile = null;
                _state.Phase = LegacyModemPhase.WaitingNameStart;
                _state.Send(LegacyModemControlBytes.Nak);
            }
        }

        private void HandleTimeout()
        {
            if (_state.Phase == LegacyModemPhase.Idle ||
                _state.Phase == LegacyModemPhase.WaitingFileDecision ||
                _state.Phase == LegacyModemPhase.WaitingDataDecision)
            {
                return;
            }

            _state.Input.Clear();
            switch (_state.Phase)
            {
                case LegacyModemPhase.WaitingNameStart:
                case LegacyModemPhase.WaitingNameCharacter:
                case LegacyModemPhase.WaitingNameChecksumAck:
                    RetryName();
                    return;
                case LegacyModemPhase.WaitingHeader:
                    if (_state.Retry("The next file header did not arrive."))
                    {
                        if (_nameFile == null && _state.Options.AllowFallback && _state.Retries >= 3)
                        {
                            _state.Phase = LegacyModemPhase.WaitingNameStart;
                            _state.Send(LegacyModemControlBytes.Nak);
                        }
                        else
                        {
                            _state.Send(Poll);
                        }
                    }

                    return;
                case LegacyModemPhase.WaitingResyncAck:
                    RetryResync();
                    return;
                default:
                    if (_unknownEotSeen && _state.Retry("The second EOT did not arrive."))
                    {
                        _state.Send(LegacyModemControlBytes.Nak);
                    }
                    else if (!_unknownEotSeen)
                    {
                        RejectData("Data did not arrive before the transport timeout.");
                    }

                    return;
            }
        }

        private void CompleteBatch()
        {
            _state.Phase = LegacyModemPhase.Completed;
            _state.Actions.Add(new LegacyModemAction.Complete());
        }

        private LegacyModemStepResult Result()
        {
            return new LegacyModemStepResult(Snapshot, _state.Actions.ToArray());
        }
    }
}
