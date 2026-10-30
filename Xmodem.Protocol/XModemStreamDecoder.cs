using System;
using System.Collections.Generic;
using System.IO;

namespace Xmodem.Protocol
{
    public sealed class XModemStreamDecoder
    {
        private readonly byte[] _frame = new byte[1029];
        private readonly XModemPacketDecoder _decoder = new XModemPacketDecoder();
        private XModemIntegrity _integrity;
        private int _count;
        private int _length;

        public XModemStreamDecoder(XModemIntegrity integrity = XModemIntegrity.Crc16)
        {
            XModemChecks.ValidateIntegrity(integrity);
            _integrity = integrity;
        }

        public XModemIntegrity Integrity
        {
            get => _integrity;
            set
            {
                XModemChecks.ValidateIntegrity(value);
                if (_count != 0 && value != _integrity)
                {
                    throw new InvalidOperationException("Cannot change integrity while a frame is incomplete.");
                }

                _integrity = value;
            }
        }

        public int BufferedByteCount => _count;

        public IReadOnlyList<XModemEvent> Feed(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            var events = new List<XModemEvent>();
            foreach (var value in bytes)
            {
                if (_count == 0)
                {
                    if (value == XModemControlBytes.Soh || value == XModemControlBytes.Stx)
                    {
                        _length = (value == XModemControlBytes.Soh ? 128 : 1024)
                            + 3 + (_integrity == XModemIntegrity.Crc16 ? 2 : 1);
                    }
                    else
                    {
                        if (value == XModemControlBytes.Eot)
                        {
                            events.Add(new XModemEvent.PacketReceived(new XModemPacket.Eot()));
                        }
                        else
                        {
                            events.Add(new XModemEvent.PeerByteReceived(value));
                        }

                        continue;
                    }
                }

                _frame[_count++] = value;
                if (_count != _length)
                {
                    continue;
                }

                var frame = new byte[_length];
                Buffer.BlockCopy(_frame, 0, frame, 0, frame.Length);
                _count = 0;
                try
                {
                    events.Add(new XModemEvent.PacketReceived(_decoder.Decode(frame, _integrity)));
                }
                catch (InvalidDataException exception)
                {
                    events.Add(new XModemEvent.InvalidFrame(exception.Message));
                }
            }

            return events.ToArray();
        }

        public XModemEvent.TimeoutElapsed OnTimeout()
        {
            Reset();
            return new XModemEvent.TimeoutElapsed();
        }

        public void Reset()
        {
            _count = 0;
            _length = 0;
        }
    }
}
