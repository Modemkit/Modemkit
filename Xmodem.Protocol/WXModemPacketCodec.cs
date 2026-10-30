using System;
using System.Collections.Generic;
using System.IO;

namespace Xmodem.Protocol
{
    public sealed class WXModemPacketEncoder
    {
        public byte[] Encode(XModemPacket packet)
        {
            if (packet == null)
            {
                throw new ArgumentNullException(nameof(packet));
            }

            if (packet is XModemPacket.Eot)
            {
                return new[] { XModemControlBytes.Eot };
            }

            var data = (XModemPacket.Data)packet;
            if (data.BlockSize != 128)
            {
                throw new ArgumentException("WXMODEM uses 128-byte packets.", nameof(packet));
            }

            var wire = new List<byte> { WXModemControlBytes.Syn };
            foreach (var value in new XModemPacketEncoder().Encode(packet))
            {
                if (value == WXModemControlBytes.Dle || value == WXModemControlBytes.Xon
                    || value == WXModemControlBytes.Xoff || value == WXModemControlBytes.Syn)
                {
                    wire.Add(WXModemControlBytes.Dle);
                    wire.Add((byte)(value ^ 0x40));
                }
                else
                {
                    wire.Add(value);
                }
            }

            return wire.ToArray();
        }
    }

    public sealed class WXModemPacketDecoder
    {
        public XModemPacket Decode(byte[] wire)
        {
            var decoder = new WXModemStreamDecoder();
            var events = decoder.Feed(wire);
            if (events.Count != 1 || !(events[0] is XModemEvent.PacketReceived packet) || decoder.BufferedByteCount != 0)
            {
                throw new InvalidDataException("Expected one complete WXMODEM packet.");
            }

            return packet.Packet;
        }
    }

    public sealed class WXModemStreamDecoder
    {
        private readonly byte[] _frame = new byte[133];
        private readonly XModemPacketDecoder _decoder = new XModemPacketDecoder();
        private int _count;
        private bool _synchronized;
        private bool _escaped;

        public int BufferedByteCount => _count + (_escaped ? 1 : 0) + (_synchronized ? 1 : 0);

        public IReadOnlyList<XModemEvent> Feed(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            var events = new List<XModemEvent>();
            foreach (var value in bytes)
            {
                if (_escaped)
                {
                    _escaped = false;
                    Append((byte)(value ^ 0x40), events);
                }
                else if (value == WXModemControlBytes.Syn)
                {
                    if (_count > 0)
                    {
                        events.Add(new XModemEvent.InvalidFrame("Unescaped SYN interrupted a WXMODEM packet."));
                    }

                    _count = 0;
                    _synchronized = true;
                }
                else if (value == WXModemControlBytes.Xon || value == WXModemControlBytes.Xoff)
                {
                    events.Add(new XModemEvent.PeerByteReceived(value));
                }
                else if (_count > 0)
                {
                    if (value == WXModemControlBytes.Dle)
                    {
                        _escaped = true;
                    }
                    else
                    {
                        Append(value, events);
                    }
                }
                else if (_synchronized && value == XModemControlBytes.Soh)
                {
                    _synchronized = false;
                    Append(value, events);
                }
                else
                {
                    _synchronized = false;
                    events.Add(value == XModemControlBytes.Eot
                        ? (XModemEvent)new XModemEvent.PacketReceived(new XModemPacket.Eot())
                        : new XModemEvent.PeerByteReceived(value));
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
            _synchronized = false;
            _escaped = false;
        }

        private void Append(byte value, List<XModemEvent> events)
        {
            _frame[_count++] = value;
            if (_count != _frame.Length)
            {
                return;
            }

            _count = 0;
            try
            {
                events.Add(new XModemEvent.PacketReceived(_decoder.Decode(_frame)));
            }
            catch (InvalidDataException exception)
            {
                events.Add(new XModemEvent.InvalidFrame(exception.Message));
            }
        }
    }
}
