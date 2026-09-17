# Proposed callback-first transport API

> **Status:** Revised design candidate. The API is illustrative, experimental, unapproved, and unimplemented.
>
> **Primary change from the first draft:** the low-level data plane no longer exposes `ReadAsync`, `WriteAsync`, `AcceptAsync`, or `AuthenticateAsServerAsync`. It uses synchronous submission and provider-owned callbacks. Task-based connect, Stream, Pipelines, and Kestrel APIs are adapters above this layer.

## Decision

The core transport API should follow the useful part of SocketSet's model:

- one engine owns shared provider resources;
- `Listen` and `Connect` synchronously submit control-plane work;
- accept, connect, receive, write completion, TLS progression, and close are callbacks;
- receive buffers are borrowed only for the callback by default, with an optional bounded retained lease for adapters that must keep the received bytes;
- a connection is stable identity, outbound writer, close control, backpressure control, and metadata;
- TLS is selected as connection policy and driven by the engine/provider, not invoked as a high-level method on the connection;
- async APIs are optional adapters.

This does **not** make the implementation synchronous. epoll, io_uring, IOCP, RIO, SAEA, OpenSSL, and Schannel still have asynchronous state, operation identities, cancellation races, and terminal completions. The difference is where that state is represented: provider-owned slots and callbacks rather than one `Task`/`ValueTask` state machine per read and write.

## Acceptance criteria for this API revision

1. The core surface contains no task-based read, write, accept, or TLS-authentication method.
2. A listener has an independent lifetime instead of being owned only by the whole engine.
3. An outbound connection attempt has a first-class correlation and cancellation handle.
4. A write submission has a first-class completion identity.
5. Close notification carries phase, reason, and exception.
6. Portable engine policy is separated from typed managed, epoll, io_uring, IOCP, and RIO provider options.
7. ClientHello observation is a callback raised by the TLS handshake state machine, not a separate consumer invocation.
8. Built-in provider implementations remain internal; consumers select them through one provider-level API.
9. The managed provider uses ordinary `System.Net.Sockets.Socket` and `SocketAsyncEventArgs` while presenting exactly the same callbacks as native providers.
10. Higher-level async, Pipelines, and message-framing adapters can retain provider receive storage and submit stable segmented writes without changing the core provider contract.

## Assembly map

Preferred API placement:

| Assembly | Responsibility |
|---|---|
| `System.Net.Transport.dll` | Core callback API, provider selection/options, and internal backend implementations |
| `System.Net.Transport.Pipelines.dll` | Optional Task, Stream, and Pipelines adapters |
| `System.Net.Sockets.dll` | Existing Socket/SAEA implementation used by the managed provider |
| `System.Net.Security.dll` | Existing TLS options, certificates, and `SslStream` |

`System.Net.Security.dll` already references `System.Net.Sockets.dll`. A new `System.Net.Transport.dll` can reference both existing assemblies without creating a dependency cycle.

The unresolved part is native TLS reuse. fd-bound OpenSSL and the Schannel token engine currently depend on internal security implementation. The preferred public placement therefore requires one deliberate follow-up:

- expose a small experimental low-level TLS engine from `System.Net.Security`; or
- refactor shared TLS implementation into a lower internal component referenced by both assemblies.

Putting the entire transport API in `System.Net.Security.dll` remains the simplest implementation option, but the assembly name and responsibility would be misleading. Duplicating TLS policy or adding framework `InternalsVisibleTo`/`UnsafeAccessor` is not acceptable.

## Layer boundaries

```mermaid
flowchart TB
    App["Application or framework"]

    subgraph Transport["NEW: System.Net.Transport.dll"]
        Core["Transport callbacks<br/>System.Net.Transport"]
        Provider["Provider options<br/>Transport subnamespaces"]
        Managed["Managed Socket provider<br/>internal"]
        Native["Native providers<br/>internal"]
        TlsBridge["Native TLS bridge<br/>internal / boundary TBD"]
    end

    subgraph Adapters["NEW: Transport.Pipelines.dll"]
        Async["Task / Stream / Pipelines"]
    end

    Sockets["System.Net.Sockets.dll<br/>Socket / SAEA"]
    Security["System.Net.Security.dll<br/>TLS options / SslStream"]

    App --> Core
    App --> Async
    Async --> Core
    Core --> Provider
    Provider --> Managed
    Provider --> Native
    Managed --> Sockets
    Managed --> Security
    Native --> TlsBridge
    TlsBridge -. TLS boundary .-> Security

    classDef consumer fill:#f5f5f5,stroke:#616161,color:#111
    classDef existingAssembly fill:#e8f5e9,stroke:#2e7d32,color:#111
    classDef newAssembly fill:#e3f2fd,stroke:#1565c0,color:#111

    class App consumer
    class Core,Provider,Managed,Native,TlsBridge,Async newAssembly
    class Sockets,Security existingAssembly
```

Diagram colors show assembly placement: blue is a new assembly, green is an existing runtime assembly, and gray is consumer code.

The core API ends at callbacks and borrowed buffers. `Task`, `Stream`, and `IDuplexPipe` ownership begins in the adapter layer.

## Proposed reference surface

### Usage chain and callback ownership

The caller creates three independent objects and joins them at `TransportProvider.CreateEngine`:

```csharp
TransportProvider provider = TransportProviders.CreateDefault();

var application = new EchoApplication();

var engineOptions = new TransportEngineOptions
{
    InitialWorkerCount = Environment.ProcessorCount,
};

using TransportEngine engine = provider.CreateEngine(engineOptions, application);

using TransportListener listener = engine.Listen(new TransportListenOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 5000),
});
```

Their roles are different:

