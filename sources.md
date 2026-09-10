# Source basis

> Sources were checked on 2026-09-10. Repository links use immutable commits or tags. Documentation links are versioned where the publisher provides a versioned URL.

## ASP.NET Core

<a id="s-kestrel-https"></a>
### S-KESTREL-HTTPS: Kestrel TLS surface and adapter

Revision: `dotnet/aspnetcore@67a02a66a21ac4f2e8ab19201b59cf45e97c8c98`

- [`HttpsConnectionAdapterOptions`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Core/src/HttpsConnectionAdapterOptions.cs) establishes the certificate, chain, selector, client-certificate, protocol, revocation, `OnAuthenticate`, raw ClientHello, and timeout surface.
- [`TlsHandshakeCallbackOptions`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Core/src/TlsHandshakeCallbackOptions.cs) and [`TlsHandshakeCallbackContext`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Core/src/TlsHandshakeCallbackContext.cs) establish the asynchronous per-connection callback, actual `SslStream`, parsed ClientHello, state, cancellation, connection, and delayed-client-certificate switch.
- [`HttpsConnectionMiddleware`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Core/src/Middleware/HttpsConnectionMiddleware.cs) establishes option construction, `OnAuthenticate` timing, `SslStream.AuthenticateAsServerAsync`, feature publication, and the current `SslDuplexPipe` layering.
- [`ListenOptionsHttpsExtensions`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Core/src/ListenOptionsHttpsExtensions.cs) establishes `UseHttps` callback overloads and `UseTlsClientHelloListener`.
- [`TlsListener`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Core/src/Middleware/TlsListener.cs) establishes the current first-record parser, 16 KiB plaintext-fragment cap, borrowed `ReadOnlySequence<byte>`, and pipe advancement.
- [`SniOptionsSelector`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Core/src/Internal/SniOptionsSelector.cs) establishes SNI selection and the complete set of `SslServerAuthenticationOptions` properties cloned by current Kestrel.
- [`TlsConnectionFeature`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Core/src/Internal/TlsConnectionFeature.cs) establishes TLS feature metadata, delayed client certificate behavior, snapshot lifetime, `ISslStreamFeature`, and channel binding.
- [`SslDuplexPipe`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Shared/ServerInfrastructure/SslDuplexPipe.cs) and [`DuplexPipeStreamAdapter`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Shared/ServerInfrastructure/DuplexPipeStreamAdapter.cs) establish the current pipe -> stream -> `SslStream` -> pipe adapter shape.

<a id="s-kestrel-sockets"></a>
### S-KESTREL-SOCKETS: Kestrel socket scheduling and backpressure

Revision: `dotnet/aspnetcore@67a02a66a21ac4f2e8ab19201b59cf45e97c8c98`

- [`SocketConnectionContextFactory`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Transport.Sockets/src/SocketConnectionContextFactory.cs) establishes the current direction-specific `PipeOptions`, memory pool, IO queue, and application scheduler choices.
- [`IOQueue`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Transport.Sockets/src/Internal/IOQueue.cs) establishes the one-drainer thread-pool scheduler.
- [`SocketTransportOptions`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Transport.Sockets/src/SocketTransportOptions.cs) establishes backlog, `NoDelay`, read/write bounds, queue count, and explicit warning that inline scheduling can hurt and must be measured.

<a id="s-directtls"></a>
### S-DIRECTTLS: current experimental fd-bound TLS study

Revision: `dotnet/aspnetcore@67a02a66a21ac4f2e8ab19201b59cf45e97c8c98`

