# Skeptical public API review

**Verdict:** Recommend not adding

**Need assessment**

- **Scenarios & environment:** The surface serves server frameworks and long-lived multiplexed client libraries on Linux and Windows. The dominant current scenario already works through `Socket`, `NetworkStream`, `SslStream`, and Pipelines; native epoll/io_uring execution, provider-owned receive buffers, fd-bound TLS, and kTLS are specialized alternatives. It is inert on browser/WASI and likely niche on mobile. Endpoint inputs come from server configuration or client DNS/IP targets, certificates come from configuration/stores, bytes come from protocol parsers or Pipelines, and offload availability comes from host build/kernel/device state rather than application data.
- **Commonality:** Extensibility. Kestrel and Redis-style clients are credible consumers, but this is not an end-application happy path.
- **Target audience:** Library-author.
- **Conclusion:** Incubate the design as a runtime-owned experimental surface in `System.Net.Security.dll`, where it can reuse TLS policy and PAL internals without a new friend-assembly boundary. Do not add stable BCL surface until at least two independent consumers and multiple backends demonstrate semantic necessity and measurable value beyond internal improvements to `Socket`, `SslStream`, and Pipelines.

### System.Net.Transport provider, listener, and TCP option types

- Keep every type experimental.
- Remove `TransportProvider.Name` from a future stable surface unless code needs it for more than diagnostics.
- Keep the first surface TCP-specific: `IPEndPoint` for listen/local/connected endpoints and `IPEndPoint` or `DnsEndPoint` for remote connect.
- Do not stabilize provider-specific selection or tuning here.
- Incubate these types in `System.Net.Security.dll`; do not use `InternalsVisibleTo` or `UnsafeAccessor` from another framework assembly.

**Why:** The provider object is justified only if shared ring, completion-port, worker, and buffer-pool ownership proves necessary across independent consumers. That is a credible hypothesis, not yet a stable ecosystem contract. A diagnostic string and concrete backend names would freeze implementation choices early. Restricting the endpoint model to the stated TCP scenario avoids generic-extensibility surface with no current need. TLS is central, so placing the experiment in the assembly that owns TLS policy is safer than duplicating validation or opening a privileged assembly boundary.

```diff
 namespace System.Net.Transport;

-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public abstract class TransportProvider : IAsyncDisposable
-{
-    protected TransportProvider();
-    public abstract string Name { get; }
-    public abstract ValueTask<TransportListener> ListenAsync(TransportListenOptions options, CancellationToken cancellationToken = default);
-    public abstract ValueTask<TransportConnection> ConnectAsync(TransportConnectOptions options, CancellationToken cancellationToken = default);
-    public abstract ValueTask DisposeAsync();
-}
-
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public sealed class TransportListenOptions
-{
-    public TransportListenOptions();
-    public required IPEndPoint EndPoint { get; init; }
-    public int Backlog { get; set; }
-    public bool NoDelay { get; set; }
-}
-
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public sealed class TransportConnectOptions
-{
-    public TransportConnectOptions();
-    public required EndPoint RemoteEndPoint { get; init; }
-    public IPEndPoint? LocalEndPoint { get; init; }
-    public bool NoDelay { get; set; }
-}
-
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public abstract class TransportListener : IAsyncDisposable
-{
-    protected TransportListener();
-    public abstract IPEndPoint LocalEndPoint { get; }
-    public abstract ValueTask<TransportConnection> AcceptAsync(CancellationToken cancellationToken = default);
-    public abstract ValueTask DisposeAsync();
-}
+// No stable BCL API in the first increment.
```

### System.Net.Transport.TransportConnection and TransportReadResult

- Keep the leased-read contract experimental until managed Socket, epoll, and io_uring implementations prove its lifetime and cancellation rules.
- Keep the type narrower than `Socket`; do not add raw handles, socket options, synchronous I/O, datagrams, or polling.
- Re-evaluate whether both `Completion` and `DisposeAsync` are necessary after implementation experience.
- Keep one-read/one-write concurrency and all-or-error writes as behavioral contracts rather than adding backend operation primitives.

**Why:** A completion-selected `ReadOnlySequence<byte>` plus explicit advancement is the clearest semantic gap in `Socket` and `Stream`, particularly for io_uring provided buffers. It is also the most difficult contract to evolve: consumers hold provider memory, and cancellation cannot permit early reuse. Stabilizing before several implementations would freeze the highest-risk lifetime design. Expanding it into a second socket would duplicate a mature API while still failing to represent shared provider ownership honestly.