| Object | Created by | Responsibility |
|---|---|---|
| `TransportProvider` | Caller through `TransportProviders` | Selects one implementation and carries provider-specific configuration. It is a cheap factory and does not own active connections. |
| `ITransportApplication` | Caller, normally by deriving from `TransportApplication` | Receives every lifecycle and data callback from the engine created with it. It contains adapter or protocol integration logic, not native provider resources. |
| `TransportEngineOptions` | Caller | Configures provider-independent worker and capacity policy for one engine. |
| `TransportEngine` | Provider when `CreateEngine` is called | Owns workers, rings, pollers, completion ports, buffer pools, listeners, connections, and the registered application callback target. |
| `TransportListener` | Engine when `Listen` is called | Owns one bound server endpoint and its accept operations. It reports accepted connections through the engine's application. |

`CreateEngine(options, application)` is the attachment point between the engine and application. The engine retains the application reference for its entire lifetime. Every listener and outbound connection created by that engine reports through that same application:

```text
TransportProviders
    -> creates TransportProvider

caller creates ITransportApplication
caller creates TransportEngineOptions

TransportProvider.CreateEngine(options, application)
    -> creates TransportEngine
    -> engine retains application
    -> engine starts provider resources

TransportEngine.Listen(...)
    -> OnAccepting
    -> optional TLS handshake
    -> OnReady
    -> OnReceive*
    -> OnWriteCompleted*
    -> OnClosed

TransportEngine.Connect(...)
    -> OnReady
       or OnConnectFailed
```

For a server, creating the engine only starts the shared provider resources. It does not bind a TCP endpoint. The next operation is `engine.Listen(options)`.

`Listen` performs the server control-plane setup synchronously:

1. validate the endpoint and listener options;
2. create and bind the native listening socket;
3. start listening with the requested backlog;
4. register the listener with the selected provider worker or workers;
5. arm the initial accept operations;
6. return the persistent `TransportListener`.

At that point the server is accepting connections. The caller does not call `AcceptAsync` on the low-level listener. Accepted connections are delivered to the application registered with the engine:

```text
engine.Listen(options)
    -> returns TransportListener

remote client connects
    -> provider accepts TCP connection
    -> provider creates TransportConnection
    -> application.OnAccepting
         can tag or reject the connection
         can adjust the preseeded server TLS policy
    -> provider performs TLS handshake when configured
    -> application.OnReady
         connection is now available for application data
    -> application.OnReceive
    -> application can call connection.Send or SendBorrowed
    -> application.OnWriteCompleted
    -> application.OnClosed
```

The returned listener controls the accept source:

```csharp
listener.Dispose();
```

Disposing it stops new accepts but does not close `TransportConnection` instances which were already delivered to the application. Disposing the engine stops every listener and terminates the remaining engine-owned connections.

The higher-level Pipelines adapter converts this callback-oriented server path into `AcceptAsync`:

```csharp
await using TransportPipeListener listener = pipeEngine.Listen(options);

TransportPipeConnection? connection = await listener.AcceptAsync();
```

Internally, its engine-wide application receives `OnReady`, creates a `TransportPipeConnection`, and places it in the corresponding listener's accept queue.

The application is not a connection and does not mean that application business logic runs inside provider callbacks. It is the engine-wide callback receiver, usually implemented by an adapter:

- `System.Net.Transport.Pipelines` uses one internal application to turn callbacks into `TransportPipeListener`, `TransportPipeConnection`, `PipeReader`, and `PipeWriter` operations;
- a protocol library can use one application to route callbacks into its own per-connection state;
- a low-level server can derive directly from `TransportApplication`.

One application instance may therefore receive callbacks for many listeners and connections concurrently. Callback contexts identify the relevant listener, connection, connect operation, or write operation. Per-connection adapter state is normally stored in `TransportConnection.State`:

```csharp
protected override void OnReady(ref TransportReadyContext context)
{
    context.Connection.State = new ProtocolConnection(context.Connection);
}

protected override void OnReceive(ref TransportReceiveContext context)
{
    var connection = (ProtocolConnection)context.Connection.State!;

    connection.OnReceive(ref context);
}
```

Disposing the engine stops callback production only after provider operations reach their required terminal state. The caller must keep the application alive until engine disposal returns. The application does not dispose the engine from inside a callback.

### Provider selection

Built-in provider implementation classes remain internal. The public static factory gives callers typed configuration without exposing the implementation class itself.

```csharp
namespace System.Net.Transport;

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public static class TransportProviders
{
    public static TransportProvider CreateDefault();

    public static TransportProvider ManagedSockets(Sockets.ManagedSocketTransportOptions? options = null);

    [SupportedOSPlatform("linux")]
    public static TransportProvider Epoll(Epoll.EpollTransportOptions? options = null);

    [SupportedOSPlatform("linux")]
    public static TransportProvider IoUring(IoUring.IoUringTransportOptions? options = null);

    [SupportedOSPlatform("windows")]
    public static TransportProvider WindowsIocp(Iocp.IocpTransportOptions? options = null);

    [SupportedOSPlatform("windows")]
    public static TransportProvider WindowsRio(Rio.RioTransportOptions? options = null);
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public abstract class TransportProvider
{
    protected TransportProvider();

    public abstract string Name { get; }
    public abstract bool IsSupported { get; }

    public abstract TransportEngine CreateEngine(TransportEngineOptions options, ITransportApplication application);
}
```

The provider is not the active event loop. It becomes active only when the caller supplies an engine configuration and application callback target to `CreateEngine`.

`CreateDefault` probes every time it is called rather than returning one globally cached provider. Proposed default order:

1. Windows IOCP;
2. Linux io_uring when the required features are usable;
3. Linux epoll;
4. managed Socket.

RIO is never selected automatically.

### Typed provider options

