namespace System.Net.Transport.Sockets
{
    public sealed class ManagedSocketTransportOptions
    {
        public int ReceiveBufferSize { get; set; } = 4096;
        public int WriteBufferSize { get; set; } = 4096;
        public int WriteBufferCount { get; set; } = 1024;
        public bool WaitForDataBeforeAllocatingBuffer { get; set; } = true;
        public bool PreferInlineCompletions { get; set; }
    }
}

namespace System.Net.Transport.Epoll
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
    }

}

namespace System.Net.Transport.IoUring
{
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
    }
}

namespace System.Net.Transport.Iocp
{
    public sealed class IocpTransportOptions
    {
        public int CompletionBatchSize { get; set; } = 128;
        public int AcceptConcurrency { get; set; } = 32;
        public int ReceiveBufferSize { get; set; } = 4096;
        public int WriteBufferSize { get; set; } = 4096;
        public int WriteBufferCount { get; set; } = 1024;
    }

}

namespace System.Net.Transport.Rio
{
    public sealed class RioTransportOptions
    {
        public int CompletionQueueSize { get; set; } = 4096;
        public int AcceptConcurrency { get; set; } = 32;
        public int ReceiveBufferSize { get; set; } = 4096;
        public int SendBufferSize { get; set; } = 65536;
        public int RegisteredSendBufferCount { get; set; } = 256;
    }
}