```diff
 namespace System.Net.Transport;

-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public abstract class TransportConnection : IAsyncDisposable
-{
-    protected TransportConnection();
-    public abstract IPEndPoint? LocalEndPoint { get; }
-    public abstract IPEndPoint? RemoteEndPoint { get; }
-    public abstract Task Completion { get; }
-    public abstract TransportTlsInfo? TlsInfo { get; }
-    public abstract ValueTask<TransportReadResult> ReadAsync(CancellationToken cancellationToken = default);
-    public abstract void AdvanceRead(SequencePosition consumed, SequencePosition examined);
-    public abstract ValueTask WriteAsync(ReadOnlySequence<byte> buffer, CancellationToken cancellationToken = default);
-    public abstract ValueTask ObserveTlsClientHelloAsync(TransportClientHelloCallback callback, CancellationToken cancellationToken = default);
-    public abstract ValueTask<TransportTlsInfo> AuthenticateAsClientAsync(TransportClientAuthenticationOptions options, CancellationToken cancellationToken = default);
-    public abstract ValueTask<TransportTlsInfo> AuthenticateAsServerAsync(TransportServerAuthenticationOptions options, CancellationToken cancellationToken = default);
-    public abstract Task<X509Certificate2?> RequestClientCertificateAsync(CancellationToken cancellationToken = default);
-    public abstract ValueTask ShutdownWriteAsync(CancellationToken cancellationToken = default);
-    public abstract void Abort(Exception? error = null);
-    public abstract ValueTask DisposeAsync();
-}
-
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public readonly struct TransportReadResult
-{
-    public TransportReadResult(ReadOnlySequence<byte> buffer, bool isCompleted);
-    public ReadOnlySequence<byte> Buffer { get; }
-    public bool IsCompleted { get; }
-}
+// No stable BCL API in the first increment.
```

### System.Net.Transport TLS authentication and ClientHello types

- Keep `SslClientAuthenticationOptions` and `SslServerAuthenticationOptions` as the semantic source of truth.
- Keep server raw data explicitly named `FirstRecordBytes`; retain `ContainsCompleteClientHello`.
- Keep the provider-neutral options callback separate from `SslStream`'s callback because native providers cannot supply a truthful `SslStream`.
- Keep delayed client authentication explicit and reject/fallback when a provider cannot implement it.

**Why:** Reusing the existing options avoids a second certificate, protocol, cipher, validation, and platform-policy model. The first-record name prevents today's Kestrel behavior from turning into a false complete-message guarantee. A native TLS session cannot fabricate the actual `SslStream` required by the existing delegate, so a provider-neutral callback is necessary if the experiment supports native TLS at all. Delayed client certificates are observable behavior, not a hint a provider may ignore.

```diff
 namespace System.Net.Transport;

-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public sealed class TransportClientAuthenticationOptions
-{
-    public TransportClientAuthenticationOptions();
-    public required SslClientAuthenticationOptions AuthenticationOptions { get; init; }
-    public TransportTlsOffloadOptions Offload { get; init; }
-}
-
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public sealed class TransportServerAuthenticationOptions
-{
-    public TransportServerAuthenticationOptions();
-    public required SslServerAuthenticationOptions AuthenticationOptions { get; init; }
-    public TransportClientHelloCallback? ClientHelloCallback { get; init; }
-    public TransportServerOptionsSelectionCallback? OptionsSelectionCallback { get; init; }
-    public bool AllowPostHandshakeClientAuthentication { get; init; }
-    public TransportTlsOffloadOptions Offload { get; init; }
-}
-
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public delegate ValueTask TransportClientHelloCallback(
-    TransportServerHandshakeContext context,
-    CancellationToken cancellationToken);
-
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public delegate ValueTask<SslServerAuthenticationOptions> TransportServerOptionsSelectionCallback(
-    TransportServerHandshakeContext context,
-    SslServerAuthenticationOptions defaultOptions,
-    CancellationToken cancellationToken);
-
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public sealed class TransportServerHandshakeContext
-{
-    public TransportServerHandshakeContext(
-        TransportConnection connection,
-        SslClientHelloInfo clientHelloInfo,
-        ReadOnlySequence<byte> firstRecordBytes,
-        bool containsCompleteClientHello);
-    public TransportConnection Connection { get; }
-    public SslClientHelloInfo ClientHelloInfo { get; }
-    public ReadOnlySequence<byte> FirstRecordBytes { get; }
-    public bool ContainsCompleteClientHello { get; }
-}
+// No stable BCL API in the first increment.
```

### System.Net.Transport TLS metadata and offload types

- Keep TX and RX request and result state separate.
- Keep request policy distinct from negotiated TLS and actual activation.
- Move `TlsOffloadFallbackReason` to diagnostics unless real consumers demonstrate stable programmatic branching.
- Keep hardware state able to report `Unknown`.
- Do not expose TLS-library handles or an `SslStream` property.

