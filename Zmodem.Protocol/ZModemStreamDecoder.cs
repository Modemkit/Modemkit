using System;
using System.Collections.Generic;

namespace Zmodem.Protocol
{
    public abstract class ZModemWireEvent
    {
        private ZModemWireEvent()
        {
        }

        public sealed class HeaderReceived : ZModemWireEvent
        {
            public HeaderReceived(ZModemHeader header)
            {
                Header = header;
            }

            public ZModemHeader Header
            {
                get;
            }
        }

        public sealed class DataReceived : ZModemWireEvent
        {
            public DataReceived(byte[] payload, ZModemDataEnd end)
            {
                Payload = payload;
                End = end;
            }

            public byte[] Payload
            {
                get;
            }

            public ZModemDataEnd End
            {
                get;
            }
        }

        public sealed class InvalidFrame : ZModemWireEvent
        {
        }

        public sealed class Cancelled : ZModemWireEvent
        {
        }

        public sealed class OverAndOut : ZModemWireEvent
        {
        }
    }

    public sealed class ZModemStreamDecoder
    {
        private readonly List<byte> _buffer = new List<byte>();
        private readonly List<byte> _encodedData = new List<byte>();
        private readonly List<byte> _checksum = new List<byte>();
        private readonly int _maximumSubpacketSize;
        private readonly bool _allowLegacyExtensions;
        private bool _dataMode;
        private bool _crc32;
        private bool _rle;
        private bool _escaped;
        private ZModemDataEnd? _end;
        private int _cancelCount;

        public ZModemStreamDecoder(ZModemProfile profile = ZModemProfile.Standard, bool allowLegacyExtensions = false)
        {
            _maximumSubpacketSize = ZModemCodec.MaximumSubpacketSize(profile);
            _allowLegacyExtensions = allowLegacyExtensions;
        }

        public int BufferedByteCount => _buffer.Count + _encodedData.Count + _checksum.Count;

        public IReadOnlyList<ZModemWireEvent> Feed(byte[] bytes)
        {
            var events = new List<ZModemWireEvent>();
            Feed(bytes, events.Add);
            return events;
        }

        public void Feed(byte[] bytes, Action<ZModemWireEvent> dispatch)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (dispatch == null)
            {
                throw new ArgumentNullException(nameof(dispatch));
            }

            var events = new List<ZModemWireEvent>();
            foreach (byte value in bytes)
            {
                if (value == 0x11 || value == 0x13 || value == 0x91 || value == 0x93)
                {
                    continue;
                }

                _cancelCount = value == ZModemCodec.Zdle ? _cancelCount + 1 : 0;
                if (_cancelCount >= 5)
                {
                    _buffer.Clear();
                    ClearData();
                    _dataMode = false;
                    if (_cancelCount == 5)
                    {
                        dispatch(new ZModemWireEvent.Cancelled());
                    }

                    continue;
                }

                if (_dataMode)
                {
                    ReadDataByte(value, events);
                }
                else
                {
                    _buffer.Add(value);
                    while (!_dataMode && ReadHeader(events))
                    {
                    }

                    if (_buffer.Count > 128)
                    {
                        BadFrame(events, _buffer.Count);
                    }
                }

                foreach (ZModemWireEvent incoming in events)
                {
                    dispatch(incoming);
                }

                events.Clear();
            }
        }

        public void Reset()
        {
            _buffer.Clear();
            _dataMode = false;
            _cancelCount = 0;
            ClearData();
        }

        private void ClearData()
        {
            _encodedData.Clear();
            _checksum.Clear();
            _escaped = false;
            _end = null;
        }

