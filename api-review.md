# Skeptical review of the callback-first API

**Verdict:** Changes recommended

**Need assessment**

- **Scenarios & environment:** The API targets server frameworks, proxies, protocol engines, and long-lived multiplexed clients on Linux and Windows. The dominant end-application scenario remains better served by `Socket`, `NetworkStream`, `SslStream`, Pipelines, and protocol-specific libraries. The demonstrated consumers are Kestrel, StackExchange.Redis-style clients, and Garnet-like servers. The common path is one shared engine with many connections; one engine per connection is rare and should not shape defaults. The API is not meaningful in browser/WASI environments and may be niche on mobile.
- **Commonality:** Extensibility. High value for a narrow class of infrastructure libraries, but not a general networking happy path.
- **Target audience:** Library-author.
- **Justified:** A callback-first experimental surface is justified for incubation because provider-selected receive buffers, shared worker/ring lifetime, native TLS progression, and completion batching are not naturally represented by the existing public `Socket` API. Stable BCL addition is not yet justified; the surface needs real managed, readiness, completion, server, and client implementations first.

### System.Net.Transport.TransportProviders and TransportProvider

- Use a `TransportProviders.CreateDefault()` method rather than a cached `Default` property.
- Keep built-in implementation classes internal and return the common `TransportProvider`.
- Keep typed provider options on each factory method.
- Make `CreateEngine` the explicit resource-creation boundary.
- Keep `Name` diagnostic-only and do not use it for feature branching.

**Why:** Provider selection is a real consumer operation, while epoll/io_uring/IOCP implementation classes are not. Static factory methods retain SocketSet's compact selection model without forcing concrete provider types into the public API. Re-probing per call avoids process-global cached environmental decisions. `CreateEngine` makes worker threads, rings, ports, and pools explicit instead of hiding expensive work inside a user-derived engine constructor.

```diff
 namespace System.Net.Transport;

+[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
+public static class TransportProviders
+{
+    public static TransportProvider CreateDefault();
+    public static TransportProvider ManagedSockets(Sockets.ManagedSocketTransportOptions? options = null);
+    [SupportedOSPlatform("linux")]
+    public static TransportProvider Epoll(Epoll.EpollTransportOptions? options = null);
+    [SupportedOSPlatform("linux")]
+    public static TransportProvider IoUring(IoUring.IoUringTransportOptions? options = null);
+    [SupportedOSPlatform("windows")]
+    public static TransportProvider WindowsIocp(Iocp.IocpTransportOptions? options = null);
+    [SupportedOSPlatform("windows")]
+    public static TransportProvider WindowsRio(Rio.RioTransportOptions? options = null);
+}
+
+[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
+public abstract class TransportProvider
+{
+    protected TransportProvider();
+    public abstract string Name { get; }
+    public abstract bool IsSupported { get; }
+    public abstract TransportEngine CreateEngine(TransportEngineOptions options, ITransportApplication application);
+}
```

### System.Net.Transport.TransportEngineOptions

- Keep only provider-independent policy.
- Rename SocketSet's shard terminology to worker terminology in the common API.
- Use zero only for explicitly documented provider-chosen/fixed behavior.
- Keep timeout policy that applies after readiness; keep TLS handshake timeout with TLS configuration.
- Do not add provider buffer geometry here.

**Why:** A common options type is useful only when every provider can implement the same semantic. Ring entries, event batch sizes, accept depth, and registered-buffer counts do not meet that test. Worker count, capacity, endpoint tracking, and idle policy do. Separating them makes it possible to validate or reject every option rather than silently ignoring irrelevant properties.

```diff
 namespace System.Net.Transport;

+[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
+public sealed class TransportEngineOptions
+{
+    public TransportEngineOptions();
+    public int InitialWorkerCount { get; set; }
+    public int MaximumWorkerCount { get; set; }
+    public int MaximumConnectionsPerWorker { get; set; }
+    public bool PinWorkerThreads { get; set; }
+    public bool TrackEndpoints { get; set; }
+    public TimeSpan IdleTimeout { get; set; }
+}
```

### Provider-specific options