The first draft omitted these. The revised API makes them explicit and provider-specific.

```csharp
namespace System.Net.Transport.Sockets
{
    [Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
    public sealed class ManagedSocketTransportOptions
    {
        public ManagedSocketTransportOptions();

        public int ReceiveBufferSize { get; set; } = 4096;
        public int WriteBufferSize { get; set; } = 4096;
        public int WriteBufferCount { get; set; } = 1024;
        public bool WaitForDataBeforeAllocatingBuffer { get; set; } = true;
        public bool PreferInlineCompletions { get; set; }
    }
}

namespace System.Net.Transport.Epoll
{
    [Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
    public sealed class EpollTransportOptions
    {
        public EpollTransportOptions();

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
    [Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
    public sealed class IoUringTransportOptions
    {
        public IoUringTransportOptions();

        public int RingEntryCount { get; set; } = 4096;
        public int ProvidedBufferCount { get; set; } = 256;
        public int ReceiveBufferSize { get; set; } = 4096;
        public int WriteBufferSize { get; set; } = 4096;
        public int WriteBufferCount { get; set; } = 1024;
        public int OutOfBandWriteBufferCount { get; set; } = 256;
        public int MaximumRetainedReceiveBuffers { get; set; } = 128;
        public bool ReusePort { get; set; } = true;
    }
}

namespace System.Net.Transport.Iocp
{
    [Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
    public sealed class IocpTransportOptions
    {
        public IocpTransportOptions();

        public int CompletionBatchSize { get; set; } = 128;
        public int AcceptConcurrency { get; set; } = 32;
        public int ReceiveBufferSize { get; set; } = 4096;
        public int WriteBufferSize { get; set; } = 4096;
        public int WriteBufferCount { get; set; } = 1024;
    }
}

namespace System.Net.Transport.Rio
{
    [Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
    public sealed class RioTransportOptions
    {
        public RioTransportOptions();

        public int CompletionQueueSize { get; set; } = 4096;
        public int AcceptConcurrency { get; set; } = 32;
        public int ReceiveBufferSize { get; set; } = 4096;
        public int SendBufferSize { get; set; } = 65536;
        public int RegisteredSendBufferCount { get; set; } = 256;
    }
}
```

These are candidate experimental resource knobs, not a claim that every value should stabilize. TLS implementation strategy is deliberately absent: configuring TLS has one public meaning, while fd-bound OpenSSL, memory-BIO OpenSSL, `SslStream`, Schannel, and kTLS are provider implementation decisions.

### Application callback target

The application is created by the caller and passed to `TransportProvider.CreateEngine`. The resulting engine retains it as its callback target. It is separate from the engine so one object does not simultaneously represent provider resources, listener lifetime, callbacks, and user protocol state.

```csharp
[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
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
    protected TransportApplication();

    protected virtual void OnAccepting(ref TransportAcceptingContext context);

    protected virtual void OnReady(ref TransportReadyContext context);

    protected virtual void OnConnectFailed(ref TransportConnectFailedContext context);

    protected virtual void OnReceive(ref TransportReceiveContext context);

    protected virtual void OnWriteCompleted(ref TransportWriteCompletedContext context);

    protected virtual void OnClosed(ref TransportClosedContext context);

    protected virtual void OnListenerClosed(ref TransportListenerClosedContext context);

    protected virtual void OnWorkerFaulted(ref TransportWorkerFaultedContext context);

    protected virtual void OnBatchCompleted(int workerIndex);

    void ITransportApplication.OnAccepting(ref TransportAcceptingContext context);
    void ITransportApplication.OnReady(ref TransportReadyContext context);
    void ITransportApplication.OnConnectFailed(ref TransportConnectFailedContext context);
    void ITransportApplication.OnReceive(ref TransportReceiveContext context);
    void ITransportApplication.OnWriteCompleted(ref TransportWriteCompletedContext context);
    void ITransportApplication.OnClosed(ref TransportClosedContext context);
    void ITransportApplication.OnListenerClosed(ref TransportListenerClosedContext context);
    void ITransportApplication.OnWorkerFaulted(ref TransportWorkerFaultedContext context);
    void ITransportApplication.OnBatchCompleted(int workerIndex);
}
```

Providers invoke the public `ITransportApplication` contract. Most consumers derive from `TransportApplication`, which explicitly implements that interface and offers protected virtual methods so only the callbacks they need must be overridden.

The callback target is engine-wide:

- `OnAccepting`, `OnReady`, `OnReceive`, `OnWriteCompleted`, and `OnClosed` identify one connection;
- `OnConnectFailed` identifies one outbound connect operation which never became ready;
- `OnListenerClosed` identifies one listener;
- `OnWorkerFaulted` identifies provider-wide worker failure;
- `OnBatchCompleted` identifies completion of one provider worker batch.

Callback rules:

- callbacks for one connection are serialized on its owning provider context;
- callbacks for different connections may run concurrently;
- callback buffers are borrowed and cannot escape the callback unless `TryRetainPayload` succeeds;
- a retained payload lease may escape the callback, but the provider does not reuse its storage until the lease is disposed;
- an exception from application code fails only that connection and is reported through `OnClosed`;
- `OnWorkerFaulted` is reserved for a provider-wide worker failure;
- `OnBatchCompleted` permits protocol adapters to coalesce work at provider-batch granularity.

### Engine configuration

After selecting a provider and creating the application callback target, the caller configures the engine which will join them. These options express policy shared by every provider. They deliberately exclude ring entries, epoll event batches, IOCP completion batches, RIO queue depth, and provider buffer-registration mechanics.