        private bool ReadHeader(List<ZModemWireEvent> events)
        {
            if (_buffer.Count == 0)
            {
                return false;
            }

            if (_buffer[0] == (byte)'O')
            {
                if (_buffer.Count == 1)
                {
                    return false;
                }

                if (_buffer[1] == (byte)'O')
                {
                    _buffer.RemoveRange(0, 2);
                    events.Add(new ZModemWireEvent.OverAndOut());
                    return true;
                }
            }

            if (_buffer[0] != 0x2a)
            {
                _buffer.RemoveAt(0);
                return true;
            }

            int index = 1;
            while (index < _buffer.Count && _buffer[index] == 0x2a)
            {
                index++;
            }

            if (index >= _buffer.Count)
            {
                return false;
            }

            if (_buffer[index] != ZModemCodec.Zdle)
            {
                _buffer.RemoveRange(0, index);
                return true;
            }

            if (++index >= _buffer.Count)
            {
                return false;
            }

            byte indicator = _buffer[index++];
            if (!TryFormat(indicator, out ZModemHeaderFormat format))
            {
                _buffer.RemoveAt(0);
                return true;
            }

            bool variable = ZModemHeader.IsVariableFormat(format);
            bool rle = ZModemHeader.IsRleFormat(format);
            if ((variable || rle) && !_allowLegacyExtensions)
            {
                return BadFrame(events, index);
            }

            bool hex = format == ZModemHeaderFormat.Hex || format == ZModemHeaderFormat.VariableHex;
            bool crc32 = ZModemHeader.IsCrc32Format(format);
            int length = 4;
            if (variable)
            {
                int status = hex ? ReadHex(ref index, out byte size) : ReadEscaped(ref index, out size);
                if (status == 0)
                {
                    return false;
                }

                if (status < 0 || size > 16)
                {
                    return BadFrame(events, index);
                }

                length = size;
            }

            int needed = 1 + length + (crc32 ? 4 : 2);
            var decoded = new List<byte>();
            for (int i = 0; i < needed; i++)
            {
                int status = hex ? ReadHex(ref index, out byte value) : ReadEscaped(ref index, out value);
                if (status == 0)
                {
                    return false;
                }

                if (status < 0)
                {
                    return BadFrame(events, index);
                }

                decoded.Add(value);
            }

            if (hex)
            {
                if (_buffer.Count <= index)
                {
                    return false;
                }

                byte lineEnd = (byte)(_buffer[index++] & 0x7f);
                if (lineEnd == 13)
                {
                    if (_buffer.Count == index)
                    {
                        return false;
                    }

                    if ((_buffer[index++] & 0x7f) != 10)
                    {
                        return BadFrame(events, index);
                    }
                }
                else if (lineEnd != 10)
                {
                    return BadFrame(events, index);
                }
            }

            byte[] body = decoded.GetRange(0, length + 1).ToArray();
            byte[] checksum = decoded.GetRange(length + 1, needed - length - 1).ToArray();
            if (!ZModemCodec.CheckCrc(body, checksum, crc32) || body[0] > 19)
            {
                return BadFrame(events, index);
            }

            var header = new ZModemHeader((ZModemHeaderType)body[0], decoded.GetRange(1, length).ToArray(), format);
            _buffer.RemoveRange(0, index);
            _dataMode = header.Type == ZModemHeaderType.Zfile || header.Type == ZModemHeaderType.Zdata ||
                header.Type == ZModemHeaderType.Zsinit || header.Type == ZModemHeaderType.Zcommand;
            _crc32 = crc32;
            _rle = rle;
            ClearData();
            events.Add(new ZModemWireEvent.HeaderReceived(header));
            return true;
        }

