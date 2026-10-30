using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Zmodem.Protocol
{
    public enum ZModemHeaderType : byte
    {
        Zrqinit = 0,
        Zrinit = 1,
        Zsinit = 2,
        Zack = 3,
        Zfile = 4,
        Zskip = 5,
        Znak = 6,
        Zabort = 7,
        Zfin = 8,
        Zrpos = 9,
        Zdata = 10,
        Zeof = 11,
        Zferr = 12,
        Zcrc = 13,
        Zchallenge = 14,
        Zcompl = 15,
        Zcan = 16,
        Zfreecnt = 17,
        Zcommand = 18,
        Zstderr = 19
    }

    public enum ZModemHeaderFormat
    {
        Hex,
        Binary16,
        Binary32,
        BinaryRle32,
        VariableHex,
        VariableBinary16,
        VariableBinary32,
        VariableBinaryRle32
    }

    public enum ZModemProfile
    {
        Standard,
        Extended4K,
        Extended8K,
        ZedZap,
        ZedZip
    }

    public enum ZModemFlowControl
    {
        StopAndWait,
        Streaming,
        Windowed
    }

    public enum ZModemDataEnd : byte
    {
        End = 0x68,
        Continue = 0x69,
        ContinueAndAck = 0x6a,
        EndAndAck = 0x6b
    }

    public enum ZModemPhase
    {
        Idle,
        WaitingReceiver,
        WaitingFile,
        WaitingFileResponse,
        WaitingFileDecision,
        WaitingData,
        WaitingDataDecision,
        WaitingDataAck,
        WaitingEofResponse,
        WaitingFinish,
        WaitingOverAndOut,
        Completed,
        Cancelled,
        Faulted,
        WaitingFileCrc,
        WaitingFileCrcResponse
    }

    public sealed class ZModemHeader
    {
        private readonly byte[] _data;

        public ZModemHeader(ZModemHeaderType type, uint position = 0, ZModemHeaderFormat format = ZModemHeaderFormat.Hex)
            : this(type, new[] { (byte)position, (byte)(position >> 8), (byte)(position >> 16), (byte)(position >> 24) }, format)
        {
        }

        public ZModemHeader(ZModemHeaderType type, byte[] data, ZModemHeaderFormat format)
        {
            if ((byte)type > 19)
            {
                throw new ArgumentOutOfRangeException(nameof(type));
            }

            if (format < ZModemHeaderFormat.Hex || format > ZModemHeaderFormat.VariableBinaryRle32)
            {
                throw new ArgumentOutOfRangeException(nameof(format));
            }

            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (data.Length > 16 || (!IsVariableFormat(format) && data.Length != 4))
            {
                throw new ArgumentException("Fixed headers require four bytes; variable headers allow zero through sixteen.", nameof(data));
            }

            _data = (byte[])data.Clone();
            uint position = 0;
            for (int index = 0; index < Math.Min(4, data.Length); index++)
            {
                position |= (uint)data[index] << (index * 8);
            }

            Type = type;
            Position = position;
            Format = format;
        }

        public ZModemHeaderType Type
        {
            get;
        }

        public uint Position
        {
            get;
        }

        public ZModemHeaderFormat Format
        {
            get;
        }

        public byte Flags => (byte)(Position >> 24);

        public byte[] Data => (byte[])_data.Clone();

        internal static bool IsVariableFormat(ZModemHeaderFormat format) => format >= ZModemHeaderFormat.VariableHex;

        internal static bool IsCrc32Format(ZModemHeaderFormat format) =>
            format == ZModemHeaderFormat.Binary32 || format == ZModemHeaderFormat.BinaryRle32 ||
            format == ZModemHeaderFormat.VariableBinary32 || format == ZModemHeaderFormat.VariableBinaryRle32;

        internal static bool IsRleFormat(ZModemHeaderFormat format) =>
            format == ZModemHeaderFormat.BinaryRle32 || format == ZModemHeaderFormat.VariableBinaryRle32;
    }

    public sealed class ZModemFileDescriptor
    {
        public ZModemFileDescriptor(string fileName, uint fileSize)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                throw new ArgumentException("A file name is required.", nameof(fileName));
            }

            foreach (char value in fileName)
            {
                if (value < 0x20 || value > 0x7e)
                {
                    throw new ArgumentException("File names must contain printable ASCII.", nameof(fileName));
                }
            }

            if (Encoding.ASCII.GetByteCount(fileName) > 900)
            {
                throw new ArgumentOutOfRangeException(nameof(fileName));
            }

            FileName = fileName;
            FileSize = fileSize;
        }

        public string FileName
        {
            get;
        }

        public uint FileSize
        {
            get;
        }

        internal byte[] Encode()
        {
            return Encoding.ASCII.GetBytes(FileName + "\0" + FileSize.ToString(CultureInfo.InvariantCulture) + "\0");
        }

        internal static ZModemFileDescriptor Decode(byte[] payload)
        {
            int separator = Array.IndexOf(payload, (byte)0);
            if (separator <= 0)
            {
                throw new FormatException("Missing ZFILE pathname terminator.");
            }

            string name = Encoding.ASCII.GetString(payload, 0, separator);
            for (int i = 0; i < separator; i++)
            {
                if (payload[i] < 0x20 || payload[i] > 0x7e)
                {
                    throw new FormatException("Invalid ASCII pathname.");
                }
            }

            int lengthEnd = separator + 1;
            while (lengthEnd < payload.Length && payload[lengthEnd] != 0 && payload[lengthEnd] != 32)
            {
                lengthEnd++;
            }

            uint length = 0;
            if (lengthEnd > separator + 1 && !uint.TryParse(
                Encoding.ASCII.GetString(payload, separator + 1, lengthEnd - separator - 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out length))
            {
                throw new FormatException("Invalid ZFILE length.");
            }

            return new ZModemFileDescriptor(name, length);
        }
    }

    public sealed class ZModemOptions
    {
        public ZModemOptions(
            int blockSize = 1024,
            int maxRetries = 10,
            bool useCrc32 = true,
            int maxBufferedBytes = 65536,
            ZModemProfile profile = ZModemProfile.Standard,
            ZModemFlowControl flowControl = ZModemFlowControl.StopAndWait,
            int windowSize = 16384,
            int acknowledgementInterval = 4096,
            bool useVariableHeaders = false,
            bool useRunLengthEncoding = false)
        {
            if (profile < ZModemProfile.Standard || profile > ZModemProfile.ZedZip)
            {
                throw new ArgumentOutOfRangeException(nameof(profile));
            }

            int maximum = ZModemCodec.MaximumSubpacketSize(profile);
            if (blockSize < 1 || blockSize > maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(blockSize));
            }

            if (maxRetries < 0 || maxRetries > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRetries));
            }

            if (maxBufferedBytes < 4096 || maxBufferedBytes > 16 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBufferedBytes));
            }

            if (maxBufferedBytes < (maximum * 2) + 64)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBufferedBytes), "Buffer must accommodate an escaped maximum subpacket.");
            }

            if (flowControl < ZModemFlowControl.StopAndWait || flowControl > ZModemFlowControl.Windowed)
            {
                throw new ArgumentOutOfRangeException(nameof(flowControl));
            }

            if (windowSize < 1 || windowSize > 16 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(windowSize));
            }

            if (acknowledgementInterval < 1 || acknowledgementInterval > windowSize)
            {
                throw new ArgumentOutOfRangeException(nameof(acknowledgementInterval));
            }

            if (useRunLengthEncoding && (!useCrc32 || !useVariableHeaders))
            {
                throw new ArgumentException("Legacy RLE requires CRC32 and variable-header capability negotiation.", nameof(useRunLengthEncoding));
            }

            BlockSize = blockSize;
            MaxRetries = maxRetries;
            UseCrc32 = useCrc32;
            MaxBufferedBytes = maxBufferedBytes;
            Profile = profile;
            FlowControl = flowControl;
            WindowSize = windowSize;
            AcknowledgementInterval = acknowledgementInterval;
            UseVariableHeaders = useVariableHeaders;
            UseRunLengthEncoding = useRunLengthEncoding;
            MaximumSubpacketSize = maximum;
        }

        public int BlockSize
        {
            get;
        }

        public int MaxRetries
        {
            get;
        }

        public bool UseCrc32
        {
            get;
        }

        public int MaxBufferedBytes
        {
            get;
        }

        public ZModemProfile Profile
        {
            get;
        }

        public ZModemFlowControl FlowControl
        {
            get;
        }

        public int WindowSize
        {
            get;
        }

        public int AcknowledgementInterval
        {
            get;
        }

        public bool UseVariableHeaders
        {
            get;
        }

        public bool UseRunLengthEncoding
        {
            get;
        }

        public int MaximumSubpacketSize
        {
            get;
        }
    }

    public sealed class ZModemSnapshot
    {
        internal ZModemSnapshot(ZModemPhase phase, uint position, int retryCount, string? failureReason)
        {
            Phase = phase;
            Position = position;
            RetryCount = retryCount;
            FailureReason = failureReason;
        }

        public ZModemPhase Phase
        {
            get;
        }

        public uint Position
        {
            get;
        }

        public int RetryCount
        {
            get;
        }

        public string? FailureReason
        {
            get;
        }
    }

    public sealed class ZModemStepResult
    {
        internal ZModemStepResult(ZModemSnapshot snapshot, IReadOnlyList<ZModemAction> actions)
        {
            Snapshot = snapshot;
            Actions = actions;
        }

        public ZModemSnapshot Snapshot
        {
            get;
        }

        public IReadOnlyList<ZModemAction> Actions
        {
            get;
        }
    }

    public abstract class ZModemEvent
    {
        private ZModemEvent()
        {
        }

        public sealed class StartRequested : ZModemEvent
        {
        }

        public sealed class TimeoutElapsed : ZModemEvent
        {
        }

        public sealed class NoMoreFiles : ZModemEvent
        {
        }

        public sealed class FileSkipped : ZModemEvent
        {
        }

        public sealed class DataBlockAccepted : ZModemEvent
        {
        }

        public sealed class DataBlockRejected : ZModemEvent
        {
        }

        public sealed class PeerBytesReceived : ZModemEvent
        {
            public PeerBytesReceived(byte[] bytes)
            {
                Bytes = bytes == null ? throw new ArgumentNullException(nameof(bytes)) : (byte[])bytes.Clone();
            }

            public byte[] Bytes
            {
                get;
            }
        }

        public sealed class FileHeaderReady : ZModemEvent
        {
            public FileHeaderReady(ZModemFileDescriptor file)
            {
                File = file ?? throw new ArgumentNullException(nameof(file));
            }

            public ZModemFileDescriptor File
            {
                get;
            }
        }

        public sealed class FileAccepted : ZModemEvent
        {
            public FileAccepted(uint resumeOffset = 0)
            {
                ResumeOffset = resumeOffset;
            }

            public uint ResumeOffset
            {
                get;
            }
        }

        public sealed class DataBlockReady : ZModemEvent
        {
            public DataBlockReady(uint offset, byte[] data)
            {
                Offset = offset;
                Data = data == null ? throw new ArgumentNullException(nameof(data)) : (byte[])data.Clone();
            }

            public uint Offset
            {
                get;
            }

            public byte[] Data
            {
                get;
            }
        }

        public sealed class FileCrcRequested : ZModemEvent
        {
            public FileCrcRequested(uint byteCount = 0)
            {
                ByteCount = byteCount;
            }

            public uint ByteCount
            {
                get;
            }
        }

        public sealed class FileCrcReady : ZModemEvent
        {
            public FileCrcReady(uint crc)
            {
                Crc = crc;
            }

            public uint Crc
            {
                get;
            }
        }

        public sealed class CancelRequested : ZModemEvent
        {
            public CancelRequested(string? reason = null)
            {
                Reason = reason ?? "Transfer cancelled.";
            }

            public string Reason
            {
                get;
            }
        }
    }

    public abstract class ZModemAction
    {
        private ZModemAction()
        {
        }

        public sealed class RequestFileHeader : ZModemAction
        {
        }

        public sealed class Complete : ZModemAction
        {
        }

        public sealed class SendBytes : ZModemAction
        {
            public SendBytes(byte[] bytes)
            {
                Bytes = bytes == null ? throw new ArgumentNullException(nameof(bytes)) : (byte[])bytes.Clone();
            }

            public byte[] Bytes
            {
                get;
            }
        }

        public sealed class RequestDataBlock : ZModemAction
        {
            public RequestDataBlock(uint offset, int maxLength)
            {
                Offset = offset;
                MaxLength = maxLength;
            }

            public uint Offset
            {
                get;
            }

            public int MaxLength
            {
                get;
            }
        }

        public sealed class RequestFileCrc : ZModemAction
        {
            public RequestFileCrc(ZModemFileDescriptor file, uint byteCount)
            {
                File = file ?? throw new ArgumentNullException(nameof(file));
                ByteCount = byteCount;
            }

            public ZModemFileDescriptor File
            {
                get;
            }

            public uint ByteCount
            {
                get;
            }
        }

        public sealed class FileCrcReceived : ZModemAction
        {
            public FileCrcReceived(uint byteCount, uint crc)
            {
                ByteCount = byteCount;
                Crc = crc;
            }

            public uint ByteCount
            {
                get;
            }

            public uint Crc
            {
                get;
            }
        }

        public sealed class OfferFileHeader : ZModemAction
        {
            public OfferFileHeader(ZModemFileDescriptor file, ZModemHeader? header = null)
            {
                File = file ?? throw new ArgumentNullException(nameof(file));
                Header = header;
            }

            public ZModemFileDescriptor File
            {
                get;
            }

            public ZModemHeader? Header
            {
                get;
            }
        }

        public sealed class DeliverDataBlock : ZModemAction
        {
            public DeliverDataBlock(uint offset, byte[] payload)
            {
                Offset = offset;
                Payload = (byte[])(payload ?? throw new ArgumentNullException(nameof(payload))).Clone();
            }

            public uint Offset
            {
                get;
            }

            public byte[] Payload
            {
                get;
            }
        }

        public sealed class FileCompleted : ZModemAction
        {
            public FileCompleted(ZModemFileDescriptor file, uint length)
            {
                File = file ?? throw new ArgumentNullException(nameof(file));
                Length = length;
            }

            public ZModemFileDescriptor File
            {
                get;
            }

            public uint Length
            {
                get;
            }
        }

        public sealed class FileSkipped : ZModemAction
        {
            public FileSkipped(ZModemFileDescriptor file)
            {
                File = file ?? throw new ArgumentNullException(nameof(file));
            }

            public ZModemFileDescriptor File
            {
                get;
            }
        }

        public sealed class Cancel : ZModemAction
        {
            public Cancel(string reason)
            {
                Reason = reason;
            }

            public string Reason
            {
                get;
            }
        }

        public sealed class Fail : ZModemAction
        {
            public Fail(string reason)
            {
                Reason = reason;
            }

            public string Reason
            {
                get;
            }
        }
    }
}
