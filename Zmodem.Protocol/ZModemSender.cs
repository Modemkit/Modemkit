using System;
using System.Collections.Generic;

namespace Zmodem.Protocol
{
    public sealed class ZModemSender : ZModemSession
    {
        private readonly ZModemStreamDecoder _terminalDecoder;
        private readonly HashSet<uint> _supersededOffsets = new HashSet<uint>();
        private ZModemFileDescriptor? _file;
        private ZModemHeaderFormat _format = ZModemHeaderFormat.Binary16;
        private byte[]? _fileTransmission;
        private uint _pendingEnd;
        private uint _nextSendOffset;
        private uint _requestedOffset;
        private uint _crcCount;
        private uint? _cachedCrc;
        private uint _cachedCrcCount;
        private int _requestedLength;
        private int _peerSegmentSize;
        private int _frameBytes;
        private int _sinceAckRequest;
        private bool _streaming;
        private bool _frameOpen;
        private bool _forceSync;

        public ZModemSender(ZModemOptions? options = null)
            : base(options)
        {
            _terminalDecoder = new ZModemStreamDecoder(Options.Profile, Options.UseVariableHeaders);
        }

        public uint AcknowledgedOffset => Position;

        public uint NextSendOffset => _nextSendOffset;

        protected override bool AwaitingApplication => Phase == ZModemPhase.WaitingFile || Phase == ZModemPhase.WaitingData || Phase == ZModemPhase.WaitingFileCrc;

        protected override bool PauseIncoming => Phase == ZModemPhase.WaitingFile || Phase == ZModemPhase.WaitingFileCrc;

        protected override void OnApplicationEvent(ZModemEvent protocolEvent)
        {
            if (protocolEvent is ZModemEvent.DataBlockReady stale &&
                (Phase != ZModemPhase.WaitingData || stale.Offset != _requestedOffset) && _supersededOffsets.Remove(stale.Offset))
            {
                return;
            }

            switch (protocolEvent)
            {
                case ZModemEvent.StartRequested _ when Phase == ZModemPhase.Idle:
                    Phase = ZModemPhase.WaitingReceiver;
                    SendHeader(ZModemHeaderType.Zrqinit);
                    break;
                case ZModemEvent.FileHeaderReady ready when Phase == ZModemPhase.WaitingFile:
                    _file = ready.File;
                    Position = _nextSendOffset = 0;
                    _cachedCrc = null;
                    Progress();
                    Phase = ZModemPhase.WaitingFileResponse;
                    uint flags = (UseVariableResponseHeaders ? 1u : 0u) | (ZModemHeader.IsRleFormat(_format) ? 0x300u : 0u);
                    _fileTransmission = Join(
                        ZModemCodec.EncodeHeader(new ZModemHeader(ZModemHeaderType.Zfile, flags, _format)),
                        ZModemCodec.EncodeData(ready.File.Encode(), ZModemDataEnd.EndAndAck, _format, Options.Profile));
                    Send(_fileTransmission);
                    break;
                case ZModemEvent.NoMoreFiles _ when Phase == ZModemPhase.WaitingFile:
                    Phase = ZModemPhase.WaitingFinish;
                    Progress();
                    SendHeader(ZModemHeaderType.Zfin);
                    break;
                case ZModemEvent.DataBlockReady ready when Phase == ZModemPhase.WaitingData:
                    if (ready.Offset != _requestedOffset || ready.Data.Length != _requestedLength)
                    {
                        Fault("Data must match the requested offset and length.");
                        break;
                    }

                    SendData(ready.Data);
                    break;
                case ZModemEvent.FileCrcReady ready when Phase == ZModemPhase.WaitingFileCrc:
                    _cachedCrc = ready.Crc;
                    _cachedCrcCount = _crcCount;
                    Phase = ZModemPhase.WaitingFileResponse;
                    SendHeader(ZModemHeaderType.Zcrc, ready.Crc);
                    break;
                default:
                    Fault("Application event is invalid in the current sender phase.");
                    break;
            }
        }

