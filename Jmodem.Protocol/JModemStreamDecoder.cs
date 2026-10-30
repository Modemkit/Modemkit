using System;
using System.Collections.Generic;
using System.IO;

namespace Jmodem.Protocol
{
    public sealed class JModemStreamDecoder
    {
        private readonly byte[] _frame = new byte[8198];
        private readonly JModemPacketDecoder _decoder = new JModemPacketDecoder();
        private int _count;
        private int _length;
        private int _cancelPrefix;

        public int BufferedByteCount => _count + _cancelPrefix;

        public bool IsDiscarding { get; private set; }

        public IReadOnlyList<JModemEvent> Feed(byte[] bytes)
        {
            var events = new List<JModemEvent>();
            Feed(bytes, events.Add);
            return events.ToArray();
        }

        public void Feed(byte[] bytes, Action<JModemEvent> dispatch)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (dispatch == null)
            {
                throw new ArgumentNullException(nameof(dispatch));
            }

            foreach (var value in bytes)
            {
                if (IsDiscarding)
                {
                    continue;
                }

                if (_count == 0 && value == JModemControlBytes.Can)
                {
                    _cancelPrefix++;
                    if (_cancelPrefix == 6)
                    {
                        Reset();
                        dispatch(new JModemEvent.CancelRequested("Peer cancelled the transfer."));
                    }

                    continue;
                }

                var prefix = _cancelPrefix;
                _cancelPrefix = 0;
                for (var index = 0; index < prefix && !IsDiscarding; index++)
                {
                    Append(JModemControlBytes.Can, dispatch);
                }

                if (!IsDiscarding)
                {
                    Append(value, dispatch);
                }
            }
        }

        public JModemEvent.TimeoutElapsed OnTimeout()
        {
            Reset();
            return new JModemEvent.TimeoutElapsed();
        }

        public void Reset()
        {
            _count = 0;
            _length = 0;
            _cancelPrefix = 0;
            IsDiscarding = false;
        }

        public void DiscardUntilTimeout()
        {
            Reset();
            IsDiscarding = true;
        }

        private void Append(byte value, Action<JModemEvent> dispatch)
        {
            _frame[_count++] = value;
            if (_count == 2)
            {
                _length = _frame[0] | (_frame[1] << 8);
                if (_length < 6 || _length > _frame.Length)
                {
                    Invalid("Invalid JMODEM frame length.", dispatch);
                    return;
                }
            }

            if (_count == 3 && (_frame[2] & ~7) != 0)
            {
                Invalid("Invalid JMODEM frame type.", dispatch);
                return;
            }

            if (_count != _length || _length == 0)
            {
                return;
            }

            var frame = new byte[_length];
            Buffer.BlockCopy(_frame, 0, frame, 0, frame.Length);
            _count = _length = 0;
            JModemPacket packet;
            try
            {
                packet = _decoder.Decode(frame);
            }
            catch (InvalidDataException exception)
            {
                Invalid(exception.Message, dispatch);
                return;
            }

            dispatch(new JModemEvent.PacketReceived(packet));
        }

        private void Invalid(string reason, Action<JModemEvent> dispatch)
        {
            DiscardUntilTimeout();
            dispatch(new JModemEvent.InvalidFrame(reason));
        }
    }
}