```csharp
[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportEngineOptions
{
    public TransportEngineOptions();

    public int InitialWorkerCount { get; set; }
    public int MaximumWorkerCount { get; set; }
    public int MaximumConnectionsPerWorker { get; set; } = 4096;
    public bool PinWorkerThreads { get; set; }
    public bool TrackEndpoints { get; set; } = true;
    public TimeSpan IdleTimeout { get; set; }
}
```

Semantics:

- `InitialWorkerCount == 0` lets the selected provider choose.
- `MaximumWorkerCount == 0` disables dynamic growth.
- a provider may clamp worker count to its model; the managed provider uses one logical worker;
- `MaximumConnectionsPerWorker` is a capacity policy, not a ring-entry or buffer-count setting;
- provider-resolved values are readable from the created engine.

### Engine creation and lifetime

`TransportProvider.CreateEngine(options, application)` synchronously creates the active resource owner. It may allocate native resources and start workers, and it fails before returning.

```csharp
[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public abstract class TransportEngine : IDisposable
{
    protected TransportEngine();

    public abstract TransportProvider Provider { get; }
    public abstract TransportEngineOptions Options { get; }
    public abstract int WorkerCount { get; }

    public abstract TransportListener Listen(TransportListenOptions options);

    public abstract TransportConnectOperation Connect(TransportConnectOptions options);

    public abstract void Dispose();
}
```

An engine has exactly one application callback target in this proposal. The same target receives events for all listeners and outbound connections created by the engine. Consumers which want separate logical applications can create separate engines or use one application adapter which routes by listener, connection, or operation state.

The engine/application relationship is:

```text
TransportEngine
    owns provider workers and connections
    retains one ITransportApplication
    invokes that application for completions

ITransportApplication
    does not own native provider resources
    receives contexts identifying the affected object
    routes work to per-listener or per-connection state
```

### Listener creation and lifetime

```csharp
[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportListenOptions
{
    public TransportListenOptions();

    public required IPEndPoint EndPoint { get; init; }
    public int Backlog { get; set; } = 512;
    public bool NoDelay { get; set; } = true;
    public object? State { get; init; }
    public TransportServerTlsOptions? Tls { get; set; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public abstract class TransportListener : IDisposable
{
    protected TransportListener();

    public abstract long Id { get; }
    public abstract IPEndPoint LocalEndPoint { get; }
    public abstract object? State { get; }
    public abstract bool IsAccepting { get; }

    public abstract void Dispose();
}
```

`Listen` returns only after bind, listen, provider registration, and accept arming succeed. Disposing a listener:

- stops new accepts;
- waits for accept operations owned by that listener to reach terminal state;
- does not close connections already delivered through `OnAccepting`;
- triggers `OnListenerClosed` once.

`TransportEngine.Dispose` synchronously stops every listener, aborts remaining connections, drains provider operation state, joins provider workers, and then returns. It must not be called from a provider callback; doing so throws `InvalidOperationException`. A higher-level host can wrap blocking shutdown in its own async lifetime if needed.

### Connect submission and correlation

```csharp
[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportConnectOptions
{
    public TransportConnectOptions();

    public required EndPoint RemoteEndPoint { get; init; }
    public IPEndPoint? LocalEndPoint { get; init; }
    public bool NoDelay { get; set; } = true;
    public int? RequiredWorkerIndex { get; init; }
    public object? State { get; init; }
    public TransportClientTlsOptions? Tls { get; set; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public readonly struct TransportConnectOperation :
    IEquatable<TransportConnectOperation>
{
    public long Id { get; }
    public object? State { get; }
    public bool IsValid { get; }

    public bool Cancel();
}
```

`Connect` is a synchronous submission:

- it validates the endpoint and required worker;
- reserves provider capacity;
- creates or queues the native connect operation;
- returns a value-type operation handle.

Completion:

- success produces `OnReady` with both the operation and the ready connection;
- connect, TLS, cancellation, or provider failure before readiness produces `OnConnectFailed`;
- `Cancel` requests cancellation but the provider retains native state until its terminal completion;
- one operation callback is produced exactly once.

This fixes SocketSet's current `void Connect(...)` correlation gap without requiring a `Task` in the core.

### Pre-handshake accept and post-handshake ready phases

An accepted TCP connection is visible before TLS so frameworks can count, reject, tag, and apply handshake limits before expensive authentication.

```csharp
[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public ref struct TransportAcceptingContext
{
    public TransportListener Listener { get; }
    public TransportConnection Connection { get; }

    public TransportServerTlsOptions? Tls { get; set; }

    public void Reject(Exception? error = null);
}

public enum TransportConnectionOrigin
{
    Accepted = 0,
    Connected = 1,
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public ref struct TransportReadyContext
{
    public TransportConnection Connection { get; }
    public TransportConnectionOrigin Origin { get; }
    public TransportListener? Listener { get; }
    public TransportConnectOperation ConnectOperation { get; }

    public Span<byte> GetWriteSpan(int sizeHint = 0);
    public int WriteBytes { get; set; }
}
```

Lifecycle:

1. TCP accept creates connection identity.
2. `OnAccepting` runs before TLS.
3. The callback may reject, change the preseeded TLS policy, or leave it plaintext.
4. If TLS is selected, the provider drives the handshake and its registered ClientHello/options callbacks.
5. `OnReady` runs only after plaintext selection or successful TLS authentication.
6. `OnReceive` begins only after `OnReady` returns.

For outbound connections, `OnReady` is raised after TCP connect and any configured TLS handshake.

### Connection API

The connection has no read method. Receive belongs to the provider and is delivered through `OnReceive`.

