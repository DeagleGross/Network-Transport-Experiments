# Requirement traceability

> **Status:** This table is the acceptance index for the proposal. Each requirement maps to a concrete API or SPI contract, an example or backend mapping, and a limitation or evidence gate.

| Requirement | API or SPI contract | Usage or backend demonstration | Known limitation or acceptance evidence |
|---|---|---|---|
| **R1: TCP-only and TCP+TLS; native TLS central** | `TransportProvider.ListenAsync`, `ConnectAsync`; `TransportConnection.ReadAsync`, `WriteAsync`; `AuthenticateAsClientAsync`, `AuthenticateAsServerAsync`; `TransportTlsInfo` | [Plain TCP server](api.md#plain-tcp), [TLS server](api.md#tls-server-with-raw-clienthello-observation-and-per-sni-certificate-selection), managed, epoll, io_uring, and Windows mappings in [backends.md](backends.md) | Native TLS is unimplemented in this proposal. Every provider must supply native TLS or the `SslStream` fallback; TLS is not a Kestrel-only layer. |
| **R1.5: all relevant Kestrel TLS hooks/options** | Existing `SslServerAuthenticationOptions` remains authoritative; `ObserveTlsClientHelloAsync`; `TransportClientHelloCallback`; `TransportServerOptionsSelectionCallback`; `AllowPostHandshakeClientAuthentication`; `RequestClientCertificateAsync`; `TransportTlsInfo` | Full [callback parity matrix](tls.md#callback-parity-matrix), [server option matrix](tls.md#server-option-parity-matrix), [Kestrel feature mapping](tls.md#kestrel-feature-mapping-after-authentication) | Exact `TlsHandshakeCallbackContext.SslStream` and `ISslStreamFeature` require the real `SslStream` path. Delayed client cert and channel binding require provider support or fallback/rejection. Current raw callback is first-record, not guaranteed complete-message. |
| **R2: epoll, io_uring, managed Socket fallback, Windows** | `TransportProvider`, `TransportListener`, `TransportConnection`; provider-specific implementation packages | Dedicated sections for [managed Socket](backends.md#managed-socket-provider), [epoll](backends.md#linux-epoll-provider), [io_uring](backends.md#linux-io_uring-provider), and [Windows](backends.md#windows-provider) | Managed Socket is the mandatory baseline. Native provider stabilization depends on correctness and measurements. Windows raw IOCP is optional because managed Socket already uses IOCP. |
| **R3: generic accept/connect/read/write/handshake SPI** | Full candidate surface in [api.md](api.md#candidate-reference-surface); member justification in [api.md](api.md#member-by-member-necessity) | Server, TLS, client, Redis-style, and Pipelines snippets in [api.md](api.md) | The surface is proposed for experimental incubation only. Generic extensibility alone does not justify stable BCL API. |
| **R4: fd-bound nonblocking OpenSSL** | TLS authentication override plus the common read/write lifecycle; provider-private readiness state | [fd-bound OpenSSL contract](tls.md#fd-bound-openssl-contract), [epoll mapping](backends.md#fd-bound-openssl) | `WANT_READ`/`WANT_WRITE` can cross directions; same SSL session must be serialized. Full raw ClientHello and option parity require runtime work. |
| **R5: io_uring not prematurely narrowed** | Provider-owned receive sequences, explicit `AdvanceRead`, all-or-error `WriteAsync`, cancellation/terminal lifetime invariants | [Three io_uring TLS models](backends.md#linux-io_uring-provider), [identity table](backends.md#completion-and-buffer-identity), [integration questions](backends.md#io_uring-integration-questions) | No preferred io_uring TLS design is asserted. Multishot, memory BIO, fd-bound readiness, kTLS, buffer rings, and cancellation must be compared. |
| **R6: kTLS** | `TransportTlsOffloadOptions` with independent TX/RX `Disabled`/`Prefer`/`Require`; `TransportTlsOffloadInfo` reports actual state | [kTLS contract](tls.md#ktls-and-hardware-offload), epoll and io_uring kTLS sections | Build, OS, kernel, protocol, cipher, TLS feature, and backend can prevent activation. `Require` fails; `Prefer` reports fallback. |
| **R7: ordinary NIC and optional hardware TLS offload** | No generic switch for ordinary offloads; `TlsHardwareOffloadStatus` reports `NotApplicable`, `Inactive`, `Active`, or `Unknown` | [Ordinary offload distinction](tls.md#ordinary-nic-offloads), [hardware TLS](tls.md#hardware-tls), Linux provider mapping | Runtime does not configure drivers/NICs. kTLS is not proof of NIC crypto. Zero-copy is not inferred from either. |
| **R8: reuse versus new Socket** | `Socket` remains unchanged; `SocketTransportProvider` wraps/adopts it; common abstraction omits socket-specific surface | [Why this is not a second Socket](README.md#why-this-is-not-a-second-socket), [best counterargument](README.md#best-counterargument), [rejected API ideas](api.md#rejected-surface-ideas) | No raw native handle transfer in v1. No fabricated non-owning `Socket` for native providers. Stable new type is deferred pending evidence. |
| **R9: PipeIOBridge-like adapter** | `TransportPipelines.Create`, `TransportPipeOptions.InputOptions`/`OutputOptions`, `TransportDuplexPipe` | [Pipelines example and algorithms](api.md#illustrative-pipelines-adapter), [adapter mapping](backends.md#pipe-adapter-mapping), [Kestrel section](README.md#pipelines-and-kestrel) | Universal bridge may copy inbound. Provider-specific zero-copy adapters remain internal until they prove identical semantics. |
| **R10: first-class clients and Redis-style long-lived use** | `ConnectAsync`, client TLS options, connection `Completion`, explicit provider/connection ownership | [Authenticated client](api.md#authenticated-client), [Redis-style multiplexer](api.md#redis-style-long-lived-multiplexer) | Runtime does not own pooling, retry, request replay, or protocol multiplexing. No performance benefit is asserted without measurement. |

## Cross-cutting acceptance mapping

| Concern | Contract | Evidence required |
|---|---|---|
| Buffer lifetime | Receive memory valid through `AdvanceRead`; write memory valid through `WriteAsync` completion | Forced cancellation, delayed completion, partial consumption, and slot reuse tests |
| Same-direction concurrency | One read and one write may coexist; a second read or second write fails deterministically | Contract tests across every provider and TLS mode |
| Callback isolation | User callbacks are off backend loops and provider-global locks | A blocking callback must not stall unrelated connections; callback throw fails only its connection |
| Terminal completion | Provider retains native state until final CQE/OVERLAPPED/callback completion | Cancellation-race and provider-shutdown stress tests |
| Backpressure | Adapter awaits inbound flush before next logical read; provider bounds already-issued completions | Slow-consumer tests with memory bound and peer-side slowdown where backend can park |
| Graceful close | Output drain -> TLS `close_notify` -> TCP write shutdown; reads may continue | Final-data-plus-EOF, close-notify, peer FIN, and abort tests |
| TLS parity | Every current option and callback is implemented, fallback-selected, or rejected | Matrix-driven tests and per-provider support report |
| Offload truthfulness | Request, negotiated TLS, kernel TX/RX, and hardware status remain distinct | Positive and negative activation probes; no flag-only success |
| Socket compatibility | Existing Socket can be adopted by managed provider with explicit ownership | Dispose/cancel/half-close tests for owning and non-owning cases |
| Client lifecycle | Provider shared; connection owned by multiplexer; reconnect outside runtime | Long-lived pipelining, server close, reconnect, and cancellation tests |

## Deliberately excluded

| Exclusion | Reason |
|---|---|
| UDP/datagrams | Read leases and ordered all-or-error writes are stream semantics. A datagram API needs message boundaries and endpoint-per-operation semantics. |
| QUIC | QUIC is multiplexed, encrypted, and message/stream oriented with an existing runtime API. |
| HTTP concerns | Protocol parsing, request scheduling, limits, and metrics belong above the runtime transport. |
| Automatic pooling/retry | Retry safety is protocol-specific; the transport cannot know whether an operation is replayable. |
| Portable NIC configuration | Requires administrative policy, driver support, routing knowledge, and platform-specific tooling. |
| Stable provider tuning | Ring entries, shard counts, accept depth, and buffer groups are implementation parameters until evidence shows a portable semantic. |
| Fake `Socket` or `SslStream` | A facade would misrepresent ownership and valid operations. |