**Why:** Linux installs TX and RX independently, and actual OpenSSL activation can differ by direction. A single bool cannot represent disabled, preferred fallback, required failure, TX-only, or hardware-unknown states. Detailed native failure categories are likely to grow with kernels and TLS libraries, so a stable enum would turn implementation diagnostics into compatibility obligations. `Unknown` is necessary because proving kTLS does not always prove per-connection NIC crypto.

```diff
 namespace System.Net.Transport;

-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public sealed class TransportTlsOffloadOptions
-{
-    public TransportTlsOffloadOptions();
-    public TlsOffloadPolicy Transmit { get; set; }
-    public TlsOffloadPolicy Receive { get; set; }
-}
-
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public enum TlsOffloadPolicy
-{
-    Disabled = 0,
-    Prefer = 1,
-    Require = 2,
-}
-
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public abstract class TransportTlsInfo
-{
-    protected TransportTlsInfo();
-    public abstract SslProtocols Protocol { get; }
-    public abstract TlsCipherSuite? NegotiatedCipherSuite { get; }
-    public abstract SslApplicationProtocol ApplicationProtocol { get; }
-    public abstract string? ServerName { get; }
-    public abstract X509Certificate2? RemoteCertificate { get; }
-    public abstract TransportTlsOffloadInfo Offload { get; }
-    public abstract bool TryGetChannelBindingBytes(ChannelBindingKind kind, out ReadOnlyMemory<byte> channelBindingToken);
-}
-
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public sealed class TransportTlsOffloadInfo
-{
-    public TransportTlsOffloadInfo(TransportTlsOffloadDirectionInfo transmit, TransportTlsOffloadDirectionInfo receive);
-    public TransportTlsOffloadDirectionInfo Transmit { get; }
-    public TransportTlsOffloadDirectionInfo Receive { get; }
-}
-
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public readonly struct TransportTlsOffloadDirectionInfo
-{
-    public TransportTlsOffloadDirectionInfo(
-        TlsOffloadPolicy requestedPolicy,
-        bool kernelRecordLayerActive,
-        TlsHardwareOffloadStatus hardwareOffload,
-        TlsOffloadFallbackReason fallbackReason);
-    public TlsOffloadPolicy RequestedPolicy { get; }
-    public bool KernelRecordLayerActive { get; }
-    public TlsHardwareOffloadStatus HardwareOffload { get; }
-    public TlsOffloadFallbackReason FallbackReason { get; }
-}
-
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public enum TlsHardwareOffloadStatus
-{
-    NotApplicable = 0,
-    Inactive = 1,
-    Active = 2,
-    Unknown = 3,
-}
-
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public enum TlsOffloadFallbackReason
-{
-    None = 0,
-    ProviderBuild = 1,
-    OperatingSystem = 2,
-    Kernel = 3,
-    Protocol = 4,
-    CipherSuite = 5,
-    TlsFeature = 6,
-    Backend = 7,
-    Unknown = 8,
-}
+// No stable BCL API in the first increment.
```

### System.Net.Transport.Pipelines adapter types

- Incubate the adapter with the transport surface.
- Reuse two `PipeOptions` values instead of duplicating scheduler, memory-pool, segment, and threshold properties.
- Return an owned `TransportDuplexPipe`, not a bare `IDuplexPipe`.
- Keep the type in a companion assembly rather than adding a dependency from the transport core to Pipelines.

**Why:** The adapter has concrete Kestrel and client value, but its shutdown and disposal semantics require an owner beyond `IDuplexPipe`. Reusing `PipeOptions` matches the established direction-specific scheduler and backpressure model. A companion assembly keeps the low-level transport usable without forcing a Pipelines dependency into every consumer.

```diff
 namespace System.Net.Transport.Pipelines;

-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public sealed class TransportPipeOptions
-{
-    public TransportPipeOptions();
-    public PipeOptions InputOptions { get; set; }
-    public PipeOptions OutputOptions { get; set; }
-    public bool LeaveOpen { get; set; }
-}
-
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public sealed class TransportDuplexPipe : IDuplexPipe, IAsyncDisposable
-{
-    public PipeReader Input { get; }
-    public PipeWriter Output { get; }
-    public Task Completion { get; }
-    public ValueTask DisposeAsync();
-}
-
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public static class TransportPipelines
-{
-    public static TransportDuplexPipe Create(TransportConnection connection, TransportPipeOptions? options = null);
-}
+// No stable BCL API in the first increment.
```

### System.Net.Transport.Sockets.SocketTransportProvider

- Use this as the first implementation and behavioral control.
- Keep `CreateConnection` and `CreateListener` experimental with explicit `ownsSocket`.
- Require exclusive I/O use even when disposal ownership is retained by the caller.
- Do not add native-handle transfer to the common surface.

