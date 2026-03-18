using System.Collections.Generic;

namespace Ymodem.Protocol
{
    public sealed class YModemStepResult
    {
        public YModemStepResult(YModemSenderSnapshot snapshot, IReadOnlyList<YModemAction> actions)
        {
            Snapshot = snapshot;
            Actions = actions;
        }

        public YModemSenderSnapshot Snapshot
        {
            get;
        }

        public IReadOnlyList<YModemAction> Actions
        {
            get;
        }
    }
}
