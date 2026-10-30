using System;
using System.Collections.Generic;

namespace Xmodem.Protocol
{
    public abstract class WXModemSession
    {
        private readonly List<XModemAction> _actions = new List<XModemAction>();
        private int _cancelCount;

        protected WXModemSession(WXModemOptions? options)
        {
            Options = options ?? new WXModemOptions();
        }

        public WXModemSnapshot Snapshot => new WXModemSnapshot(
            Phase, Mode, NextBlockNumber, BytesTransferred, OutstandingBlocks, RetryCount, IsPaused, FailureReason);

        protected WXModemOptions Options { get; }

        protected XModemPhase Phase { get; set; }

        protected WXModemMode Mode { get; set; }

        protected int NextBlockNumber { get; set; } = 1;

        protected long BytesTransferred { get; set; }

        protected virtual int OutstandingBlocks => 0;

        protected int RetryCount { get; set; }

        protected bool IsPaused { get; set; }

        protected string? FailureReason { get; set; }

        protected bool IsTerminal => Phase == XModemPhase.Completed || Phase == XModemPhase.Cancelled || Phase == XModemPhase.Faulted;

        public WXModemStepResult Advance(XModemEvent protocolEvent)
        {
            if (protocolEvent == null)
            {
                throw new ArgumentNullException(nameof(protocolEvent));
            }

            _actions.Clear();
            if (IsTerminal)
            {
                OnTerminalEvent(protocolEvent);
            }
            else if (protocolEvent is XModemEvent.CancelRequested cancelled)
            {
                Send(new[] { XModemControlBytes.Can, XModemControlBytes.Can });
                Phase = XModemPhase.Cancelled;
                Add(new XModemAction.Cancel(cancelled.Reason));
            }
            else
            {
                OnEvent(protocolEvent);
            }

            return new WXModemStepResult(Snapshot, _actions.ToArray());
        }

        protected abstract void OnEvent(XModemEvent protocolEvent);

        protected virtual void OnTerminalEvent(XModemEvent protocolEvent)
        {
        }

        protected void Add(XModemAction action)
        {
            _actions.Add(action);
        }

        protected void Send(byte[] bytes)
        {
            Add(new XModemAction.SendBytes(bytes));
        }

        protected bool HandleCancellation(byte value)
        {
            _cancelCount = value == XModemControlBytes.Can ? _cancelCount + 1 : 0;
            if (_cancelCount >= 2)
            {
                Phase = XModemPhase.Cancelled;
                Add(new XModemAction.Cancel("Peer cancelled the transfer."));
            }

            return value == XModemControlBytes.Can;
        }

        protected void ClearCancellation()
        {
            _cancelCount = 0;
        }

        protected bool TryRetry()
        {
            if (RetryCount >= Options.MaxRetries)
            {
                Fault("WXMODEM retry limit exceeded.");
                return false;
            }

            RetryCount++;
            return true;
        }

        protected void Fault(string reason)
        {
            Phase = XModemPhase.Faulted;
            FailureReason = reason;
            Send(new[] { XModemControlBytes.Can, XModemControlBytes.Can });
            Add(new XModemAction.Fail(reason));
        }

        protected void ImportFallback(XModemStepResult step)
        {
            if (step == null)
            {
                throw new ArgumentNullException(nameof(step));
            }

            Phase = step.Snapshot.Phase;
            NextBlockNumber = step.Snapshot.NextBlockNumber;
            BytesTransferred = step.Snapshot.BytesTransferred;
            RetryCount = step.Snapshot.RetryCount;
            FailureReason = step.Snapshot.FailureReason;
            foreach (var action in step.Actions)
            {
                if (action is XModemAction.SendPacket packet)
                {
                    Send(new XModemPacketEncoder().Encode(packet.Packet, packet.Integrity));
                }
                else if (action is XModemAction.SendControl control)
                {
                    Send(new[] { control.Value });
                }
                else
                {
                    Add(action);
                }
            }
        }
    }
}
