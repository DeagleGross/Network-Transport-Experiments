# How async runtimes expose epoll, io_uring, and TLS

**Source snapshots: 2026-09-25 and 2026-09-26.** This is implementation research for our future C# transport work, not a ranking of framework performance or an approved .NET API design. The references pin source revisions rather than assuming that a default-branch feature is available in every published package. No framework code was executed for this write-up.

This is not only a Rust pattern. **Monoio and Compio are Rust; Seastar and Boost.Asio are C++; Netty is Java with native transport/TLS components.** All five provide useful comparisons involving Linux epoll and io_uring. **Tokio versus Tokio-uring** illustrates the cost of changing from borrowed-buffer readiness APIs to owned-buffer completion APIs. **Glommio** is useful for its per-core execution and buffer management, but is not presented here as an interchangeable epoll/io_uring implementation. **libuv, written in C, is a useful boundary case: its documented io_uring filesystem option does not turn TCP receives into completion-based io_uring receives.**

## 1. The central distinction: a socket fd is not an I/O architecture

All the ordinary Linux TCP paths discussed here ultimately use kernel socket descriptors. That does **not** mean their TLS layers use `SSL_set_fd()`, or that their operations execute the same way.

| Term | What it actually means |
|---|---|
| An fd-backed TCP stream | The transport ultimately owns or references a kernel socket. Both readiness and completion backends can do this. |
| A readiness backend | Observe readiness, then attempt nonblocking socket I/O. Readiness is not an I/O completion and does not guarantee that a retry will make progress. |
| A completion backend | Submit an operation identifying a socket and storage; receive its result later. The storage and socket must remain valid while the operation is outstanding. |
| Fd-bound OpenSSL | `SSL_set_fd()` installs a socket BIO. OpenSSL itself performs socket I/O while executing its TLS state machine. The event loop waits when OpenSSL requests read/write readiness. |
| Application-driven TLS I/O | OpenSSL or another TLS engine exchanges ciphertext through buffers, callbacks, or an adapted stream. The runtime, rather than a socket BIO, decides how to perform network I/O. |

**Both epoll and io_uring can underpin either TLS integration strategy.** The choice of event backend does not automatically choose the TLS adapter.

Also:

- OpenSSL always uses BIO abstractions for its I/O. "Fd versus BIO" is shorthand for **socket BIO versus application-managed BIO/stream**, not "BIO versus no BIO."
- A custom BIO is not necessarily a pair of `BIO_s_mem()` objects. It can call into a runtime's own buffering, ownership, and backpressure mechanisms.
- There is no SQE that means "execute `SSL_read()`." OpenSSL runs in userspace; io_uring handles socket operations or readiness notifications around it.
- A provided buffer, registered buffer, or returned buffer lease does not, by itself, establish zero-copy networking or zero-copy TLS.

## 2. Framework comparison

| Framework | How the application chooses the backend | Application-facing network API | Important implementation distinction |
|---|---|---|---|
| Monoio (Rust) | Runtime driver types: `IoUringDriver`, `LegacyDriver`, or `FusionDriver`, subject to compiled features | `TcpListener` / `TcpStream`, owned-buffer `AsyncReadRent` / `AsyncWriteRent` | The same logical operation has an io_uring SQE implementation and a legacy readiness/syscall implementation. |
| Compio (Rust) | `ProactorBuilder::driver_type(DriverType::...)`; Linux fusion driver when both backends are enabled | `TcpListener` / `TcpStream`, owned-buffer `AsyncRead` / `AsyncWrite`, plus managed-buffer APIs | Completion-shaped operations are implemented over io_uring or emulated over a polling driver. Explicit backend selection differs from automatic fallback. |
| Seastar (C++), POSIX socket stack | Reactor backend selection: `epoll`, `io_uring`, and a separate `asymmetric_io_uring` option when available | `server_socket`, `connected_socket`, input/output streams and futures | Normal io_uring uses readiness speculation and synchronous fast paths as well as submitted operations. TLS is layered over Seastar streams. |
| Netty (Java/native), inspected 4.2 branch | Matching I/O handler factories and channel types for epoll or io_uring | Channels, pipelines, `ByteBuf`, and `SslHandler` | Transport selection is separate from TLS; the OpenSSL engine has a direct-buffer custom BIO path, not just a naive memory-BIO staging loop. |
| Boost.Asio (C++) | Build configuration selects reactive/epoll or io_uring socket services | Same socket/asynchronous-operation APIs; `ssl::stream<NextLayer>` for TLS | Its io_uring receive operation can use native completion or readiness-plus-retry according to socket state. TLS uses a BIO pair over the next layer. |
| Tokio / Tokio-uring (Rust) | Different runtime/resource APIs, not a backend flag on one `TcpStream` type | Tokio's borrowed-buffer traits versus Tokio-uring's owned-buffer methods | Running a Tokio library on a Tokio-uring runtime does not automatically convert that library's ordinary Tokio sockets into io_uring data operations. |
| Glommio (Rust) | Local executors and an io_uring-oriented reactor | Shard-local async application model | Useful ownership/scheduling reference; no interchangeable epoll backend was established by the reviewed material. |
| libuv (C) | Linux epoll event loop; optional io_uring support for filesystem work | `uv_tcp_t`, stream read/write callbacks | Not a like-for-like native io_uring TCP backend. Internal io_uring use must not be confused with replacing socket reads. |