        protected override void OnWireEvent(ZModemWireEvent wireEvent)
        {
            if (wireEvent is ZModemWireEvent.InvalidFrame)
            {
                if (_file != null && (Phase == ZModemPhase.WaitingData || Phase == ZModemPhase.WaitingDataAck || Phase == ZModemPhase.WaitingEofResponse))
                {
                    if (TryRetry())
                    {
                        RestartAt(Position, true);
                    }
                }
                else
                {
                    Retry();
                }

                return;
            }

            if (!(wireEvent is ZModemWireEvent.HeaderReceived received))
            {
                return;
            }

            ZModemHeader header = received.Header;
            if (header.Type == ZModemHeaderType.Zabort || header.Type == ZModemHeaderType.Zcan || header.Type == ZModemHeaderType.Zferr)
            {
                Cancel("Peer aborted the transfer.", true);
                return;
            }

            if (header.Type == ZModemHeaderType.Zchallenge)
            {
                SendHeader(ZModemHeaderType.Zack, header.Position, remember: false);
                return;
            }

            if (header.Type == ZModemHeaderType.Znak)
            {
                if (_file != null && (Phase == ZModemPhase.WaitingData || Phase == ZModemPhase.WaitingDataAck || Phase == ZModemPhase.WaitingEofResponse))
                {
                    if (TryRetry())
                    {
                        RestartAt(Position, true);
                    }
                }
                else
                {
                    Retry();
                }

                return;
            }

            switch (header.Type)
            {
                case ZModemHeaderType.Zrinit when Phase == ZModemPhase.Idle || Phase == ZModemPhase.WaitingReceiver:
                    ConfigurePeer(header);
                    RequestFile();
                    break;
                case ZModemHeaderType.Zrinit when Phase == ZModemPhase.WaitingEofResponse:
                    Position = _nextSendOffset;
                    if (_file != null)
                    {
                        Add(new ZModemAction.FileCompleted(_file, Position));
                    }

                    ConfigurePeer(header);
                    RequestFile();
                    break;
                case ZModemHeaderType.Zrinit when Phase == ZModemPhase.WaitingFileResponse:
                    if (TryRetry() && _fileTransmission != null)
                    {
                        Send(_fileTransmission);
                    }

                    break;
                case ZModemHeaderType.Zrpos when _file != null &&
                    (Phase == ZModemPhase.WaitingFileResponse || Phase == ZModemPhase.WaitingData || Phase == ZModemPhase.WaitingDataAck || Phase == ZModemPhase.WaitingEofResponse):
                    if (header.Position > _file.FileSize)
                    {
                        Fault("Peer requested an offset beyond the file.");
                        break;
                    }

                    bool initial = Phase == ZModemPhase.WaitingFileResponse;
                    if (initial || header.Position > Position)
                    {
                        Progress();
                    }
                    else if (!TryRetry())
                    {
                        break;
                    }

                    RestartAt(header.Position, !initial);
                    break;
                case ZModemHeaderType.Zack when _file != null &&
                    (Phase == ZModemPhase.WaitingData || Phase == ZModemPhase.WaitingDataAck || Phase == ZModemPhase.WaitingEofResponse):
                    if (header.Position < Position || header.Position > _nextSendOffset)
                    {
                        break;
                    }

                    if (header.Position > Position)
                    {
                        Progress();
                        Position = header.Position;
                    }

                    if (Phase == ZModemPhase.WaitingDataAck && header.Position == _pendingEnd)
                    {
                        _forceSync = false;
                        RequestData();
                    }

                    break;
                case ZModemHeaderType.Zskip when Phase == ZModemPhase.WaitingFileResponse:
                    if (_file != null)
                    {
                        Add(new ZModemAction.FileSkipped(_file));
                    }

                    RequestFile();
                    break;
                case ZModemHeaderType.Zfin when Phase == ZModemPhase.WaitingFinish:
                    Send(new byte[] { (byte)'O', (byte)'O' }, false);
                    Complete();
                    break;
                case ZModemHeaderType.Zcrc when Phase == ZModemPhase.WaitingFileResponse && _file != null:
                    if (header.Position > _file.FileSize)
                    {
                        Fault("CRC request exceeds file length.");
                        break;
                    }

                    if (_cachedCrc.HasValue && _cachedCrcCount == header.Position)
                    {
                        SendHeader(ZModemHeaderType.Zcrc, _cachedCrc.Value);
                    }
                    else
                    {
                        _crcCount = header.Position;
                        Phase = ZModemPhase.WaitingFileCrc;
                        Add(new ZModemAction.RequestFileCrc(_file, _crcCount));
                    }

                    break;
            }
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
                    Send(new byte[] { (byte)'O', (byte)'O' }, false);
                }
            });
        }

        private void ConfigurePeer(ZModemHeader header)
        {
            bool crc32 = Options.UseCrc32 && (header.Flags & 0x20) != 0;
            bool variable = Options.UseVariableHeaders && (header.Position & 0x00010000) != 0;
            bool rle = variable && crc32 && Options.UseRunLengthEncoding && (header.Flags & 0x08) != 0;
            _format = variable
                ? rle ? ZModemHeaderFormat.VariableBinaryRle32 : crc32 ? ZModemHeaderFormat.VariableBinary32 : ZModemHeaderFormat.VariableBinary16
                : crc32 ? ZModemHeaderFormat.Binary32 : ZModemHeaderFormat.Binary16;
            UseVariableResponseHeaders = variable;
            _peerSegmentSize = (int)(header.Position & 0xffff);
            _streaming = Options.FlowControl != ZModemFlowControl.StopAndWait && (header.Flags & 0x03) == 0x03;

            // ESC8 has incompatible historical meanings and is ignored, never advertised.
        }

        private void RequestFile()
        {
            if (IsTerminal)
            {
                return;
            }

            _file = null;
            _frameOpen = false;
            _frameBytes = _sinceAckRequest = 0;
            Progress();
            Phase = ZModemPhase.WaitingFile;
            Add(new ZModemAction.RequestFileHeader());
        }

        private void RestartAt(uint offset, bool forceSync)
        {
            bool reuseRequest = Phase == ZModemPhase.WaitingData && _requestedOffset == offset;
            if (Phase == ZModemPhase.WaitingData && !reuseRequest)
            {
                _supersededOffsets.Add(_requestedOffset);
                if (_supersededOffsets.Count > Options.MaxBufferedBytes / sizeof(uint))
                {
                    Fault("Too many source reads were superseded before completion.");
                    return;
                }
            }

            Position = _nextSendOffset = offset;
            _frameOpen = false;
            _frameBytes = _sinceAckRequest = 0;
            _forceSync = forceSync;
            if (!reuseRequest)
            {
                RequestData();
            }
        }

        private void RequestData()
        {
            if (_file == null)
            {
                Fault("No active file.");
                return;
            }

            if (_nextSendOffset == _file.FileSize)
            {
                Phase = ZModemPhase.WaitingEofResponse;
                SendHeader(ZModemHeaderType.Zeof, _nextSendOffset);
                return;
            }

            int maximum = Options.BlockSize;
            if (_peerSegmentSize > 0)
            {
                maximum = Math.Min(maximum, _peerSegmentSize - _frameBytes);
            }

            if (_streaming && Options.FlowControl == ZModemFlowControl.Windowed)
            {
                maximum = (int)Math.Min((uint)maximum, (uint)Options.WindowSize - (_nextSendOffset - Position));
            }

            if (maximum <= 0)
            {
                Fault("Peer segment or send window cannot accommodate data.");
                return;
            }

            _requestedOffset = _nextSendOffset;
            _requestedLength = (int)Math.Min((uint)maximum, _file.FileSize - _nextSendOffset);
            Phase = ZModemPhase.WaitingData;
            Add(new ZModemAction.RequestDataBlock(_requestedOffset, _requestedLength));
        }

        private void SendData(byte[] payload)
        {
            uint start = _nextSendOffset;
            uint endOffset = checked(start + (uint)payload.Length);
            bool last = _file != null && endOffset == _file.FileSize;
            _frameBytes += payload.Length;
            _sinceAckRequest += payload.Length;
            bool segmentFull = _peerSegmentSize > 0 && _frameBytes >= _peerSegmentSize;
            bool windowFull = Options.FlowControl == ZModemFlowControl.Windowed && endOffset - Position >= Options.WindowSize;
            ZModemDataEnd end;
            if (!_streaming || _forceSync || segmentFull)
            {
                end = ZModemDataEnd.EndAndAck;
            }
            else if (last)
            {
                end = ZModemDataEnd.End;
            }
            else if (windowFull)
            {
                end = ZModemDataEnd.EndAndAck;
            }
            else if (_sinceAckRequest >= Options.AcknowledgementInterval)
            {
                end = ZModemDataEnd.ContinueAndAck;
            }
            else
            {
                end = ZModemDataEnd.Continue;
            }

            byte[] packet = ZModemCodec.EncodeData(payload, end, _format, Options.Profile);
            byte[] header = ZModemCodec.EncodeHeader(new ZModemHeader(ZModemHeaderType.Zdata, start, _format));
            Send(_frameOpen ? packet : Join(header, packet), false);
            _nextSendOffset = endOffset;
            _frameOpen = end == ZModemDataEnd.Continue || end == ZModemDataEnd.ContinueAndAck;
            if (end == ZModemDataEnd.ContinueAndAck || end == ZModemDataEnd.EndAndAck)
            {
                _sinceAckRequest = 0;
            }

            if (!_frameOpen)
            {
                _frameBytes = 0;
            }

            if (end == ZModemDataEnd.EndAndAck)
            {
                // A retry starts a fresh frame so the receiver can compare the offset.
                RememberTransmission(Join(header, packet));
                _pendingEnd = endOffset;
                Phase = ZModemPhase.WaitingDataAck;
            }
            else
            {
                RequestData();
            }
        }
    }
}