```csharp
[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public abstract class TransportConnection : IBufferWriter<byte>
{
    protected TransportConnection();

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

    public virtual TransportWriteOperation Send(ReadOnlySpan<byte> data, object? state = null);

    public virtual TransportWriteOperation Send(in ReadOnlySequence<byte> data, object? state = null);

    public abstract TransportWriteOperation SendBorrowed(in ReadOnlySequence<byte> data, object? state = null);

    public abstract void ShutdownRead();
    public abstract void ShutdownWrite();
    public abstract void Abort(Exception? error = null);
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public readonly struct TransportWriteOperation :
    IEquatable<TransportWriteOperation>
{
    public long Id { get; }
    public object? State { get; }
    public bool IsValid { get; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public abstract class TransportReceiveLease : IDisposable
{
    protected TransportReceiveLease();

    public abstract ReadOnlySequence<byte> Buffer { get; }
    public abstract void Dispose();
}
```

Write contract:

- `GetSpan`/`GetMemory` return provider-owned outbound memory;
- `Advance` commits bytes to the current composition;
- `Flush` submits one logical write and returns its correlation handle;
- `Send` copies into provider-owned memory and flushes;
- `SendBorrowed` submits a caller-owned sequence which must remain readable and unchanged until `OnWriteCompleted`;
- connection commands may be called after a callback returns and from a non-provider thread; the provider marshals the command to the connection's owning worker when necessary;
- the caller or adapter must serialize write composition and submission for one connection; concurrent calls have no defined ordering;
- a provider may pin and scatter/gather the borrowed sequence, use registered or zero-copy send paths, or copy internally; the API promises lifetime and logical completion semantics, not a specific zero-copy mechanism;
- writes are ordered;
- `OnWriteCompleted` fires once per logical flush after all partial native writes complete;
- the callback carries success or failure and the original operation/state;
- only one application writer may compose bytes at a time;
- providers may queue multiple completed compositions, but they retain ownership and ordering.

This avoids exposing a task while still giving an async adapter enough identity to complete the correct waiter.

### Receive and write callbacks

```csharp
[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public ref struct TransportReceiveContext
{
    public TransportConnection Connection { get; }
    public ReadOnlySpan<byte> Payload { get; }
    public bool IsCompleted { get; }

    public bool TryRetainPayload([NotNullWhen(true)] out TransportReceiveLease? lease);

    public Span<byte> GetResponseSpan(int sizeHint = 0);
    public int ResponseBytes { get; set; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public ref struct TransportWriteCompletedContext
{
    public TransportConnection Connection { get; }
    public TransportWriteOperation Operation { get; }
    public Exception? Error { get; }

    public Span<byte> GetWriteSpan(int sizeHint = 0);
    public int WriteBytes { get; set; }
}
```

Receive contract:

- `Payload` is borrowed for the callback and cannot itself be retained;
- `TryRetainPayload` optionally transfers the callback payload into a provider-owned `TransportReceiveLease` over the same bytes, without copying;
- `TryRetainPayload` returns `false` when the selected provider cannot retain that receive or when its documented retained-buffer bound has been reached; it does not return a copied lease;
- a successful lease may outlive the callback, and its `Buffer` remains readable and unchanged until `Dispose`;
- disposing the lease releases the entire retained payload; consumers retain only the leases that cover bytes still referenced by their parser or messages;
- connection close and engine disposal do not invalidate an outstanding lease; on shutdown the provider detaches retained storage from reusable native pools so engine disposal does not wait indefinitely for application-held leases;
- `Dispose` is idempotent, and accessing `Buffer` after disposal throws `ObjectDisposedException`;
- the lease exposes managed sequence positions, not file descriptors, ring IDs, buffer-group IDs, CQE flags, native addresses, or writable memory;
- retaining a payload does not pause future receives by itself; slow consumers still use `TryPauseReceive`, and providers must bound completed and retained receive storage;
- receive and write progress are independent: one receive and one write may be active concurrently on a connection;
- `IsCompleted` means no later receive callback will carry payload;
- writing an immediate response into `GetResponseSpan` avoids an additional composition step;
- `ResponseBytes` may not exceed the returned span;
- no public unwiped-buffer escape hatch is proposed;
- slow-consumer adapters call `TryPauseReceive` and later `ResumeReceive`;
- provider-specific already-completed receive work remains bounded by provider options.

### Dispatching received data and writing later

`OnReceive` is a transport notification, not the scope in which an HTTP request, Orleans message, or other application operation must finish. The callback has three choices:

1. consume `Payload` synchronously and return;
2. retain the provider storage with `TryRetainPayload`, hand the lease to another scheduler, and dispose it after those bytes are consumed;
3. copy `Payload` into application-owned storage, hand the copy to another scheduler, and return immediately.

Only the borrowed `Payload` span expires when `OnReceive` returns. The `TransportConnection` is a persistent object and may be stored in per-connection state. Later code can call `Send`, compose through `GetMemory`/`Advance`/`Flush`, call `SendBorrowed`, pause or resume receives, or close the connection without first entering another `ITransportApplication` callback.

The provider still reports terminal write completion through `OnWriteCompleted`. A direct low-level consumer can ignore successful completion after a copying `Send` if it does not need notification. An adapter must observe completion when it owns a waiter or when memory was submitted through `SendBorrowed`.

#### Immediate callback response

`GetResponseSpan` is an optimization for a response which is completely known during the callback, such as an echo, protocol acknowledgment, or fixed rejection:

```csharp
protected override void OnReceive(ref TransportReceiveContext context)
{
    if (!context.Payload.IsEmpty)
    {
        Span<byte> response = context.GetResponseSpan(context.Payload.Length);
        context.Payload.CopyTo(response);
        context.ResponseBytes = context.Payload.Length;
    }

    if (context.IsCompleted)
    {
        context.Connection.ShutdownWrite();
    }
}
```

This does not retain the receive bytes until the network write completes. The callback copies the bytes from the borrowed receive buffer into separate provider-owned output memory. When the callback returns:

