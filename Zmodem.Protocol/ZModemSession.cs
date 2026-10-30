using System;
using System.Collections.Generic;

namespace Zmodem.Protocol
{
    public abstract class ZModemSession
    {
        private readonly ZModemStreamDecoder _decoder;
        private readonly Queue<ZModemWireEvent> _incoming = new Queue<ZModemWireEvent>();
        private readonly List<ZModemAction> _actions = new List<ZModemAction>();
        private byte[]? _lastTransmission;
        private int _bufferedBytes;
        private int _retries;
        private string? _failureReason;

        protected ZModemSession(ZModemOptions? options)
        {
            Options = options ?? new ZModemOptions();
            _decoder = new ZModemStreamDecoder(Options.Profile, Options.UseVariableHeaders || Options.UseRunLengthEncoding);
        }

        public ZModemSnapshot Snapshot => new ZModemSnapshot(Phase, Position, _retries, _failureReason);

        protected ZModemOptions Options
        {
            get;
        }

        protected ZModemPhase Phase
        {
            get; set;
        }

        protected uint Position
        {
            get; set;
        }

        protected bool IsTerminal => Phase == ZModemPhase.Completed || Phase == ZModemPhase.Cancelled || Phase == ZModemPhase.Faulted;

        protected virtual bool AwaitingApplication => false;

        protected virtual bool PauseIncoming => AwaitingApplication;

        protected bool UseVariableResponseHeaders
        {
            get; set;
        }

        public ZModemStepResult Advance(ZModemEvent protocolEvent)
        {
            if (protocolEvent == null)
            {
                throw new ArgumentNullException(nameof(protocolEvent));
            }

            _actions.Clear();
            if (IsTerminal)
            {
                if (!(protocolEvent is ZModemEvent.PeerBytesReceived terminalInput) || terminalInput.Bytes.Length <= Options.MaxBufferedBytes)
                {
                    OnTerminalEvent(protocolEvent);
                }

                return new ZModemStepResult(Snapshot, _actions.ToArray());
            }

            if (protocolEvent is ZModemEvent.CancelRequested cancelled)
            {
                Cancel(cancelled.Reason, true);
            }
            else if (protocolEvent is ZModemEvent.PeerBytesReceived received)
            {
                if (received.Bytes.Length > Options.MaxBufferedBytes)
                {
                    Fault("Input chunk exceeds the configured buffering limit.");
                }
                else
                {
                    _decoder.Feed(received.Bytes, EnqueueIncoming);
                }
            }
            else if (protocolEvent is ZModemEvent.TimeoutElapsed)
            {
                if (!AwaitingApplication)
                {
                    _decoder.Reset();
                    _incoming.Clear();
                    _bufferedBytes = 0;
                    OnTimeout();
                }
            }
            else
            {
                OnApplicationEvent(protocolEvent);
            }

            DrainIncoming();

            return new ZModemStepResult(Snapshot, _actions.ToArray());
        }

        protected abstract void OnApplicationEvent(ZModemEvent protocolEvent);

        protected abstract void OnWireEvent(ZModemWireEvent wireEvent);

        protected virtual void OnTimeout()
        {
            Retry();
        }

        protected virtual void OnTerminalEvent(ZModemEvent protocolEvent)
        {
        }

        protected void Add(ZModemAction action)
        {
            _actions.Add(action);
        }

        protected void Progress()
        {
            _retries = 0;
        }

        protected void ResetDecoder()
        {
            _decoder.Reset();
        }

        protected void Send(byte[] bytes, bool remember = true)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (remember)
            {
                _lastTransmission = (byte[])bytes.Clone();
            }

            Add(new ZModemAction.SendBytes(bytes));
        }

        protected void RememberTransmission(byte[] bytes)
        {
            _lastTransmission = (byte[])(bytes ?? throw new ArgumentNullException(nameof(bytes))).Clone();
        }

        protected void SendHeader(ZModemHeaderType type, uint position = 0, ZModemHeaderFormat format = ZModemHeaderFormat.Hex, bool remember = true)
        {
            if (format == ZModemHeaderFormat.Hex && UseVariableResponseHeaders)
            {
                format = ZModemHeaderFormat.VariableHex;
            }

            Send(ZModemCodec.EncodeHeader(new ZModemHeader(type, position, format)), remember);
        }

        protected bool TryRetry()
        {
            if (_retries >= Options.MaxRetries)
            {
                Fault("Retry limit exceeded.");
                return false;
            }

            _retries++;
            return true;
        }

        protected void Retry()
        {
            if (!TryRetry())
            {
                return;
            }

            if (_lastTransmission == null)
            {
                Fault("No frame is available for retry.");
                return;
            }

            Send(_lastTransmission, false);
        }

        protected void Complete()
        {
            Phase = ZModemPhase.Completed;
            Add(new ZModemAction.Complete());
        }

        protected void Cancel(string reason, bool notifyPeer)
        {
            if (notifyPeer)
            {
                Send(ZModemCodec.EncodeCancellation(), false);
            }

            Phase = ZModemPhase.Cancelled;
            Add(new ZModemAction.Cancel(reason));
        }

        protected void Fault(string reason)
        {
            _failureReason = reason;
            Phase = ZModemPhase.Faulted;
            Send(ZModemCodec.EncodeCancellation(), false);
            Add(new ZModemAction.Fail(reason));
        }

        protected static byte[] Join(byte[] first, byte[] second)
        {
            if (first == null)
            {
                throw new ArgumentNullException(nameof(first));
            }

            if (second == null)
            {
                throw new ArgumentNullException(nameof(second));
            }

            var combined = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, combined, 0, first.Length);
            Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);
            return combined;
        }

        private void EnqueueIncoming(ZModemWireEvent incoming)
        {
            if (IsTerminal)
            {
                return;
            }

            if (incoming is ZModemWireEvent.Cancelled)
            {
                Cancel("Peer cancelled the transfer.", false);
                return;
            }

            _bufferedBytes += incoming is ZModemWireEvent.DataReceived data ? data.Payload.Length + 16 : 16;
            if (_bufferedBytes > Options.MaxBufferedBytes)
            {
                Fault("Pending application decisions exceeded the buffering limit.");
                return;
            }

            _incoming.Enqueue(incoming);
            DrainIncoming();
        }

        private void DrainIncoming()
        {
            while (!IsTerminal && !PauseIncoming && _incoming.Count > 0)
            {
                ZModemWireEvent incoming = _incoming.Dequeue();
                _bufferedBytes -= incoming is ZModemWireEvent.DataReceived data ? data.Payload.Length + 16 : 16;
                OnWireEvent(incoming);
            }
        }
    }
}
