# Provider usage examples

These files show the proposed **consumer API**, not backend implementation code. Each provider uses the same callback application; only provider creation, provider-specific options, and TLS strategy differ.

The examples are compiled by `NetworkTransportExamples.csproj`, which references the nonfunctional proposal stubs in `..\validation`. They are not runnable transport implementations.

| File | Provider and TLS path |
|---|---|
| `ManagedSocketsPlaintext.cs` | Ordinary `System.Net.Sockets.Socket`/SAEA provider, plaintext |
| `ManagedSocketsTls.cs` | Ordinary Socket provider with the exact `SslStream` compatibility strategy |
| `EpollPlaintext.cs` | Linux level-triggered epoll, plaintext |
| `EpollSocketBoundTls.cs` | epoll plus fd-bound OpenSSL; ClientHello callback is raised by the handshake; kTLS preferred |
| `IoUringPlaintext.cs` | io_uring multishot/provided-buffer plaintext path |
| `IoUringMemoryBioTls.cs` | io_uring ciphertext receive/send plus memory-BIO TLS transform |
| `IoUringSocketBoundTls.cs` | io_uring poll drives fd-bound OpenSSL; kTLS preferred |
| `WindowsIocpTls.cs` | Windows IOCP with Schannel token/record processing |
| `WindowsRioTls.cs` | Windows RIO registered data path with Schannel; explicit, non-default provider |

All TLS examples configure TLS on `TransportListenOptions` or `TransportConnectOptions`. There is no consumer call to `AuthenticateAsServerAsync`: the provider drives the handshake and raises `OnReady` only after authentication succeeds.
