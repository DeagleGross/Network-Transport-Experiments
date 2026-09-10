# Proposed API and behavioral contracts

> **Status:** Illustrative experimental API. Names, namespaces, assemblies, and signatures require runtime API review. This file is internally consistent with the examples in this proposal, but it is not a checked-in reference assembly.

## Design principles

- The core abstraction is a connected, reliable, ordered byte stream, not an operating-system socket.
- Provider instances own resources shared across connections.
- Receive memory is leased from the provider and explicitly advanced.
- Writes are all-or-error and retain source memory only until the returned operation completes.
- TLS authentication is part of the connection lifecycle and reuses existing `SslClientAuthenticationOptions` and `SslServerAuthenticationOptions`.
- The portable TLS implementation is `SslStream`; native providers use a shared runtime helper or their own native implementation only when they preserve configured semantics.
- Public APIs do not expose epoll flags, SQEs, CQEs, OVERLAPPED pointers, OpenSSL handles, or kernel TLS socket options.
- Provider-specific tuning belongs on provider-specific experimental types.
- Abstract base classes, rather than interfaces, reserve space for shared implementation helpers and non-breaking virtual evolution while still allowing out-of-assembly providers.

## Candidate reference surface

```csharp
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
    protected TransportProvider();

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
    public TransportListenOptions();

    public required IPEndPoint EndPoint { get; init; }
    public int Backlog { get; set; } = 512;
    public bool NoDelay { get; set; } = true;
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportConnectOptions
{
    public TransportConnectOptions();

    public required EndPoint RemoteEndPoint { get; init; }
    public IPEndPoint? LocalEndPoint { get; init; }
    public bool NoDelay { get; set; } = true;
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public abstract class TransportListener : IAsyncDisposable
{
    protected TransportListener();

    public abstract IPEndPoint LocalEndPoint { get; }

    public abstract ValueTask<TransportConnection> AcceptAsync(
        CancellationToken cancellationToken = default);

    public abstract ValueTask DisposeAsync();
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public abstract class TransportConnection : IAsyncDisposable
{
    protected TransportConnection();

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
        bool isCompleted);

    public ReadOnlySequence<byte> Buffer { get; }
    public bool IsCompleted { get; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportClientAuthenticationOptions
{
    public TransportClientAuthenticationOptions();

    public required SslClientAuthenticationOptions AuthenticationOptions { get; init; }
    public TransportTlsOffloadOptions Offload { get; init; } = new();
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportServerAuthenticationOptions
{
    public TransportServerAuthenticationOptions();

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
        bool containsCompleteClientHello);

    public TransportConnection Connection { get; }
    public SslClientHelloInfo ClientHelloInfo { get; }
    public ReadOnlySequence<byte> FirstRecordBytes { get; }
    public bool ContainsCompleteClientHello { get; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportTlsOffloadOptions
{
    public TransportTlsOffloadOptions();

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
    protected TransportTlsInfo();

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
        TransportTlsOffloadDirectionInfo receive);

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
        TlsOffloadFallbackReason fallbackReason);

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
```

The provider-specific managed fallback is the only implementation type proposed for the first experimental increment:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Net.Transport;

namespace System.Net.Transport.Sockets;

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class SocketTransportProvider : TransportProvider
{
    public SocketTransportProvider();

    public override string Name { get; }

    public TransportConnection CreateConnection(
        Socket socket,
        bool ownsSocket);

    public TransportListener CreateListener(
        Socket socket,
        bool ownsSocket);

    public override ValueTask<TransportListener> ListenAsync(
        TransportListenOptions options,
        CancellationToken cancellationToken = default);

    public override ValueTask<TransportConnection> ConnectAsync(
        TransportConnectOptions options,
        CancellationToken cancellationToken = default);

    public override ValueTask DisposeAsync();
}
```

The reusable Pipelines adapter is a companion layer:

```csharp
using System.IO.Pipelines;
using System.Net.Transport;