- the receive buffer can be reused unless it was retained;
- the provider owns the output memory until the write completes;
- the application does not wait for `OnWriteCompleted` unless it needs completion or error correlation.

This path is not the expected ASP.NET Core request path because HTTP parsing, middleware, and application execution should not run on a provider worker.

#### Retain, dispatch, process, and send

The following simplified example moves received chunks to an application queue while the transport remains free to receive later chunks and send earlier responses. It retains the original receive storage when possible and copies only when retention is unavailable:

```csharp
protected override void OnReceive(ref TransportReceiveContext context)
{
    ReceivedRequest request;
    if (context.Payload.IsEmpty)
    {
        request = ReceivedRequest.Completed(context.Connection);
    }
    else if (context.TryRetainPayload(out TransportReceiveLease? lease))
    {
        request = ReceivedRequest.FromLease(context.Connection, lease, context.IsCompleted);
    }
    else
    {
        request = ReceivedRequest.FromCopy(context.Connection, context.Payload.ToArray(), context.IsCompleted);
    }

    if (!_requests.Writer.TryWrite(request))
    {
        request.Dispose();
        context.Connection.Abort(new InvalidOperationException("The application queue is full."));
    }
}

private async Task ProcessRequestsAsync()
{
    await foreach (ReceivedRequest request in _requests.Reader.ReadAllAsync())
    {
        using (request)
        {
            if (!request.Buffer.IsEmpty)
            {
                byte[] response = await _handler(request.Buffer);

                // Send copies before returning, so response does not need to remain alive
                // until OnWriteCompleted.
                request.Connection.Send(response);
            }

            if (request.IsCompleted)
            {
                request.Connection.ShutdownWrite();
            }
        }
    }
}
```

The lease is returned by `request.Dispose()` after `_handler` has finished reading `request.Buffer`. It is not tied to response completion. The response uses different outbound storage.

There is deliberately no receive stop in this flow. While one retained chunk is waiting for or undergoing application processing, the provider may deliver later receive callbacks and may transmit previously submitted responses. A production adapter bounds queued bytes. It calls `TryPauseReceive` only when unconsumed input crosses its pause threshold and calls `ResumeReceive` after it falls below the resume threshold.

`ShutdownRead` permanently ends the local receive direction. It is for protocol shutdown or abort behavior, not ordinary request dispatch.

If the response is already held in stable segmented memory, the application can avoid the copying `Send`:

```csharp
OutboundMessage message = await _handler.CreateMessageAsync(request.Buffer);
request.Connection.SendBorrowed(message.Buffer, state: message);
```

The application must then release `message` only from terminal completion:

```csharp
protected override void OnWriteCompleted(ref TransportWriteCompletedContext context)
{
    if (context.Operation.State is OutboundMessage message)
    {
        message.Dispose();
    }

    if (context.Error is not null)
    {
        context.Connection.Abort(context.Error);
    }
}
```

The complete compilable shape of the first flow is in [`examples/DispatchedReceive.cs`](examples/DispatchedReceive.cs). It deliberately uses an abort-on-full bounded queue to keep the example small. A production protocol adapter should use byte-based thresholds and `TryPauseReceive`/`ResumeReceive`.

#### ASP.NET Core through the Pipelines adapter

ASP.NET Core application code does not retain a `TransportConnection` and does not implement `ITransportApplication`. The Pipelines adapter does both internally:

```text
provider OnReceive
    -> adapter retains or copies the received bytes
    -> adapter makes those bytes readable through TransportPipeConnection.Input
    -> adapter schedules the PipeReader continuation
    -> OnReceive returns

Kestrel protocol loop
    -> reads and parses TransportPipeConnection.Input
    -> advances Input after request bytes are consumed
       -> retained receive leases covering consumed bytes are disposed
    -> dispatches the parsed request to middleware/application code

application response
    -> writes to TransportPipeConnection.Output
    -> adapter send pump calls TransportConnection.SendBorrowed
    -> internal OnWriteCompleted advances Output and permits its memory to be reused
```

Request input memory therefore remains alive until the protocol advances past it, not until the application produces a response. Parsed request metadata has its own lifetime, and request-body bytes remain only while the HTTP stack still exposes or buffers them. The response travels through an independent output pipe and independent outbound buffers.

### Close and failure callbacks

```csharp
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

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public readonly ref struct TransportConnectFailedContext
{
    public TransportConnectOperation Operation { get; }
    public Exception Error { get; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public readonly ref struct TransportClosedContext
{
    public TransportConnection Connection { get; }
    public TransportConnectionPhase Phase { get; }
    public TransportCloseReason Reason { get; }
    public Exception? Error { get; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public readonly ref struct TransportListenerClosedContext
{
    public TransportListener Listener { get; }
    public Exception? Error { get; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public readonly ref struct TransportWorkerFaultedContext
{
    public int WorkerIndex { get; }
    public Exception Error { get; }
}
```

Unlike SocketSet's `OnClosed(Connection)`, this preserves the terminal reason. A connection that reached `OnAccepting` always reaches `OnClosed`, even when TLS fails before `OnReady`. A failed outbound attempt that never created application-visible connection identity reaches `OnConnectFailed`.

### Worker affinity

```csharp
[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public static class TransportExecutionContext
{
    public static int CurrentWorkerIndex { get; }
}
```

`CurrentWorkerIndex` is `-1` outside a provider-owned callback or for a callback-driven provider that cannot expose stable worker affinity. `TransportConnectOptions.RequiredWorkerIndex` permits proxy implementations to place an outbound connection on the current native worker. A provider that cannot honor an explicit required worker fails the connect submission rather than silently changing placement.

## TLS configuration is callback-driven

The connection has no `AuthenticateAsServerAsync` or `AuthenticateAsClientAsync`.

