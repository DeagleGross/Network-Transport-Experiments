# Provider usage examples

These files show the proposed **consumer API**, not backend implementation code. Each provider uses the same callback application and the same TLS configuration; only provider creation and provider-specific resource options differ.

The examples are compiled by `NetworkTransportExamples.csproj`, which references the nonfunctional proposal stubs in `..\validation`. They are not runnable transport implementations.

| File | Provider and TLS path |
|---|---|
| `ManagedSocketsPlaintext.cs` | Ordinary `System.Net.Sockets.Socket`/SAEA provider, plaintext |
| `ManagedSocketsTls.cs` | Ordinary Socket provider with provider-owned TLS |
| `EpollPlaintext.cs` | Linux level-triggered epoll, plaintext |
| `EpollTls.cs` | epoll with provider-owned TLS; ClientHello callback is raised by the handshake; kTLS preferred |
| `IoUringPlaintext.cs` | io_uring multishot/provided-buffer plaintext path |
| `IoUringTls.cs` | io_uring with provider-owned TLS; the internal TLS mechanism does not change usage |
| `WindowsIocpTls.cs` | Windows IOCP with provider-owned TLS |
| `WindowsRioTls.cs` | Windows RIO registered data path with provider-owned TLS; explicit, non-default provider |
| `DispatchedReceive.cs` | Retain-or-copy receive handoff to an application queue, followed by a later copying send |

All TLS examples configure TLS on `TransportListenOptions` or `TransportConnectOptions`. There is no consumer call to `AuthenticateAsServerAsync`: the provider drives the handshake and raises `OnReady` only after authentication succeeds.

`EchoApplication.cs` shows the immediate-response path: it copies the callback payload into provider-owned output memory through `GetResponseSpan`. `DispatchedReceive.cs` shows the different lifetime used by application processing: retain or copy the input, return from `OnReceive`, continue receiving, process queued input on another scheduler, dispose each input lease after reading it, and call `TransportConnection.Send` later. Receive and write progress independently. The connection is a persistent command handle and does not require the send call to occur inside an `ITransportApplication` callback.