The backend selection boundary is generally **runtime/reactor/driver construction**, not an option repeated on each read. The application then uses the selected runtime's socket and buffer contracts.

## 3. Monoio: one logical operation, two execution strategies

### Public surface and selection

Monoio's own builder example constructs `RuntimeBuilder::<FusionDriver>` and separately demonstrates starting `IoUringDriver` or `LegacyDriver`. Its TCP stream implements `AsyncReadRent` and `AsyncWriteRent`. [M1] [M2]

The essential consumer pattern is:

```rust
// Shape of Monoio's ownership-transfer API; surrounding setup omitted.
let (result, buffer) = stream.read(buffer).await;
```

The buffer is passed into the operation and returned with the result. This gives the runtime a lifetime it can preserve while a kernel completion operation is pending. The fact that the other backend is epoll does not require giving up that application-facing contract.

### Receive trace

`TcpStream::read` creates `Op::recv(...)`. The `Recv<T>` operation stores both `SharedFd` and the buffer. Its two implementations are particularly instructive: [M2] [M3]

```text
TcpStream.read(owned buffer)
    -> Op<Recv<T>>
        -> io_uring: opcode::Recv(fd, buffer pointer, capacity)
        -> legacy: register/read readiness as needed, call recv(fd, buffer, ...)
    -> result plus the owned buffer
```

The strong fd reference prevents closing the descriptor while the operation still owns it. Completion updates the initialized length of the returned receive buffer. The send operation similarly translates to an io_uring send operation or a legacy socket syscall. [M3] [M4]

**Answer to "are both fd-based?": yes at the raw TCP resource layer; no, that does not make both paths readiness-based or OpenSSL-fd-bound.**

### Local ownership and batching

Monoio is designed around thread-per-core execution, avoiding task migration for its local tasks. The io_uring driver uses local driver state and operation storage, queues SQEs, and deliberately postpones submission until the driver is parked or submission is otherwise needed. Its park path can combine submission and waiting. [M5] [M6]

This is the relevant pattern for a C# implementation:

```text
worker owns connections and driver
    -> run ready application/TLS work
    -> queue multiple native operations
    -> submit / wait at a deliberate driver boundary
    -> complete operations and wake local work
```

Thread-per-core is not a guarantee of balanced load. Monoio's documentation explicitly notes that uneven workloads can leave cores underutilized compared with a work-stealing runtime.

### Readiness compatibility inside an io_uring runtime

Monoio's `poll-io` path addresses libraries expecting readiness/borrowed-buffer I/O. On Linux with the io_uring driver, it maintains an **epoll fd**, registers the relevant sockets in epoll, and monitors that epoll fd through io_uring. When notified, it calls `epoll_wait(0)` to collect the ready sockets. On the legacy driver, the same compatibility need can use the existing poller directly. [M7] [M6]

```text
readiness-oriented consumer
    -> socket registered with epoll
    -> io_uring observes epoll-fd readiness
    -> epoll_wait(0) returns a batch of ready sockets
    -> retry consumer I/O
```

That avoids requiring an independent io_uring readiness registration for every wait by every compatibility consumer. It also adds an integration layer; it is an architectural option, not proof of better performance.

### TLS is a separate layer

The inspected Monoio native-TLS wrapper contains `native_tls::TlsStream<Buffers>` plus `IOWrapper<S>`. It invokes TLS reads/writes, then drives the underlying asynchronous transport when the TLS layer needs more I/O. It is not simply attaching OpenSSL to the transport's Linux fd. The separate compatibility wrapper source also explicitly maintains buffers and copies between its synchronous-facing and asynchronous-facing interfaces. [M8] [M9]

Do not generalize these particular wrappers into "every Monoio TLS configuration has exactly two copies." The concrete TLS engine and adapter determine the copy graph.

**Useful lesson:** native completion APIs and readiness compatibility can coexist, while TLS remains separately selectable. The mere existence of an fd does not decide which integration should be used.

## 4. Compio: a completion-shaped cross-platform driver

### Public surface and selection

