using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Security.Authentication;

namespace System.Net.Transport;

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public static class TransportProviders
{
    public static TransportProvider CreateDefault() => throw new NotImplementedException();

    public static TransportProvider ManagedSockets(
        Sockets.ManagedSocketTransportOptions? options = null)
        => throw new NotImplementedException();

    public static TransportProvider Epoll(
        Linux.EpollTransportOptions? options = null)
        => throw new NotImplementedException();

    public static TransportProvider IoUring(
        Linux.IoUringTransportOptions? options = null)
        => throw new NotImplementedException();

    public static TransportProvider WindowsIocp(
        Windows.IocpTransportOptions? options = null)
        => throw new NotImplementedException();

    public static TransportProvider WindowsRio(
        Windows.RioTransportOptions? options = null)
        => throw new NotImplementedException();
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public abstract class TransportProvider
{
    protected TransportProvider()
    {
    }

    public abstract string Name { get; }
    public abstract bool IsSupported { get; }

    public abstract TransportEngine CreateEngine(
        TransportEngineOptions options,
        ITransportApplication application);
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportEngineOptions
{
    public int InitialWorkerCount { get; set; }
    public int MaximumWorkerCount { get; set; }
    public int MaximumConnectionsPerWorker { get; set; } = 4096;
    public bool PinWorkerThreads { get; set; }
    public bool TrackEndpoints { get; set; } = true;
    public TimeSpan IdleTimeout { get; set; }
}

public interface ITransportApplication
{
    void OnAccepting(ref TransportAcceptingContext context);
    void OnReady(ref TransportReadyContext context);
    void OnConnectFailed(ref TransportConnectFailedContext context);
    void OnReceive(ref TransportReceiveContext context);
    void OnWriteCompleted(ref TransportWriteCompletedContext context);
    void OnClosed(ref TransportClosedContext context);
    void OnListenerClosed(ref TransportListenerClosedContext context);
    void OnWorkerFaulted(ref TransportWorkerFaultedContext context);
    void OnBatchCompleted(int workerIndex);
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public abstract class TransportApplication : ITransportApplication
{
    protected TransportApplication()
    {
    }

    protected virtual void OnAccepting(ref TransportAcceptingContext context)
    {
    }

    protected virtual void OnReady(ref TransportReadyContext context)
    {
    }

    protected virtual void OnConnectFailed(ref TransportConnectFailedContext context)
    {
    }

    protected virtual void OnReceive(ref TransportReceiveContext context)
    {
    }

    protected virtual void OnWriteCompleted(ref TransportWriteCompletedContext context)
    {
    }

    protected virtual void OnClosed(ref TransportClosedContext context)
    {
    }

    protected virtual void OnListenerClosed(ref TransportListenerClosedContext context)
    {
    }

    protected virtual void OnWorkerFaulted(ref TransportWorkerFaultedContext context)
    {
    }

    protected virtual void OnBatchCompleted(int workerIndex)
    {
    }

    void ITransportApplication.OnAccepting(ref TransportAcceptingContext context) => OnAccepting(ref context);
    void ITransportApplication.OnReady(ref TransportReadyContext context) => OnReady(ref context);
    void ITransportApplication.OnConnectFailed(ref TransportConnectFailedContext context) => OnConnectFailed(ref context);
    void ITransportApplication.OnReceive(ref TransportReceiveContext context) => OnReceive(ref context);
    void ITransportApplication.OnWriteCompleted(ref TransportWriteCompletedContext context) => OnWriteCompleted(ref context);
    void ITransportApplication.OnClosed(ref TransportClosedContext context) => OnClosed(ref context);
    void ITransportApplication.OnListenerClosed(ref TransportListenerClosedContext context) => OnListenerClosed(ref context);
    void ITransportApplication.OnWorkerFaulted(ref TransportWorkerFaultedContext context) => OnWorkerFaulted(ref context);
    void ITransportApplication.OnBatchCompleted(int workerIndex) => OnBatchCompleted(workerIndex);
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public abstract class TransportEngine : IDisposable
{
    protected TransportEngine()
    {
    }

    public abstract TransportProvider Provider { get; }
    public abstract TransportEngineOptions Options { get; }
    public abstract int WorkerCount { get; }

    public abstract TransportListener Listen(TransportListenOptions options);
    public abstract TransportConnectOperation Connect(TransportConnectOptions options);
    public abstract void Dispose();
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportListenOptions
{
    public required IPEndPoint EndPoint { get; init; }
    public int Backlog { get; set; } = 512;
    public bool NoDelay { get; set; } = true;
    public object? State { get; init; }
    public TransportServerTlsOptions? Tls { get; set; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public abstract class TransportListener : IDisposable
{
    protected TransportListener()
    {
    }

    public abstract long Id { get; }
    public abstract IPEndPoint LocalEndPoint { get; }
    public abstract object? State { get; }
    public abstract bool IsAccepting { get; }
    public abstract void Dispose();
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportConnectOptions
{
    public required EndPoint RemoteEndPoint { get; init; }
    public IPEndPoint? LocalEndPoint { get; init; }
    public bool NoDelay { get; set; } = true;
    public int? RequiredWorkerIndex { get; init; }
    public object? State { get; init; }
    public TransportClientTlsOptions? Tls { get; set; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public readonly struct TransportConnectOperation : IEquatable<TransportConnectOperation>
{
    public long Id { get; }
    public object? State { get; }
    public bool IsValid { get; }
    public bool Cancel() => false;
    public bool Equals(TransportConnectOperation other) => Id == other.Id;
    public override bool Equals(object? obj) => obj is TransportConnectOperation other && Equals(other);
    public override int GetHashCode() => Id.GetHashCode();
}

public enum TransportConnectionOrigin
{
    Accepted = 0,
    Connected = 1,
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public abstract class TransportConnection : IBufferWriter<byte>
{
    protected TransportConnection()
    {
    }

    public abstract long Id { get; }
    public abstract object? State { get; set; }
    public abstract IPEndPoint? LocalEndPoint { get; }
    public abstract IPEndPoint? RemoteEndPoint { get; }
    public abstract TransportConnectionOrigin Origin { get; }
    public abstract TransportTlsInfo? TlsInfo { get; }

    public abstract bool SupportsReceivePause { get; }
    public abstract bool TryPauseReceive();
    public abstract void ResumeReceive();

    public abstract Span<byte> GetSpan(int sizeHint = 0);
    public abstract Memory<byte> GetMemory(int sizeHint = 0);
    public abstract void Advance(int count);

    public abstract TransportWriteOperation Flush(object? state = null);

    public virtual TransportWriteOperation Send(
        ReadOnlySpan<byte> data,
        object? state = null)
        => throw new NotImplementedException();

    public virtual TransportWriteOperation Send(
        in ReadOnlySequence<byte> data,
        object? state = null)
        => throw new NotImplementedException();

    public abstract void ShutdownRead();
    public abstract void ShutdownWrite();
    public abstract void Abort(Exception? error = null);
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public readonly struct TransportWriteOperation : IEquatable<TransportWriteOperation>
{
    public long Id { get; }
    public object? State { get; }
    public bool IsValid { get; }
    public bool Equals(TransportWriteOperation other) => Id == other.Id;
    public override bool Equals(object? obj) => obj is TransportWriteOperation other && Equals(other);
    public override int GetHashCode() => Id.GetHashCode();
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public ref struct TransportAcceptingContext
{
    public TransportListener Listener => throw new NotImplementedException();
    public TransportConnection Connection => throw new NotImplementedException();
    public TransportServerTlsOptions? Tls { get; set; }
    public void Reject(Exception? error = null)
    {
    }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public ref struct TransportReadyContext
{
    public TransportConnection Connection => throw new NotImplementedException();
    public TransportConnectionOrigin Origin { get; }
    public TransportListener? Listener => throw new NotImplementedException();
    public TransportConnectOperation ConnectOperation { get; }
    public Span<byte> GetWriteSpan(int sizeHint = 0) => throw new NotImplementedException();
    public int WriteBytes { get; set; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public ref struct TransportReceiveContext
{
    public TransportConnection Connection => throw new NotImplementedException();
    public ReadOnlySpan<byte> Payload => throw new NotImplementedException();
    public bool IsCompleted { get; }
    public Span<byte> GetResponseSpan(int sizeHint = 0) => throw new NotImplementedException();
    public int ResponseBytes { get; set; }
    public void StopReceiving()
    {
    }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public ref struct TransportWriteCompletedContext
{
    public TransportConnection Connection => throw new NotImplementedException();
    public TransportWriteOperation Operation { get; }
    public Exception? Error => throw new NotImplementedException();
    public Span<byte> GetWriteSpan(int sizeHint = 0) => throw new NotImplementedException();
    public int WriteBytes { get; set; }
}

public enum TransportConnectionPhase
{
    Connecting = 0,
    Accepting = 1,
    Handshaking = 2,
    Ready = 3,
    Closing = 4,
}

public enum TransportCloseReason
{
    LocalShutdown = 0,
    LocalAbort = 1,
    PeerClosed = 2,
    PeerReset = 3,
    ConnectFailed = 4,
    TlsHandshakeFailed = 5,
    Timeout = 6,
    ProviderStopped = 7,
    Error = 8,
}

public readonly ref struct TransportConnectFailedContext
{
    public TransportConnectOperation Operation { get; }
    public Exception Error => throw new NotImplementedException();
}

public readonly ref struct TransportClosedContext
{
    public TransportConnection Connection => throw new NotImplementedException();
    public TransportConnectionPhase Phase { get; }
    public TransportCloseReason Reason { get; }
    public Exception? Error => throw new NotImplementedException();
}

public readonly ref struct TransportListenerClosedContext
{
    public TransportListener Listener => throw new NotImplementedException();
    public Exception? Error => throw new NotImplementedException();
}

public readonly ref struct TransportWorkerFaultedContext
{
    public int WorkerIndex { get; }
    public Exception Error => throw new NotImplementedException();
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public static class TransportExecutionContext
{
    public static int CurrentWorkerIndex => -1;
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportClientTlsOptions
{
    public required SslClientAuthenticationOptions AuthenticationOptions { get; init; }
    public TransportTlsOffloadOptions Offload { get; init; } = new();
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportServerTlsOptions
{
    public required SslServerAuthenticationOptions AuthenticationOptions { get; init; }
    public TransportClientHelloCallback? ClientHelloCallback { get; init; }
    public TransportServerOptionsSelectionCallback? OptionsSelectionCallback { get; init; }
    public TimeSpan ClientHelloTimeout { get; set; } = TimeSpan.FromSeconds(8);
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public bool AllowPostHandshakeClientAuthentication { get; init; }
    public TransportTlsOffloadOptions Offload { get; init; } = new();
}

public delegate void TransportClientHelloCallback(
    ref TransportClientHelloContext context);

public delegate ValueTask<SslServerAuthenticationOptions>
    TransportServerOptionsSelectionCallback(
        TransportServerOptionsSelectionContext context,
        CancellationToken cancellationToken);

public readonly ref struct TransportClientHelloContext
{
    public TransportConnection Connection => throw new NotImplementedException();
    public SslClientHelloInfo ClientHelloInfo { get; }
    public ReadOnlySequence<byte> FirstRecordBytes { get; }
    public bool ContainsCompleteClientHello { get; }
}

public sealed class TransportServerOptionsSelectionContext
{
    internal TransportServerOptionsSelectionContext()
    {
    }

    public TransportConnection Connection => throw new NotImplementedException();
    public SslClientHelloInfo ClientHelloInfo { get; }
    public SslServerAuthenticationOptions DefaultOptions => throw new NotImplementedException();
}

public sealed class TransportTlsOffloadOptions
{
    public TlsOffloadPolicy Transmit { get; set; }
    public TlsOffloadPolicy Receive { get; set; }
}

public enum TlsOffloadPolicy
{
    Disabled = 0,
    Prefer = 1,
    Require = 2,
}

public abstract class TransportTlsInfo
{
    protected TransportTlsInfo()
    {
    }

    public abstract SslProtocols Protocol { get; }
    public abstract TlsCipherSuite? NegotiatedCipherSuite { get; }
    public abstract SslApplicationProtocol ApplicationProtocol { get; }
    public abstract string? ServerName { get; }
    public abstract TransportTlsOffloadInfo Offload { get; }
}

public sealed class TransportTlsOffloadInfo
{
    public bool TransmitKernelRecordLayerActive { get; init; }
    public bool ReceiveKernelRecordLayerActive { get; init; }
}
