using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Authentication.ExtendedProtection;
using System.Security.Cryptography.X509Certificates;

namespace System.Net.Transport;

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public abstract class TransportProvider : IAsyncDisposable
{
    protected TransportProvider()
    {
    }

    public abstract string Name { get; }

    public abstract ValueTask<TransportListener> ListenAsync(
        TransportListenOptions options,
        CancellationToken cancellationToken = default);

    public abstract ValueTask<TransportConnection> ConnectAsync(
        TransportConnectOptions options,
        CancellationToken cancellationToken = default);

    public abstract ValueTask DisposeAsync();
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportListenOptions
{
    public TransportListenOptions()
    {
    }

    public required IPEndPoint EndPoint { get; init; }
    public int Backlog { get; set; } = 512;
    public bool NoDelay { get; set; } = true;
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportConnectOptions
{
    public TransportConnectOptions()
    {
    }

    public required EndPoint RemoteEndPoint { get; init; }
    public IPEndPoint? LocalEndPoint { get; init; }
    public bool NoDelay { get; set; } = true;
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public abstract class TransportListener : IAsyncDisposable
{
    protected TransportListener()
    {
    }

    public abstract IPEndPoint LocalEndPoint { get; }

    public abstract ValueTask<TransportConnection> AcceptAsync(
        CancellationToken cancellationToken = default);

    public abstract ValueTask DisposeAsync();
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public abstract class TransportConnection : IAsyncDisposable
{
    protected TransportConnection()
    {
    }

    public abstract IPEndPoint? LocalEndPoint { get; }
    public abstract IPEndPoint? RemoteEndPoint { get; }
    public abstract Task Completion { get; }
    public abstract TransportTlsInfo? TlsInfo { get; }

    public abstract ValueTask<TransportReadResult> ReadAsync(
        CancellationToken cancellationToken = default);

    public abstract void AdvanceRead(
        SequencePosition consumed,
        SequencePosition examined);

    public abstract ValueTask WriteAsync(
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken = default);

    public abstract ValueTask ObserveTlsClientHelloAsync(
        TransportClientHelloCallback callback,
        CancellationToken cancellationToken = default);

    public abstract ValueTask<TransportTlsInfo> AuthenticateAsClientAsync(
        TransportClientAuthenticationOptions options,
        CancellationToken cancellationToken = default);

    public abstract ValueTask<TransportTlsInfo> AuthenticateAsServerAsync(
        TransportServerAuthenticationOptions options,
        CancellationToken cancellationToken = default);

    public abstract Task<X509Certificate2?> RequestClientCertificateAsync(
        CancellationToken cancellationToken = default);

    public abstract ValueTask ShutdownWriteAsync(
        CancellationToken cancellationToken = default);

    public abstract void Abort(Exception? error = null);

    public abstract ValueTask DisposeAsync();
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public readonly struct TransportReadResult
{
    public TransportReadResult(
        ReadOnlySequence<byte> buffer,
        bool isCompleted)
    {
        Buffer = buffer;
        IsCompleted = isCompleted;
    }

    public ReadOnlySequence<byte> Buffer { get; }
    public bool IsCompleted { get; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportClientAuthenticationOptions
{
    public TransportClientAuthenticationOptions()
    {
    }

    public required SslClientAuthenticationOptions AuthenticationOptions { get; init; }
    public TransportTlsOffloadOptions Offload { get; init; } = new();
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportServerAuthenticationOptions
{
    public TransportServerAuthenticationOptions()
    {
    }

    public required SslServerAuthenticationOptions AuthenticationOptions { get; init; }
    public TransportClientHelloCallback? ClientHelloCallback { get; init; }
    public TransportServerOptionsSelectionCallback? OptionsSelectionCallback { get; init; }
    public bool AllowPostHandshakeClientAuthentication { get; init; }
    public TransportTlsOffloadOptions Offload { get; init; } = new();
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public delegate ValueTask TransportClientHelloCallback(
    TransportServerHandshakeContext context,
    CancellationToken cancellationToken);

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public delegate ValueTask<SslServerAuthenticationOptions> TransportServerOptionsSelectionCallback(
    TransportServerHandshakeContext context,
    SslServerAuthenticationOptions defaultOptions,
    CancellationToken cancellationToken);

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportServerHandshakeContext
{
    public TransportServerHandshakeContext(
        TransportConnection connection,
        SslClientHelloInfo clientHelloInfo,
        ReadOnlySequence<byte> firstRecordBytes,
        bool containsCompleteClientHello)
    {
        Connection = connection;
        ClientHelloInfo = clientHelloInfo;
        FirstRecordBytes = firstRecordBytes;
        ContainsCompleteClientHello = containsCompleteClientHello;
    }

    public TransportConnection Connection { get; }
    public SslClientHelloInfo ClientHelloInfo { get; }
    public ReadOnlySequence<byte> FirstRecordBytes { get; }
    public bool ContainsCompleteClientHello { get; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportTlsOffloadOptions
{
    public TransportTlsOffloadOptions()
    {
    }

    public TlsOffloadPolicy Transmit { get; set; }
    public TlsOffloadPolicy Receive { get; set; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public enum TlsOffloadPolicy
{
    Disabled = 0,
    Prefer = 1,
    Require = 2,
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public abstract class TransportTlsInfo
{
    protected TransportTlsInfo()
    {
    }

    public abstract SslProtocols Protocol { get; }
    public abstract TlsCipherSuite? NegotiatedCipherSuite { get; }
    public abstract SslApplicationProtocol ApplicationProtocol { get; }
    public abstract string? ServerName { get; }
    public abstract X509Certificate2? RemoteCertificate { get; }
    public abstract TransportTlsOffloadInfo Offload { get; }

    public abstract bool TryGetChannelBindingBytes(
        ChannelBindingKind kind,
        out ReadOnlyMemory<byte> channelBindingToken);
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportTlsOffloadInfo
{
    public TransportTlsOffloadInfo(
        TransportTlsOffloadDirectionInfo transmit,
        TransportTlsOffloadDirectionInfo receive)
    {
        Transmit = transmit;
        Receive = receive;
    }

    public TransportTlsOffloadDirectionInfo Transmit { get; }
    public TransportTlsOffloadDirectionInfo Receive { get; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public readonly struct TransportTlsOffloadDirectionInfo
{
    public TransportTlsOffloadDirectionInfo(
        TlsOffloadPolicy requestedPolicy,
        bool kernelRecordLayerActive,
        TlsHardwareOffloadStatus hardwareOffload,
        TlsOffloadFallbackReason fallbackReason)
    {
        RequestedPolicy = requestedPolicy;
        KernelRecordLayerActive = kernelRecordLayerActive;
        HardwareOffload = hardwareOffload;
        FallbackReason = fallbackReason;
    }

    public TlsOffloadPolicy RequestedPolicy { get; }
    public bool KernelRecordLayerActive { get; }
    public TlsHardwareOffloadStatus HardwareOffload { get; }
    public TlsOffloadFallbackReason FallbackReason { get; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public enum TlsHardwareOffloadStatus
{
    NotApplicable = 0,
    Inactive = 1,
    Active = 2,
    Unknown = 3,
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public enum TlsOffloadFallbackReason
{
    None = 0,
    ProviderBuild = 1,
    OperatingSystem = 2,
    Kernel = 3,
    Protocol = 4,
    CipherSuite = 5,
    TlsFeature = 6,
    Backend = 7,
    Unknown = 8,
}
