using System.Collections.Generic;

namespace Ymodem.Protocol
{
    public sealed class YModemBatchReceiveStepResult
    {
        public YModemBatchReceiveStepResult(YModemBatchReceiverSnapshot snapshot, IReadOnlyList<YModemAction> actions)
        {
            Snapshot = snapshot;
            Actions = actions;
        }

        public YModemBatchReceiverSnapshot Snapshot { get; }

        public IReadOnlyList<YModemAction> Actions { get; }
    }
}
