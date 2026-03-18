using System.Collections.Generic;

namespace Ymodem.Protocol
{
    public sealed class YModemBatchStepResult
    {
        public YModemBatchStepResult(YModemBatchSenderSnapshot snapshot, IReadOnlyList<YModemAction> actions)
        {
            Snapshot = snapshot;
            Actions = actions;
        }

        public YModemBatchSenderSnapshot Snapshot { get; }

        public IReadOnlyList<YModemAction> Actions { get; }
    }
}
