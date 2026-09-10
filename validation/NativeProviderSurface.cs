using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace System.Net.Transport.Linux
{
    [SupportedOSPlatform("linux")]
    [Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
    public sealed class EpollTransportProvider : TransportProvider
    {
        public EpollTransportProvider()
        {
        }

        public override string Name => "epoll";

        public override ValueTask<TransportListener> ListenAsync(
            TransportListenOptions options,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<TransportConnection> ConnectAsync(
            TransportConnectOptions options,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public override ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    [SupportedOSPlatform("linux")]
    [Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
    public sealed class IoUringTransportProvider : TransportProvider
    {
        public IoUringTransportProvider()
        {
        }

        public override string Name => "io_uring";

        public override ValueTask<TransportListener> ListenAsync(
            TransportListenOptions options,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<TransportConnection> ConnectAsync(
            TransportConnectOptions options,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public override ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}

namespace System.Net.Transport.Windows
{
    [SupportedOSPlatform("windows")]
    [Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
    public sealed class IocpTransportProvider : TransportProvider
    {
        public IocpTransportProvider()
        {
        }

        public override string Name => "iocp";

        public override ValueTask<TransportListener> ListenAsync(
            TransportListenOptions options,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public override ValueTask<TransportConnection> ConnectAsync(
            TransportConnectOptions options,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public override ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