TLS is configured on the listener, connect options, or `OnAccepting` context. The engine drives the handshake before `OnReady`.

When TLS is configured:

- the provider owns the socket from accept/connect through handshake and application I/O;
- application callbacks never receive arbitrary raw TCP ciphertext;
- `ClientHelloCallback` is the supported early observation point;
- `OnReady` and `OnReceive` expose only authenticated plaintext;
- no `UseHttps`-style middleware insertion contract is required.

Condensed transport-facing configuration:

```csharp
[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportClientTlsOptions
{
    public TransportClientTlsOptions();

    public required SslClientAuthenticationOptions AuthenticationOptions { get; init; }
    public TransportTlsOffloadOptions Offload { get; init; } = new();
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportServerTlsOptions
{
    public TransportServerTlsOptions();

    public required SslServerAuthenticationOptions AuthenticationOptions { get; init; }
    public TransportClientHelloCallback? ClientHelloCallback { get; init; }
    public TransportServerOptionsSelectionCallback? OptionsSelectionCallback { get; init; }
    public TimeSpan ClientHelloTimeout { get; set; } = TimeSpan.FromSeconds(8);
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public bool AllowPostHandshakeClientAuthentication { get; init; }
    public TransportTlsOffloadOptions Offload { get; init; } = new();
}

public delegate void TransportClientHelloCallback(ref TransportClientHelloContext context);

public delegate ValueTask<SslServerAuthenticationOptions> TransportServerOptionsSelectionCallback(TransportServerOptionsSelectionContext context, CancellationToken cancellationToken);

public readonly ref struct TransportClientHelloContext
{
    public TransportConnection Connection { get; }
    public SslClientHelloInfo ClientHelloInfo { get; }
    public ReadOnlySequence<byte> FirstRecordBytes { get; }
    public bool ContainsCompleteClientHello { get; }
}

public sealed class TransportServerOptionsSelectionContext
{
    internal TransportServerOptionsSelectionContext();

    public TransportConnection Connection { get; }
    public SslClientHelloInfo ClientHelloInfo { get; }
    public SslServerAuthenticationOptions DefaultOptions { get; }
}
```

The raw ClientHello callback is synchronous and executes inside the handshake state transition. It is suitable for JA4 input capture and other bounded observation. The async options callback may suspend the handshake for certificate or policy selection.

`ClientHelloTimeout` bounds reaching and completing the raw callback phase. `HandshakeTimeout` bounds the remaining handshake, including async option selection. This preserves Kestrel's two timeout phases without a separate public `ObserveTlsClientHelloAsync` operation.

For fd-bound OpenSSL:

1. `SSL_do_handshake` reads through the nonblocking socket BIO.
2. OpenSSL's ClientHello callback runs after parsing the ClientHello.
3. the runtime invokes `ClientHelloCallback`;
4. if async option selection is required, the TLS engine suspends and returns a retry state;
5. the provider changes epoll/poll interest according to the TLS operation result and resumes the same handshake later.

No memory BIO or raw socket peek is required for the ClientHello callback.

The exact internal OpenSSL/Schannel TLS-session API is not proposed as public transport API here. Built-in providers choose their implementation internally. If third-party providers need the same native TLS engine, that lower-level security API requires a separate public API review.

## How the managed Socket provider fits

The managed provider is not a wrapper above a different connection model. It is one implementation of the same callback engine.

```mermaid
flowchart LR
    Select["TransportProviders.ManagedSockets"]
    Engine["TransportEngine"]
    Socket["System.Net.Sockets.Socket"]
    Saea["SocketAsyncEventArgs"]
    Callbacks["TransportApplication callbacks"]
    Tls["Provider-owned TLS<br/>internal choice"]

    Select --> Engine
    Engine --> Socket
    Socket --> Saea
    Saea --> Callbacks
    Engine --> Tls
```

Mapping:

| Core operation | Managed implementation |
|---|---|
| `Listen` | Create/bind/listen ordinary `Socket`; arm `Socket.AcceptAsync(SocketAsyncEventArgs)` |
| `Connect` | Create ordinary `Socket`; use SAEA connect completion; map the returned operation handle to that SAEA |
| receive callback | One SAEA receive buffer per connection; invoke `OnReceive` from completion processing |
| write composition | Provider-owned pooled arrays or pinned memory behind `IBufferWriter<byte>` |
| write completion | One logical operation tracked across partial `Socket.SendAsync` completions, then `OnWriteCompleted` |
| receive pause | Do not arm the next SAEA receive |
| abort | Dispose the Socket, retain SAEA state until callbacks are terminal |
| TLS | Use the same listener/connect TLS configuration and callbacks; implementation may choose the exact `SslStream` compatibility path or the runtime's internal OpenSSL/Schannel transform |
| worker affinity | `CurrentWorkerIndex == -1`; explicit required-worker connect is rejected |

This is the same consumer API as epoll, io_uring, IOCP, and RIO. The managed provider is therefore a compatibility provider and the benchmark control, not an adapter pretending to be a native engine.

## How provider-specific use differs

Consumer protocol and TLS code does not change. Only provider creation and provider resource options change.

```csharp
TransportProvider provider = TransportProviders.IoUring(new IoUringTransportOptions
{
    RingEntryCount = 4096,
    ProvidedBufferCount = 512,
    ReceiveBufferSize = 4096,
    WriteBufferSize = 16384,
});

using TransportEngine engine = provider.CreateEngine(new TransportEngineOptions
{
    InitialWorkerCount = Environment.ProcessorCount,
    MaximumConnectionsPerWorker = 4096,
    PinWorkerThreads = true,
}, application);

using TransportListener listener = engine.Listen(new TransportListenOptions
{
    EndPoint = new IPEndPoint(IPAddress.Any, 8443),
    Tls = serverTls,
});
```

