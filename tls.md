# TLS lifecycle, callback parity, and offload

> **Status:** Proposed contract plus a source-grounded inventory of current behavior. Native-provider support described as "required" is unimplemented unless explicitly identified as current prototype evidence.

## Compatibility principle

`SslClientAuthenticationOptions` and `SslServerAuthenticationOptions` remain the semantic source of truth. A runtime-owned native transport must not create a smaller parallel TLS options model and call it equivalent.

For each configured property or callback, a provider must do one of the following:

1. implement the same observable behavior;
2. use the documented `Socket`/`SslStream` compatibility implementation when provider selection is automatic;
3. reject the configuration with a precise `NotSupportedException` when the provider was explicitly selected.

Silently ignoring a property, callback, validation decision, or offload requirement is not permitted.

## Current Kestrel surface

At aspnetcore revision `67a02a66a21ac4f2e8ab19201b59cf45e97c8c98`, `HttpsConnectionAdapterOptions` exposes:

- `ServerCertificate`;
- `ServerCertificateChain`;
- `ServerCertificateSelector`;
- `ClientCertificateMode`;
- `ClientCertificateValidation`;
- `SslProtocols`;
- `CheckCertificateRevocation`;
- `AllowAnyClientCertificate()`;
- `OnAuthenticate`;
- obsolete `TlsClientHelloBytesCallback`;
- `HandshakeTimeout`.

Kestrel also exposes `TlsHandshakeCallbackOptions.OnConnection`, an asynchronous callback returning `SslServerAuthenticationOptions`. Its context contains the actual `SslStream`, parsed `SslClientHelloInfo`, state, cancellation token, ASP.NET Core `ConnectionContext`, and the delayed-client-certificate switch.

`OnAuthenticate` is invoked after Kestrel has populated `SslServerAuthenticationOptions` and before `SslStream.AuthenticateAsServerAsync`. The callback can therefore alter every public property on that options type.

Kestrel's SNI configuration path creates and clones `SslServerAuthenticationOptions`, including the current properties `AllowRenegotiation`, `AllowTlsResume`, `ApplicationProtocols`, `CertificateChainPolicy`, `CertificateRevocationCheckMode`, `CipherSuitesPolicy`, `ClientCertificateRequired`, `EnabledSslProtocols`, `EncryptionPolicy`, `RemoteCertificateValidationCallback`, `ServerCertificate`, `ServerCertificateContext`, `ServerCertificateSelectionCallback`, `AllowRsaPssPadding`, and `AllowRsaPkcs1Padding`.

