using System;
using System.Collections.Generic;
using System.Text;

namespace ModemKit.Testing
{
    /// <summary>
    /// Deterministic, single-threaded byte transport. Reads may split writes or combine
    /// several writes. It deliberately retains no protocol packet boundaries.
    /// </summary>
    public sealed class InMemoryDuplexLink
    {
        private readonly Queue<byte> _toSender = new Queue<byte>();
        private readonly Queue<byte> _toReceiver = new Queue<byte>();
        private readonly Queue<WireTraceEntry> _trace = new Queue<WireTraceEntry>();
        private readonly int _maxBufferedBytes;
        private readonly int _maxTraceEntries;
        private int _sequence;

        public InMemoryDuplexLink(int maxBufferedBytes = 1048576, int maxTraceEntries = 100)
        {
            if (maxBufferedBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBufferedBytes));
            }

            if (maxTraceEntries < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxTraceEntries));
            }

            _maxBufferedBytes = maxBufferedBytes;
            _maxTraceEntries = maxTraceEntries;
        }

        public IList<WireFault> Faults { get; } = new List<WireFault>();

        public int PendingBytes => _toSender.Count + _toReceiver.Count;

        public int WriteCount => _sequence;

        public IReadOnlyList<WireTraceEntry> Trace => _trace.ToArray();

        public void WriteFromSender(byte[] bytes)
        {
            Write(WireDirection.SenderToReceiver, bytes);
        }

        public void WriteFromReceiver(byte[] bytes)
        {
            Write(WireDirection.ReceiverToSender, bytes);
        }

        public byte[] ReadAtSender(int maxBytes)
        {
            return Read(_toSender, maxBytes);
        }

        public byte[] ReadAtReceiver(int maxBytes)
        {
            return Read(_toReceiver, maxBytes);
        }

        /// <summary>Discards only bytes not yet delivered, as required by window resynchronization.</summary>
        public int DiscardPending(WireDirection direction)
        {
            if (direction != WireDirection.SenderToReceiver && direction != WireDirection.ReceiverToSender)
            {
                throw new ArgumentOutOfRangeException(nameof(direction));
            }

            var queue = direction == WireDirection.SenderToReceiver ? _toReceiver : _toSender;
            var discarded = queue.Count;
            queue.Clear();
            return discarded;
        }

        public bool Pump(Action<byte[]> receiveAtSender, Action<byte[]> receiveAtReceiver, int maxChunkSize)
        {
            if (receiveAtSender == null)
            {
                throw new ArgumentNullException(nameof(receiveAtSender));
            }

            if (receiveAtReceiver == null)
            {
                throw new ArgumentNullException(nameof(receiveAtReceiver));
            }

            var toReceiver = ReadAtReceiver(maxChunkSize);
            if (toReceiver.Length > 0)
            {
                receiveAtReceiver(toReceiver);
            }

            var toSender = ReadAtSender(maxChunkSize);
            if (toSender.Length > 0)
            {
                receiveAtSender(toSender);
            }

            return toReceiver.Length > 0 || toSender.Length > 0;
        }

        /// <summary>
        /// Pumps until both endpoints finish and all queued bytes are delivered. A stalled
        /// link invokes the caller's virtual timer; no sleeps or wall clock are used.
        /// </summary>
        public int Run(
            Action<byte[]> receiveAtSender,
            Action<byte[]> receiveAtReceiver,
            Func<bool> completed,
            Action onIdle,
            int maxChunkSize = 17,
            int maxSteps = 100000)
        {
            if (receiveAtSender == null)
            {
                throw new ArgumentNullException(nameof(receiveAtSender));
            }

            if (receiveAtReceiver == null)
            {
                throw new ArgumentNullException(nameof(receiveAtReceiver));
            }

            if (completed == null)
            {
                throw new ArgumentNullException(nameof(completed));
            }

            if (onIdle == null)
            {
                throw new ArgumentNullException(nameof(onIdle));
            }

            if (maxChunkSize < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxChunkSize));
            }

            if (maxSteps < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxSteps));
            }

            for (var step = 0; step < maxSteps; step++)
            {
                if (PendingBytes == 0 && completed())
                {
                    return step;
                }

                if (!Pump(receiveAtSender, receiveAtReceiver, maxChunkSize))
                {
                    onIdle();
                }
            }

            throw new InvalidOperationException("Transfer exceeded " + maxSteps + " steps.\n" + DescribeTrace());
        }

        public string DescribeTrace()
        {
            var description = new StringBuilder();
            foreach (var entry in _trace)
            {
                description.AppendLine(entry.ToString());
            }

            return description.ToString();
        }

        private void Write(WireDirection direction, byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            var transmitted = (byte[])bytes.Clone();
            foreach (var fault in Faults)
            {
                transmitted = fault.Apply(direction, transmitted);
            }

            if (transmitted.Length > _maxBufferedBytes - PendingBytes)
            {
                throw new InvalidOperationException("Byte link capacity exceeded.\n" + DescribeTrace());
            }

            _sequence++;
            _trace.Enqueue(new WireTraceEntry(_sequence, direction, bytes, transmitted));
            while (_trace.Count > _maxTraceEntries)
            {
                _trace.Dequeue();
            }

            var queue = direction == WireDirection.SenderToReceiver ? _toReceiver : _toSender;
            foreach (var value in transmitted)
            {
                queue.Enqueue(value);
            }
        }

        private static byte[] Read(Queue<byte> queue, int maxBytes)
        {
            if (maxBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }

            var bytes = new byte[Math.Min(maxBytes, queue.Count)];
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] = queue.Dequeue();
            }

            return bytes;
        }
    }
}