**Why:** Existing `Socket` adoption is the least disruptive migration path and gives the experiment full TLS compatibility through `SslStream`. Disposal ownership cannot be inferred, but `ownsSocket: false` must not imply concurrent independent I/O. Raw handle transfer is a separate problem: wrapping or duplicating a handle does not establish exclusive ownership, and a second active owner can corrupt byte ordering or TLS framing.

```diff
 namespace System.Net.Transport.Sockets;

-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public sealed class SocketTransportProvider : TransportProvider
-{
-    public SocketTransportProvider();
-    public override string Name { get; }
-    public TransportConnection CreateConnection(Socket socket, bool ownsSocket);
-    public TransportListener CreateListener(Socket socket, bool ownsSocket);
-    public override ValueTask<TransportListener> ListenAsync(TransportListenOptions options, CancellationToken cancellationToken = default);
-    public override ValueTask<TransportConnection> ConnectAsync(TransportConnectOptions options, CancellationToken cancellationToken = default);
-    public override ValueTask DisposeAsync();
-}
+// No stable BCL API in the first increment.
```

### System.Net.Transport.Linux and System.Net.Transport.Windows provider types

- Do not stabilize concrete epoll, io_uring, or raw IOCP provider type names in the first increment.
- Keep backend-specific configuration out of the common option types.
- Keep managed Socket/`SslStream` as the required fallback and benchmark control.

**Why:** These type names expose implementation choices whose support and selection policies are unresolved. Stabilizing them would make backend identity itself a compatibility promise before kernel requirements, container behavior, TLS parity, and Windows value over current Socket IOCP have been established. The experiment can use these names internally or under `ExperimentalAttribute` without committing the BCL.

```diff
 namespace System.Net.Transport.Linux;

-[SupportedOSPlatform("linux")]
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public sealed class EpollTransportProvider : TransportProvider
-{
-    public EpollTransportProvider();
-    public override string Name { get; }
-    public override ValueTask<TransportListener> ListenAsync(TransportListenOptions options, CancellationToken cancellationToken = default);
-    public override ValueTask<TransportConnection> ConnectAsync(TransportConnectOptions options, CancellationToken cancellationToken = default);
-    public override ValueTask DisposeAsync();
-}
-
-[SupportedOSPlatform("linux")]
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public sealed class IoUringTransportProvider : TransportProvider
-{
-    public IoUringTransportProvider();
-    public override string Name { get; }
-    public override ValueTask<TransportListener> ListenAsync(TransportListenOptions options, CancellationToken cancellationToken = default);
-    public override ValueTask<TransportConnection> ConnectAsync(TransportConnectOptions options, CancellationToken cancellationToken = default);
-    public override ValueTask DisposeAsync();
-}
-
 namespace System.Net.Transport.Windows;

-[SupportedOSPlatform("windows")]
-[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
-public sealed class IocpTransportProvider : TransportProvider
-{
-    public IocpTransportProvider();
-    public override string Name { get; }
-    public override ValueTask<TransportListener> ListenAsync(TransportListenOptions options, CancellationToken cancellationToken = default);
-    public override ValueTask<TransportConnection> ConnectAsync(TransportConnectOptions options, CancellationToken cancellationToken = default);
-    public override ValueTask DisposeAsync();
-}
+// No stable BCL API in the first increment.
```

## Current-to-proposed surface comparison

| Current API | What it already solves | Candidate experimental addition | Why not change the current type now |
|---|---|---|---|
| `Socket` | OS socket creation, bind/listen/connect, sync/async I/O, options, handles, datagrams | Provider-owned connected stream and shared backend lifetime | Provider polymorphism would imply unsupported socket semantics and cannot naturally express completion-selected receive buffers |
| `SafeSocketHandle` | Explicit native handle ownership and close coordination | No common raw-handle API | Handle access does not establish exclusive I/O ownership |
| `NetworkStream`/`Stream` | Stream-compatible read/write and `SslStream` substrate | Leased multi-segment receive plus terminal completion | Caller-provided read buffers lose completion-selected buffer identity |
| `SslStream` | Full current TLS policy, callbacks, metadata, and platform behavior | Provider-neutral native TLS lifecycle and offload result | Native providers cannot fabricate an actual `SslStream`; runtime should reuse its policy implementation |
| `IDuplexPipe`/`PipeOptions` | High-level buffered duplex I/O, scheduling, and backpressure | Owned adapter over low-level transport | Pipes do not define listener/connect/TLS/native terminal completion ownership |

The review recommendation is to implement and validate the design experimentally under runtime ownership, leave existing stable APIs unchanged, and return with evidence for the smallest surface that cannot remain internal.
