# Backend mappings and implementation primers

> **Status:** Source-grounded design guidance. This document constrains behavior while deliberately leaving provider-specific algorithms open where evidence is incomplete.

## Common provider model

Every provider owns:

- its shared execution resource: runtime socket engine, epoll instances, io_uring rings, IOCP handles, or worker queues;
- listener registrations;
- connection slot and generation state;
- native operation descriptors;
- receive and write buffer pools;
- TLS credential/session caches that are safe to share;
- callback dispatch and provider shutdown coordination.

Every connection owns:

- one logical read lane and one logical write lane;
- endpoint metadata;
- its current plaintext/TLS state;
- pending read/write state;
- terminal error and completion;
- half-close and abort state;
- references to any provider buffers or native operations that have not reached terminal completion.

The backend can batch, prefetch, or use multiple native operations internally, but the consumer contract remains one active read result and one write operation.

## Completion and buffer identity

The common API intentionally hides native identifiers, but the SPI implementation must retain them.

| Backend | Native operation identity | Buffer identity | Safe reuse point |
|---|---|---|---|
| Managed `Socket`/SAEA | `SocketAsyncEventArgs` instance or runtime async operation object | SAEA buffer or provider pool lease | Completion callback processed and consumer advanced receive data |
| epoll | Registered fd plus connection generation and current interest state | Provider pool slot used by the synchronous `recv` performed after readiness | Consumer advanced receive data |
| io_uring | CQE `user_data` encoding operation kind, connection slot, and generation | CQE buffer ID from `IORING_CQE_F_BUFFER`, plus buffer group | Consumer advanced segment and buffer returned to ring; operation identity retained until final CQE without `F_MORE` |
| IOCP | OVERLAPPED address plus connection slot/generation | Receive buffer associated with the OVERLAPPED | Completion dequeued and consumer advanced receive data |

Connection generations are not optional bookkeeping. File descriptors, socket handles, slot indexes, buffer IDs, and native memory addresses are reused. A late completion must be distinguishable from the current tenant of that reused resource.

## Cancellation and terminal completion

Cancellation has two phases:

1. the managed operation is marked canceled and a native cancellation request or close is initiated;
2. the backend observes the original operation's terminal completion and only then releases its identity and native memory.

For io_uring, the cancellation request has its own CQE, and a canceled multishot operation produces a final CQE without `IORING_CQE_F_MORE`. `-EALREADY` means the target may complete shortly; it does not mean its resources can be reclaimed.

For IOCP, an overlapped operation can complete after cancellation was requested. The OVERLAPPED block and its connection generation remain valid until the completion packet is consumed.

For epoll, removing readiness interest does not by itself make a concurrently executing operation disappear. Ownership is simpler when all native calls and state transitions are serialized on one loop, but cross-thread callback completions and disposal still need generation or reference identity.

For managed `Socket`, the runtime already owns much of this lifecycle. The provider must not layer its own premature pooling on top.

## Managed `Socket` provider