Compio describes itself as a thread-per-core runtime with io_uring, polling, and Windows IOCP support. Its public TCP API uses owned buffers:

```rust
// Shape of the compio::io trait implemented by TcpStream:
async fn read<B: IoBufMut>(&mut self, buf: B) -> BufResult<usize, B>;
```

This is **Compio's** `AsyncRead`, not the similarly named borrowed-buffer Tokio or futures trait. `TcpStream` also exposes managed-buffer reads, and a multi-read surface in the inspected snapshot. [C1] [C2]

The low-level builder has:

```rust
builder.driver_type(DriverType::Poll);
// Or DriverType::IoUring when that driver is compiled in.
```

In Linux's fusion implementation, automatic selection probes the required operation support. If automatic io_uring creation fails, it warns and can fall back to polling. If the caller explicitly requests io_uring, the corresponding creation error is returned instead of silently substituting polling. This distinction is valuable when conducting performance experiments. [C3] [C4]

### How one receive becomes two different implementations

The io_uring `Recv<T, S>` implementation constructs `opcode::Recv` using the socket fd and the owned buffer's writable region. The polling implementation uses a readable interest, a `pre_submit` decision, and an `operate` retry that calls the socket operation. The polling driver maintains per-fd interest queues and registrations through the `polling` crate, whose Linux implementation uses epoll. [C5] [C6] [C7]

```text
Compio receive operation
    -> io_uring: submit Recv and finish on CQE
    -> polling: try operation / await readiness / retry operation
    -> report an operation result through the same completion-shaped API
```

Compio therefore demonstrates that a **proactor-style public API need not require a native proactor backend on every platform**.

A high-level stream of accepted connections or received buffers must not be confused with a kernel multishot request. The source contains distinct native and fallback paths. For example, the polling `AcceptMulti` delegates to the ordinary accept operation. Do not promise that a high-level multi-operation API always corresponds to one SQE. [C8]

### TLS and the compatibility boundary

`compio-tls` supports native TLS and rustls, with TLS connectors/acceptors operating over futures-style `AsyncRead + AsyncWrite` streams. Its native-TLS integration wraps that stream in an `AllowStd` adapter and maps `WouldBlock` into asynchronous waiting. It does not require passing a raw fd to OpenSSL. [C9] [C10]

The ownership mismatch is handled by compatibility layers. For example, `compio-io::compat::AsyncStream` adapts Compio's owned-buffer I/O to futures-style borrowed I/O using internal read/write buffering. That is a concrete place to account for storage and copies, rather than attributing them vaguely to "io_uring overhead." [C11]

**Useful lesson for C#:** an operation-and-buffer ownership core can support multiple OS drivers, while Stream/Pipelines/TLS adapters retain their own contracts. Make the selected driver observable and distinguish explicit selection from fallback.

## 5. Seastar: reactor choice beneath a stable socket/stream API

This section concerns Seastar's POSIX socket path. It does not claim that every networking stack Seastar can support is ordinary kernel TCP.

### Application API and backend selection

Applications use `server_socket::accept()`, `connected_socket::input()`, and `connected_socket::output()`. The reactor backend changes how the underlying I/O is performed; the application does not need a different `connected_socket` type for epoll and io_uring. The inspected backend selector contains `epoll`, `io_uring`, `linux-aio`, and `asymmetric_io_uring`, with availability dependent on the build and environment. [S1] [S2]

### Normal io_uring is not "every call must become an SQE"

The epoll backend delegates networking operations to the reactor's nonblocking I/O helpers and readiness machinery.

The normal io_uring backend mixes **speculative synchronous fast paths** and submitted operations. For example, its `recvmsg` path checks cached readiness, may attempt `recvmsg(..., MSG_DONTWAIT)` immediately, and otherwise constructs a completion operation. Its source also maps network requests into `io_uring_prep_recv`, `recvmsg`, `send`, `sendmsg`, accept, and connect preparations. Some methods continue to delegate to existing reactor helpers. [S2]

Thus the appropriate comparison is not:

```text
epoll = syscalls; io_uring = never issue a socket syscall
```

It is:

```text
runtime policy chooses a likely-cheap immediate attempt
    OR
runtime submits an asynchronous operation and retains its resources
```

The quality of the readiness/speculation policy is part of the implementation.

### OpenSSL uses custom BIOs over Seastar streams

The inspected `openssl_session` owns a `connected_socket_impl`, takes its source and sink, and installs **custom BIO methods** using `SSL_set_bio()`. It does not attach the TLS engine to a raw socket with `SSL_set_fd()`. [S3]

Its output callback copies ciphertext into an owned `temporary_buffer<char>` and submits it to the underlying data sink. While a previous output is pending, the synchronous BIO callback returns retry-write rather than issuing a second overlapping sink operation. The TLS machinery awaits that pending work and retries OpenSSL. The read side similarly coordinates buffered input with `SSL_read_ex`. [S3]