- Keep one sealed options type per built-in provider.
- Prefer semantic resource names such as `RingEntryCount`, `CompletionBatchSize`, and `AcceptConcurrency`.
- Do not expose fd-bound, memory-BIO, `SslStream`, or Schannel strategy switches.
- Treat burst limits and exact pool counts as experimental until measurements demonstrate operator value.
- Require the provider to reject invalid values and report resolved values.

**Why:** The provider cannot be tuned or even characterized honestly without these settings, but putting them in one common bag recreates SocketSet's ambiguity. Typed options make unsupported combinations unrepresentable at the normal call site. TLS has one public meaning; exposing the provider's current internal mechanism would multiply usage patterns and freeze implementation choices. Several resource knobs are still implementation-shaped, so they should remain experimental and be removed if only benchmark authors use them.

```diff
 namespace System.Net.Transport.Sockets;

+public sealed class ManagedSocketTransportOptions
+{
+    public int ReceiveBufferSize { get; set; }
+    public int WriteBufferSize { get; set; }
+    public int WriteBufferCount { get; set; }
+    public bool WaitForDataBeforeAllocatingBuffer { get; set; }
+    public bool PreferInlineCompletions { get; set; }
+}

 namespace System.Net.Transport.Epoll;

+public sealed class EpollTransportOptions
+{
+    public int MaximumEventsPerWait { get; set; }
+    public int ReadBurstLimit { get; set; }
+    public int WriteBurstLimit { get; set; }
+    public int ReceiveBufferSize { get; set; }
+    public int WriteBufferSize { get; set; }
+    public int WriteBufferCount { get; set; }
+    public bool ReusePort { get; set; }
+}

 namespace System.Net.Transport.IoUring;
+
+public sealed class IoUringTransportOptions
+{
+    public int RingEntryCount { get; set; }
+    public int ProvidedBufferCount { get; set; }
+    public int ReceiveBufferSize { get; set; }
+    public int WriteBufferSize { get; set; }
+    public int WriteBufferCount { get; set; }
+    public int OutOfBandWriteBufferCount { get; set; }
+    public int MaximumRetainedReceiveBuffers { get; set; }
+    public bool ReusePort { get; set; }
+}

 namespace System.Net.Transport.Iocp;

+public sealed class IocpTransportOptions
+{
+    public int CompletionBatchSize { get; set; }
+    public int AcceptConcurrency { get; set; }
+    public int ReceiveBufferSize { get; set; }
+    public int WriteBufferSize { get; set; }
+    public int WriteBufferCount { get; set; }
+}

 namespace System.Net.Transport.Rio;
+
+public sealed class RioTransportOptions
+{
+    public int CompletionQueueSize { get; set; }
+    public int AcceptConcurrency { get; set; }
+    public int ReceiveBufferSize { get; set; }
+    public int SendBufferSize { get; set; }
+    public int RegisteredSendBufferCount { get; set; }
+}
```

### System.Net.Transport.ITransportApplication and TransportApplication

- Separate callbacks from `TransportEngine`.
- Give providers a public callback interface they can invoke.
- Provide a convenience base class with protected virtual methods and explicit interface dispatch.
- Add separate pre-handshake accept and post-handshake ready callbacks.
- Keep worker and batch callbacks optional.
- Contain application exceptions per connection.

**Why:** SocketSet's subclass model makes one object both resource owner and callback target. Passing a separate application object permits one protocol handler to be tested with different engines and makes provider lifetime explicit. A public interface is necessary if third-party providers are part of the public SPI; they cannot invoke another assembly's protected callbacks directly. The base class keeps normal consumer code concise. The pre-handshake phase is required for Kestrel connection accounting and TLS selection; the ready phase prevents application bytes from racing authentication.