- [`DirectTlsEndpointOptions`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Core/src/DirectTlsEndpointOptions.cs) establishes the current DirectTLS option subset and its explicit lack of delayed client certificate support.
- [`DirectTlsTransportFactory`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Transport.DirectTls/src/DirectTlsTransportFactory.cs) establishes the TLS-only endpoint, context cache, certificate selection, callback adaptation, and Linux restriction.
- [`ConnectionIoState`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Transport.DirectTls/src/ConnectionIoState.cs) establishes fd-bound OpenSSL read/write serialization and cross-direction `WANT_READ`/`WANT_WRITE` interest.
- [`TlsEventPump`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Transport.DirectTls/src/TlsEventPump.cs) and [`TlsEventPump.UserCallbacks`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Transport.DirectTls/src/TlsEventPump.UserCallbacks.cs) establish epoll ownership, callback suspension, wakeup, identity checks, and shutdown draining.
- [`DirectTlsConnection`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Transport.DirectTls/src/Connection/DirectTlsConnection.cs) establishes the current pipe bridge, scheduler split, backpressure, and lifecycle.
- [`DirectTlsConnection.FeatureCollection`](https://github.com/dotnet/aspnetcore/blob/67a02a66a21ac4f2e8ab19201b59cf45e97c8c98/src/Servers/Kestrel/Transport.DirectTls/src/Connection/DirectTlsConnection.FeatureCollection.cs) explicitly documents the reconstructed non-owning `Socket` sharp edge.

## dotnet/runtime

The inspected local runtime checkout was on a feature commit, but the relevant `System.Net.Sockets`, `System.Net.Security`, and `System.IO.Pipelines` files were unchanged from fetched `upstream/main` revision `7b8b0c6661375d5eb2dad119ee017041f642ded8`.

<a id="s-runtime-sockets"></a>
### S-RUNTIME-SOCKETS: public Socket and handle ownership

Revision: `dotnet/runtime@7b8b0c6661375d5eb2dad119ee017041f642ded8`

- [`System.Net.Sockets` reference surface](https://github.com/dotnet/runtime/blob/7b8b0c6661375d5eb2dad119ee017041f642ded8/src/libraries/System.Net.Sockets/ref/System.Net.Sockets.cs) establishes `Socket` accept/connect/read/write/scatter-gather/shutdown/options and `SafeSocketHandle`.
- [`Socket`](https://github.com/dotnet/runtime/blob/7b8b0c6661375d5eb2dad119ee017041f642ded8/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.cs) establishes construction over `SafeSocketHandle` and best-effort property discovery.
- [`SafeSocketHandle`](https://github.com/dotnet/runtime/blob/7b8b0c6661375d5eb2dad119ee017041f642ded8/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SafeSocketHandle.cs) establishes ownership and close coordination. The Windows partial shows that a non-owning handle still explicitly cancels ongoing operations before releasing its wrapper state.

<a id="s-runtime-unix"></a>
### S-RUNTIME-UNIX: current Unix async socket engine

Revision: `dotnet/runtime@7b8b0c6661375d5eb2dad119ee017041f642ded8`

- [`SocketAsyncEngine.Unix`](https://github.com/dotnet/runtime/blob/7b8b0c6661375d5eb2dad119ee017041f642ded8/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEngine.Unix.cs) states that epoll/kqueue notifications are normally dispatched to thread-pool work, with an opt-in inline completion mode.
- [`SocketAsyncContext.Unix`](https://github.com/dotnet/runtime/blob/7b8b0c6661375d5eb2dad119ee017041f642ded8/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncContext.Unix.cs) establishes immediate nonblocking attempts, operation queues after `EWOULDBLOCK`, cancellation state, and callback dispatch.

<a id="s-runtime-windows"></a>
### S-RUNTIME-WINDOWS: current Windows async socket and Schannel paths

Revision: `dotnet/runtime@7b8b0c6661375d5eb2dad119ee017041f642ded8`

- [`SafeSocketHandle.Windows`](https://github.com/dotnet/runtime/blob/7b8b0c6661375d5eb2dad119ee017041f642ded8/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SafeSocketHandle.Windows.cs) binds socket handles through `ThreadPoolBoundHandle`.
- [`SocketAsyncEventArgs.Windows`](https://github.com/dotnet/runtime/blob/7b8b0c6661375d5eb2dad119ee017041f642ded8/src/libraries/System.Net.Sockets/src/System/Net/Sockets/SocketAsyncEventArgs.Windows.cs) establishes pinned/preallocated OVERLAPPED operation state.
- [`SslStreamPal.Windows`](https://github.com/dotnet/runtime/blob/7b8b0c6661375d5eb2dad119ee017041f642ded8/src/libraries/System.Net.Security/src/System/Net/Security/SslStreamPal.Windows.cs) establishes Schannel's input/output token handshake and in-place record encryption/decryption.
- [Microsoft I/O completion ports documentation](https://learn.microsoft.com/en-us/windows/win32/fileio/i-o-completion-ports), source revision `b5d1b1a57f09b10ff0e928820af26db24a2d4fd0`, establishes completion queue and concurrency behavior.
- [Microsoft `AcceptSecurityContext` documentation](https://learn.microsoft.com/en-us/windows/win32/api/sspi/nf-sspi-acceptsecuritycontext), source revision `9267262487657894a8af112d7165006fed5035a7`, establishes iterative input/output token processing, incomplete input, extra data, and continuation statuses.

<a id="s-runtime-tls"></a>
### S-RUNTIME-TLS: current SslStream surface and semantics

Revision: `dotnet/runtime@7b8b0c6661375d5eb2dad119ee017041f642ded8`

- [`System.Net.Security` reference surface](https://github.com/dotnet/runtime/blob/7b8b0c6661375d5eb2dad119ee017041f642ded8/src/libraries/System.Net.Security/ref/System.Net.Security.cs) establishes the current public options, callbacks, metadata, authentication, read/write, shutdown, and delayed client certificate APIs.
- [`SslServerAuthenticationOptions`](https://github.com/dotnet/runtime/blob/7b8b0c6661375d5eb2dad119ee017041f642ded8/src/libraries/System.Net.Security/src/System/Net/Security/SslServerAuthenticationOptions.cs) and [`SslClientAuthenticationOptions`](https://github.com/dotnet/runtime/blob/7b8b0c6661375d5eb2dad119ee017041f642ded8/src/libraries/System.Net.Security/src/System/Net/Security/SslClientAuthenticationOptions.cs) establish the full option properties.
- [`SslAuthenticationOptions`](https://github.com/dotnet/runtime/blob/7b8b0c6661375d5eb2dad119ee017041f642ded8/src/libraries/System.Net.Security/src/System/Net/Security/SslAuthenticationOptions.cs) establishes option validation, callback precedence, certificate context preparation, and chain-policy cloning.
- [`SslStream.IO`](https://github.com/dotnet/runtime/blob/7b8b0c6661375d5eb2dad119ee017041f642ded8/src/libraries/System.Net.Security/src/System/Net/Security/SslStream.IO.cs) establishes ClientHello option callback timing and one-operation-per-direction enforcement.
- [`SslStream.Protocol`](https://github.com/dotnet/runtime/blob/7b8b0c6661375d5eb2dad119ee017041f642ded8/src/libraries/System.Net.Security/src/System/Net/Security/SslStream.Protocol.cs) establishes certificate selection and remote certificate validation behavior.
- [`SslStreamPal.Unix`](https://github.com/dotnet/runtime/blob/7b8b0c6661375d5eb2dad119ee017041f642ded8/src/libraries/System.Net.Security/src/System/Net/Security/SslStreamPal.Unix.cs) establishes the OpenSSL token/record transform used by current `SslStream`.
- [`CipherSuitesPolicyPal.Windows`](https://github.com/dotnet/runtime/blob/7b8b0c6661375d5eb2dad119ee017041f642ded8/src/libraries/System.Net.Security/src/System/Net/Security/CipherSuitesPolicyPal.Windows.cs) establishes current Windows non-support for custom cipher suite policy.

<a id="s-runtime-pipelines"></a>
### S-RUNTIME-PIPELINES: scheduler and backpressure semantics

Revision: `dotnet/runtime@7b8b0c6661375d5eb2dad119ee017041f642ded8`

- [`PipeOptions`](https://github.com/dotnet/runtime/blob/7b8b0c6661375d5eb2dad119ee017041f642ded8/src/libraries/System.IO.Pipelines/src/System/IO/Pipelines/PipeOptions.cs) establishes separate reader/writer schedulers, memory pool, pause threshold, resume threshold, minimum segment size, and synchronization-context behavior.

## SocketSet implementation study

<a id="s-socketset"></a>
### S-SOCKETSET: multi-backend feasibility evidence

Revision: `mgravell/SocketSet@adc470a8e5607ee50397a79caabf31aca7151ef9`

- [`SocketSetFactory`](https://github.com/mgravell/SocketSet/blob/adc470a8e5607ee50397a79caabf31aca7151ef9/src/SocketSet/SocketSetFactory.cs) exposes io_uring, epoll, managed, Windows IOCP, and RIO implementations.
- [`Connection`](https://github.com/mgravell/SocketSet/blob/adc470a8e5607ee50397a79caabf31aca7151ef9/src/SocketSet/Connection.cs) demonstrates stable per-connection identity, provider-owned writes, receive parking, and explicit lifecycle.
- [`IoUringShard`](https://github.com/mgravell/SocketSet/blob/adc470a8e5607ee50397a79caabf31aca7151ef9/src/SocketSet/IoUring/IoUringShard.cs) demonstrates multishot accept/receive, provided-buffer IDs, packed operation identity, cancellation, fd-bound OpenSSL polling, and kTLS.
- [`EpollShard`](https://github.com/mgravell/SocketSet/blob/adc470a8e5607ee50397a79caabf31aca7151ef9/src/SocketSet/Epoll/EpollShard.cs) demonstrates level-triggered readiness, bounded drains, dynamic write interest, direct receive into pipe memory, fd-bound OpenSSL, and kTLS.
- [`ManagedSocketShard`](https://github.com/mgravell/SocketSet/blob/adc470a8e5607ee50397a79caabf31aca7151ef9/src/SocketSet/Managed/ManagedSocketShard.cs) demonstrates a SAEA fallback and callback-driven receive parking.
- [`IocpShard`](https://github.com/mgravell/SocketSet/blob/adc470a8e5607ee50397a79caabf31aca7151ef9/src/SocketSet/Windows/IocpShard.cs) demonstrates raw IOCP operation identity, generation checks, accept/connect/receive/send, and terminal completion before slot reuse.
- [`TlsFilter`](https://github.com/mgravell/SocketSet/blob/adc470a8e5607ee50397a79caabf31aca7151ef9/src/SocketSet/Tls/TlsFilter.cs) and [`OpenSslTlsFilter`](https://github.com/mgravell/SocketSet/blob/adc470a8e5607ee50397a79caabf31aca7151ef9/src/SocketSet/Tls/OpenSsl/OpenSslTlsFilter.cs) demonstrate a memory-BIO push TLS model.
- [`PipeIoBridge`](https://github.com/mgravell/SocketSet/blob/adc470a8e5607ee50397a79caabf31aca7151ef9/src/SocketSet/PipeIoBridge.cs) demonstrates a universal pipe bridge, staging, receive parking, and explicit ownership tradeoffs.
- [`SocketSetTransport`](https://github.com/mgravell/SocketSet/blob/adc470a8e5607ee50397a79caabf31aca7151ef9/src/SocketSet.StackExchange.Redis/SocketSetTransport.cs) demonstrates one shared engine serving long-lived client transports.

These sources demonstrate feasibility and failure modes. They do not establish production readiness or a performance advantage over current runtime APIs.

<a id="s-openssl"></a>
## OpenSSL

Version: OpenSSL 3.5 documentation, with immutable source links to tag `openssl-3.5.2`.

- [`SSL_get_error`](https://github.com/openssl/openssl/blob/openssl-3.5.2/doc/man3/SSL_get_error.pod) establishes same-thread error classification, `WANT_READ`/`WANT_WRITE`, opposite-direction needs, retry, and fatal error rules.
- [`SSL_read`](https://github.com/openssl/openssl/blob/openssl-3.5.2/doc/man3/SSL_read.pod) establishes record buffering and that reads can require writes.
- [`SSL_write`](https://github.com/openssl/openssl/blob/openssl-3.5.2/doc/man3/SSL_write.pod) establishes same-buffer retry requirements, full-write default semantics, and `SSL_sendfile` availability only with kTLS.
- [`SSL_set_bio`](https://github.com/openssl/openssl/blob/openssl-3.5.2/doc/man3/SSL_set_bio.pod) establishes BIO ownership and nonblocking behavior.
- [`SSL_CTX_set_client_hello_cb`](https://github.com/openssl/openssl/blob/openssl-3.5.2/doc/man3/SSL_CTX_set_client_hello_cb.pod) establishes early callback ordering, suspend/resume, raw field access, and extension-order limitations.
- [`SSL_CTX_set_cert_cb`](https://github.com/openssl/openssl/blob/openssl-3.5.2/doc/man3/SSL_CTX_set_cert_cb.pod) establishes certificate callback timing and `WANT_X509_LOOKUP` suspension.
- [`SSL_CTX_set_options`](https://github.com/openssl/openssl/blob/openssl-3.5.2/doc/man3/SSL_CTX_set_options.pod) establishes `SSL_OP_ENABLE_KTLS`, eligibility caveats, feature limitations, provider/FIPS implications, and the distinct sendfile zero-copy option.
- [`BIO_ctrl`](https://github.com/openssl/openssl/blob/openssl-3.5.2/doc/man3/BIO_ctrl.pod) establishes `BIO_get_ktls_send` and `BIO_get_ktls_recv` as actual TX/RX activation checks.

<a id="s-linux"></a>
## Linux kernel

Version: Linux `v6.12`.

- [`Documentation/networking/tls.rst`](https://github.com/torvalds/linux/blob/v6.12/Documentation/networking/tls.rst) establishes userspace handshake followed by independent TX/RX installation, plaintext send/receive semantics, control-record ancillary data, KeyUpdate behavior, optional zero-copy conditions, and TLS statistics.
- [`Documentation/networking/tls-offload.rst`](https://github.com/torvalds/linux/blob/v6.12/Documentation/networking/tls-offload.rst) distinguishes software kTLS, packet-based NIC crypto, and full TCP NIC offload; states that device mode is selected from device configuration and is not currently a per-connection user opt-in/out.
- [`Documentation/networking/segmentation-offloads.rst`](https://github.com/torvalds/linux/blob/v6.12/Documentation/networking/segmentation-offloads.rst) distinguishes TSO, checksum dependency, software GSO, and GRO from TLS offload.

<a id="s-io-uring"></a>
## io_uring/liburing

Revision: `axboe/liburing@4cf73437863c2e492d2a1d0f24330f391c0f075b`

- [`io_uring_multishot(7)`](https://github.com/axboe/liburing/blob/4cf73437863c2e492d2a1d0f24330f391c0f075b/man/io_uring_multishot.7) establishes one SQE producing multiple CQEs, `IORING_CQE_F_MORE`, final CQE semantics, cancellation, and provided-buffer return.
- [`io_uring_prep_accept(3)`](https://github.com/axboe/liburing/blob/4cf73437863c2e492d2a1d0f24330f391c0f075b/man/io_uring_prep_accept.3) establishes multishot accept and address-buffer caveats.
- [`io_uring_prep_recv(3)`](https://github.com/axboe/liburing/blob/4cf73437863c2e492d2a1d0f24330f391c0f075b/man/io_uring_prep_recv.3) establishes multishot buffer selection, `F_MORE`, length/fairness behavior, bundles, and kernel availability.
- [`io_uring_provided_buffers(7)`](https://github.com/axboe/liburing/blob/4cf73437863c2e492d2a1d0f24330f391c0f075b/man/io_uring_provided_buffers.7) establishes buffer groups, buffer IDs, return requirements, and `-ENOBUFS`.
- [`io_uring_prep_poll_add(3)`](https://github.com/axboe/liburing/blob/4cf73437863c2e492d2a1d0f24330f391c0f075b/man/io_uring_prep_poll_add.3) establishes persistent multishot readiness notification and reissue after terminal completion.
- [`io_uring_prep_cancel(3)`](https://github.com/axboe/liburing/blob/4cf73437863c2e492d2a1d0f24330f391c0f075b/man/io_uring_prep_cancel.3) establishes matching by `user_data`, cancel CQEs, `-ENOENT`, `-EALREADY`, and the requirement to await the target's completion.

## Source interpretation limits

- The local DirectTLS and SocketSet implementations are experimental design evidence.
- Source comments that report historical measurements are not treated as transferable performance proof.
- The proposal does not infer current release commitments from branch names, commits, pull requests, or user planning context.
- Platform documentation describes mechanisms; provider behavior still requires runtime tests on supported OS, kernel, TLS-library, and hardware combinations.