```text
OpenSSL custom BIO callback
    -> Seastar-owned buffer and source/sink
    -> connected socket
    -> selected reactor backend
```

This is a particularly useful model for our TLS-provider boundary: a synchronous TLS callback cannot itself await, but it can report "retry after this transport work completes." It is also direct evidence against treating all custom BIOs as identical to the simple memory-BIO pair in our C sample.

The repository contains both OpenSSL and GnuTLS implementations; the detailed trace above covers OpenSSL, not every TLS-provider path.

### A different architecture: asymmetric io_uring

Seastar also documents a backend that moves kernel-side I/O work onto dedicated worker cores. It removes normal speculative synchronous fast paths, uses SQ polling/worker affinity, and groups application shards to share worker resources with `ATTACH_WQ`. The documented assignment considers SMT and NUMA topology. [S4]

The stated target is compute-heavy shards. The documentation explicitly cautions that this can be unsuitable for I/O-bound workloads because shared worker cores can become a bottleneck.

**Useful lesson:** do not collapse "thread per core" into one scheduling architecture. A framework may keep application ownership local while deliberately placing kernel I/O work elsewhere. Count those additional cores when comparing performance.

## 6. Tokio versus Tokio-uring: compatibility does not imply backend substitution

Ordinary Tokio's TCP stream stores `PollEvented<mio::net::TcpStream>` and implements borrowed-buffer `AsyncRead`/`AsyncWrite`. On Linux this is the familiar readiness-oriented socket path. [T1]

Tokio-uring provides a separate runtime and separate resource types. Its TCP stream's `read` accepts a `BoundedBufMut` and returns `BufResult<usize, T>`. It also exposes fixed-buffer operations and a distinct write-submission API. These are not the same signatures as Tokio's borrowed `ReadBuf` interface. [T2]

The inspected Tokio-uring runtime uses a **current-thread Tokio runtime plus a `LocalSet`**, with an io_uring driver. It batches pending operations through a park hook and dispatches CQEs to stored operation state. Notably, it uses Tokio `AsyncFd` to observe its driver fd: readiness infrastructure can drive completion infrastructure as well as the reverse. [T3]

Consequences:

- Starting a Tokio-uring runtime does not rewrite an existing `tokio::net::TcpStream` into an io_uring socket.
- A TLS library written for Tokio traits can still be usable in that runtime while retaining its ordinary readiness-based socket path.
- To use owned-buffer completion resources underneath that library, an appropriate adapter or a compatible TLS integration is needed. Compatibility alone is not evidence of zero-copy or of native io_uring receives.
- This review did not establish a universal fd-bound TLS implementation for Tokio-uring; do not infer one from its runtime name.

**Useful lesson for C#:** putting an existing Stream or Pipelines consumer on a new event loop is not sufficient proof that its bytes actually travel through the new backend. Trace the resource type and I/O calls.

## 7. Glommio: useful per-core patterns, but a different comparison boundary

Glommio describes a cooperative thread-per-core runtime based on io_uring. Its reactor implementation includes submission/cancellation queues, completion dispatch, and a buffer allocator associated with ring resources. These are useful examples of local ownership, pooling, and batched work. [G1] [G2]

The reviewed material does not establish an epoll alternative behind the same Glommio socket API. Its TLS integration was not traced here. Therefore it is a scheduling/buffering reference in this document, not evidence that an epoll-backed and io_uring-backed TLS implementation use identical fd-binding techniques.

## 8. Additional non-Rust ecosystems

### Netty: Java APIs, native transports, and a buffer-oriented TLS engine

The inspected Netty 4.2 branch contains both `EpollIoHandler` and `IoUringIoHandler`, with public `newFactory(...)` methods, plus matching socket-channel implementations such as `EpollSocketChannel` and `IoUringSocketChannel`. This is current source evidence, not a claim that older Netty releases or the older incubator package expose exactly the same API. [N1] [N2]

The application-facing abstraction remains a channel pipeline. The native channel/handler selection determines transport behavior; `SslHandler` supplies TLS processing in the pipeline, independently of that choice.

The io_uring stream implementation has multiple receive paths: [N3]

- With no usable configured buffer ring, allocate a `ByteBuf` and submit a receive targeting its writable native region.
- With a provided-buffer ring, submit buffer-selection receives.
- Enable multishot receive or receive bundling only on the corresponding configured/supported path.
- Use socket-state information and feature support to choose `IORING_RECVSEND_POLL_FIRST` rather than always speculatively attempting a receive.

The driver batches submissions and completions, handles CQ overflow and pending task work, and integrates waiting with the event loop's blocking/deadline policy. This is an event-loop implementation, not a promise that every channel has a dedicated native thread. [N1]