```diff
 namespace System.Net.Transport;

+public interface ITransportApplication
+{
+    void OnAccepting(ref TransportAcceptingContext context);
+    void OnReady(ref TransportReadyContext context);
+    void OnConnectFailed(ref TransportConnectFailedContext context);
+    void OnReceive(ref TransportReceiveContext context);
+    void OnWriteCompleted(ref TransportWriteCompletedContext context);
+    void OnClosed(ref TransportClosedContext context);
+    void OnListenerClosed(ref TransportListenerClosedContext context);
+    void OnWorkerFaulted(ref TransportWorkerFaultedContext context);
+    void OnBatchCompleted(int workerIndex);
+}
+
+[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
+public abstract class TransportApplication : ITransportApplication
+{
+    protected TransportApplication();
+    protected virtual void OnAccepting(ref TransportAcceptingContext context);
+    protected virtual void OnReady(ref TransportReadyContext context);
+    protected virtual void OnConnectFailed(ref TransportConnectFailedContext context);
+    protected virtual void OnReceive(ref TransportReceiveContext context);
+    protected virtual void OnWriteCompleted(ref TransportWriteCompletedContext context);
+    protected virtual void OnClosed(ref TransportClosedContext context);
+    protected virtual void OnListenerClosed(ref TransportListenerClosedContext context);
+    protected virtual void OnWorkerFaulted(ref TransportWorkerFaultedContext context);
+    protected virtual void OnBatchCompleted(int workerIndex);
+}
```

### System.Net.Transport.TransportEngine and TransportListener

- Make `Listen` and `Connect` synchronous submissions.
- Return an independently disposable listener.
- Keep engine disposal synchronous and deterministic in the low-level API.
- Prohibit disposal from a provider callback.
- Report the selected provider and resolved worker count.

**Why:** Bind/start is control-plane setup and can fail before returning; an async method adds a state machine without improving the native lifetime. Listener disposal is a separate ownership boundary from engine disposal and accepted connections. Deterministic provider drain is valuable at shutdown, while hosts that cannot block can adapt disposal above this layer.

```diff
 namespace System.Net.Transport;

+[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
+public abstract class TransportEngine : IDisposable
+{
+    protected TransportEngine();
+    public abstract TransportProvider Provider { get; }
+    public abstract TransportEngineOptions Options { get; }
+    public abstract int WorkerCount { get; }
+    public abstract TransportListener Listen(TransportListenOptions options);
+    public abstract TransportConnectOperation Connect(TransportConnectOptions options);
+    public abstract void Dispose();
+}
+
+[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
+public abstract class TransportListener : IDisposable
+{
+    protected TransportListener();
+    public abstract long Id { get; }
+    public abstract IPEndPoint LocalEndPoint { get; }
+    public abstract object? State { get; }
+    public abstract bool IsAccepting { get; }
+    public abstract void Dispose();
+}
```

### Transport listen/connect options and TransportConnectOperation

- Keep listener and connect state on their own option types.
- Return a value-type connect operation for correlation and cancellation.
- Use `RequiredWorkerIndex` only as an explicit low-level affinity request.
- Keep TLS policy on listen/connect, with accepted connections able to override it in `OnAccepting`.

**Why:** SocketSet's `UserToken` is useful but cannot identify a specific failed submission by itself. A small operation handle lets an adapter complete the correct waiter without putting Tasks into the core. Required worker placement serves a concrete proxy scenario; the name makes it clear that failure is preferable to silently losing affinity.

```diff
 namespace System.Net.Transport;

+public sealed class TransportListenOptions
+{
+    public required IPEndPoint EndPoint { get; init; }
+    public int Backlog { get; set; }
+    public bool NoDelay { get; set; }
+    public object? State { get; init; }
+    public TransportServerTlsOptions? Tls { get; set; }
+}
+
+public sealed class TransportConnectOptions
+{
+    public required EndPoint RemoteEndPoint { get; init; }
+    public IPEndPoint? LocalEndPoint { get; init; }
+    public bool NoDelay { get; set; }
+    public int? RequiredWorkerIndex { get; init; }
+    public object? State { get; init; }
+    public TransportClientTlsOptions? Tls { get; set; }
+}
+
+public readonly struct TransportConnectOperation : IEquatable<TransportConnectOperation>
+{
+    public long Id { get; }
+    public object? State { get; }
+    public bool IsValid { get; }
+    public bool Cancel();
+}
```

### System.Net.Transport.TransportConnection and TransportWriteOperation

