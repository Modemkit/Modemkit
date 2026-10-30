using System;

namespace Zmodem.Protocol
{
    public sealed class ZModemReceiver : ZModemSession
    {
        private readonly ZModemStreamDecoder _terminalDecoder;
        private ZModemFileDescriptor? _file;
        private ZModemFileDescriptor? _skippedFile;
        private ZModemHeaderType? _dataKind;
        private ZModemHeader? _fileHeader;
        private ZModemWireEvent.DataReceived? _pendingData;
        private bool _acceptData;
        private uint? _lastCompletedPosition;
        private uint _crcCount;

        public ZModemReceiver(ZModemOptions? options = null)
            : base(options)
        {
            _terminalDecoder = new ZModemStreamDecoder(Options.Profile, Options.UseVariableHeaders);
        }

        protected override bool AwaitingApplication => Phase == ZModemPhase.WaitingFileDecision || Phase == ZModemPhase.WaitingDataDecision;

        protected override void OnApplicationEvent(ZModemEvent protocolEvent)
        {
            switch (protocolEvent)
            {
                case ZModemEvent.StartRequested _ when Phase == ZModemPhase.Idle:
                    Phase = ZModemPhase.WaitingFile;
                    SendInit();
                    break;
                case ZModemEvent.FileAccepted accepted when Phase == ZModemPhase.WaitingFileDecision:
                    Position = accepted.ResumeOffset;
                    _skippedFile = null;
                    Progress();
                    Phase = ZModemPhase.WaitingData;
                    SendHeader(ZModemHeaderType.Zrpos, Position);
                    break;
                case ZModemEvent.FileSkipped _ when Phase == ZModemPhase.WaitingFileDecision:
                    if (_file != null)
                    {
                        Add(new ZModemAction.FileSkipped(_file));
                    }

                    _skippedFile = _file;
                    _file = null;
                    Phase = ZModemPhase.WaitingFile;
                    Progress();
                    SendHeader(ZModemHeaderType.Zskip);
                    break;
                case ZModemEvent.FileCrcRequested requested when Phase == ZModemPhase.WaitingFileDecision:
                    _crcCount = requested.ByteCount;
                    Phase = ZModemPhase.WaitingFileCrcResponse;
                    SendHeader(ZModemHeaderType.Zcrc, _crcCount);
                    break;
                case ZModemEvent.DataBlockAccepted _ when Phase == ZModemPhase.WaitingDataDecision && _pendingData != null:
                    AcceptData(_pendingData);
                    _pendingData = null;
                    break;
                case ZModemEvent.DataBlockRejected _ when Phase == ZModemPhase.WaitingDataDecision:
                    _pendingData = null;
                    Phase = ZModemPhase.WaitingData;
                    Recover();
                    break;
                default:
                    Fault("Application event is invalid in the current receiver phase.");
                    break;
            }
        }

        protected override void OnWireEvent(ZModemWireEvent wireEvent)
        {
            if (wireEvent is ZModemWireEvent.InvalidFrame)
            {
                Recover();
                return;
            }

            if (wireEvent is ZModemWireEvent.OverAndOut)
            {
                if (Phase == ZModemPhase.WaitingOverAndOut)
                {
                    Complete();
                }

                return;
            }

            if (wireEvent is ZModemWireEvent.HeaderReceived received)
            {
                HandleHeader(received.Header);
                return;
            }

            if (!(wireEvent is ZModemWireEvent.DataReceived data))
            {
                return;
            }

            if (_dataKind == ZModemHeaderType.Zfile)
            {
                HandleFile(data);
                return;
            }

            if (_dataKind == ZModemHeaderType.Zsinit)
            {
                _dataKind = null;
                SendHeader(ZModemHeaderType.Zack, remember: false);
                return;
            }

            if (_dataKind == ZModemHeaderType.Zcommand)
            {
                _dataKind = null;
                SendHeader(ZModemHeaderType.Zferr, remember: false);
                return;
            }

            if (_dataKind != ZModemHeaderType.Zdata || !_acceptData || _file == null)
            {
                return;
            }

            if ((ulong)Position + (uint)data.Payload.Length > uint.MaxValue)
            {
                Fault("ZMODEM 32-bit offset limit exceeded.");
                return;
            }

            if (data.Payload.Length == 0)
            {
                AcceptData(data);
                return;
            }

            _pendingData = data;
            Phase = ZModemPhase.WaitingDataDecision;
            Add(new ZModemAction.DeliverDataBlock(Position, data.Payload));
        }

