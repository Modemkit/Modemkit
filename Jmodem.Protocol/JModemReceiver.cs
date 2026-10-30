using System;
using System.Collections.Generic;
using System.IO;

namespace Jmodem.Protocol
{
    public sealed class JModemReceiver : JModemSession
    {
        private readonly JModemStreamDecoder _decoder = new JModemStreamDecoder();
        private readonly List<byte> _startup = new List<byte>();
        private JModemPacket? _pendingPacket;
        private JModemPacket? _lastAcceptedPacket;
        private byte[]? _finalWire;
        private int[]? _finalFailure;
        private int _finalMatch;

        public JModemReceiver(JModemOptions? options = null)
            : base(options)
        {
        }

        protected override void OnEvent(JModemEvent protocolEvent)
        {
            switch (protocolEvent)
            {
                case JModemEvent.StartRequested _ when Phase == JModemPhase.Idle:
                    Phase = JModemPhase.WaitingHandshake;
                    Send(JModemControlBytes.Nak);
                    break;
                case JModemEvent.PeerBytesReceived bytes:
                    Receive(bytes.Bytes);
                    break;
                case JModemEvent.PeerByteReceived peer:
                    Receive(new[] { peer.Value });
                    break;
                case JModemEvent.PacketReceived packet:
                    HandlePacket(packet.Packet);
                    break;
                case JModemEvent.InvalidFrame _:
                    BeginDrain();
                    break;
                case JModemEvent.DataBlockAccepted _:
                    AcceptData();
                    break;
                case JModemEvent.DataBlockRejected _:
                    if (Phase != JModemPhase.WaitingDataBlockDecision)
                    {
                        Fault("No JMODEM block is awaiting a storage decision.");
                        break;
                    }

                    _pendingPacket = null;
                    Phase = JModemPhase.WaitingPacket;
                    if (TryRetry())
                    {
                        Send(JModemControlBytes.Nak);
                    }

                    break;
                case JModemEvent.TimeoutElapsed _:
                    HandleTimeout();
                    break;
                default:
                    Fault("Unsupported JMODEM receiver event.");
                    break;
            }
        }

        protected override void OnTerminalEvent(JModemEvent protocolEvent)
        {
            if (Phase != JModemPhase.Completed)
            {
                return;
            }

            if (protocolEvent is JModemEvent.TimeoutElapsed)
            {
                _finalMatch = 0;
            }
            else if (protocolEvent is JModemEvent.PeerBytesReceived bytes)
            {
                ReacknowledgeFinal(bytes.Bytes);
            }
            else if (protocolEvent is JModemEvent.PeerByteReceived peer)
            {
                ReacknowledgeFinal(new[] { peer.Value });
            }
        }

        private void Receive(byte[] bytes)
        {
            if (Phase == JModemPhase.Idle)
            {
                return;
            }

            var offset = 0;
            while (Phase == JModemPhase.WaitingHandshake && offset < bytes.Length)
            {
                HandleStartupByte(bytes[offset++]);
            }

            if (!IsTerminal && offset < bytes.Length)
            {
                if (offset == 0)
                {
                    _decoder.Feed(bytes, OnDecoded);
                }
                else
                {
                    var remaining = new byte[bytes.Length - offset];
                    Buffer.BlockCopy(bytes, offset, remaining, 0, remaining.Length);
                    _decoder.Feed(remaining, OnDecoded);
                }
            }
        }

        private void HandleStartupByte(byte value)
        {
            _startup.Add(value);
            var cancellationOffset = _startup[0] == JModemControlBytes.Ack || _startup[0] == JModemControlBytes.Nak ? 1 : 0;
            var cancellationLength = _startup.Count - cancellationOffset;
            var cancellationPrefix = cancellationLength > 0 && cancellationLength <= 6;
            for (var index = cancellationOffset; index < _startup.Count && cancellationPrefix; index++)
            {
                cancellationPrefix &= _startup[index] == JModemControlBytes.Can;
            }

            if (cancellationPrefix)
            {
                if (cancellationLength == 6)
                {
                    Cancel("Peer cancelled the handshake.", false);
                    _startup.Clear();
                }

                return;
            }

            // An initial ACK may be lost. Validate a complete first frame before treating
            // its length/payload bytes as controls; ACK/NAK can also be length-low bytes.
            var direct = ProbeStartupFrame(0, out var packet);
            var prefixed = 0;
            if (direct != 2 && (_startup[0] == JModemControlBytes.Ack || _startup[0] == JModemControlBytes.Nak))
            {
                prefixed = ProbeStartupFrame(1, out packet);
            }

            if (direct == 2 || prefixed == 2)
            {
                _startup.Clear();
                RetryCount = 0;
                Phase = JModemPhase.WaitingPacket;
                HandlePacket(packet!);
            }
            else if ((direct == 0 && prefixed == 0) || _startup.Count > 8199)
            {
                BeginDrain();
            }
        }

        private int ProbeStartupFrame(int offset, out JModemPacket? packet)
        {
            packet = null;
            var available = _startup.Count - offset;
            if (available < 2)
            {
                return 1;
            }

            var length = _startup[offset] | (_startup[offset + 1] << 8);
            if (length < 6 || length > 8198
                || (available >= 3 && (_startup[offset + 2] & ~7) != 0)
                || (available >= 4 && _startup[offset + 3] != (NextBlockNumber & 0xFF)))
            {
                return 0;
            }

            if (available < length)
            {
                return 1;
            }

            if (available != length)
            {
                return 0;
            }

            try
            {
                packet = new JModemPacketDecoder().Decode(_startup.GetRange(offset, length).ToArray());
                return 2;
            }
            catch (InvalidDataException)
            {
                return 0;
            }
        }