- Do not add `ReadAsync`.
- Keep receive delivery exclusively in `OnReceive`.
- Keep provider-owned `IBufferWriter<byte>` composition and synchronous submission.
- Return a write operation handle from each logical flush.
- Add `SendBorrowed` for stable caller-owned segmented buffers that remain valid through terminal write completion.
- Keep receive pause/resume and explicit half-close/abort.
- Do not expose raw handles or generic socket options.
- Add an optional retained receive lease so protocol adapters can hold the exact provider buffer beyond `OnReceive` without exposing backend identities.

**Why:** This is the strongest distinction from `Socket`: the provider owns receive buffers and invokes the application at completion time. A task-based caller-buffer API would duplicate `Socket.ReceiveAsync` and weaken the case for a new surface. The write-operation handle fixes callback correlation while preserving an allocation-free core. `SendBorrowed` lets protocol libraries keep using their reference-counted serialized segments and permits provider-private scatter/gather or zero-copy paths without promising them. A bounded retained lease lets message-framing and Pipelines adapters preserve provider-selected receive memory after the callback instead of copying, while the provider still owns registration and reuse. Raw handles would permit a second I/O owner and break ordering or TLS framing.

```diff
 namespace System.Net.Transport;

+[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
+public abstract class TransportConnection : IBufferWriter<byte>
+{
+    protected TransportConnection();
+    public abstract long Id { get; }
+    public abstract object? State { get; set; }
+    public abstract IPEndPoint? LocalEndPoint { get; }
+    public abstract IPEndPoint? RemoteEndPoint { get; }
+    public abstract TransportConnectionOrigin Origin { get; }
+    public abstract TransportTlsInfo? TlsInfo { get; }
+    public abstract bool SupportsReceivePause { get; }
+    public abstract bool TryPauseReceive();
+    public abstract void ResumeReceive();
+    public abstract Span<byte> GetSpan(int sizeHint = 0);
+    public abstract Memory<byte> GetMemory(int sizeHint = 0);
+    public abstract void Advance(int count);
+    public abstract TransportWriteOperation Flush(object? state = null);
+    public virtual TransportWriteOperation Send(ReadOnlySpan<byte> data, object? state = null);
+    public virtual TransportWriteOperation Send(in ReadOnlySequence<byte> data, object? state = null);
+    public abstract TransportWriteOperation SendBorrowed(in ReadOnlySequence<byte> data, object? state = null);
+    public abstract void ShutdownRead();
+    public abstract void ShutdownWrite();
+    public abstract void Abort(Exception? error = null);
+}
+
+public readonly struct TransportWriteOperation : IEquatable<TransportWriteOperation>
+{
+    public long Id { get; }
+    public object? State { get; }
+    public bool IsValid { get; }
+}
+
+public abstract class TransportReceiveLease : IDisposable
+{
+    protected TransportReceiveLease();
+    public abstract ReadOnlySequence<byte> Buffer { get; }
+    public abstract void Dispose();
+}
```

### Callback context types

- Use `ref struct` for callbacks carrying borrowed buffers.
- Do not expose unwiped buffer variants.
- Include initial/next-write fast paths as optional context members.
- Include the connect operation, listener, and origin where applicable.
- Keep receive EOF separate from terminal connection close.
- Let a receive callback try to retain the same read-only bytes under a provider-owned lease; do not expose writable memory or native buffer identities.

**Why:** Lexical buffer lifetime remains the safe allocation-free default. Some infrastructure consumers, however, deliberately retain framed-message slices after native completion. Requiring those consumers to copy would prevent reuse of the provider's registered-buffer engine. `TryRetainPayload` makes that uncommon path explicit and bounded, while a read-only `TransportReceiveLease` prevents mutation and keeps io_uring buffer IDs, RIO registrations, OVERLAPPED state, and addresses private. A lease remains valid across connection and engine shutdown, so providers must detach retained storage rather than block disposal or expose freed memory. Contexts also avoid allocating a result object for each ordinary completion. The immediate-response and next-write paths are useful, but they must be bounded and safe by default; the runtime API should not expose SocketSet's sharp unwiped escape hatch.

