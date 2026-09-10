namespace System.Net.Transport.Sockets
{
    public sealed class ManagedSocketTransportOptions
    {
        public int ReceiveBufferSize { get; set; } = 4096;
        public int WriteBufferSize { get; set; } = 4096;
        public int WriteBufferCount { get; set; } = 1024;
        public bool WaitForDataBeforeAllocatingBuffer { get; set; } = true;
        public bool PreferInlineCompletions { get; set; }
        public ManagedSocketTlsStrategy TlsStrategy { get; set; } =
            ManagedSocketTlsStrategy.Auto;
    }

    public enum ManagedSocketTlsStrategy
    {
        Auto = 0,
        SslStream = 1,
        PlatformFilter = 2,
    }
}

namespace System.Net.Transport.Linux
{
    public sealed class EpollTransportOptions
    {
        public int MaximumEventsPerWait { get; set; } = 256;
        public int ReadBurstLimit { get; set; } = 8;
        public int WriteBurstLimit { get; set; } = 16;
        public int ReceiveBufferSize { get; set; } = 4096;
        public int WriteBufferSize { get; set; } = 4096;
        public int WriteBufferCount { get; set; } = 1024;
        public bool ReusePort { get; set; } = true;
        public EpollTlsStrategy TlsStrategy { get; set; } = EpollTlsStrategy.Auto;
    }

    public enum EpollTlsStrategy
    {
        Auto = 0,
        SocketBoundOpenSsl = 1,
        MemoryBio = 2,
        SslStream = 3,
    }

    public sealed class IoUringTransportOptions
    {
        public int RingEntryCount { get; set; } = 4096;
        public int ProvidedBufferCount { get; set; } = 256;
        public int ReceiveBufferSize { get; set; } = 4096;
        public int WriteBufferSize { get; set; } = 4096;
        public int WriteBufferCount { get; set; } = 1024;
        public int OutOfBandWriteBufferCount { get; set; } = 256;
        public int MaximumBorrowedReceiveBuffers { get; set; } = 128;
        public bool ReusePort { get; set; } = true;
        public IoUringTlsStrategy TlsStrategy { get; set; } = IoUringTlsStrategy.Auto;
    }

    public enum IoUringTlsStrategy
    {
        Auto = 0,
        MemoryBio = 1,
        SocketBoundPoll = 2,
        SslStream = 3,
    }
}

namespace System.Net.Transport.Windows
{
    public sealed class IocpTransportOptions
    {
        public int CompletionBatchSize { get; set; } = 128;
        public int AcceptConcurrency { get; set; } = 32;
        public int ReceiveBufferSize { get; set; } = 4096;
        public int WriteBufferSize { get; set; } = 4096;
        public int WriteBufferCount { get; set; } = 1024;
        public WindowsTlsStrategy TlsStrategy { get; set; } = WindowsTlsStrategy.Auto;
    }

    public sealed class RioTransportOptions
    {
        public int CompletionQueueSize { get; set; } = 4096;
        public int AcceptConcurrency { get; set; } = 32;
        public int ReceiveBufferSize { get; set; } = 4096;
        public int SendBufferSize { get; set; } = 65536;
        public int RegisteredSendBufferCount { get; set; } = 256;
        public WindowsTlsStrategy TlsStrategy { get; set; } = WindowsTlsStrategy.Auto;
    }

    public enum WindowsTlsStrategy
    {
        Auto = 0,
        Schannel = 1,
        SslStream = 2,
    }
}