        private void ReadDataByte(byte value, List<ZModemWireEvent> events)
        {
            if (_escaped)
            {
                _escaped = false;
                if (!_end.HasValue && value >= 0x68 && value <= 0x6b)
                {
                    _end = (ZModemDataEnd)value;
                    return;
                }

                if (!Unescape(value, out value))
                {
                    BadFrame(events, 0);
                    return;
                }
            }
            else if (value == ZModemCodec.Zdle)
            {
                _escaped = true;
                return;
            }

            if (!_end.HasValue)
            {
                _encodedData.Add(value);
                if (_encodedData.Count > _maximumSubpacketSize * (_rle ? 2 : 1))
                {
                    BadFrame(events, 0);
                }

                return;
            }

            _checksum.Add(value);
            if (_checksum.Count < (_crc32 ? 4 : 2))
            {
                return;
            }

            ZModemDataEnd end = _end.Value;
            var crcData = new List<byte>(_encodedData) { (byte)end };
            if (!ZModemCodec.CheckCrc(crcData.ToArray(), _checksum.ToArray(), _crc32))
            {
                BadFrame(events, 0);
                return;
            }

            byte[] payload;
            try
            {
                payload = _rle ? ZModemCodec.DecodeRunLength(_encodedData.ToArray(), _maximumSubpacketSize) : _encodedData.ToArray();
            }
            catch (FormatException)
            {
                BadFrame(events, 0);
                return;
            }

            ClearData();
            _dataMode = end == ZModemDataEnd.Continue || end == ZModemDataEnd.ContinueAndAck;
            events.Add(new ZModemWireEvent.DataReceived(payload, end));
        }

        private int ReadEscaped(ref int index, out byte value)
        {
            value = 0;
            if (index >= _buffer.Count)
            {
                return 0;
            }

            value = _buffer[index++];
            if (value != ZModemCodec.Zdle)
            {
                return 1;
            }

            if (index >= _buffer.Count)
            {
                return 0;
            }

            return Unescape(_buffer[index++], out value) ? 1 : -1;
        }

        private int ReadHex(ref int index, out byte value)
        {
            value = 0;
            if (index + 1 >= _buffer.Count)
            {
                return 0;
            }

            int high = Hex(_buffer[index++]);
            int low = Hex(_buffer[index++]);
            if (high < 0 || low < 0)
            {
                return -1;
            }

            value = (byte)((high << 4) | low);
            return 1;
        }

        private bool BadFrame(List<ZModemWireEvent> events, int consumed)
        {
            _buffer.RemoveRange(0, Math.Min(consumed, _buffer.Count));
            _dataMode = false;
            ClearData();
            events.Add(new ZModemWireEvent.InvalidFrame());
            return true;
        }

        private static bool TryFormat(byte indicator, out ZModemHeaderFormat format)
        {
            switch (indicator)
            {
                case 0x41:
                    format = ZModemHeaderFormat.Binary16;
                    return true;
                case 0x42:
                    format = ZModemHeaderFormat.Hex;
                    return true;
                case 0x43:
                    format = ZModemHeaderFormat.Binary32;
                    return true;
                case 0x44:
                    format = ZModemHeaderFormat.BinaryRle32;
                    return true;
                case 0x61:
                    format = ZModemHeaderFormat.VariableBinary16;
                    return true;
                case 0x62:
                    format = ZModemHeaderFormat.VariableHex;
                    return true;
                case 0x63:
                    format = ZModemHeaderFormat.VariableBinary32;
                    return true;
                case 0x64:
                    format = ZModemHeaderFormat.VariableBinaryRle32;
                    return true;
                default:
                    format = default;
                    return false;
            }
        }

        private static bool Unescape(byte escaped, out byte value)
        {
            value = 0;
            if (escaped == 0x6c)
            {
                value = 0x7f;
                return true;
            }

            if (escaped == 0x6d)
            {
                value = 0xff;
                return true;
            }

            if ((escaped & 0x60) != 0x40)
            {
                return false;
            }

            value = (byte)(escaped ^ 0x40);
            return true;
        }

        private static int Hex(byte value)
        {
            value &= 0x7f;
            if (value >= '0' && value <= '9')
            {
                return value - '0';
            }

            if (value >= 'a' && value <= 'f')
            {
                return value - 'a' + 10;
            }

            return -1;
        }
    }
}