**The most relevant TLS finding:** Netty's `ReferenceCountedOpenSslEngine` creates a custom byte-buffer BIO. For direct encrypted input, it points that BIO at the input buffer's native address; heap input takes a staging-copy path. For direct output, it configures the BIO to write encryption results into the supplied destination buffer. It clears the temporary BIO buffer association in cleanup. [N4]

```text
Netty transport receives into ByteBuf
    -> SslHandler / OpenSSL engine
    -> custom BIO refers to direct buffer storage where possible
    -> wrap/unwrap produces ciphertext/plaintext in supplied buffers
    -> channel transport performs network I/O
```

`SslHandler` also has an OpenSSL-specific multi-buffer unwrap path so some segmented inputs need not be consolidated first. [N5]

This is **not `SSL_set_fd()`**, and it is **not equivalent to copying every incoming chunk into `BIO_s_mem()` and draining every outgoing chunk into another buffer**. It avoids particular adapter copies when the buffer conditions permit. It does not eliminate all TLS-internal copying, kernel/userspace copying, or lifetime requirements.

The native callback boundary makes that qualification concrete: Netty-tcnative's `tcn_read_from_bytebuffer` still calls `memcpy(out, bioUserData->buffer, readAmount)`, and its write callback copies into the supplied destination buffer. Pointing the BIO at existing storage eliminates an intermediate staging step, not every copy. OpenSSL's ordinary BIO read callback asks the BIO to fill a destination pointer; it does not ask the BIO to return a borrowed pointer to its input page. [N6]

**C# relevance:** this is a strong example to study for a custom BIO over stable, explicitly owned native buffers. The choice is not limited to our current fd sample versus our deliberately simple memory-BIO sample. A third design can preserve transport-controlled I/O while reducing staging copies.

### Boost.Asio: the same socket API with different implementation services

`basic_socket` selects its implementation service at build time. When `BOOST_ASIO_HAS_IO_URING_AS_DEFAULT` applies, it uses `io_uring_socket_service`; otherwise the Linux readiness configuration uses the reactive socket service and epoll reactor. In the inspected configuration logic, enabling io_uring support while disabling epoll is one way to select io_uring as the default. Merely having io_uring support compiled in does not prove that a TCP socket is using it. [A1] [A2]

The public model is an asynchronous operation over caller-supplied buffer sequences and a completion token/handler. Unlike the Rust ownership-transfer signatures above, the caller generally must keep the referenced storage alive until completion; copying a buffer descriptor does not acquire ownership of its underlying bytes.

Asio's actual io_uring receive operation is more nuanced than "always submit a recv": [A3]

```text
async receive
    -> internally nonblocking socket:
         io_uring poll readiness + nonblocking receive retry
    -> suitable registered single-buffer case:
         fixed-buffer read preparation
    -> otherwise:
         io_uring recvmsg preparation
```

The operation also handles a would-block result by transitioning into the readiness/retry path. That is another concrete example of one completion-oriented API accommodating different low-level strategies.

For TLS, `ssl::stream` drives an OpenSSL engine through a **BIO pair**. The engine uses `SSL_set_bio`, exposes `put_input`/`get_output`, and the composed I/O operation calls the next layer's `async_read_some` or `async_write` when TLS needs network work. It serializes pending reads/writes at that boundary. It does not simply attach OpenSSL to the next layer's socket fd. [A4] [A5]

**C# relevance:** preserve a clear contract for asynchronous buffer lifetime, even when the public API is borrowed-buffer-shaped. Keep TLS continuation/retry logic separate from whichever service implements the underlying socket. This is also evidence that a mature non-Rust library deliberately uses application-driven TLS I/O.

### libuv: a valuable negative result for this particular comparison

The inspected libuv documentation describes `UV_LOOP_USE_IO_URING_SQPOLL` as enabling an io_uring instance for **asynchronous filesystem operations**. Its Linux TCP path still reaches `uv__io_poll`/`epoll_pwait`, followed by ordinary `read` calls in `uv__read`. [L1] [L2] [L3]

The Linux source can also use io_uring to batch epoll-control work. That does not turn TCP payload transfer into `IORING_OP_RECV` completions.

Therefore, libuv is **not** added to the interchangeable native epoll/io_uring TCP examples. This is useful knowledge rather than an absence to hide: finding an io_uring flag or an io_uring dependency in a framework does not establish what operations actually use it.

### What was valuable outside Rust?

Yes, there are substantial examples: Seastar's reactor/stream separation, Netty's channel transports and direct-buffer BIO, and Asio's selectable socket services and composed TLS operations. None of these frameworks was benchmarked here. Their architecture is evidence of available design choices, not evidence that any one choice will beat our C samples.

