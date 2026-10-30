using System;
using System.Collections.Generic;

namespace Jmodem.Protocol
{
    public abstract class JModemSession
    {
        private readonly List<JModemAction> _actions = new List<JModemAction>();

        protected JModemSession(JModemOptions? options)
        {
            Options = options ?? new JModemOptions();
            BlockSize = Options.InitialBlockSize;
        }

        public JModemSnapshot Snapshot => new JModemSnapshot(
            Phase, NextBlockNumber, BytesTransferred, BlockSize, RetryCount, FailureReason);

        protected JModemOptions Options { get; }

        protected JModemPhase Phase { get; set; }

        protected int NextBlockNumber { get; set; } = 1;

        protected long BytesTransferred { get; set; }

        protected int BlockSize { get; set; }

        protected int RetryCount { get; set; }

        protected string? FailureReason { get; set; }

        protected bool IsTerminal => Phase == JModemPhase.Completed || Phase == JModemPhase.Cancelled || Phase == JModemPhase.Faulted;

        public JModemStepResult Advance(JModemEvent protocolEvent)
        {
            if (protocolEvent == null)
            {
                throw new ArgumentNullException(nameof(protocolEvent));
            }

            _actions.Clear();
            if (protocolEvent is JModemEvent.PeerBytesReceived received && received.Bytes.Length > Options.MaxInputBytes)
            {
                if (!IsTerminal)
                {
                    Fault("JMODEM input chunk exceeds the configured bound.");
                }
            }
            else if (IsTerminal)
            {
                OnTerminalEvent(protocolEvent);
            }
            else if (protocolEvent is JModemEvent.CancelRequested cancelled)
            {
                Cancel(cancelled.Reason, true);
            }
            else
            {
                OnEvent(protocolEvent);
            }

            return new JModemStepResult(Snapshot, _actions.ToArray());
        }

        protected abstract void OnEvent(JModemEvent protocolEvent);

        protected virtual void OnTerminalEvent(JModemEvent protocolEvent)
        {
        }

        protected void Add(JModemAction action)
        {
            _actions.Add(action);
        }

        protected void Send(byte value)
        {
            Add(new JModemAction.SendBytes(new[] { value }));
        }

        protected bool TryRetry()
        {
            if (RetryCount >= Options.MaxRetries)
            {
                Fault("JMODEM retry limit exceeded.");
                return false;
            }

            RetryCount++;
            return true;
        }

        protected void Cancel(string reason, bool notifyPeer)
        {
            if (notifyPeer)
            {
                SendCancellation();
            }

            Phase = JModemPhase.Cancelled;
            Add(new JModemAction.Cancel(reason));
        }

        protected void Fault(string reason)
        {
            Phase = JModemPhase.Faulted;
            FailureReason = reason;
            SendCancellation();
            Add(new JModemAction.Fail(reason));
        }

        private void SendCancellation()
        {
            Add(new JModemAction.SendBytes(new[]
            {
                JModemControlBytes.Can, JModemControlBytes.Can, JModemControlBytes.Can,
                JModemControlBytes.Can, JModemControlBytes.Can, JModemControlBytes.Can
            }));
        }
    }
}
