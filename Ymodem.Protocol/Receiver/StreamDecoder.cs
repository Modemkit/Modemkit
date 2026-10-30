using System;
using System.Collections.Generic;

namespace Ymodem.Protocol
{
    /// <summary>
    /// Collects arbitrary transport chunks into complete YMODEM events. Feed one frame
    /// at a time when application decisions can change the block-zero interpretation.
    /// Call Timeout to discard a truncated frame before retrying.
    /// </summary>
    public sealed class YModemStreamDecoder
    {
        private readonly List<byte> _buffer = new List<byte>(1029);
        private readonly YModemPacketDecoder _decoder = new YModemPacketDecoder();
        private YModemIntegrity _integrity;

        public YModemStreamDecoder(YModemIntegrity integrity = YModemIntegrity.Crc16)
        {
            YModemIntegrityChecks.Validate(integrity);
            _integrity = integrity;
        }

        public YModemIntegrity Integrity
        {
            get => _integrity;
            set
            {
                YModemIntegrityChecks.Validate(value);
                if (_buffer.Count != 0 && value != _integrity)
                {
                    throw new InvalidOperationException("Cannot change integrity during a partial frame.");
                }

                _integrity = value;
            }
        }

        public int BufferedByteCount => _buffer.Count;

        public IReadOnlyList<YModemEvent> Feed(byte[] bytes, bool isDataPhase = false)
        {
            var events = new List<YModemEvent>();
            Feed(bytes, () => isDataPhase, events.Add);
            return events;
        }

        /// <summary>
        /// Dispatches each event immediately and consults the current receive phase
        /// before decoding each frame. Use this overload for chunks that can cross
        /// file boundaries or the first block-number rollover.
        /// </summary>
        public void Feed(byte[] bytes, Func<bool> isDataPhase, Action<YModemEvent> dispatch)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (isDataPhase == null)
            {
                throw new ArgumentNullException(nameof(isDataPhase));
            }

            if (dispatch == null)
            {
                throw new ArgumentNullException(nameof(dispatch));
            }

            foreach (var value in bytes)
            {
                if (_buffer.Count == 0)
                {
                    if (value == YModemControlBytes.Can)
                    {
                        dispatch(new YModemEvent.CancelRequested("Peer cancelled the transfer."));
                        continue;
                    }

                    if (value == YModemControlBytes.Eot)
                    {
                        dispatch(new YModemEvent.PacketReceived(new YModemPacket.Eot()));
                        continue;
                    }

                    if (value != YModemControlBytes.Soh && value != YModemControlBytes.Stx)
                    {
                        dispatch(new YModemEvent.InvalidFrameReceived("Unexpected byte outside a frame."));
                        continue;
                    }
                }

                _buffer.Add(value);
                var frameLength = (_buffer[0] == YModemControlBytes.Soh ? 128 : 1024)
                    + (_integrity == YModemIntegrity.Crc16 ? 5 : 4);
                if (_buffer.Count != frameLength)
                {
                    continue;
                }

                YModemEvent protocolEvent;
                try
                {
                    protocolEvent = new YModemEvent.PacketReceived(_decoder.Decode(_buffer.ToArray(), _integrity, isDataPhase()));
                }
                catch (InvalidOperationException exception)
                {
                    protocolEvent = new YModemEvent.InvalidFrameReceived(exception.Message);
                }

                _buffer.Clear();
                dispatch(protocolEvent);
            }
        }

        public YModemEvent.TimeoutElapsed Timeout()
        {
            Reset();
            return new YModemEvent.TimeoutElapsed();
        }

        public void Reset()
        {
            _buffer.Clear();
        }
    }
}
