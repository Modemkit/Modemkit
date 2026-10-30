using System;
using System.Collections.Generic;

namespace LegacyModem.Protocol
{
    internal sealed class LegacyModemContext
    {
        internal LegacyModemContext(LegacyModemOptions? options, LegacyModemPhase phase)
        {
            Options = options ?? new LegacyModemOptions();
            Phase = phase;
        }

        internal LegacyModemOptions Options { get; }

        internal List<byte> Input { get; } = new List<byte>();

        internal List<LegacyModemAction> Actions { get; } = new List<LegacyModemAction>();

        internal LegacyModemPhase Phase { get; set; }

        internal int Retries { get; set; }

        internal string? Failure { get; private set; }

        internal bool Terminal => Phase == LegacyModemPhase.Completed || Phase == LegacyModemPhase.Cancelled || Phase == LegacyModemPhase.Faulted;

        internal void Append(byte[] bytes)
        {
            if (bytes.Length > Options.MaxBufferedBytes - Input.Count)
            {
                Fail("The receive buffer limit was exceeded.");
                return;
            }

            Input.AddRange(bytes);
        }

        internal byte[] Read(int count)
        {
            var bytes = Input.GetRange(0, count).ToArray();
            Input.RemoveRange(0, count);
            return bytes;
        }

        internal void Send(params byte[] bytes)
        {
            Actions.Add(new LegacyModemAction.SendBytes(bytes));
        }

        internal bool Retry(string reason)
        {
            if (Retries++ >= Options.MaxRetries)
            {
                Fail("Retry limit exceeded: " + reason);
                return false;
            }

            return true;
        }

        internal void Fail(string reason)
        {
            Failure = reason;
            Phase = LegacyModemPhase.Faulted;
            Input.Clear();
            Actions.Add(new LegacyModemAction.PurgeOutput());
            Send(LegacyModemControlBytes.Can, LegacyModemControlBytes.Can);
            Actions.Add(new LegacyModemAction.Fail(reason));
        }

        internal void Cancel(string reason, bool notifyPeer)
        {
            Phase = LegacyModemPhase.Cancelled;
            Input.Clear();
            Actions.Add(new LegacyModemAction.PurgeOutput());
            if (notifyPeer)
            {
                Send(LegacyModemControlBytes.Can, LegacyModemControlBytes.Can);
            }

            Actions.Add(new LegacyModemAction.Cancel(reason));
        }
    }
}