```diff
 namespace System.Net.Transport;

+public ref struct TransportAcceptingContext
+{
+    public TransportListener Listener { get; }
+    public TransportConnection Connection { get; }
+    public TransportServerTlsOptions? Tls { get; set; }
+    public void Reject(Exception? error = null);
+}
+
+public ref struct TransportReadyContext
+{
+    public TransportConnection Connection { get; }
+    public TransportConnectionOrigin Origin { get; }
+    public TransportListener? Listener { get; }
+    public TransportConnectOperation ConnectOperation { get; }
+    public Span<byte> GetWriteSpan(int sizeHint = 0);
+    public int WriteBytes { get; set; }
+}
+
+public ref struct TransportReceiveContext
+{
+    public TransportConnection Connection { get; }
+    public ReadOnlySpan<byte> Payload { get; }
+    public bool IsCompleted { get; }
+    public bool TryRetainPayload([NotNullWhen(true)] out TransportReceiveLease? lease);
+    public Span<byte> GetResponseSpan(int sizeHint = 0);
+    public int ResponseBytes { get; set; }
+    public void StopReceiving();
+}
+
+public ref struct TransportWriteCompletedContext
+{
+    public TransportConnection Connection { get; }
+    public TransportWriteOperation Operation { get; }
+    public Exception? Error { get; }
+    public Span<byte> GetWriteSpan(int sizeHint = 0);
+    public int WriteBytes { get; set; }
+}
```

### Close and failure types

- Preserve phase, close reason, and exception.
- Pair every accepted connection with exactly one close callback, including pre-ready TLS failures.
- Use `OnConnectFailed` only when no application-visible connection is ready.
- Keep listener and worker failures separate from connection close.

**Why:** A generic close callback with no error makes adapters invent exceptions and prevents useful retry or diagnostic decisions. Phase distinguishes a failed handshake from a live-connection failure without requiring exception-string parsing. Separating worker failure prevents one connection error from looking provider-wide.

```diff
 namespace System.Net.Transport;

+public enum TransportConnectionPhase
+{
+    Connecting,
+    Accepting,
+    Handshaking,
+    Ready,
+    Closing,
+}
+
+public enum TransportCloseReason
+{
+    LocalShutdown,
+    LocalAbort,
+    PeerClosed,
+    PeerReset,
+    ConnectFailed,
+    TlsHandshakeFailed,
+    Timeout,
+    ProviderStopped,
+    Error,
+}
+
+public readonly ref struct TransportConnectFailedContext
+{
+    public TransportConnectOperation Operation { get; }
+    public Exception Error { get; }
+}
+
+public readonly ref struct TransportClosedContext
+{
+    public TransportConnection Connection { get; }
+    public TransportConnectionPhase Phase { get; }
+    public TransportCloseReason Reason { get; }
+    public Exception? Error { get; }
+}
+
+public readonly ref struct TransportListenerClosedContext
+{
+    public TransportListener Listener { get; }
+    public Exception? Error { get; }
+}
+
+public readonly ref struct TransportWorkerFaultedContext
+{
+    public int WorkerIndex { get; }
+    public Exception Error { get; }
+}
```

### Transport TLS configuration and ClientHello callbacks

- Remove `AuthenticateAsClientAsync`, `AuthenticateAsServerAsync`, and `ObserveTlsClientHelloAsync` from the low-level connection.
- Configure TLS on listen/connect or in the accepting callback.
- Make raw ClientHello observation a synchronous handshake callback.
- Permit an async options-selection callback that suspends the handshake.
- Reuse `SslClientAuthenticationOptions` and `SslServerAuthenticationOptions`.
- Do not promise a raw-byte `UseHttps` middleware boundary for native TLS providers.
- Keep fd-bound OpenSSL, memory-BIO OpenSSL, Schannel, and kTLS implementation types internal.

**Why:** Authentication is a transport state transition before readiness, not an application read/write operation. OpenSSL and the DirectTLS study both demonstrate that ClientHello can be observed from the fd-bound handshake callback without a memory BIO or a separate public invocation. Reusing the existing options avoids a second TLS policy model. The choice between fd-bound OpenSSL, memory BIO, `SslStream`, Schannel, and kTLS belongs to internal provider code, not public usage.