        protected override void OnTimeout()
        {
            if (Phase == ZModemPhase.WaitingOverAndOut)
            {
                Complete();
                return;
            }

            if (Phase == ZModemPhase.WaitingFileCrcResponse)
            {
                Retry();
                return;
            }

            Recover();
        }

        protected override void OnTerminalEvent(ZModemEvent protocolEvent)
        {
            if (Phase != ZModemPhase.Completed || !(protocolEvent is ZModemEvent.PeerBytesReceived received))
            {
                return;
            }

            _terminalDecoder.Feed(received.Bytes, incoming =>
            {
                if (incoming is ZModemWireEvent.HeaderReceived header && header.Header.Type == ZModemHeaderType.Zfin)
                {
                    SendHeader(ZModemHeaderType.Zfin, remember: false);
                }
            });
        }

        private void HandleHeader(ZModemHeader header)
        {
            if (Options.UseVariableHeaders && ZModemHeader.IsVariableFormat(header.Format))
            {
                UseVariableResponseHeaders = true;
            }

            if (header.Type == ZModemHeaderType.Zabort || header.Type == ZModemHeaderType.Zcan || header.Type == ZModemHeaderType.Zferr)
            {
                Cancel("Peer aborted the transfer.", true);
                return;
            }

            switch (header.Type)
            {
                case ZModemHeaderType.Zrqinit:
                    if (Phase == ZModemPhase.Idle)
                    {
                        Phase = ZModemPhase.WaitingFile;
                    }

                    SendInit();
                    break;
                case ZModemHeaderType.Zfile:
                    _dataKind = header.Type;
                    _fileHeader = header;
                    break;
                case ZModemHeaderType.Zsinit:
                case ZModemHeaderType.Zcommand:
                    _dataKind = header.Type;
                    break;
                case ZModemHeaderType.Zdata:
                    _dataKind = header.Type;
                    _acceptData = _file != null && header.Position == Position && Phase == ZModemPhase.WaitingData;
                    if (!_acceptData)
                    {
                        Recover();
                    }

                    break;
                case ZModemHeaderType.Zeof:
                    _dataKind = null;
                    if (_file != null && Phase == ZModemPhase.WaitingData && header.Position == Position)
                    {
                        Add(new ZModemAction.FileCompleted(_file, Position));
                        _file = null;
                        _lastCompletedPosition = Position;
                        Phase = ZModemPhase.WaitingFile;
                        Progress();
                        SendInit();
                    }
                    else if (_file == null && _lastCompletedPosition == header.Position)
                    {
                        SendInit();
                    }

                    // A stale ZEOF is ignored; a corrected ZDATA may already be in flight.
                    break;
                case ZModemHeaderType.Zfin:
                    if (_file == null)
                    {
                        Phase = ZModemPhase.WaitingOverAndOut;
                        SendHeader(ZModemHeaderType.Zfin);
                    }
                    else
                    {
                        Recover();
                    }

                    break;
                case ZModemHeaderType.Zchallenge:
                    SendHeader(ZModemHeaderType.Zack, header.Position, remember: false);
                    break;
                case ZModemHeaderType.Zfreecnt:
                    SendHeader(ZModemHeaderType.Zack, uint.MaxValue, remember: false);
                    break;
                case ZModemHeaderType.Znak:
                    Retry();
                    break;
                case ZModemHeaderType.Zcrc when Phase == ZModemPhase.WaitingFileCrcResponse:
                    Progress();
                    Phase = ZModemPhase.WaitingFileDecision;
                    Add(new ZModemAction.FileCrcReceived(_crcCount, header.Position));
                    break;
            }
        }