namespace System.Net.Transport.Pipelines;

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportPipeOptions
{
    public TransportPipeOptions();

    public PipeOptions InputOptions { get; set; } = PipeOptions.Default;
    public PipeOptions OutputOptions { get; set; } = PipeOptions.Default;
    public bool LeaveOpen { get; set; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportDuplexPipe : IDuplexPipe, IAsyncDisposable
{
    public PipeReader Input { get; }
    public PipeWriter Output { get; }
    public Task Completion { get; }

    public ValueTask DisposeAsync();
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public static class TransportPipelines
{
    public static TransportDuplexPipe Create(
        TransportConnection connection,
        TransportPipeOptions? options = null);
}
```

Illustrative native provider packages can derive from the same core without adding backend switches to the common options:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Net.Transport;

namespace System.Net.Transport.Linux
{
    [SupportedOSPlatform("linux")]
    [Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
    public sealed class EpollTransportProvider : TransportProvider
    {
        public EpollTransportProvider();

        public override string Name { get; }
        public override ValueTask<TransportListener> ListenAsync(TransportListenOptions options, CancellationToken cancellationToken = default);
        public override ValueTask<TransportConnection> ConnectAsync(TransportConnectOptions options, CancellationToken cancellationToken = default);
        public override ValueTask DisposeAsync();
    }

    [SupportedOSPlatform("linux")]
    [Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
    public sealed class IoUringTransportProvider : TransportProvider
    {
        public IoUringTransportProvider();

        public override string Name { get; }
        public override ValueTask<TransportListener> ListenAsync(TransportListenOptions options, CancellationToken cancellationToken = default);
        public override ValueTask<TransportConnection> ConnectAsync(TransportConnectOptions options, CancellationToken cancellationToken = default);
        public override ValueTask DisposeAsync();
    }
}

namespace System.Net.Transport.Windows
{
    [SupportedOSPlatform("windows")]
    [Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
    public sealed class IocpTransportProvider : TransportProvider
    {
        public IocpTransportProvider();

        public override string Name { get; }
        public override ValueTask<TransportListener> ListenAsync(TransportListenOptions options, CancellationToken cancellationToken = default);
        public override ValueTask<TransportConnection> ConnectAsync(TransportConnectOptions options, CancellationToken cancellationToken = default);
        public override ValueTask DisposeAsync();
    }
}
```

These native implementation type names are placeholders. They should not be stabilized until the implementations and their selection policy are proven.

## Assembly placement

The experimental phase should place the `System.Net.Transport` and `System.Net.Transport.Sockets` namespaces in `System.Net.Security.dll`. That assembly already owns `SslStream`, TLS option normalization, certificate validation, OpenSSL/Schannel PALs, and a dependency on sockets. Keeping the new implementation there avoids both duplicated TLS policy and prohibited cross-framework `InternalsVisibleTo` or `UnsafeAccessor` access. Namespace and assembly name do not need to match.

`System.Net.Transport.Pipelines` should be a companion assembly that depends on `System.Net.Security` and `System.IO.Pipelines`. Kestrel references the transport and adapter assemblies; runtime never references ASP.NET Core.

If later evidence supports splitting a dedicated `System.Net.Transport` assembly, the prerequisite is a deliberate extraction of the reusable TLS engine and policy into a lower dependency layer or a separately reviewed public contract. The split must not be implemented by granting a new friend-assembly boundary to reach `System.Net.Security` internals.

Native Linux and Windows providers remain runtime-built implementation types until their support policy is established. The design does not require a third-party native dependency in the shared framework.

## Consumer API versus backend SPI

The same abstract types have two clearly separated roles:

| Role | Surface used |
|---|---|
| Consumer creates or receives a provider | Concrete provider constructor or dependency injection |
| Consumer listens/connects | `ListenAsync`, `AcceptAsync`, `ConnectAsync` and the two TCP option types |
| Consumer performs I/O | `ReadAsync`, `AdvanceRead`, `WriteAsync`, `ShutdownWriteAsync`, `Abort`, `Completion` |
| Consumer configures TLS | Existing `Ssl*AuthenticationOptions` wrapped by transport authentication options |
| Consumer uses Pipelines | `TransportPipelines.Create` and the owned `TransportDuplexPipe` |
| Provider implementation | Derives `TransportProvider`, `TransportListener`, `TransportConnection`, and `TransportTlsInfo`; constructs `TransportReadResult`, handshake contexts, and offload result values |

The SPI is not a second set of submission/completion APIs. Providers translate their own epoll, SQE/CQE, SAEA, OVERLAPPED, OpenSSL, or Schannel state into the abstract operations and lifetime invariants. The public consumer never handles backend operation IDs.

An out-of-assembly provider is possible, but it receives no privileged access to runtime TLS internals. Such a provider either uses public `SslStream` over its connection adapter or implements TLS fully itself. Built-in runtime providers may share internal TLS policy because they live in the owning assembly.

## Member-by-member necessity

| Member | Why it exists |
|---|---|
| `TransportProvider` | Represents resources shared across connections: poller/ring/completion port, worker ownership, buffer pools, fixed files, and dispatch queues. A per-connection `Socket` cannot express this ownership boundary. An abstract base leaves room for shared helpers and non-breaking virtual additions. |
| Protected constructors on abstract contract types | Allow provider subclasses while preventing meaningless direct construction of an incomplete provider, listener, connection, or TLS-info base. |
| `Name` | Stable diagnostics and test output need to report the implementation actually selected. It is not used for behavioral branching. |
| `ListenAsync` | Binds and initializes backend resources that may fail asynchronously, including native registration. |
| `ConnectAsync` | Gives client libraries the same provider and connection model as servers. Completion means the underlying byte stream is connected, not TLS-authenticated. |
| Provider `DisposeAsync` | Stops listeners, aborts owned connections, drains terminal completions, and releases shared native memory safely. |
| `TransportListenOptions` constructor | Supports the standard required-property options pattern without constructor-overload growth. |
| `TransportListenOptions.EndPoint` | Uses `IPEndPoint` because the proposed first surface is specifically TCP over IP, not generic endpoint extensibility. |
| `Backlog` | Portable TCP listener behavior needed by Kestrel and other servers. |
| `NoDelay` | Portable TCP behavior used by low-latency servers and clients. It is applied to accepted/connected sockets. |
| `TransportConnectOptions` constructor | Supports the standard required-property options pattern without constructor-overload growth. |
| `TransportConnectOptions.RemoteEndPoint` | Required target; the first contract accepts `IPEndPoint` and `DnsEndPoint`, rejecting other endpoint kinds. |
| `TransportConnectOptions.LocalEndPoint` | Supports explicit source binding without exposing the whole socket API. |
| `TransportListener` | Separates listener lifetime from provider lifetime and accepted connection lifetime. An abstract base leaves room for common helpers and non-breaking virtual additions. |
| `TransportListener.LocalEndPoint` | Reports the actual bound endpoint, including an ephemeral port selected by the OS. |
| `AcceptAsync` | Returns one raw connected byte stream. TLS is explicit so a failed client handshake does not terminate the listener or ambiguously fault an accept stream. |
| `TransportConnection` | Defines the narrow post-connect stream and lifetime contract while leaving provider mechanics behind an abstract base that can share implementation helpers. |
| `TransportConnection.LocalEndPoint`/`RemoteEndPoint` | Common metadata needed by servers, clients, admission checks, metrics, and diagnostics. Values come from the connected transport, not merely copied arguments. |
| `Completion` | A reusable terminal signal for peer close, abort, and provider failure; required by long-lived clients and adapters without allocating one waiter per observation. |
| `TlsInfo` | Publishes immutable post-handshake facts. Configuration and actual outcome are deliberately separate. |
| `ReadAsync` | Returns provider-owned data without requiring a caller buffer, preserving io_uring provided-buffer identity and allowing multi-segment batches. |
| `AdvanceRead` | Defines exactly when provider memory may be returned to a ring or pool and communicates consumed/examined positions for backpressure. |
| `WriteAsync(ReadOnlySequence<byte>)` | Supports segmented Pipelines output and scatter/gather backends without forcing coalescing. Completion defines source-memory release. |
| `ObserveTlsClientHelloAsync` | Lets a server observe the first ClientHello record under a separate timeout before authentication, preserving Kestrel's `UseTlsClientHelloListener` phase. The provider retains the bytes for the later handshake. |
| `AuthenticateAsClientAsync`/`AuthenticateAsServerAsync` | Makes TLS a first-class lifecycle operation. The managed provider is the compatibility implementation; native providers may call a shared runtime `SslStream` adapter or implement the full semantics directly. |
| `RequestClientCertificateAsync` | Preserves Kestrel delayed client-certificate semantics when the provider supports post-handshake authentication. Unsupported providers fail explicitly. |
| `ShutdownWriteAsync` | Separates graceful close, TLS `close_notify`, and TCP FIN from abortive disposal. |
| `Abort` | Gives servers and clients an immediate failure path that does not wait for graceful drain. |
| Connection `DisposeAsync` | Waits for backend terminal completions before memory, operation IDs, slots, TLS state, or handles are reclaimed. |
| `TransportReadResult.Buffer` | Carries one or more completion-selected buffers as a `ReadOnlySequence<byte>`. |
| `IsCompleted` | Preserves the important "final bytes plus EOF" state. |
| `TransportReadResult` constructor | Lets an out-of-assembly provider create the value without exposing its native completion or buffer identifiers. |
| `TransportClientAuthenticationOptions`/`TransportServerAuthenticationOptions` constructors | Support required `AuthenticationOptions` properties and future optional settings without constructor-overload growth. |
| `Transport*AuthenticationOptions.AuthenticationOptions` | Reuses the existing runtime TLS semantic model rather than creating a parallel certificate/protocol/cipher API. |
| `Transport*AuthenticationOptions.Offload` | Keeps optional record-layer offload policy adjacent to, but distinct from, TLS negotiation policy. |
| `ClientHelloCallback` | Preserves raw first-record observation needed by current Kestrel users, including JA4-style consumers. |
| `OptionsSelectionCallback` | Supplies an async, provider-neutral per-connection option hook after ClientHello parsing. |
| `AllowPostHandshakeClientAuthentication` | Makes delayed client certificate behavior explicit and allows unsupported providers to reject before application traffic. |
| `TransportClientHelloCallback`/`TransportServerOptionsSelectionCallback` | Named delegates document timing and permit allocation-conscious implementations without introducing ASP.NET Core types. |
| `TransportServerHandshakeContext` constructor | Allows an external provider implementation to construct the callback context; the type contains only provider-neutral borrowed data. |
| `TransportServerHandshakeContext.Connection` | Gives library adapters stable connection identity and endpoint metadata without depending on ASP.NET Core `ConnectionContext`. |
| `ClientHelloInfo` | Reuses the runtime's parsed SNI/protocol-version view. |
| `FirstRecordBytes` | Preserves exact current Kestrel raw-record semantics; it is intentionally not named as a complete ClientHello. |
| `ContainsCompleteClientHello` | Prevents consumers from mistaking a first-record callback for a guaranteed complete handshake message. |
| `TransportTlsOffloadOptions.Transmit`/`Receive` | kTLS TX and RX are independently installed and may have different availability. |
| `TlsOffloadPolicy.Disabled`/`Prefer`/`Require` | Distinguishes no request, allowed fallback, and a hard activation requirement. |
| `TransportTlsInfo.Protocol`/`NegotiatedCipherSuite`/`ApplicationProtocol`/`ServerName` | Publishes the negotiated facts required by Kestrel and client protocol dispatch. |
| `TransportTlsInfo.RemoteCertificate` | Publishes the authenticated peer certificate with connection-scoped lifetime. |
| `TransportTlsInfo.TryGetChannelBindingBytes` | Preserves Kestrel's existing channel-binding feature without exposing TLS-library handles. |
| `TransportTlsInfo.Offload` | Publishes actual post-handshake offload state separately from requested policy and negotiated TLS. |
| `TransportTlsOffloadInfo.Transmit`/`Receive` | Keeps direction-specific outcomes together as one immutable handshake result. |
| `TransportTlsOffloadDirectionInfo` constructor | Lets an external provider report its typed result without exposing backend internals. |
| `RequestedPolicy` | Records which caller contract produced the result. |
| `KernelRecordLayerActive` | Reports actual activation after negotiation rather than restating configuration. |
| `HardwareOffload` | Separates kernel TLS from NIC crypto and allows an honest unknown state. |
| `FallbackReason` | Makes a `Prefer` fallback diagnosable without requiring backend-specific exception parsing. |
| `TlsHardwareOffloadStatus` values | Distinguish absence, proven inactivity, proven activity, and unavailable per-connection evidence. |
| `TlsOffloadFallbackReason` values | Classify the stable reason categories that determine whether `Prefer` fell back or `Require` failed. The API review recommends keeping the detailed enum diagnostic-only initially. |
| `TransportPipeOptions.InputOptions`/`OutputOptions` | Reuses `PipeOptions` for memory pool, four scheduler roles, and directional backpressure instead of inventing parallel knobs. |
| `TransportPipeOptions` constructor | Keeps adapter creation compatible with object-initializer configuration. |
| `LeaveOpen` | Allows adapters to participate in a larger owner without double-disposal. |
| `TransportDuplexPipe.Input`/`Output` | Supplies the standard `IDuplexPipe` orientation expected by Kestrel and protocol libraries. |
| `TransportDuplexPipe.Completion` | Reports adapter completion independently of the underlying connection terminal task. |
| `TransportDuplexPipe.DisposeAsync` | Coordinates both pumps, cancellation, pipe completion, and optional connection disposal. |
| `TransportPipelines.Create` | Centralizes the reusable bridge so Kestrel and clients do not independently reimplement ownership and shutdown. |
| `SocketTransportProvider.CreateConnection`/`CreateListener` | Provides explicit migration for existing managed sockets with unambiguous ownership. |
| `SocketTransportProvider` overrides | Supply the portable reference implementation against which native providers are tested. |
| `EpollTransportProvider`/`IoUringTransportProvider`/`IocpTransportProvider` | Demonstrate that provider choice is dependency injection, not a switch on `TransportConnection`; their names and constructors remain experimental. |

## Behavioral contract

### Provider lifetime

A provider is intended to be long-lived and shared. Creating one provider per connection is legal but defeats shared engine, pool, and registration reuse. Disposing a provider:

1. stops accepting new work;
2. causes pending `AcceptAsync` and `ConnectAsync` calls to fail;
3. initiates abort of connections it still owns;
4. waits for native terminal completions and callback work that can touch provider resources;
5. releases rings, completion ports, poll handles, registered memory, TLS contexts, and worker threads.

A listener owns its bound handle but not connections already returned by `AcceptAsync`.

Canceling one `AcceptAsync` wait does not close the listener. Disposing the listener stops new accepts and completes pending accepts with cancellation or disposal consistently across providers. Canceling `ConnectAsync` prevents a connection from being returned; the provider closes the in-progress socket and retains its native operation state until terminal completion.

### Existing Socket adoption

`SocketTransportProvider.CreateConnection` requires a connected TCP `Socket`. `CreateListener` requires a bound, listening TCP `Socket`. In both cases:

- the caller must have no active I/O and must not start independent I/O after adoption;
- `ownsSocket: true` transfers disposal ownership to the returned transport object;
- `ownsSocket: false` leaves final `Socket` disposal to the caller, but the caller still grants exclusive I/O use until the returned transport object is disposed;
- adoption does not duplicate the handle;
- provider disposal cannot close a non-owned socket, but it cancels and drains the operations it started before returning control.

These APIs are migration seams for existing Socket-based hosts, socket activation adapters, and tests. They are not a native-provider handle-transfer mechanism.

### Read lifetime

For each connection:

1. At most one `ReadAsync` is active.
2. A successful result establishes one active read lease.
3. `Buffer` remains valid until `AdvanceRead`.
4. `AdvanceRead` must be called exactly once before the next `ReadAsync`.
5. `consumed` releases complete segments before that position.
6. `examined` tells the provider whether the consumer needs more bytes to make progress.
7. A result may contain data with `IsCompleted == true`; the consumer must process and advance the final data.
8. An empty result with `IsCompleted == false` is not returned.
9. Both positions must belong to the active `Buffer`, and `consumed` must not be after `examined`.
10. Bytes at or after `consumed` remain part of the next read result until consumed. If `examined` is before the current end, the next read may complete immediately with the same unconsumed data; if it is at the end, the provider waits for additional data or terminal completion.

A canceled or faulted `ReadAsync` establishes no consumer lease and requires no `AdvanceRead`; the provider remains responsible for any native buffer already selected by a racing completion. Providers may pool the objects backing a successful result, but they must detect double advancement and stale use in debug/test builds. Native buffer identity remains internal.

### Write lifetime and ordering

- At most one `WriteAsync` is active.
- A read and a write may be active concurrently.
- The provider preserves byte order across calls.
- A write either accepts the entire sequence and completes when its memory is no longer retained, or throws.
- Partial `send`, `writev`, `WSASend`, SQE, or TLS-record progress is internal.
- Cancellation never makes source memory reusable before the provider has observed terminal native completion. The returned task does not complete as canceled until this guarantee holds, unless the provider copied the remaining bytes into memory it owns.
- If cancellation wins before any bytes are accepted, the connection may remain usable. If a prefix may already have been transmitted and the full write cannot complete, the provider aborts the connection before completing the operation with cancellation or error; it never leaves the caller with an apparently reusable stream after an unknown partial protocol write.

A transport receive EOF closes only the read half when the underlying protocol permits half-close. `Completion` does not complete until the connection is fully terminal, aborted, or disposed. A successful `ShutdownWriteAsync` prevents later writes but does not cancel an active or future read.

`Completion` completes successfully after both directions have ended orderly, or after an intentional local disposal with no supplied failure, and all native operations are terminal. It faults with the first terminal connection/provider error on failure or explicit abort; `Abort(null)` uses `OperationCanceledException` as the terminal error. Disposing an otherwise healthy connection initiates immediate local teardown unless graceful shutdown has already made both halves terminal, and `DisposeAsync` does not return before `Completion` is complete.

### Authentication transition

Authentication is valid only before application reads or writes start. Calling it while a read/write is active, after authentication, or after shutdown throws `InvalidOperationException`.

The implementation snapshots options for that connection. It does not retain a mutable options instance as shared live state.

`ObserveTlsClientHelloAsync` is valid only on an accepted server connection before authentication or application I/O. It reads and parses the first TLS record without losing those bytes from the later handshake, invokes the supplied callback only when that record is a TLS ClientHello, and then completes. Non-TLS input or EOF before a complete first record skips the observer and remains for authentication to reject. The callback's sequence is valid only until its returned `ValueTask` completes. `ContainsCompleteClientHello` is computed from the handshake-message length and reports whether that first record contains the whole message. The method may be called more than once before authentication; later calls observe the cached first record and do not reread the network. Cancellation or a callback exception fails the connection because the pre-authentication stream state is no longer safe to hand to unrelated plaintext logic.

When authentication succeeds:

- subsequent reads and writes are plaintext;
- `TlsInfo` is non-null and immutable;
- `Completion` covers both TLS and transport failures;
- the provider owns the TLS session and any certificate instance it exposes.

When authentication fails:

- the connection is aborted;
- no plaintext fallback occurs;
- the authentication operation throws the callback, authentication, cancellation, or unsupported-feature exception;
- `Completion` reaches the same terminal state.

### Callback execution

- `ClientHelloCallback` runs before `OptionsSelectionCallback`.
- Callbacks are serialized for one connection and may run concurrently for different connections.
- They are not invoked on a backend poll/completion loop.
- No provider-global lock or TLS-session lock is held while user code runs.
- The cancellation token represents handshake cancellation and timeout.
- `FirstRecordBytes` is borrowed and valid only until the callback operation completes; callers copy data they retain.
- Reentrant I/O, authentication, shutdown, or disposal on the same connection is unsupported and throws.
- A callback exception fails only that connection.

### TLS option validation

The returned `SslServerAuthenticationOptions` and supplied `SslClientAuthenticationOptions` are the semantic contract. A native provider validates every property. It cannot ignore a property because the underlying TLS library has no direct equivalent.

Platform behavior already present in `SslStream` remains platform behavior. For example, `CipherSuitesPolicy` is not supported by the current Windows PAL. A native Windows provider may reject it in the same way; "parity" does not mean inventing cross-platform support absent from `SslStream`.

### Offload

`Disabled` requires userspace record protection. `Prefer` requests kernel record protection but permits fallback. `Require` fails authentication unless that direction is active.

The result is recorded separately for transmit and receive. The negotiated protocol and cipher are always reported separately because a supported-looking cipher is not proof that kTLS installed it.

`HardwareOffload` reports:

- `NotApplicable` when the kernel record layer is not active;
- `Inactive` only when the provider has reliable evidence that host software handles crypto;
- `Active` only with reliable per-connection evidence;
- `Unknown` when kTLS is active but per-connection hardware attribution is unavailable.

No public method changes NIC or kernel configuration.

Result invariants:

- `Disabled` produces `KernelRecordLayerActive == false`, `HardwareOffload == NotApplicable`, and `FallbackReason == None`.
- An active direction produces `KernelRecordLayerActive == true` and `FallbackReason == None`.
- A preferred but inactive direction produces a non-`None` fallback reason.
- A required but inactive direction never appears in a successful `TransportTlsInfo`; authentication fails instead.
- `HardwareOffload` is `NotApplicable` unless the kernel record layer is active.

### Task and ValueTask choices

`AcceptAsync`, `ReadAsync`, `WriteAsync`, and `ShutdownWriteAsync` are repeated operations with legitimate synchronous-completion paths, so the experimental surface uses `ValueTask`. `ConnectAsync`, `ListenAsync`, and authentication also mirror existing runtime/Kestrel `ValueTask`-returning provider patterns and allow native setup to complete without allocating a `Task`; this choice must be revisited with usage data before stabilization. `Completion` is a reusable `Task`, and `RequestClientCertificateAsync` returns `Task` to mirror the existing `SslStream` and Kestrel contract. The callback delegates use `ValueTask` because the common certificate/raw-observation decisions are synchronous and Kestrel's existing server-options callback already has that shape.

## Illustrative server examples

### Plain TCP

```csharp
using System.Buffers;
using System.Net;
using System.Net.Transport;
using System.Net.Transport.Sockets;

await using TransportProvider provider = new SocketTransportProvider();
await using TransportListener listener = await provider.ListenAsync(
    new TransportListenOptions
    {
        EndPoint = new IPEndPoint(IPAddress.Any, 5000),
        Backlog = 512,
        NoDelay = true,
    },
    shutdownToken);

while (!shutdownToken.IsCancellationRequested)
{
    TransportConnection connection = await listener.AcceptAsync(shutdownToken);
    await EchoAsync(connection, shutdownToken); // Simplified serial sample; a real server supervises concurrent handlers.
}

static async Task EchoAsync(
    TransportConnection connection,
    CancellationToken cancellationToken)
{
    await using (connection)
    {
        while (true)
        {
            TransportReadResult result = await connection.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            try
            {
                if (!buffer.IsEmpty)
                {
                    await connection.WriteAsync(buffer, cancellationToken);
                }
            }
            finally
            {
                connection.AdvanceRead(buffer.End, buffer.End);
            }

            if (result.IsCompleted)
            {
                await connection.ShutdownWriteAsync(cancellationToken);
                break;
            }
        }
    }
}
```

### TLS server with raw ClientHello observation and per-SNI certificate selection

```csharp
using System.Buffers;
using System.Net.Security;
using System.Net.Transport;
using System.Security.Authentication;

var defaultOptions = new SslServerAuthenticationOptions
{
    ServerCertificateContext = defaultCertificateContext,
    EnabledSslProtocols = SslProtocols.None,
    ApplicationProtocols =
    [
        SslApplicationProtocol.Http2,
        SslApplicationProtocol.Http11,
    ],
};

TransportClientHelloCallback observeClientHello =
    static (context, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The sequence is borrowed. Parse synchronously or copy what must survive.
        ObserveJa4Input(
            context.FirstRecordBytes,
            context.ContainsCompleteClientHello);

        return ValueTask.CompletedTask;
    };

var tlsOptions = new TransportServerAuthenticationOptions
{
    AuthenticationOptions = defaultOptions,
    OptionsSelectionCallback = (context, options, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();

        options.ServerCertificateContext =
            certificates.GetRequired(context.ClientHelloInfo.ServerName);

        return ValueTask.FromResult(options);
    },
    Offload = new TransportTlsOffloadOptions
    {
        Transmit = TlsOffloadPolicy.Prefer,
        Receive = TlsOffloadPolicy.Prefer,
    },
};

TransportConnection connection = await listener.AcceptAsync(shutdownToken);

// Kestrel's UseTlsClientHelloListener-style phase can have its own timeout.
await connection.ObserveTlsClientHelloAsync(
    observeClientHello,
    clientHelloTimeoutToken);

TransportTlsInfo tls = await connection.AuthenticateAsServerAsync(
    tlsOptions,
    handshakeTimeoutToken);

Console.WriteLine(
    $"TLS={tls.Protocol}, cipher={tls.NegotiatedCipherSuite}, " +
    $"kTLS TX={tls.Offload.Transmit.KernelRecordLayerActive}, " +
    $"kTLS RX={tls.Offload.Receive.KernelRecordLayerActive}");
```

This example does not claim that `Prefer` activates kTLS. It reports the actual result.

## Illustrative client examples

### Authenticated client

```csharp
using System.Net;
using System.Net.Security;
using System.Net.Transport;

TransportConnection connection = await provider.ConnectAsync(
    new TransportConnectOptions
    {
        RemoteEndPoint = new DnsEndPoint("cache.example.net", 6380),
        NoDelay = true,
    },
    cancellationToken);

await connection.AuthenticateAsClientAsync(
    new TransportClientAuthenticationOptions
    {
        AuthenticationOptions = new SslClientAuthenticationOptions
        {
            TargetHost = "cache.example.net",
            ApplicationProtocols = [new SslApplicationProtocol("resp3")],
            CertificateRevocationCheckMode = X509RevocationMode.Online,
        },
        Offload = new TransportTlsOffloadOptions
        {
            Transmit = TlsOffloadPolicy.Prefer,
            Receive = TlsOffloadPolicy.Prefer,
        },
    },
    cancellationToken);
```

The connection owns the TLS state. The caller owns the connection and must dispose it.

### Redis-style long-lived multiplexer

```csharp
using System.Net.Transport.Pipelines;

public sealed class RespConnectionFactory
{
    private readonly TransportProvider _provider;
    private readonly EndPoint _endpoint;
    private readonly string _targetHost;

    public RespConnectionFactory(
        TransportProvider provider,
        EndPoint endpoint,
        string targetHost)
    {
        _provider = provider;
        _endpoint = endpoint;
        _targetHost = targetHost;
    }

    public async ValueTask<TransportDuplexPipe> ConnectAsync(
        CancellationToken cancellationToken)
    {
        TransportConnection connection = await _provider.ConnectAsync(
            new TransportConnectOptions
            {
                RemoteEndPoint = _endpoint,
                NoDelay = true,
            },
            cancellationToken);

        try
        {
            await connection.AuthenticateAsClientAsync(
                new TransportClientAuthenticationOptions
                {
                    AuthenticationOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = _targetHost,
                    },
                },
                cancellationToken);

            return TransportPipelines.Create(
                connection,
                new TransportPipeOptions
                {
                    LeaveOpen = false,
                });
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
```

The multiplexer owns:

- request IDs and response correlation;
- a single writer queue for protocol ordering;
- reconnect delays and retry policy;
- logical request timeout and replay safety;
- physical connection pooling, if any.

The transport owns:

- connect cancellation;
- one physical stream;
- TLS authentication and certificate validation;
- receive/write memory lifetime;
- close and terminal error reporting.

The provider is shared across reconnects. Replacing it for every physical connection would discard the shared engine and pool model.

For a real multiplexer, the returned pipe is handed to one protocol read loop and one serialized protocol write loop. The reconnection owner observes `TransportDuplexPipe.Completion`, disposes the failed pipe, creates a replacement through this factory, and decides which pending commands are safe to retry.

The transport does not retry a canceled or failed connect and does not replay writes. Those decisions require protocol knowledge: a Redis command may or may not be idempotent, and bytes accepted by a local send completion are not proof that the peer processed the command.

## Illustrative Pipelines adapter

```csharp
using System.IO.Pipelines;
using System.Net.Transport;
using System.Net.Transport.Pipelines;

var inputOptions = new PipeOptions(
    pool: memoryPool,
    readerScheduler: applicationScheduler,
    writerScheduler: transportScheduler,
    pauseWriterThreshold: maxReadBufferSize,
    resumeWriterThreshold: maxReadBufferSize / 2,
    useSynchronizationContext: false);

var outputOptions = new PipeOptions(
    pool: memoryPool,
    readerScheduler: transportScheduler,
    writerScheduler: applicationScheduler,
    pauseWriterThreshold: maxWriteBufferSize,
    resumeWriterThreshold: maxWriteBufferSize / 2,
    useSynchronizationContext: false);

await using TransportDuplexPipe transport = TransportPipelines.Create(
    connection,
    new TransportPipeOptions
    {
        InputOptions = inputOptions,
        OutputOptions = outputOptions,
        LeaveOpen = false,
    });

connectionContext.Transport = transport;
```

### Adapter receive loop

Illustrative algorithm:

```csharp
while (true)
{
    TransportReadResult read = await connection.ReadAsync(stopToken);

    try
    {
        foreach (ReadOnlyMemory<byte> segment in read.Buffer)
        {
            inbound.Writer.Write(segment.Span);
        }

        FlushResult flush = await inbound.Writer.FlushAsync(stopToken);
        if (flush.IsCanceled || flush.IsCompleted)
        {
            break;
        }
    }
    finally
    {
        connection.AdvanceRead(read.Buffer.End, read.Buffer.End);
    }

    if (read.IsCompleted)
    {
        break;
    }
}
```

The universal adapter copies inbound bytes into the pipe. A provider-specific optimized adapter may expose backend memory directly through a custom `PipeReader`, but only if `AdvanceTo` returns every underlying lease correctly. That optimization is not part of the first common SPI.

### Adapter send loop

Illustrative algorithm:

```csharp
while (true)
{
    ReadResult read = await outbound.Reader.ReadAsync(stopToken);
    ReadOnlySequence<byte> buffer = read.Buffer;

    try
    {
        if (!buffer.IsEmpty)
        {
            await connection.WriteAsync(buffer, stopToken);
        }
    }
    finally
    {
        outbound.Reader.AdvanceTo(buffer.End);
    }

    if (read.IsCompleted)
    {
        await connection.ShutdownWriteAsync(stopToken);
        break;
    }
}
```

The adapter advances the output pipe only after `WriteAsync` releases the sequence. This is the key ownership invariant for scatter/gather, pinned segments, IOCP `WSABUF`s, and io_uring iovecs.

### Adapter concurrency and lifecycle

| Event | Adapter behavior |
|---|---|
| Remote orderly EOF | Complete the inbound `PipeWriter` normally after publishing final bytes; allow pending outbound data to drain unless the protocol/TLS state makes further writes invalid |
| Transport read error | Complete inbound with the exception, cancel the outbound pump, abort the connection, and fault adapter `Completion` |
| Application completes `Input` early | Stop the receive pump; abort if unread network data cannot be safely ignored |
| Application completes `Output` normally | Drain all buffered output, call `ShutdownWriteAsync`, and permit the receive half to continue |
| Application completes `Output` with error | Abort the connection and complete inbound with that error |
| Transport write error | Complete the outbound reader with the exception, abort the connection, and fault inbound |
| `PipeReader.CancelPendingRead` | Cancel only the current application wait; do not silently close the transport |
| `PipeWriter.CancelPendingFlush` | Cancel only the current application flush wait; the send pump retains already accepted bytes until their ownership contract completes |
| `TransportConnection.Abort` | Cancel both pumps, complete both pipe directions with the terminal error, and await native drain during adapter disposal |
| `TransportDuplexPipe.DisposeAsync` | Stop both pumps, abort if graceful output completion has not already occurred, await pump termination, and dispose the connection unless `LeaveOpen` is true |

The adapter never performs two concurrent reads or two concurrent writes. It may run one receive pump and one send pump concurrently. A completion callback from one direction must not run arbitrary application work on the backend's I/O loop; `InputOptions` and `OutputOptions` determine where those continuations execute.

## Kestrel adapter rules

The Kestrel adapter should:

1. create one provider per server or configured provider scope, not per connection;
2. create one listener per endpoint;
3. run TLS authentication before passing a secured connection to HTTP processing;
4. use the current Kestrel memory pool and direction-specific schedulers through `TransportPipeOptions`;
5. set generic TLS features from `TransportTlsInfo`;
6. expose `ISslStreamFeature` only for the real `SslStream` fallback;
7. never fabricate `IConnectionSocketFeature` for a raw provider;
8. treat explicit provider incompatibility as bind failure;
9. allow an automatic mode to choose the managed fallback, with an observable provider-selection event;
10. keep connection limits, middleware, logging, and HTTP protocol selection in ASP.NET Core.

## Backend SPI invariants

Every provider implementation must satisfy these invariants even though the native mechanisms differ:

- A connection has a monotonic generation or equivalent identity. Native completions are validated against it before touching managed state.
- A receive segment has a provider-private buffer identity. It is returned only after `AdvanceRead`.
- A canceled operation remains represented until its terminal native completion is observed.
- A connection slot, OVERLAPPED block, SQE identity, registered buffer, pin, or fd is never reused while a stale completion can still reference it.
- Read and write errors are terminal unless a documented retry status is handled internally.
- Partial sends preserve ordering and source lifetime.
- Callbacks run outside shared I/O loops and locks.
- Provider shutdown waits for callbacks and asynchronous disposals that can reach provider resources.
- A TLS implementation serializes access to a single TLS session even while allowing one application read and one application write to be pending.
- No unsupported TLS option is silently ignored.

## Why the API does not expose backend operations

The public API does not expose `WaitForReadabilityAsync`, `Submit`, `Poll`, buffer IDs, CQE flags, OVERLAPPED pointers, or event masks. Those are implementation mechanics, not portable semantics:

- fd-bound OpenSSL needs readiness because `SSL_read` can want write and `SSL_write` can want read;
- memory-BIO TLS needs ciphertext input/output, not socket readiness;
- io_uring plaintext receive may be multishot;
- IOCP identifies completion through an OVERLAPPED address;
- managed `Socket` may complete synchronously or asynchronously.

The portable contract is the outcome and lifetime: data, advancement, write completion, cancellation, authentication, shutdown, and terminal completion.

## Rejected surface ideas

### `Socket2`, `AsyncSocket`, or `NativeSocket`

Rejected because the semantics are not a new kind of operating-system socket. Such names imply datagrams, socket options, raw handles, and compatibility that the abstraction intentionally does not provide.

### A generic `Capabilities` flags bag

Rejected because capabilities such as kTLS depend on role, direction, build, kernel, negotiated protocol/cipher, and configured callbacks. A static bit cannot answer whether offload is active for one connection. Typed configuration plus post-handshake outcome is more accurate.

### `Stream` as the only low-level contract

Rejected because caller-supplied read buffers erase completion-selected buffer identity, and `Stream` has no listener/provider lifetime, endpoint metadata, offload state, or terminal completion contract. A `Stream` adapter remains useful for compatibility and `SslStream`.

### `IDuplexPipe` as the only low-level contract

Rejected for the core because a pipe does not define listener/connect/TLS lifecycle or native terminal completion ownership, and a standard `PipeWriter` cannot adopt arbitrary provider-owned receive buffers. Pipelines remain the primary higher-level adapter.

### Raw handle exposure

Rejected from the common surface because two independent I/O owners on one TCP stream can corrupt ordering and TLS framing. Provider-specific adoption APIs require explicit ownership and should not imply that the handle remains independently usable.

## API evolution rule

The experimental package should track unsupported or unused members aggressively. Before stabilization:

- remove members with no demonstrated consumer;
- move backend tuning to provider-specific packages;
- retain only option-independent, testable semantics;
- avoid compatibility promises for native provider names;
- compare the final shape against the existing `Socket`, `SslStream`, `Stream`, and Pipelines surfaces again.