Source basis: [runtime Socket and handle ownership](sources.md#s-runtime-sockets), [Unix socket engine](sources.md#s-runtime-unix), and [Windows socket engine](sources.md#s-runtime-windows).

### Primer

`Socket` is the compatibility baseline:

- on Unix, the runtime registers sockets with a readiness engine and normally dispatches work to the thread pool;
- on Windows, the socket handle is bound to the thread pool completion port and overlapped completions drive async operations;
- `Socket` already supports cancellation-aware accept/connect/receive/send APIs;
- `SafeSocketHandle` already represents handle ownership and coordinates close with in-flight operations;
- `NetworkStream` and `SslStream` supply stream and TLS layers.

### Mapping

| Contract operation | Implementation |
|---|---|
| Listen | Create/bind/listen `Socket`, retain listener ownership |
| Accept | `Socket.AcceptAsync(CancellationToken)` |
| Connect | `Socket.ConnectAsync(EndPoint, CancellationToken)` |
| Read | One pooled receive buffer plus `Socket.ReceiveAsync`; expose filled memory until `AdvanceRead` |
| Write | Iterate/scatter over `ReadOnlySequence<byte>` with existing Socket APIs; hide partial sends |
| Pre-auth ClientHello | Read and retain the first TLS record in the internal Stream adapter, invoke the observer, then replay the retained bytes to `SslStream` |
| TLS | `SslStream` over an internal `Stream` adapter |
| Shutdown | `SslStream.ShutdownAsync` when secured, then `Socket.Shutdown(Send)` |
| Abort/dispose | Dispose socket and wait for provider tasks to stop referencing pooled state |

### Why it remains mandatory

- It provides the broadest platform reach.
- It preserves the full current TLS surface.
- It is the control for every native provider measurement.
- Runtime can improve its implementation without changing this proposal.
- It provides a compatibility path for configurations a native provider cannot faithfully implement.

No claim is made that a custom epoll, io_uring, or IOCP provider is faster.

## Linux epoll provider

Source basis: [SocketSet implementation study](sources.md#s-socketset), [OpenSSL contracts](sources.md#s-openssl), and [Linux networking documentation](sources.md#s-linux).

### Primer

epoll reports readiness, not completed byte counts. After an fd is reported readable or writable, the provider performs nonblocking `accept4`, `recv`, `send`, or `writev` and handles `EAGAIN`.

Level-triggered operation is the conservative starting point:

- unread input is reported again;
- a bounded read burst prevents one hot connection monopolizing a loop;
- writable interest is enabled only while a partial write is blocked, because an idle level-triggered `EPOLLOUT` registration spins;
- read interest can be removed while downstream backpressure is active.

SocketSet's current epoll implementation is concrete evidence for this shape. It is not a requirement to copy its exact sharding or buffer sizes.

### Mapping

| Contract operation | epoll implementation |
|---|---|
| Listen | Nonblocking listener registered for read readiness; optionally one listener per shard with `SO_REUSEPORT` where policy permits |
| Accept | Drain bounded `accept4` batch; assign connection generation and owner loop |
| Connect | Nonblocking connect; writable readiness plus `SO_ERROR` determines completion |
| Read | On read readiness, `recv` into provider memory; return one or more segments; stop at burst limit or `EAGAIN` |
| Advance | Return consumed pool slots and re-enable read interest when backpressure clears |
| Write | `send`/`writev`; retain sequence and cursor across partial writes; arm `EPOLLOUT` only while blocked |
| Pre-auth ClientHello | Start the server TLS parser/session far enough to capture ClientHello, suspend before credential-dependent continuation, invoke the observer off-loop, and retain state for authentication |
| Cancel | Marshal state transition to owner loop and modify/deregister interest |
| Dispose | Deregister before close; do not let a recycled fd match stale managed state |

### fd-bound OpenSSL

The fd is nonblocking and attached to OpenSSL. Every handshake/read/write step immediately calls `SSL_get_error` and translates:

- `WANT_READ` to read interest;
- `WANT_WRITE` to write interest;
- success to operation progress;
- `ZERO_RETURN` to clean TLS inbound close;
- fatal SSL/syscall status to terminal abort.

Read and write needs are combined before `epoll_ctl(MOD)`. A read may require write readiness and a write may require read readiness. The TLS session is serialized while one application read and one application write are allowed to remain pending.

User callbacks suspend the handshake and remove the fd from active readiness until the callback completion is marshaled back to the owner loop.

### Memory-BIO OpenSSL

The normal epoll receive path supplies ciphertext to a memory BIO, and TLS output becomes queued ciphertext sent by the normal write path. This avoids giving OpenSSL socket ownership and can share more code with io_uring, but introduces buffering/copy questions. It should remain a benchmarked alternative.

### kTLS

The fd-bound OpenSSL path can request kTLS after negotiation. epoll remains a natural readiness driver whether RX stays in userspace or enters kTLS. Actual TX/RX activation is queried independently. Hardware crypto remains an OS/device outcome.

<a id="linux-io_uring-provider"></a>
## Linux io_uring provider

Source basis: [SocketSet implementation study](sources.md#s-socketset), [liburing multishot/buffer/cancellation contracts](sources.md#s-io-uring), [OpenSSL contracts](sources.md#s-openssl), and [Linux kTLS documentation](sources.md#s-linux).

### Primer

io_uring is completion-based. A submission queue entry may produce one completion or, for multishot operations, several. Multishot receive normally uses a provided-buffer ring: the kernel chooses a buffer and identifies it in CQE flags. The operation stays alive while `IORING_CQE_F_MORE` is set and terminates when the flag is absent.

This means three identities matter:

1. connection identity: slot plus generation;
2. operation identity: operation kind plus submission generation in `user_data`;
3. buffer identity: buffer group and CQE buffer ID.

The consumer's `TransportReadResult` does not expose those raw values. The provider attaches them to the sequence segments and returns each buffer only after `AdvanceRead` moves past it.

### Plaintext mapping

| Contract operation | io_uring implementation |
|---|---|
| Listen | One-shot or multishot accept; `F_MORE` controls rearm; direct descriptors remain an optional implementation choice |
| Accept identity | CQE result provides accepted fd/direct descriptor; assign slot/generation before exposure |
| Connect | One connect SQE; preserve endpoint storage until submission/required stable lifetime |
| Read | Multishot recv/recvmsg with provided-buffer selection, or one-shot receive; queue CQE-selected buffers into connection order |
| Advance | Return fully consumed buffer IDs to the buffer ring; partial segment remains leased |
| Write | send/writev SQEs; retain source pins or copied provider buffers until each terminal CQE |
| Pre-auth ClientHello | Memory-BIO mode retains the CQE-selected ciphertext record; fd-bound mode uses poll-driven ClientHello callback suspension and preserves the initialized TLS session |
| Backpressure | Stop rearming, cancel a multishot receive by exact `user_data`, or absorb only a documented bounded number of already-produced CQEs |
| Teardown | Cancel all operations for the fd or exact identities; hold slot and generation until every original operation posts its terminal CQE |

### TLS model A: fd-bound OpenSSL driven by readiness

One-shot or multishot poll CQEs tell the owner loop when to retry the same OpenSSL operation.

Advantages:

- directly fits OpenSSL's documented nonblocking socket-BIO contract;
- simplest route to OpenSSL-managed kTLS activation;
- avoids a separate ciphertext queue between ring and TLS engine.

Costs and constraints:

- userspace TLS application reads are driven by poll plus `SSL_read`, not multishot receive;
- an SSL write pending after `WANT_*` retains its exact source;
- TLS state still requires single-owner serialization;
- raw ClientHello capture needs a runtime hook or a pre-read/peek design;
- a readiness poll completion is not an application receive completion.

SocketSet currently demonstrates this model for kTLS: handshake and RX use io_uring poll, while plaintext TX after activation uses normal send operations.

### TLS model B: memory-BIO OpenSSL over asynchronous ciphertext I/O

Multishot receive continues to fill provided buffers with ciphertext. The owner loop feeds those buffers to the TLS engine, drains plaintext into provider-owned plaintext buffers, and submits emitted ciphertext through send/writev.

Advantages:

- preserves multishot receive and provided-buffer batching;
- TLS engine does not own the fd;
- same high-level model works for OpenSSL and Schannel-like token engines;
- raw ClientHello bytes are naturally visible before TLS consumption.

Costs and constraints:

- ciphertext and plaintext ownership must be represented separately;
- OpenSSL memory BIOs may copy input/output;
- the provider needs bounded queues between socket completions, TLS transform, and consumer advancement;
- kTLS cannot be assumed to work with a memory BIO; OpenSSL's automatic kTLS path is tied to a socket BIO;
- completion batching and TLS record boundaries are independent.

### TLS model C: kTLS receive through io_uring

After userspace handshake and actual kTLS activation:

- TX can submit plaintext through normal send/writev operations because the kernel builds/encrypts TLS records;
- RX can use readiness plus `SSL_read`, allowing OpenSSL to process control records;
- a more ambitious implementation can investigate multishot `recvmsg` with ancillary record type and route non-application records through the TLS engine.

The proposal does not select the ambitious model by assumption. TLS 1.3 KeyUpdate, `TLS_GET_RECORD_TYPE`, buffer-ring layout for `recvmsg`, and cancellation/backpressure must be demonstrated together.

<a id="io_uring-integration-questions"></a>
### io_uring integration questions

These are explicit design questions, not invented answers:

1. Does the provider use normal fds or io_uring direct descriptors, and how does that choice interact with OpenSSL socket BIOs?
2. Is multishot accept used for every listener, and how are peer/local addresses obtained without unsafe shared address storage?
3. Does plaintext receive use multishot recv, multishot recvmsg, bundled receives, or a version-gated combination?
4. How are CQE `user_data`, connection generation, and buffer ID encoded without truncation across architectures?
5. When a read result spans several CQEs, how are partially consumed segments returned while later segments remain leased?
6. What exact bound applies to CQEs that arrive after downstream backpressure begins?
7. Is receive parking implemented by canceling the exact multishot `user_data`, by not rearming after terminal CQE, or by another kernel feature?
8. How does cancellation distinguish the cancellation request's CQE from the canceled operation's terminal CQE?
9. Does fd-bound TLS use one-shot poll or multishot poll, and how are level-triggered repeated notifications drained?
10. Can a memory-BIO path avoid an extra copy with the chosen runtime OpenSSL PAL, or is the copy measurable and acceptable?
11. If kTLS RX is active, will RX use `SSL_read` or control-message-aware `recvmsg`?
12. If `recvmsg` is used, how are KeyUpdate, alerts, session tickets, and close-notify fed back to the TLS engine?
13. How are `Require` TX/RX semantics enforced when one direction activates and the other does not?
14. How are callbacks suspended without blocking ring progress, and what wakes the owner after completion?
15. Which kernel features are required, how are they probed, and what is the exact fallback policy in restricted containers?

## Windows provider

Source basis: [runtime Windows Socket and Schannel paths](sources.md#s-runtime-windows).

### Baseline: managed `Socket` and `SslStream`

This is the required first Windows implementation. Current runtime sockets already bind handles to the CLR thread pool completion port and current `SslStream` uses Schannel. It preserves existing compatibility and establishes the control result.

### Optional raw IOCP provider

A raw provider may be justified if measurements or required group ownership cannot be achieved through existing `Socket`. Its model:

- `AcceptEx` and `ConnectEx` for listener/client operations;
- one or more `WSARecv` operations per connection according to the proven contract;
- `WSASend`/scatter-gather with explicit source pin lifetime;
- OVERLAPPED address as operation identity;
- connection slot plus generation as stale-completion defense;
- `GetQueuedCompletionStatusEx` batching;
- `PostQueuedCompletionStatus` for cross-thread close/resume/callback work;
- no slot or OVERLAPPED reuse before terminal completion.

The SocketSet IOCP implementation demonstrates this ownership pattern, while the runtime `Socket` implementation demonstrates that Windows sockets already use IOCP. A raw provider therefore needs evidence beyond "IOCP is faster."

### Schannel TLS

Schannel is naturally modeled as a token transform:

- `AcceptSecurityContext`/`InitializeSecurityContext` consumes ciphertext tokens;
- it may return an output token that must be sent;
- incomplete input requires another receive;
- extra bytes after a token must be retained;
- `EncryptMessage` and `DecryptMessage` transform application records.

Pre-auth observation reads and retains the first TLS record before the first `AcceptSecurityContext` call, invokes the observer outside the IOCP loop, and then supplies the retained record as the first Schannel input token.

This fits the memory-BIO-style transport contract: IOCP owns ciphertext I/O, while a runtime-owned TLS state machine consumes and produces buffers. It does not require Schannel to own the socket.

User callbacks must still leave the IOCP loop. The connection state is parked, callback work runs elsewhere, and completion is posted back to the owning port.

### Windows limitations

- There is no Linux-style kTLS contract in this proposal.
- Current runtime `CipherSuitesPolicy` is unsupported on Windows; parity preserves that platform result.
- Hardware TLS behavior is not exposed as a portable per-connection control.
- A raw provider must not duplicate runtime certificate and Schannel policy code into ASP.NET Core.

## Provider-neutral TLS versus provider-specific optimizations

The common contract allows native providers to choose an implementation, but selection must be deterministic and observable.

Suggested internal selection order:

1. Validate requested callbacks/options.
2. If explicit native mode cannot preserve them, fail.
3. If automatic mode cannot preserve them natively, choose `SslStream`.
4. If native mode is eligible, choose fd-bound or memory-BIO according to provider policy.
5. After negotiation, attempt requested kTLS directions.
6. Publish actual provider/mode and offload status through diagnostics and `TransportTlsInfo`.

The common public API should not expose `EpollTlsMode`, `IoUringTlsMode`, or `UsePollInsteadOfRecv`. Those are provider experiments until evidence shows a durable user-facing semantic.

## Pipe adapter mapping

### Baseline universal bridge

The baseline `TransportPipelines` implementation works for every provider:

- one receive pump copies provider-owned read segments into the inbound pipe and advances the provider result;
- one send pump passes outbound pipe sequences to `WriteAsync` and advances the pipe only after completion;
- an async inbound flush stops the next `ReadAsync`, creating real backpressure at the low-level contract;
- already completed native reads are bounded by provider pool size;
- errors complete both pipe directions and abort the connection;
- normal output completion drains, calls `ShutdownWriteAsync`, and permits input to continue until peer EOF;
- disposal aborts unless graceful shutdown already completed;
- `LeaveOpen` controls only final connection disposal, not operation ownership.

### Optimized adapters

A built-in provider may implement an internal optimized adapter:

- epoll can obtain inbound pipe memory after readiness and `recv` directly into it;
- io_uring can expose CQE-selected buffers through a custom `PipeReader` whose `AdvanceTo` returns buffer IDs;
- IOCP can receive into pipe-pool memory held stable for OVERLAPPED lifetime;
- outbound sequences can be sent by scatter/gather when the backend supports it.

These optimizations must preserve the same public adapter semantics and be independently observable in benchmarks. The first public SPI does not expose a generic "zero-copy" capability flag.

## Failure isolation

A provider-wide loop failure is different from a connection failure:

- malformed peer input, TLS alert, callback exception, canceled operation, and socket error fail one connection;
- an unrecoverable epoll/ring/completion-port failure faults the provider, stops listeners, aborts owned connections, and completes provider disposal;
- application exceptions never unwind through and terminate a shared native loop;
- provider diagnostics identify the provider, connection, operation kind, and terminal reason without exposing raw key material or plaintext.

## Backend acceptance matrix

| Behavior | Managed Socket | epoll | io_uring | Windows IOCP |
|---|---|---|---|---|
| TCP listen/accept/connect | Required | Required | Required | Required |
| One read + one write concurrently | Required | Required | Required | Required |
| Same-direction rejection | Required | Required | Required | Required |
| Provider-owned receive lease | Required | Required | Required | Required |
| Partial write handling | Required | Required | Required | Required |
| Cancellation race and terminal completion | Required | Required | Required | Required |
| Plaintext half-close | Required | Required | Required | Required |
| `SslStream` compatibility TLS | Required | Available through generic adapter | Available through generic adapter | Required |
| Native TLS | Not required | fd-bound OpenSSL target | fd-bound and memory-BIO experiments | Schannel token path target |
| Raw first-record ClientHello | Required | Required for native parity | Required for native parity | Required for native parity |
| Delayed client certificate | Existing platform behavior | Fallback/reject until implemented | Fallback/reject until implemented | Existing `SslStream`; raw provider requires proof |
| kTLS | Not applicable | Optional | Optional | Not applicable |
| Hardware TLS report | Not applicable | Unknown/active only with evidence | Unknown/active only with evidence | No portable contract |
| Pipelines adapter | Required | Required | Required | Required |

## Performance evaluation

Every native provider is compared against the managed provider in the same process, build, machine, network path, TLS policy, certificate, payload distribution, and scheduler configuration.

Required metrics:

- accepts/connects per second and failure rate;
- request/response throughput;
- p50, p95, p99, and max latency;
- CPU time and cycles where available;
- managed allocations and native memory;
- pinned/registered memory;
- syscalls or submissions/completions per operation;
- queue depth and buffer-ring exhaustion;
- scheduler hops;
- backpressure activations;
- actual TLS implementation and kTLS TX/RX state.

A provider is not selected by default based on a microbenchmark alone. It must also pass callback parity, cancellation, slow-consumer, connection-churn, and shutdown tests.