## 9. Techniques worth carrying into the C# implementation

These are implementation consequences, not proposed public API signatures.

| Concern | Pattern supported by the inspected implementations | C# consequence |
|---|---|---|
| Backend selection | Monoio driver types; Compio builder/fusion; Seastar reactor selection | Select at a meaningful driver/listener/runtime boundary. Expose the actual selected backend for diagnostics. |
| Buffer ownership | Monoio/Compio/Tokio-uring retain owned buffers through operations | A `Memory<byte>` reference alone does not stabilize a native address. Retain the owner and pin or otherwise stabilize storage until terminal native completion. |
| Readiness fallback | Monoio/Compio implement the same logical I/O over polling | An epoll fallback can preserve a completion-shaped consumer API; it still needs correct `EAGAIN`, partial-I/O, and readiness bookkeeping. |
| Application compatibility | Monoio PollIo; Compio AsyncStream; ordinary Tokio resources within Tokio-uring | Treat Stream/Pipelines adaptation as a real layer with an explicit copy/ownership budget, not a free conversion. |
| TLS ownership | Runtime-controlled stream/custom-BIO integrations in Monoio, Compio, Seastar | Keep TLS-engine selection separate from the network driver. A direct-fd OpenSSL provider is a separate integration, not an automatic io_uring feature. |
| Custom BIO buffer access | Netty attaches direct buffer addresses during wrap/unwrap; heap buffers take a staging path | Investigate a buffer-aware BIO rather than assuming all application-driven TLS requires the copies in our basic memory-BIO sample. Track storage lifetime and retry behavior explicitly. |
| Local execution | Monoio, Tokio-uring local tasks, Glommio, Seastar shards | Keep connection state, native operation state, and completion dispatch on an owning worker where useful. Marshal outside-thread commands to that owner. |
| Batching | Queue operations; submit when parking or at a bounded work boundary | Avoid one native transition per tiny bookkeeping action. Do not turn batching into unbounded latency or starvation. |
| Readiness speculation | Seastar fast paths; buffered-state checks in TLS layers | An immediate I/O attempt can save a wakeup when data is likely ready; an unnecessary attempt can cost a syscall. Measure the policy rather than always speculating or always waiting. |
| Cancellation | Completion resources outlive the operation's initial submission | Cancellation acknowledgement is not universally the target operation's terminal completion. Do not close/recycle resources while native work still references them. |
| Kernel optimization flags | Driver-specific configuration and Seastar's explicit offload architecture | `SINGLE_ISSUER`, deferred task work, SQPOLL, registered resources, and multishot solve different problems. Check kernel support and include extra worker CPU in comparisons. |

### Two TLS-provider shapes remain worth separating

```text
A. Fd-bound TLS
   TLS engine owns socket reads/writes
       -> backend supplies readiness waits
       -> plaintext goes to the application/Pipelines adapter

B. Transport-driven TLS
   backend receives ciphertext into owned storage
       -> TLS engine consumes/produces ciphertext via BIO/callback/stream
       -> backend sends ciphertext
       -> plaintext goes to the application/Pipelines adapter
```

A may avoid some adapter copies and preserve OpenSSL's socket-specific paths. B lets the transport control receive destinations, completion batching, and provided-buffer strategies. Neither model is inherently zero-copy. They need equivalent TLS callbacks, shutdown, buffering, and error semantics before their performance can be compared fairly.

### Keep the Pipes boundary explicit

Our C# consumer can continue seeing a `PipeReader`/`PipeWriter` even if the implementation underneath changes. The relevant choice is whether receives target pipe-owned memory directly, or whether a custom reader exposes already-filled provider buffers and releases them on consumption. An ordinary `PipeWriter` does not acquire ownership of arbitrary externally filled storage simply because the backend supports io_uring.

For TLS, distinguish **ciphertext lifetime**, **plaintext lifetime**, and **application request lifetime**. Retaining a received TLS record is not automatically retaining the HTTP parser's plaintext, and the buffer need not survive for an entire application request.

## 10. What our C experiments establish, and what they do not