These facts are pinned in [S-KESTREL-HTTPS](sources.md#s-kestrel-https) and [S-RUNTIME-TLS](sources.md#s-runtime-tls).

## Proposed handshake order

An accepted server connection carries `TransportServerTlsOptions` into the provider-driven handshake. The provider uses `ClientHelloTimeout` for the first-record/callback phase and `HandshakeTimeout` for the remaining handshake. This preserves Kestrel's two timeout phases without a separate public observation operation.

For a server connection:

1. The TCP connection is accepted and assigned stable connection identity.
2. The provider reads enough ciphertext to identify the first TLS record and parse `SslClientHelloInfo`.
3. If configured, `TransportClientHelloCallback` runs with the borrowed first-record bytes.
4. `TransportServerOptionsSelectionCallback` runs with a per-connection clone of the configured default `SslServerAuthenticationOptions`.
5. The provider validates every resulting option against the chosen TLS implementation.
6. The provider acquires or selects credentials and drives the TLS handshake.
7. Remote client-certificate validation runs when the peer certificate is available.
8. Offload is attempted after protocol and cipher negotiation, when the provider has the key material and knows eligibility.
9. A `Require` offload policy is enforced.
10. Immutable `TransportTlsInfo` is published.
11. The authentication operation completes and application reads/writes may begin.

For a client connection:

1. TCP connect completes.
2. The provider snapshots `SslClientAuthenticationOptions`.
3. It validates target-host and provider support.
4. It drives TLS authentication, including local certificate selection and remote certificate validation.
5. It attempts requested offload after negotiation.
6. It publishes `TransportTlsInfo`.
7. Application reads/writes may begin.

No application plaintext is exposed before successful authentication.

## No raw-byte `UseHttps` middleware boundary

The proposed transport does not promise today's Kestrel middleware composition where arbitrary middleware can read raw TCP bytes before `UseHttps` wraps an `IDuplexPipe`.

When a listener or connect request enables TLS:

- the provider owns the socket from accept/connect onward;
- fd-bound OpenSSL may consume the ClientHello directly through its socket BIO;
- the only user-visible pre-ready bytes are the borrowed first-record bytes in `TransportClientHelloCallback`;
- `OnReady` and all receive callbacks expose authenticated plaintext;
- Kestrel must configure TLS when it creates/binds the transport listener, not attach it later as an independent raw-byte wrapper.

ASP.NET Core may retain familiar configuration syntax by translating endpoint HTTPS configuration into `TransportServerTlsOptions` before binding. That would be configuration compatibility, not middleware/byte-stream compatibility.

## Callback execution contract

All proposed TLS callbacks have these semantics:

- **Timing:** during authentication and before the authentication operation completes.
- **Concurrency:** serialized for one connection; callbacks for different connections may run concurrently.
- **Scheduler:** not on the backend's poll/completion loop and not while a provider-global lock or TLS-session lock is held.
- **Lifetime:** borrowed buffers and callback context are valid until the returned `ValueTask` completes.
- **Cancellation:** the supplied token represents handshake timeout, caller cancellation, listener shutdown, or provider shutdown.
- **Reentrancy:** metadata reads are allowed; read, write, another authentication, shutdown, abort, or disposal on the same connection is rejected.
- **Exceptions:** any exception fails that connection's handshake. It never causes plaintext fallback and must not terminate a shared backend loop.
- **Resumption:** if the native TLS library suspends its handshake for user work, the provider records an explicit continuation identity and resumes only on the owning I/O context.

The current DirectTLS callback dispatcher demonstrates one viable mechanism: remove the fd from epoll, run user code on the thread pool, queue the result, wake the pump through `eventfd`, verify the pending-callback identity, and then re-arm and resume. This is implementation evidence, not a required API shape.

## Callback parity matrix

Legend:

- **Exact:** the backend can preserve the current observable contract.
- **Adapt:** feasible through a runtime-owned adapter, but requires implementation work.
- **Fallback:** use the managed `Socket`/`SslStream` path for exact compatibility.
- **Reject:** explicit provider selection must fail rather than ignore the feature.

| Existing Kestrel hook | Current timing and data | Lifetime, async, reentrancy, and error behavior to preserve | Proposed mapping | Managed `Socket` + `SslStream` | epoll + fd-bound OpenSSL | io_uring + memory BIO | io_uring + fd-bound OpenSSL | Windows IOCP + Schannel |
|---|---|---|---|---|---|---|---|---|
| `ServerCertificate` | Before handshake; fixed certificate | Kestrel snapshots/creates certificate context before serving connections | `AuthenticationOptions.ServerCertificate` | Exact | Adapt | Adapt | Adapt | Adapt through runtime Schannel policy |
| `ServerCertificateChain` | Before handshake; additional chain passed into `SslStreamCertificateContext.Create` | Chain/context must remain valid for credential lifetime | Convert to `ServerCertificateContext` before authentication | Exact | Adapt; native context must retain chain safely | Adapt through shared runtime TLS policy | Adapt | Adapt |
| `ServerCertificateSelector` | During handshake after SNI is known; synchronous return | May run per connection; exception fails handshake; selected certificate must be retained by credential context | Existing `SslServerAuthenticationOptions.ServerCertificateSelectionCallback`, or the broader async options callback | Exact | Adapt through OpenSSL ClientHello/certificate callback; dispatch user code off poll loop | Adapt | Adapt; suspend and resume fd-bound handshake | Adapt through runtime Schannel credential selection |
| `OnAuthenticate` | After Kestrel applies its defaults, immediately before authentication | Synchronous mutation of per-connection `SslServerAuthenticationOptions`; exception fails handshake | Kestrel builds the default options, invokes `OnAuthenticate`, then supplies the result to runtime | Exact | Exact only if every resulting property is supported; otherwise fallback/reject | Same | Same | Same, with current Windows platform limitations |
| `TlsHandshakeCallbackOptions.OnConnection` | After ClientHello parse; async; returns complete `SslServerAuthenticationOptions` | Receives actual `SslStream`, `SslClientHelloInfo`, state, cancellation, Kestrel connection, and delayed-cert flag | Kestrel can map the option result, but an actual `SslStream` requirement selects the `SslStream` path | Exact | **Fallback/Reject:** a native session cannot truthfully provide `TlsHandshakeCallbackContext.SslStream` | Fallback unless implemented through actual `SslStream` | Fallback/Reject | Exact only on actual `SslStream`; raw Schannel provider cannot fake it |
| Obsolete `TlsClientHelloBytesCallback` | Inside the HTTPS handshake timeout; synchronous; first TLS record bytes | `ReadOnlySequence<byte>` borrowed; callback must copy retained bytes; exception fails connection | `TransportServerTlsOptions.ClientHelloCallback` | Exact through the `SslStream` compatibility strategy | Adapt through the TLS engine's ClientHello callback | Exact through ciphertext input before BIO feed | Adapt through the fd-bound ClientHello callback | Adapt before/inside the first `AcceptSecurityContext` phase |
| `UseTlsClientHelloListener` | Connection middleware before `UseHttps`; currently uses the same first-record parser | Has a separate timeout additive to the HTTPS handshake timeout; borrowed bytes; callback is synchronous today | The same handshake callback with `ClientHelloTimeout`, followed by `HandshakeTimeout` for the remaining authentication | Exact | Adapt by suspending the fd-bound handshake callback | Exact | Adapt through poll-driven callback suspension | Adapt through retained first-token state |
| `ClientCertificateValidation` | During handshake after a client certificate and chain are available | Synchronous boolean decision; exception fails handshake | `SslServerAuthenticationOptions.RemoteCertificateValidationCallback` | Exact | Adapt through shared runtime validation policy | Adapt | Adapt | Adapt; current Schannel path already does manual server-side client-cert validation |
| `AllowAnyClientCertificate()` | Configuration-time convenience that replaces the validation callback with one that returns `true` | The resulting callback is still invoked through the normal validation path | Kestrel performs the same replacement before supplying runtime options | Exact | Adapt | Adapt | Adapt | Adapt |
| `ClientCertificateMode.NoCertificate` | No client certificate requested | No post-handshake request | `ClientCertificateRequired = false`; delayed flag false | Exact | Adapt | Adapt | Adapt | Adapt |
| `ClientCertificateMode.AllowCertificate` | Request but tolerate absence | Callback sees certificate when present; absence accepted | `ClientCertificateRequired = true` plus Kestrel validation adapter | Exact | Adapt | Adapt | Adapt | Adapt |
| `ClientCertificateMode.RequireCertificate` | Request and reject absence | Missing certificate fails handshake | Same mapping plus required-presence validation | Exact | Adapt | Adapt | Adapt | Adapt |
| `ClientCertificateMode.DelayCertificate` | Initial handshake omits client cert; application may request later | Kestrel forbids it for HTTP/2 and calls `SslStream.NegotiateClientCertificateAsync` on supported platforms | `AllowPostHandshakeClientAuthentication` plus a higher-level Kestrel/TLS feature adapter; the low-level callback connection method is not yet shaped | Exact subject to current OS support | Fallback/Reject until native PHA or renegotiation is implemented | Possible but unimplemented; provider must handle protocol/version semantics | Fallback/Reject, especially with kTLS | Adapt through runtime Schannel; no claim until tested |
| Handshake timeout | Bounds all handshake I/O and callbacks | Cancellation must not free native state before terminal completion | Caller cancellation token; Kestrel owns its timeout CTS | Exact | Adapt | Adapt | Adapt | Adapt |
| ALPN | Configured before handshake; selected protocol published after success | Byte value valid for connection lifetime | Existing `ApplicationProtocols`; publish `TransportTlsInfo.ApplicationProtocol` | Exact | Adapt | Adapt | Adapt | Adapt |
| TLS metadata | Protocol, cipher suite, host name, remote certificate, channel binding | Snapshot before TLS state disposal when later access is required | `TransportTlsInfo` and Kestrel feature adapter | Exact | Adapt; channel binding requires runtime native support | Adapt | Adapt | Adapt |
| `ISslStreamFeature` | Exposes the actual `SslStream` instance | Callers may depend on `SslStream`-specific members | Publish only when the TLS implementation is actually `SslStream` | Exact | Reject/fallback; never fake | Available only if this path is implemented through `SslStream` | Reject/fallback | Available only for `SslStream` path |

For Kestrel's normal HTTPS path, configuring `ServerCertificateSelector` causes the fixed `ServerCertificate` to be ignored. A selector returning `null` does not fall back to that fixed certificate; authentication has no selected certificate and fails. The current DirectTLS factory instead uses `selector(...) ?? ServerCertificate`, so its fallback behavior is observably different. A runtime transport adapter targeting Kestrel parity must follow the normal Kestrel behavior unless Kestrel deliberately changes its public contract.

## Current DirectTLS parity gap

The current experimental DirectTLS source is valuable implementation evidence, but its endpoint options are not a replacement for Kestrel's established HTTPS surface.

| Area | Normal Kestrel HTTPS | Current DirectTLS study |
|---|---|---|
| Fixed certificate | Supported | Supported |
| Additional certificate chain | `ServerCertificateChain` -> `SslStreamCertificateContext` | No corresponding endpoint option |
| Selector null behavior | No fixed-certificate fallback once selector is configured | Falls back to fixed certificate |
| Per-connection full options | `OnAuthenticate` and async `TlsHandshakeCallbackOptions` | No equivalent full-options callback |
| Cipher suites and chain policy | Reachable through `SslServerAuthenticationOptions` | No endpoint option |
| Revocation policy | `CheckCertificateRevocation` plus full runtime enum through callback | No endpoint option |
| Resumption, renegotiation, encryption policy, RSA padding | Reachable through `SslServerAuthenticationOptions` | No endpoint option |
| Raw first ClientHello record | Supported | Supported through runtime session capture |
| Client certificate allow/require | Supported | Supported |
| Delayed client certificate | Supported through `SslStream` where the OS supports it | Explicitly rejected |
| Channel binding | Exposed through `ITlsConnectionFeature` | No implementation in the inspected feature |
| Actual `SslStream` feature | Exposed | Cannot be exposed truthfully |

The runtime proposal closes these gaps through shared option normalization, fallback, and explicit incompatibility rather than by expanding another ASP.NET-specific options type.

### Important current raw ClientHello limitation

At the pinned ASP.NET Core revision, both the obsolete property and `UseTlsClientHelloListener` use the same `TlsListener`. That parser waits for and slices exactly the first TLS record (`5 + recordLength`) and then exits. It does not aggregate a ClientHello handshake message spanning several TLS records.

The obsolete property's remarks suggest the replacement for a fragmented ClientHello, but the inspected replacement still uses the same implementation. The proposal therefore does not claim complete-message parity. It names the payload `FirstRecordBytes` and adds `ContainsCompleteClientHello`.

For JA4-style processing:

- extension order and GREASE values require wire-representative data;
- OpenSSL's parsed ClientHello accessors can expose many raw fields and, in OpenSSL 3.2+, extension order, but the API explicitly omits unrecognized extensions from one accessor and does not expose the original complete record sequence as a single object;
- a native provider must use its transport-owned ciphertext, a runtime TLS capture hook, or a carefully specified pre-read/peek path if exact first-record bytes are required;
- a provider must not reconstruct "raw" bytes from parsed fields and call that parity.

## Server option parity matrix

This matrix covers every public property in the pinned `SslServerAuthenticationOptions` surface plus the Kestrel-specific inputs that build it.

| Option | Required semantic | Native provider rule | Known limitation or fallback trigger |
|---|---|---|---|
| `ServerCertificate` | Present the specified certificate | Install per-connection credentials or derive a native credential context | Certificate without a private key may require the runtime's existing store lookup behavior; do not reimplement incompletely |
| `ServerCertificateContext` | Preserve target certificate, intermediates, and prepared context semantics | Reuse runtime-owned credential preparation | External native libraries cannot inspect private runtime context state; runtime ownership is required |
| `ServerCertificateSelectionCallback` | Invoke with SNI and use returned certificate | Use early ClientHello/cert callback and suspend handshake if user work is dispatched | Callback ordering relative to resumption and ALPN must match; old OpenSSL servername callback alone is too late for some decisions |
| `ClientCertificateRequired` | Request peer certificate and enforce configured absence policy | Configure handshake and validation path | `AllowCertificate` versus `RequireCertificate` remains Kestrel adaptation logic |
| `RemoteCertificateValidationCallback` | Provide certificate, chain, and `SslPolicyErrors`; honor boolean result | Use runtime certificate validation and then invoke callback | Native backend must not reduce validation to "OpenSSL verify succeeded" because custom chain policy and callback semantics would be lost |
| `ApplicationProtocols` | Negotiate ALPN in list order according to current runtime semantics | Configure provider and publish exact selected bytes | No overlap must follow provider/runtime behavior; do not silently select a default |
| `EnabledSslProtocols` | Preserve OS-default behavior for `None`; otherwise constrain versions | Map to provider min/max or exact version set | A provider unable to represent a non-contiguous set must reject/fallback |
| `CertificateRevocationCheckMode` | Apply current runtime revocation policy | Reuse runtime chain engine or equivalent | Network access and platform behavior differ; parity means current platform behavior, not uniformity |
| `CertificateChainPolicy` | Clone and apply custom trust, policy OIDs, verification flags, and stores | Reuse runtime validation | A native library's default verify callback is insufficient |
| `CipherSuitesPolicy` | Constrain cipher suites where the platform supports it | Map exact allowed list | Current Windows PAL throws `PlatformNotSupportedException`; a Windows provider may preserve that behavior |
| `EncryptionPolicy` | Preserve required/allowed/no-encryption behavior supported by runtime | Map or reject | Obsolete no-encryption modes should not be newly emulated if the provider cannot support them |
| `AllowRenegotiation` | Preserve TLS 1.2 renegotiation behavior and TLS 1.3 post-handshake processing | Keep userspace TLS when required | kTLS documentation identifies renegotiation limitations; do not activate incompatible offload |
| `AllowTlsResume` | Enable or disable session resumption according to runtime semantics | Configure session cache/tickets per connection/context | A provider without equivalent control must fallback/reject |
| `AllowRsaPssPadding` | Control RSA-PSS signature algorithms on supported OSes | Map through runtime TLS policy | Current setter support is Windows/Linux; preserve platform exception behavior elsewhere |
| `AllowRsaPkcs1Padding` | Control RSA-PKCS#1 signature algorithms on supported OSes | Map through runtime TLS policy | Same platform constraint |
| Kestrel `ServerCertificateChain` | Include supplied intermediate chain | Convert once to `SslStreamCertificateContext` or equivalent runtime credential | DirectTLS's current endpoint options do not expose this property |
| Kestrel `CheckCertificateRevocation` | Map bool to Online/NoCheck | Kestrel performs mapping before runtime call | The richer runtime enum remains available through `OnAuthenticate`/options callback |
| Kestrel `AllowAnyClientCertificate()` | Replace custom validation with an accept callback | Kestrel performs the replacement before runtime call | This is convenience API, not a native TLS capability |

## Client option parity matrix

| Option | Required semantic | Backend notes |
|---|---|---|
| `TargetHost` | Drive certificate name validation; normally drive SNI for DNS names | The proposal does not infer that an empty target intentionally disables name validation. Client examples set it explicitly. |
| `RemoteCertificateValidationCallback` | Receive peer certificate, chain, and policy errors; return final decision | Managed `SslStream` is exact. Native providers must use runtime validation and preserve callback errors. |
| `LocalCertificateSelectionCallback` | Select a client certificate when requested by the server | Requires handshake suspension/resumption in native providers. |
| `ClientCertificates` | Candidate client certificate collection | Provider must preserve selection behavior or fallback. |
| `ClientCertificateContext` | Prepared client credential context | Requires runtime-owned TLS integration; third-party providers cannot safely inspect private representation. |
| `ApplicationProtocols` | Offer ALPN in caller order and publish selection | All proposed TLS engines can represent ALPN, subject to implementation. |
| `CertificateRevocationCheckMode` | Apply current platform revocation behavior | Native providers should reuse runtime chain validation. |
| `CertificateChainPolicy` | Apply custom trust and verification flags | Native TLS library verification alone is not equivalent. |
| `CipherSuitesPolicy` | Apply on supported platforms | Windows currently does not support this policy through the runtime PAL. |
| `EnabledSslProtocols` | Preserve OS-default or explicit versions | Provider must reject unrepresentable sets. |
| `EncryptionPolicy` | Preserve current semantics | Do not silently coerce. |
| `AllowRenegotiation` | Permit relevant TLS 1.2 behavior | May disable kTLS eligibility. |
| `AllowTlsResume` | Control session resumption | Requires provider session cache/ticket integration. |
| `AllowRsaPssPadding`/`AllowRsaPkcs1Padding` | Preserve current supported-platform behavior | Runtime-owned policy avoids duplicated platform checks. |

## Native option validation strategy

The runtime should implement one internal option normalizer used by all TLS engines:

1. Clone the public options.
2. Resolve certificate contexts and private keys using existing runtime logic.
3. Normalize target host and SNI.
4. Build or validate chain policy.
5. Validate platform restrictions.
6. Produce an internal immutable handshake policy.
7. Ask the selected provider whether it can implement that policy.
8. Select native mode, use `SslStream` fallback, or throw.

The TLS engine owns the first-record state throughout callback and authentication. The transport must not consume the ClientHello outside the handshake or reconstruct raw bytes from parsed fields.

## Certificate and callback data lifetime

- The caller keeps the supplied authentication options and certificate objects valid until the authentication operation completes. The provider snapshots mutable option values and retains or creates the credential state needed afterward.
- A certificate, chain, and `SslPolicyErrors` supplied to a validation callback are borrowed for that callback. Code that needs them later clones the certificate and builds its own retained state.
- A certificate returned from a selection callback remains caller-owned. The provider retains the native credential/reference it needs; it does not assume ownership of and dispose the caller's certificate object.
- `TransportTlsInfo.RemoteCertificate` is owned by the connection and remains valid until connection disposal. Consumers clone it if it must outlive the connection and do not dispose the connection-owned instance.
- ClientHello bytes supplied to either callback are borrowed only until that callback's `ValueTask` completes. The provider owns any cached copy required to resume authentication.
- `SslServerAuthenticationOptions` returned from the options callback is snapshotted before the callback's borrowed context is released. Mutating it afterward has no effect on the active connection.

This avoids each transport independently interpreting certificates, revocation, cipher policy, and callback precedence.

## fd-bound OpenSSL contract

Source basis: [OpenSSL 3.5.2 contracts](sources.md#s-openssl) and the [current DirectTLS study](sources.md#s-directtls).

OpenSSL documents that:

- `SSL_ERROR_WANT_READ` and `SSL_ERROR_WANT_WRITE` are retry states;
- any TLS operation may want the opposite direction;
- a write retried after `WANT_*` must use the same data and length unless the moving-buffer mode is enabled;
- `SSL_get_error` must be called on the same thread as the operation, with no intervening OpenSSL call that contaminates the error queue;
- socket BIO readiness can be driven by `poll`/epoll;
- fatal `SSL_ERROR_SSL` or `SSL_ERROR_SYSCALL` prohibits further I/O and `SSL_shutdown`.

Therefore the provider contract is:

- exactly one owner executes an `SSL*` operation and immediately classifies its result;
- the TLS session is serialized across handshake, read, write, shutdown, and callback-resume transitions;
- read interest is the union of "application read wants read" and "application write wants read";
- write interest is the union of "application write wants write" and "application read wants write";
- a pending write retains the same logical bytes until OpenSSL reports completion;
- fatal status marks the connection terminal before any next operation;
- user callbacks suspend the handshake without leaving a hot level-triggered fd armed.

Illustrative state transition:

```text
SSL_read:
  > 0                 -> complete read
  WANT_READ           -> arm/read interest
  WANT_WRITE          -> arm/write interest
  ZERO_RETURN         -> clean TLS inbound close
  SSL/SYSCALL fatal   -> abort; do not call SSL_shutdown

SSL_write:
  success             -> source released
  WANT_WRITE          -> retain exact remaining source; arm/write interest
  WANT_READ           -> retain exact remaining source; arm/read interest
  ZERO_RETURN/fatal   -> terminal
```

The current DirectTLS `ConnectionIoState` is evidence for the union-of-interests requirement and the need to serialize a single OpenSSL session while permitting an application read and write to be pending.

## Memory-BIO TLS contract

Source basis: [runtime `SslStream` PAL](sources.md#s-runtime-tls), [OpenSSL BIO ownership](sources.md#s-openssl), and the [SocketSet memory-BIO study](sources.md#s-socketset).

A memory-BIO TLS engine does not own the socket. The transport:

1. receives ciphertext through its normal async backend;
2. feeds ciphertext into the TLS read BIO;
3. calls handshake/read until it needs more input or produces plaintext;
4. drains TLS output BIO ciphertext into provider writes;
5. retains partial records in the TLS engine;
6. serializes TLS state access on the connection owner.

This model preserves io_uring multishot ciphertext receive and works with Schannel's analogous input-token/output-token model. Its costs may include ciphertext staging and plaintext/ciphertext copies. Those are measurement questions, not reasons to reject the model before testing it.

## kTLS and hardware offload

Source basis: [Linux v6.12 TLS and TLS-offload documentation](sources.md#s-linux) and [OpenSSL 3.5.2 kTLS documentation](sources.md#s-openssl).

### Terms

- **Requested:** the caller set `Disabled`, `Prefer`, or `Require` independently for TX and RX.
- **Negotiated:** TLS completed with a protocol, cipher suite, extensions, and keys.
- **Kernel record layer active:** the provider verified that TX or RX is actually using kTLS.
- **Hardware TLS active:** a capable NIC/driver is processing crypto for that connection and direction.
- **Transport copy avoidance:** an I/O path avoided copying application buffers; this is independent of where TLS crypto runs.

### Activation

The handshake remains in userspace. After negotiation, the provider attempts to install TX and RX state independently. OpenSSL's `BIO_get_ktls_send` and `BIO_get_ktls_recv` report whether its BIO actually uses the kernel data path. A configuration bit or successful handshake is not evidence of activation.

`Prefer` behavior:

- if a direction activates, report it;
- if it does not, continue with userspace record protection and report a fallback reason;
- preserve all configured TLS semantics.

`Require` behavior:

- if the requested direction does not activate, fail authentication and close the connection before application data;
- do not degrade to userspace TLS.

### Eligibility and fallback reasons

Activation can be prevented by:

- OpenSSL built without kTLS support;
- unsupported operating system;
- kernel without the TLS ULP or required capability;
- negotiated protocol or cipher not supported by the kernel/provider combination;
- a configured TLS behavior incompatible with kTLS, such as required renegotiation;
- provider integration that cannot safely service control records or key updates;
- provider policy;
- an unclassified native failure.

TX and RX can differ. Reporting a single `IsKtlsEnabled` boolean is therefore insufficient.

### Control records and TLS 1.3

Linux kTLS returns control-message record types through ancillary data on `recvmsg`. TLS 1.3 KeyUpdate can pause RX decryption until userspace installs the new key. A provider has two credible models:

- continue calling the TLS library's `SSL_read`, allowing the library to process control records and update kernel state, driven by readiness;
- use control-message-aware receive and explicitly route control records through the TLS engine.

The second model may permit io_uring multishot `recvmsg`, but it is not assumed correct until implemented and tested. The first model is simpler and is demonstrated by SocketSet, but it can sacrifice multishot receive.

### Hardware TLS

Linux distinguishes software kTLS, packet-based NIC TLS offload, and full TCP NIC offload. Device mode is selected automatically from kernel and device configuration; the kernel documentation says per-connection opt-in/out for device mode is not currently supported.

The runtime therefore:

- may request kTLS record-layer use;
- does not configure the NIC;
- does not promise hardware acceleration;
- reports `Active` only with reliable evidence;
- reports `Unknown` when it can prove kTLS but cannot attribute crypto per connection;
- leaves detailed aggregate counters and driver diagnostics to EventSource/counters/platform tooling.

### Ordinary NIC offloads

Checksum offload, TSO, GSO, and GRO are normal TCP stack behaviors and do not require a transport API switch. A native provider should avoid disabling them accidentally, but the API does not promise that a given path, payload, virtual switch, tunnel, or NIC uses hardware acceleration.

### Zero-copy wording

The proposal does not call kTLS "zero-copy." Linux documents specific copy avoidance and a separate `TLS_TX_ZEROCOPY_RO` optimization for device-offloaded `sendfile`. A normal `send`, `writev`, pipeline flush, or userspace TLS transform can still copy or pin data. Each optimization must report and measure its own path.

## Kestrel feature mapping after authentication

| Kestrel feature | Runtime source | Mapping |
|---|---|---|
| `ITlsConnectionFeature.ClientCertificate` | `TransportTlsInfo.RemoteCertificate` on server connections | Connection-owned certificate, valid through connection lifetime; clone to retain |
| `ITlsConnectionFeature.GetClientCertificateAsync` | Higher-level Kestrel/TLS adapter over a provider-internal post-handshake operation when delayed mode is enabled | The public low-level invocation shape remains open; unsupported providers throw or select fallback |
| `ITlsConnectionFeature.TryGetChannelBindingBytes` | `TransportTlsInfo.TryGetChannelBindingBytes` | Return false when provider/connection cannot supply requested binding |
| `ITlsHandshakeFeature.Protocol` | `TransportTlsInfo.Protocol` | Exact |
| `ITlsHandshakeFeature.NegotiatedCipherSuite` | `TransportTlsInfo.NegotiatedCipherSuite` | Null only when provider cannot report it |
| `ITlsHandshakeFeature.HostName` | `TransportTlsInfo.ServerName` | Empty string adapter for Kestrel's non-null property |
| `ITlsHandshakeFeature.Exception` | Authentication/connection terminal error | Snapshot before disposal |
| `ITlsApplicationProtocolFeature.ApplicationProtocol` | `TransportTlsInfo.ApplicationProtocol.Protocol` | Exact bytes |
| `ISslStreamFeature` | Actual `SslStream` implementation only | Do not publish for native TLS |
| `IConnectionSocketFeature` | Actual managed `Socket` only | Do not reconstruct a second wrapper for native handles |

## Irreconcilable tradeoffs

### Native TLS versus `SslStream` object identity

An application callback that requires the actual `SslStream` cannot run unchanged on an fd-bound OpenSSL or raw Schannel implementation. A facade would not preserve stream identity, transport context, channel bindings, virtual overrides, or implementation-specific behavior.

The only faithful choices are:

- select the `SslStream` compatibility provider;
- reject explicit native-provider configuration.

### kTLS versus unrestricted TLS semantics

kTLS is not merely a faster implementation of every userspace TLS feature. If a configured option requires a behavior the kernel record layer cannot preserve, `Prefer` falls back and `Require` fails. The design does not silently sacrifice callbacks, renegotiation, key updates, validation, or record behavior to claim offload.

### Raw bytes versus parsed ClientHello

Parsed TLS-library callbacks may be enough for SNI or cipher decisions, but they are not equivalent to raw first-record bytes for fingerprinting. Conversely, first-record bytes are not a guarantee of a complete ClientHello. The API reports the distinction instead of conflating them.

## TLS acceptance tests

The shared suite must run each supported backend through:

- static certificate and chain;
- SNI certificate selection;
- async options selection;
- `OnAuthenticate` adaptation of every current server option;
- ALPN success and no-overlap behavior;
- protocol restrictions;
- revocation and custom chain policy;
- cipher policy on supported platforms and expected platform rejection elsewhere;
- optional and required client certificates;
- custom client certificate validation, including exception;
- delayed client certificate where supported and explicit rejection elsewhere;
- client target-host validation, custom remote validation, and local certificate selection;
- session resumption enabled/disabled where observable;
- callback cancellation, callback exception, and concurrent handshakes;
- raw first-record callback lifetime, callback throw, and fragmented ClientHello reporting;
- separate pre-observation and handshake timeout behavior, including both callbacks configured on one connection;
- channel binding availability;
- clean `close_notify`, peer FIN without `close_notify`, abort, and final buffered plaintext;
- offload disabled, preferred unavailable, preferred TX-only, preferred TX+RX, and required failure;
- no application plaintext before all required authentication and offload checks complete.