        private void HandleFile(ZModemWireEvent.DataReceived data)
        {
            _dataKind = null;
            if (data.End != ZModemDataEnd.EndAndAck)
            {
                Recover();
                return;
            }

            ZModemFileDescriptor file;
            try
            {
                file = ZModemFileDescriptor.Decode(data.Payload);
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentException)
            {
                Recover();
                return;
            }

            if (_fileHeader == null)
            {
                Recover();
                return;
            }

            // Ordinary binary files, plus explicitly negotiated legacy RLE.
            byte conversion = _fileHeader.Flags;
            byte transport = (byte)(_fileHeader.Position >> 8);
            byte management = (byte)(_fileHeader.Position >> 16);
            byte extension = (byte)_fileHeader.Position;
            if ((conversion != 0 && conversion != 1 && conversion != 3) ||
                (transport != 0 && (transport != 3 || !Options.UseRunLengthEncoding)) ||
                (extension & ~1) != 0 || (management & 0x7f) > 7)
            {
                SendHeader(ZModemHeaderType.Zskip);
                return;
            }

            if (Options.UseVariableHeaders && (extension & 1) != 0)
            {
                UseVariableResponseHeaders = true;
            }

            if (_file != null)
            {
                if (SameFile(_file, file))
                {
                    if (Phase == ZModemPhase.WaitingFileCrcResponse)
                    {
                        SendHeader(ZModemHeaderType.Zcrc, _crcCount);
                    }
                    else
                    {
                        SendHeader(ZModemHeaderType.Zrpos, Position);
                    }
                }
                else
                {
                    Fault("A new file arrived before the active file ended.");
                }

                return;
            }

            if (_skippedFile != null && SameFile(_skippedFile, file))
            {
                SendHeader(ZModemHeaderType.Zskip);
                return;
            }

            _file = file;
            Position = 0;
            Progress();
            Phase = ZModemPhase.WaitingFileDecision;
            Add(new ZModemAction.OfferFileHeader(file, _fileHeader));
        }

        private void AcceptData(ZModemWireEvent.DataReceived data)
        {
            Position += (uint)data.Payload.Length;
            Progress();
            Phase = ZModemPhase.WaitingData;
            if (data.End == ZModemDataEnd.End || data.End == ZModemDataEnd.EndAndAck)
            {
                _dataKind = null;
            }

            if (data.End == ZModemDataEnd.ContinueAndAck || data.End == ZModemDataEnd.EndAndAck)
            {
                SendHeader(ZModemHeaderType.Zack, Position);
            }
        }

        private void Recover()
        {
            if (Phase == ZModemPhase.WaitingFileCrcResponse)
            {
                Retry();
                return;
            }

            if (!TryRetry())
            {
                return;
            }

            ResetDecoder();
            _acceptData = false;
            _dataKind = null;
            if (_file != null)
            {
                SendHeader(ZModemHeaderType.Zrpos, Position);
            }
            else
            {
                SendInit();
            }
        }

        private void SendInit()
        {
            bool streaming = Options.FlowControl != ZModemFlowControl.StopAndWait;
            byte flags = (byte)(0x01 | 0x40 | (Options.UseCrc32 ? 0x20 : 0) |
                (streaming ? 0x02 : 0) | (Options.UseRunLengthEncoding ? 0x08 : 0));
            uint bufferSize = streaming ? 0u : (uint)Options.BlockSize;
            SendHeader(ZModemHeaderType.Zrinit, bufferSize | ((uint)flags << 24) | (Options.UseVariableHeaders ? 0x00010000u : 0));
        }

        private static bool SameFile(ZModemFileDescriptor left, ZModemFileDescriptor right)
        {
            return left.FileName == right.FileName && left.FileSize == right.FileSize;
        }
    }
}