        private void OnDecoded(JModemEvent protocolEvent)
        {
            if (IsTerminal)
            {
                return;
            }

            if (protocolEvent is JModemEvent.CancelRequested cancelled)
            {
                Cancel(cancelled.Reason, false);
            }
            else if (protocolEvent is JModemEvent.InvalidFrame)
            {
                BeginDrain();
            }
            else if (protocolEvent is JModemEvent.PacketReceived packet)
            {
                HandlePacket(packet.Packet);
            }
        }

        private void HandlePacket(JModemPacket packet)
        {
            if (Phase == JModemPhase.WaitingDataBlockDecision)
            {
                if (_pendingPacket == null || !SamePacket(packet, _pendingPacket))
                {
                    Fault("A different JMODEM block arrived before the storage decision.");
                }

                return;
            }

            if (Phase != JModemPhase.WaitingPacket)
            {
                return;
            }

            if (_lastAcceptedPacket != null && SamePacket(packet, _lastAcceptedPacket))
            {
                Send(JModemControlBytes.Ack);
                return;
            }

            if ((packet.BlockNumber & 0xFF) != (NextBlockNumber & 0xFF))
            {
                if (TryRetry())
                {
                    Send(JModemControlBytes.Nak);
                }

                return;
            }

            _pendingPacket = packet;
            BlockSize = packet.DataLength;
            Phase = JModemPhase.WaitingDataBlockDecision;
            Add(new JModemAction.DeliverDataBlock(NextBlockNumber, packet.Payload));
        }

        private void AcceptData()
        {
            if (Phase != JModemPhase.WaitingDataBlockDecision || _pendingPacket == null)
            {
                Fault("No JMODEM block is awaiting a storage decision.");
                return;
            }

            BytesTransferred += _pendingPacket.DataLength;
            NextBlockNumber++;
            RetryCount = 0;
            _lastAcceptedPacket = _pendingPacket;
            _pendingPacket = null;
            Send(JModemControlBytes.Ack);
            if (_lastAcceptedPacket.IsLastBlock)
            {
                RememberFinalFrame(_lastAcceptedPacket.WireFrame
                    ?? new JModemPacketEncoder(Options.EnableCompression).Encode(_lastAcceptedPacket));
                Phase = JModemPhase.Completed;
                Add(new JModemAction.Complete());
            }
            else
            {
                Phase = JModemPhase.WaitingPacket;
            }
        }

        private void BeginDrain()
        {
            if (Phase == JModemPhase.Idle || Phase == JModemPhase.DrainingInput)
            {
                return;
            }

            if (Phase == JModemPhase.WaitingDataBlockDecision)
            {
                Fault("JMODEM frame corruption occurred before the storage decision.");
                return;
            }

            if (TryRetry())
            {
                _startup.Clear();
                _decoder.DiscardUntilTimeout();
                Phase = JModemPhase.DrainingInput;
                Add(new JModemAction.DiscardInput());
            }
        }

        private void HandleTimeout()
        {
            if (Phase == JModemPhase.Idle || Phase == JModemPhase.WaitingDataBlockDecision)
            {
                return;
            }

            _decoder.Reset();
            if (Phase == JModemPhase.WaitingHandshake && _startup.Count > 0)
            {
                var control = _startup.Count == 1 ? _startup[0] : 0;
                _startup.Clear();
                if (control == JModemControlBytes.Ack || control == JModemControlBytes.Nak)
                {
                    if (control == JModemControlBytes.Nak)
                    {
                        Send(JModemControlBytes.Ack);
                    }

                    RetryCount = 0;
                    Phase = JModemPhase.WaitingPacket;
                    return;
                }
            }

            if (Phase == JModemPhase.DrainingInput)
            {
                Phase = JModemPhase.WaitingPacket;
                Send(JModemControlBytes.Nak);
            }
            else if (TryRetry())
            {
                Send(JModemControlBytes.Nak);
            }
        }

        private void RememberFinalFrame(byte[] wire)
        {
            _finalWire = wire;
            _finalFailure = new int[wire.Length];
            var matched = 0;
            for (var index = 1; index < wire.Length; index++)
            {
                while (matched > 0 && wire[index] != wire[matched])
                {
                    matched = _finalFailure[matched - 1];
                }

                if (wire[index] == wire[matched])
                {
                    matched++;
                }

                _finalFailure[index] = matched;
            }
        }

        private void ReacknowledgeFinal(byte[] bytes)
        {
            if (_finalWire == null || _finalFailure == null)
            {
                return;
            }

            // Only an identical cached final frame can elicit a terminal ACK. This
            // tolerates damaged retries without guessing framing inside unknown data.
            foreach (var value in bytes)
            {
                while (_finalMatch > 0 && value != _finalWire[_finalMatch])
                {
                    _finalMatch = _finalFailure[_finalMatch - 1];
                }

                if (value == _finalWire[_finalMatch])
                {
                    _finalMatch++;
                }

                if (_finalMatch == _finalWire.Length)
                {
                    Send(JModemControlBytes.Ack);
                    _finalMatch = _finalFailure[_finalMatch - 1];
                }
            }
        }

        private static bool SamePacket(JModemPacket first, JModemPacket second)
        {
            if ((first.BlockNumber & 0xFF) != (second.BlockNumber & 0xFF)
                || first.IsLastBlock != second.IsLastBlock || first.DataLength != second.DataLength)
            {
                return false;
            }

            var left = first.Payload;
            var right = second.Payload;
            for (var index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