Changing `IoUring` to `Epoll`, `WindowsIocp`, `WindowsRio`, or `ManagedSockets` changes provider construction and valid provider options, not connection callback code.

## Provider-specific usage examples

Compilable illustrative usage files are under [`examples`](examples/README.md):

- [`ManagedSocketsPlaintext.cs`](examples/ManagedSocketsPlaintext.cs)
- [`ManagedSocketsTls.cs`](examples/ManagedSocketsTls.cs)
- [`EpollPlaintext.cs`](examples/EpollPlaintext.cs)
- [`EpollTls.cs`](examples/EpollTls.cs)
- [`IoUringPlaintext.cs`](examples/IoUringPlaintext.cs)
- [`IoUringTls.cs`](examples/IoUringTls.cs)
- [`WindowsIocpTls.cs`](examples/WindowsIocpTls.cs)
- [`WindowsRioTls.cs`](examples/WindowsRioTls.cs)

Each file shows:

- provider selection;
- provider-specific options;
- engine and listener lifetime;
- the same callback-based read/write application;
- TLS configuration and when the handshake completes;
- important limitations of that provider.

## Higher-level async adapters

The core does not reject async programming. It moves async to adapters that have a reason to allocate and correlate tasks.

Examples:

- a client adapter stores a `TaskCompletionSource` keyed by `TransportConnectOperation.Id` and completes it from `OnReady` or `OnConnectFailed`;
- a write adapter stores a waiter keyed by `TransportWriteOperation.Id` and completes it from `OnWriteCompleted`;
- a message transport can submit its existing reference-counted serialized sequence through `SendBorrowed` and release or reroute its logical messages after terminal completion;
- a Pipelines or message-framing adapter first tries to retain `OnReceive` data and otherwise copies it, then pauses receiving when downstream backpressure applies;
- a Stream adapter serializes one read waiter and one write waiter;
- Kestrel implements `IConnectionListener.AcceptAsync` over the listener callbacks.

These adapters can offer cancellation tokens because they own the waiter and can call the core operation's cancellation or abort mechanism. The core provider still retains native state until terminal completion.

The candidate adapter API is specified separately in [pipelines-adapter.md](pipelines-adapter.md). It remains outside the core reference surface because its async listener, connection, scheduler, direct receive sink, custom retained input reader, and graceful-close behavior need implementation evidence before they can constrain the low-level transport.

## How this fixes the SocketSet issues

| SocketSet issue | Revised design |
|---|---|
| Portable and backend options share one bag | `TransportEngineOptions` contains only shared policy; each built-in provider has a typed options class |
| Whole engine owns all listeners with no listener object | `TransportEngine.Listen` returns an independently disposable `TransportListener` |
| `void Connect` has weak failure correlation | `TransportConnectOperation` identifies and cancels one attempt; completion callbacks carry it |
| `OnClosed(Connection)` loses the error | `TransportClosedContext` carries phase, reason, and exception |
| Public factory/shard/connection SPI exposes many mechanics | Built-in provider classes and native operation types stay internal; the public SPI is provider -> engine -> connection plus callback contexts |
| TLS provider SPI diverges from built-in fd-bound path | Built-in TLS integration remains inside the owning runtime assembly; no claim is made that the initial public transport SPI exposes every TLS implementation hook |
| Server TLS selection callback cannot see ClientHello | Raw and async option callbacks are raised from the handshake after ClientHello parsing |
| Unwiped buffer escape hatches are public | The proposed callback buffer API has no unwiped variant |
| Construction hides threads/native setup inside a subclass constructor | Provider creation is cheap; `CreateEngine` is the explicit synchronous resource-creation boundary |
| Cached global `Default` fixes one probe result for process lifetime | `TransportProviders.CreateDefault()` probes per call and the created engine reports the selected provider |
| Adapter-specific options partially duplicate engine options | Adapter options own only bridge/scheduler behavior; provider and engine options remain provider/engine objects |

## Open questions

1. Should provider creation use static factory methods as shown, or public concrete provider classes?
2. Is synchronous blocking `TransportEngine.Dispose` acceptable, or should the core expose `Stop` plus a callback when workers are drained?
3. Should `TransportWriteOperation` support cancellation, given that cancellation after a partial stream write generally requires aborting the connection?
4. Is `RequiredWorkerIndex` too implementation-specific, or is it justified by proxy affinity?
5. Should immediate response buffers be retained in the BCL surface, or should `OnReceive` only expose payload and require `Connection.Send`?
6. Which provider options have a real user scenario versus being benchmark-only implementation knobs?
7. Does the first public SPI support third-party providers, or only built-in provider selection while the SPI incubates internally?
8. What internal or public TLS boundary lets a separate `System.Net.Transport.dll` reuse runtime TLS implementation without exposing provider strategy to consumers?
9. Should retained receive leases initially be available only to runtime-owned adapters, or should the experimental public contract permit third-party consumers after provider bounds and misuse behavior are validated?

## API evolution rule

The managed provider should be implemented first because it can prove callback semantics, listener/connect correlation, close reasons, TLS policy integration, and adapters without requiring a new native backend. epoll and IOCP then prove one readiness and one completion provider. io_uring and RIO should follow only after the provider-specific options and buffer lifetimes are tested.

No stable public surface should be proposed until:

- the managed provider runs a real Kestrel adapter and a long-lived multiplexed client;
- epoll and IOCP implement the same callbacks;
- the async/Pipelines adapters need no provider-specific public escape hatch;
- ClientHello and all required Kestrel TLS callbacks work without a separate handshake method;
- provider options report what was actually applied;
- measurements identify which low-level mechanisms justify public exposure rather than internal Socket improvements.
