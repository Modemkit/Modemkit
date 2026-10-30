namespace ModemKit.IntegrationTests
{
    // Streaming senders can request the next block immediately. Queue host responses
    // so a large file does not recursively grow the call stack.
    internal sealed class ProtocolActionQueue
    {
        private readonly Queue<Action> _pending = new Queue<Action>();
        private bool _draining;

        public void Post(Action action)
        {
            _pending.Enqueue(action);
            if (_draining)
            {
                return;
            }

            _draining = true;
            try
            {
                while (_pending.Count > 0)
                {
                    _pending.Dequeue()();
                }
            }
            finally
            {
                _draining = false;
            }
        }
    }
}
