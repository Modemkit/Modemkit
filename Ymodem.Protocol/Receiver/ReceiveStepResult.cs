using System.Collections.Generic;

namespace Ymodem.Protocol
{
    public sealed class YModemReceiveStepResult
    {
        public YModemReceiveStepResult(YModemReceiverSnapshot snapshot, IReadOnlyList<YModemAction> actions)
        {
            Snapshot = snapshot;
            Actions = actions;
        }

        public YModemReceiverSnapshot Snapshot
        {
            get;
        }

        public IReadOnlyList<YModemAction> Actions
        {
            get;
        }
    }
}