The local [sample directory](experiments/io-uring-http/) contains direct-fd, memory-BIO, read-ahead, readiness-directed, persistent-poll, and epoll/io_uring hybrid experiments. Its [README](experiments/io-uring-http/README.md#tls-architectures-and-results) records the matched RPS results and the exploratory screens separately. Those are our code, not benchmarks of the frameworks above.

In our matched keep-alive comparison, read-ahead and avoiding an immediately unsuccessful `SSL_read` after a response improved the direct-fd sample. The hybrid and persistent-poll variants reduced some registration/submission work without winning that screening workload. This supports counting actual work, not ranking designs by number of SQEs alone.

Those results do not establish:

- That fd-bound TLS universally beats application-driven TLS.
- That a particular framework has the same copies or retry policy as our sample.
- That replacing epoll with io_uring makes an unchanged consumer use completion I/O.
- That a reduction in read syscalls proves elimination of networking or TLS copies.
- That loopback WSL throughput under overload predicts physical-NIC, production TLS, or Kestrel behavior.

For the C# port, retain a comparison matrix: epoll/io_uring, fd-bound/transport-driven TLS, short/persistent connections, and identical application framing. Record achieved and offered RPS, tail latency, server/client CPU, allocation and retained memory, read/write calls, native submissions, completion counts, and errors. Prefer profiles and repeated matched runs over a desired winning architecture.

## 11. Source map

Source symbols are named above so the implementation can be followed even if line numbers move in later versions. Links below are immutable snapshots.

| Project | Inspected revision |
|---|---|
| Monoio | `f0931e48e13bbeb1bcc3a1dc881dbe5c9385aa6c` |
| Monoio TLS | `30c43d9bcdc3a3f2ecbd193be0f262e2a9b1d922` |
| Compio | `254104dd5719414aec3cabc8f892eb7d579641ce` |
| Seastar | `3e0555a93a7269e17d5fc7e032eaeb9207d15d1d` |
| Tokio | `38cdde2bf70057b316c0c8554c5110ecfebd1cd4` |
| Tokio-uring | `5fb1a4f65b8c471ba6fab8d12e42e129231d865f` |
| Glommio | `8434815962ce0bc161ace1967137213dc2334e4b` |
| Netty, 4.2 branch | `a0c8ddc0669525bcca0b593424523f70a04b34aa` |
| Netty-tcnative | `91e525bc3ebe57fcd33f5a04b08ab1682c7f31c5` |
| Boost.Asio | `4fa4abee89a62fdeeccac2585caece625f40647e` |
| libuv, v1.x branch | `abe835d41317b55b16260990821f89b4ee9e437b` |

[M1]: https://github.com/bytedance/monoio/blob/f0931e48e13bbeb1bcc3a1dc881dbe5c9385aa6c/examples/builder.rs
[M2]: https://github.com/bytedance/monoio/blob/f0931e48e13bbeb1bcc3a1dc881dbe5c9385aa6c/monoio/src/net/tcp/stream.rs
[M3]: https://github.com/bytedance/monoio/blob/f0931e48e13bbeb1bcc3a1dc881dbe5c9385aa6c/monoio/src/driver/op/recv.rs
[M4]: https://github.com/bytedance/monoio/blob/f0931e48e13bbeb1bcc3a1dc881dbe5c9385aa6c/monoio/src/driver/op/send.rs
[M5]: https://github.com/bytedance/monoio/blob/f0931e48e13bbeb1bcc3a1dc881dbe5c9385aa6c/README.md
[M6]: https://github.com/bytedance/monoio/blob/f0931e48e13bbeb1bcc3a1dc881dbe5c9385aa6c/monoio/src/driver/uring/mod.rs
[M7]: https://github.com/bytedance/monoio/blob/f0931e48e13bbeb1bcc3a1dc881dbe5c9385aa6c/docs/en/poll-io.md
[M8]: https://github.com/monoio-rs/monoio-tls/blob/30c43d9bcdc3a3f2ecbd193be0f262e2a9b1d922/monoio-native-tls/src/stream.rs
[M9]: https://github.com/monoio-rs/monoio-tls/blob/30c43d9bcdc3a3f2ecbd193be0f262e2a9b1d922/monoio-io-wrapper/src/safe_io.rs
[C1]: https://github.com/compio-rs/compio/blob/254104dd5719414aec3cabc8f892eb7d579641ce/README.md
[C2]: https://github.com/compio-rs/compio/blob/254104dd5719414aec3cabc8f892eb7d579641ce/compio-net/src/tcp.rs
[C3]: https://github.com/compio-rs/compio/blob/254104dd5719414aec3cabc8f892eb7d579641ce/compio-driver/src/lib.rs
[C4]: https://github.com/compio-rs/compio/blob/254104dd5719414aec3cabc8f892eb7d579641ce/compio-driver/src/sys/driver/fusion/mod.rs
[C5]: https://github.com/compio-rs/compio/blob/254104dd5719414aec3cabc8f892eb7d579641ce/compio-driver/src/sys/op/socket/iour.rs
[C6]: https://github.com/compio-rs/compio/blob/254104dd5719414aec3cabc8f892eb7d579641ce/compio-driver/src/sys/op/socket/poll.rs
[C7]: https://github.com/compio-rs/compio/blob/254104dd5719414aec3cabc8f892eb7d579641ce/compio-driver/src/sys/driver/poll/mod.rs
[C8]: https://github.com/compio-rs/compio/blob/254104dd5719414aec3cabc8f892eb7d579641ce/compio-driver/src/sys/op/multishot/poll.rs
[C9]: https://github.com/compio-rs/compio/blob/254104dd5719414aec3cabc8f892eb7d579641ce/compio-tls/src/adapter.rs
[C10]: https://github.com/compio-rs/compio/blob/254104dd5719414aec3cabc8f892eb7d579641ce/compio-tls/src/compat/native.rs
[C11]: https://github.com/compio-rs/compio/blob/254104dd5719414aec3cabc8f892eb7d579641ce/compio-io/src/compat/async_stream.rs
[S1]: https://github.com/scylladb/seastar/blob/3e0555a93a7269e17d5fc7e032eaeb9207d15d1d/include/seastar/net/api.hh
[S2]: https://github.com/scylladb/seastar/blob/3e0555a93a7269e17d5fc7e032eaeb9207d15d1d/src/core/reactor_backend.cc
[S3]: https://github.com/scylladb/seastar/blob/3e0555a93a7269e17d5fc7e032eaeb9207d15d1d/src/net/tls_openssl.cc
[S4]: https://github.com/scylladb/seastar/blob/3e0555a93a7269e17d5fc7e032eaeb9207d15d1d/doc/reactor_backend_asymmetric_uring.md
[T1]: https://github.com/tokio-rs/tokio/blob/38cdde2bf70057b316c0c8554c5110ecfebd1cd4/tokio/src/net/tcp/stream.rs
[T2]: https://github.com/tokio-rs/tokio-uring/blob/5fb1a4f65b8c471ba6fab8d12e42e129231d865f/src/net/tcp/stream.rs
[T3]: https://github.com/tokio-rs/tokio-uring/blob/5fb1a4f65b8c471ba6fab8d12e42e129231d865f/src/runtime/mod.rs
[G1]: https://github.com/DataDog/glommio/blob/8434815962ce0bc161ace1967137213dc2334e4b/README.md
[G2]: https://github.com/DataDog/glommio/blob/8434815962ce0bc161ace1967137213dc2334e4b/glommio/src/sys/uring.rs
[N1]: https://github.com/netty/netty/blob/a0c8ddc0669525bcca0b593424523f70a04b34aa/transport-classes-io_uring/src/main/java/io/netty/channel/uring/IoUringIoHandler.java
[N2]: https://github.com/netty/netty/blob/a0c8ddc0669525bcca0b593424523f70a04b34aa/transport-classes-epoll/src/main/java/io/netty/channel/epoll/EpollIoHandler.java
[N3]: https://github.com/netty/netty/blob/a0c8ddc0669525bcca0b593424523f70a04b34aa/transport-classes-io_uring/src/main/java/io/netty/channel/uring/AbstractIoUringStreamChannel.java
[N4]: https://github.com/netty/netty/blob/a0c8ddc0669525bcca0b593424523f70a04b34aa/handler/src/main/java/io/netty/handler/ssl/ReferenceCountedOpenSslEngine.java
[N5]: https://github.com/netty/netty/blob/a0c8ddc0669525bcca0b593424523f70a04b34aa/handler/src/main/java/io/netty/handler/ssl/SslHandler.java
[N6]: https://github.com/netty/netty-tcnative/blob/91e525bc3ebe57fcd33f5a04b08ab1682c7f31c5/openssl-dynamic/src/main/c/ssl.c
[A1]: https://github.com/boostorg/asio/blob/4fa4abee89a62fdeeccac2585caece625f40647e/include/boost/asio/basic_socket.hpp
[A2]: https://github.com/boostorg/asio/blob/4fa4abee89a62fdeeccac2585caece625f40647e/include/boost/asio/detail/config.hpp
[A3]: https://github.com/boostorg/asio/blob/4fa4abee89a62fdeeccac2585caece625f40647e/include/boost/asio/detail/io_uring_socket_recv_op.hpp
[A4]: https://github.com/boostorg/asio/blob/4fa4abee89a62fdeeccac2585caece625f40647e/include/boost/asio/ssl/detail/impl/engine.ipp
[A5]: https://github.com/boostorg/asio/blob/4fa4abee89a62fdeeccac2585caece625f40647e/include/boost/asio/ssl/detail/io.hpp
[L1]: https://github.com/libuv/libuv/blob/abe835d41317b55b16260990821f89b4ee9e437b/docs/src/loop.rst
[L2]: https://github.com/libuv/libuv/blob/abe835d41317b55b16260990821f89b4ee9e437b/src/unix/linux.c
[L3]: https://github.com/libuv/libuv/blob/abe835d41317b55b16260990821f89b4ee9e437b/src/unix/stream.c