```diff
 namespace System.Net.Transport;

+public sealed class TransportClientTlsOptions
+{
+    public required SslClientAuthenticationOptions AuthenticationOptions { get; init; }
+    public TransportTlsOffloadOptions Offload { get; init; }
+}
+
+public sealed class TransportServerTlsOptions
+{
+    public required SslServerAuthenticationOptions AuthenticationOptions { get; init; }
+    public TransportClientHelloCallback? ClientHelloCallback { get; init; }
+    public TransportServerOptionsSelectionCallback? OptionsSelectionCallback { get; init; }
+    public TimeSpan ClientHelloTimeout { get; set; }
+    public TimeSpan HandshakeTimeout { get; set; }
+    public bool AllowPostHandshakeClientAuthentication { get; init; }
+    public TransportTlsOffloadOptions Offload { get; init; }
+}
+
+public delegate void TransportClientHelloCallback(ref TransportClientHelloContext context);
+public delegate ValueTask<SslServerAuthenticationOptions> TransportServerOptionsSelectionCallback(
+    TransportServerOptionsSelectionContext context,
+    CancellationToken cancellationToken);
```

### Async, Stream, and Pipelines adapters

- Keep them out of the core assembly surface for the first implementation.
- Build them over operation IDs and callbacks.
- Put the Pipelines adapter in a separate optional assembly.
- Do not let adapter requirements force task state into every native completion.

**Why:** Kestrel, Redis, and ordinary .NET callers need async APIs, but those APIs can be implemented once above the provider engine. SocketSet's Redis adapter already proves connect callbacks can become a Task, and its Kestrel adapter proves accept callbacks can become `AcceptAsync`. Separating the layers preserves both efficient provider callbacks and idiomatic high-level APIs.

```diff
+// No task-based read/write/accept API in the core reference surface.
+// A separate System.Net.Transport.Pipelines assembly adapts callbacks to Pipelines.
```

## Assembly assessment

| Assembly | Recommendation |
|---|---|
| `System.Net.Sockets.dll` | Keep unchanged; the managed provider composes existing Socket/SAEA |
| `System.Net.Security.dll` | Keep existing TLS options and `SslStream`; define or refactor the native TLS boundary needed by transport |
| `System.Net.Transport.dll` | Preferred home for callback contracts, provider options, and internal backend implementations |
| `System.Net.Transport.Pipelines.dll` | Add only when the callback core is proven; optional adapter dependency |
| ASP.NET Core | Keep Kestrel-specific scheduling, features, limits, and listener adaptation here |

The clean API placement is the assembly whose name matches the namespace. The unresolved implementation question is how fd-bound OpenSSL and Schannel reuse runtime TLS internals. Solve that through a separately reviewed low-level API or a lower shared implementation component, not by moving the public transport API into `System.Net.Security.dll` solely for internal access and not through framework `InternalsVisibleTo` or `UnsafeAccessor`.

## Current-to-revised comparison

| Existing API or first draft | Revised recommendation |
|---|---|
| `Socket.ReceiveAsync`-like connection API | Provider-owned `OnReceive(ref context)` callback |
| `TransportConnection.ReadAsync` plus `AdvanceRead` | Borrowed `ReadOnlySpan<byte>` with lexical callback lifetime |
| `TransportConnection.WriteAsync` | `IBufferWriter<byte>` plus `Flush` operation identity and completion callback |
| `ListenAsync` returning async-disposable listener | Synchronous `Listen` returning independently disposable listener |
| `ConnectAsync` | Synchronous submission returning cancelable operation handle; async adapter above |
| `AuthenticateAsServerAsync` | TLS policy selected before readiness; provider drives handshake |
| `ObserveTlsClientHelloAsync` | Callback invoked by the TLS handshake |
| One common backend-neutral options bag | Portable engine options plus typed provider options |
| Concrete public provider classes | Static provider factory methods returning common provider |
| `OnClosed(Connection)` | Structured close context with phase, reason, and exception |
| Whole engine owns every listener | Separate listener handle |
| Managed Socket fallback as different programming model | Managed Socket/SAEA implementation of the same callback API |

The revised API is directionally stronger than the first draft. The remaining review bar is evidence: implement the managed provider and adapters first, then epoll and IOCP, before deciding which experimental types deserve stable public surface.
