# Custom BIO with kernel TLS: single-connection console demonstration

This program accepts **one** TCP connection on loopback, negotiates TLS, reads one newline-terminated message, echoes it using the same application buffer, sends `close_notify`, and exits. It is not an HTTP server or throughput benchmark. There is no io_uring in this demonstration: ordinary nonblocking sockets and `poll()` make the BIO/kTLS boundary easier to see.

## Current validation status

**Verified on 2026-09-26 after the user loaded the TLS kernel module.** Both the ordinary build and the AddressSanitizer/UndefinedBehaviorSanitizer build passed the userspace control and required-kTLS exchange with system OpenSSL 3.0.13:

| Mode | kTLS RX | kTLS TX | Application bytes echoed | Post-handshake kTLS BIO reads |
|---|---:|---:|---:|---:|
| Userspace control | 0 | 0 | 29 | 0 |
| Custom filter + socket BIO with kTLS | 1 | 1 | 29 | 1 |

The kernel's cumulative `TlsRxSw` and `TlsTxSw` counters each increased from 0 to 1 in the first required-kTLS run. Device-offload counters remained zero; current-session counters returned to zero after cleanup. `TlsDecryptError` remained zero. The echo matched exactly. Raw evidence is in `results/20260926-220448/` and the instrumented run in `results/20260926-220534/`.

The observed custom BIO read returned 34 bytes for a 29-byte application message: OpenSSL's socket BIO prepended the five-byte synthetic record header described below. The BIO read destination and the application plaintext destination were different addresses. The filter itself made no payload copy; this is not proof of an end-to-end copy-free path.

Before the module was loaded, the required-kTLS run correctly failed with:

```text
TCP_ULP tls (kernel module/support): No such file or directory
```

The WSL kernel reports `CONFIG_TLS=m`; the user loaded the existing module with `sudo modprobe tls`. No module was loaded by the agent. The kernel also reports `CONFIG_TLS_DEVICE` unset; this is not a hardware/NIC TLS-offload demonstration.

The program deliberately fails if receive-side kTLS does not activate. A passing userspace echo is not reported as kTLS success. Inspect the actual activation/callback counters on each run rather than assuming a requested option activated.

## 1. What is a BIO?

A BIO is OpenSSL's I/O abstraction. OpenSSL uses a BIO to obtain incoming bytes and deliver outgoing bytes. A BIO can be:

- A source/sink, such as a socket BIO.
- A memory-backed source/sink, such as `BIO_s_mem()`.
- A filter in front of another BIO.
- An application-defined implementation registered through `BIO_METHOD`.

The BIO interface is synchronous. A callback either makes progress, reports EOF/error, or marks itself retryable. It does not return an awaitable or wait for our managed task scheduler. The caller of `SSL_read_ex`, `SSL_write_ex`, or `SSL_do_handshake` interprets `SSL_get_error` and retries after the required I/O condition.

Ordinary TLS BIOs handle **ciphertext below the TLS record layer**. With kTLS enabled, the socket BIO participates in OpenSSL's special kernel-record-layer integration. A custom BIO is not automatically a plaintext-buffer provider just because it can access a socket.

## 2. The exact architecture in this sample

```text
Application: SSL_do_handshake / SSL_read_ex / SSL_write_ex
                         |
                  Our custom filter BIO
                  - observes calls
                  - forwards the same buffer pointers
                  - forwards control operations
                  - propagates retry flags
                         |
                OpenSSL's built-in socket BIO
                  - owns socket-specific behavior
                  - enables/checks kTLS state
                  - handles TLS record control messages
                         |
                   Kernel TCP / kTLS
                         |
                       Peer
```

**This is a real custom BIO, but it does not replace all socket-BIO machinery.** It delegates that machinery to the existing socket BIO. It proves the intended composition when kTLS activation succeeds; it does not prove that a buffer-only BIO can use kTLS or that removing OpenSSL's socket BIO is desirable.

There is no `SSL_set_fd()` call in the program. Instead, `main` constructs a chain:

```c
filter = BIO_new(method);
socket_bio = BIO_new_socket(fd, BIO_NOCLOSE);
BIO_set_data(filter, &state);
BIO_push(filter, socket_bio);
SSL_set_bio(ssl, filter, filter);
```

`BIO_new_socket` still binds the lower BIO to the accepted fd. Not calling `SSL_set_fd` does not mean the design is independent of socket descriptors.

## 3. Which API does our custom BIO implement?

`BIO_meth_new` creates the method table. `BIO_get_new_index() | BIO_TYPE_FILTER` gives this filter a distinct type. The program registers the following callbacks:

| Registration API | Our callback | Responsibility |
|---|---|---|
| `BIO_meth_set_create` | `filter_create` | Initialize the BIO object. |
| `BIO_meth_set_destroy` | `filter_destroy` | Clear the association with our observation state; do not free that stack-owned state. |
| `BIO_meth_set_read_ex` | `filter_read` | Read through the next BIO into the destination provided by OpenSSL. |
| `BIO_meth_set_write_ex` | `filter_write` | Write the source provided by OpenSSL through the next BIO. |
| `BIO_meth_set_ctrl` | `filter_ctrl` | Forward control operations to the socket BIO. |

The data callbacks have these shapes:

```c
int read_ex(BIO *bio, char *destination, size_t capacity, size_t *bytes_read);
int write_ex(BIO *bio, const char *source, size_t length, size_t *bytes_written);
long ctrl(BIO *bio, int command, long argument, void *pointer);
```

For `_ex` read/write callbacks, return 1 for success and 0 for failure/retry, with the byte count written through the output parameter. Retryability is represented by BIO flags; it is not equivalent to EOF.

`BIO_set_data`/`BIO_get_data` associate application state with the BIO. `BIO_next` finds the lower BIO. `BIO_clear_retry_flags` and `BIO_copy_next_retry` preserve the lower BIO's retry behavior. The callbacks also preserve `errno` across their bookkeeping so `SSL_get_error` sees the correct failure context.

For example, our read callback forwards exactly the same destination:

```c
int result = BIO_read_ex(BIO_next(bio), destination, capacity, bytes_read);
```

It does not allocate a staging buffer or call `memcpy` on the payload. The write callback similarly forwards the source pointer.

This is different from a buffer-referencing BIO: that BIO holds an existing received page and typically copies from it into the destination OpenSSL supplies. Our filter lets the next socket BIO fill the destination instead.

## 4. What needs to happen for kTLS?

The program requests kTLS before the handshake:

```c
SSL_CTX_set_options(ctx, SSL_OP_ENABLE_KTLS);
```

It limits this experiment to TLS 1.2, ECDSA, and AES-128-GCM, disables session resumption, and leaves read-ahead off to keep the initial kernel-record-layer handoff simple. This is a narrow supported-configuration probe, not a survey of all TLS versions/ciphers.

After accepting, it explicitly checks that attaching the TCP TLS ULP succeeds:

```c
setsockopt(fd, IPPROTO_TCP, TCP_ULP, "tls", sizeof("tls"));
```

That call enables the socket's TLS upper-layer facility. It **does not install keys or activate RX/TX encryption on its own**.

During the handshake, OpenSSL derives the TLS state and decides whether kTLS is usable. OpenSSL 3.0.13's handshake code sends kTLS setup/control operations through the configured BIO. Our `filter_ctrl` forwards them unchanged to the socket BIO. The socket BIO performs the corresponding kernel socket operations and tracks whether RX/TX are active.

This forwarding is why a filter can work without reimplementing OpenSSL's internal key-material/control ABI. The sample does not export keys, hard-code private BIO control numbers, or pretend unsupported controls succeeded.

After the handshake, the program checks both directions:

```c
int rx = BIO_get_ktls_recv(SSL_get_rbio(ssl));
int tx = BIO_get_ktls_send(SSL_get_wbio(ssl));
```

These queries reach the socket BIO through our filter. The required-kTLS mode fails unless `rx == 1`. It reports TX separately rather than assuming RX and TX activate together.

### What the read callback sees once kTLS RX is active

In the inspected OpenSSL 3.0.13 Linux implementation, the socket BIO's kTLS read path calls `recvmsg()`, obtains decrypted record data and ancillary record-type information, and reconstructs a small TLS-style record header for OpenSSL's record-layer interface.

Therefore the bytes returned through the custom BIO are not necessarily just the final application payload. The callback's byte count may include this synthetic record framing. `SSL_read_ex()` returns the actual plaintext application bytes to `main`.

The sample prints:

- `ktls_rx` and `ktls_tx`: actual BIO activation state.
- `custom_bio_ktls_calls`: successful custom-BIO reads whose lower BIO reports active kTLS RX, counted after the handshake.
- `custom_bio_ktls_bytes`: bytes returned at that BIO boundary, not necessarily equal to application payload length.
- `APPLICATION destination` and `last_bio_destination`: the addresses at two distinct boundaries. They need not be the same.
- `filter_copy_bytes=0`: our filter does not copy payload bytes. This is a statement about this callback's implementation, not an end-to-end copy counter.

## 5. What this does not prove

- It does not remove OpenSSL's socket BIO: that lower layer is deliberately reused.
- It does not turn `BIO_s_mem()` into a kTLS transport.
- It does not establish NIC-to-application zero-copy. Kernel buffers, socket-BIO record framing, and OpenSSL's final plaintext delivery can still involve storage/copies.
- It does not demonstrate arbitrary io_uring operations, multishot kTLS receives, TLS 1.3 key updates, hardware offload, or a production TLS provider.
- It does not establish better RPS. This program performs one exchange only.
- A custom source/sink which replaces the lower socket BIO would have to supply the relevant socket and record-control behavior itself; generic buffer callbacks alone are insufficient.

For a future PipeReader adapter, the straightforward consumption boundary remains the buffer supplied to `SSL_read_ex()`. The adapter can expose that plaintext storage without copying it into another pipe. That does not mean the custom BIO and application destination are the same buffer.

## 6. Run in Ubuntu WSL

Requirements: C compiler, make, OpenSSL development headers/libraries, `openssl` command-line client, and a kernel with usable software kTLS. The existing WSL environment has the userspace build dependencies.

If the TLS kernel module is not loaded, the user can load it for the current WSL kernel:

```bash
sudo modprobe tls
grep '^tls ' /proc/modules
cat /proc/net/tls_stat
```

This does not configure persistent module loading. If `modprobe` fails, keep the error and investigate that prerequisite rather than silently falling back to userspace TLS.

Run both the userspace control and required-kTLS mode:

```bash
cd ~/code/Network-Transport-Experiments/experiments/ktls-custom-bio
./run.sh
```

Or run only the userspace control while kTLS is unavailable:

```bash
./run.sh --userspace-only
```

The script generates a local P-256 test certificate, waits for the listener's `READY` message without consuming its sole accept, and connects with a certificate-verifying OpenSSL client. It sends a fixed line, compares the echoed bytes exactly, and records logs under ignored `results/`. Certificate/key files are also ignored. No root actions, system configuration changes, or kernel module loading are performed by the script.

The default two runs use ports 18970 and 18971; set `PORT` to change the starting port. Each server phase and the client have bounded timeouts.

For instrumented lifetime checks:

```bash
make sanitize
DEMO_BINARY=./bin/demo-sanitize ./run.sh
```

To step through it yourself in two terminals:

```bash
# Terminal 1, after generating the certificate with run.sh:
./bin/demo 18970 certs/cert.pem certs/key.pem

# Terminal 2:
openssl s_client -connect 127.0.0.1:18970 -servername localhost \
    -tls1_2 -cipher ECDHE-ECDSA-AES128-GCM-SHA256 \
    -CAfile certs/cert.pem -verify_hostname localhost -verify_return_error -quiet
# Type a line and press Enter.
```

## 7. Ownership and shutdown

`SSL_set_bio` transfers the chain to the `SSL` object. The same chain is used for reading and writing; it is owned once by SSL. `BIO_NOCLOSE` leaves the fd owned by `main`, which closes it after freeing SSL. The method table and stack-based observation state remain alive until the chain is destroyed.

OpenSSL operations clear the error queue before each attempt and call `SSL_get_error` immediately after failure. `WANT_READ` and `WANT_WRITE` are retried with the original operation/buffers after `poll()` permits progress. Normal completion sends `close_notify`; fatal TLS failures go directly to cleanup rather than attempting another TLS shutdown.

## Sources

- [OpenSSL BIO method API](https://docs.openssl.org/3.0/man3/BIO_meth_new/)
- [BIO chains and ownership](https://docs.openssl.org/3.0/man3/BIO_push/)
- [kTLS activation queries](https://docs.openssl.org/3.0/man3/BIO_ctrl/)
- [OpenSSL 3.0.13 socket BIO and kTLS controls](https://github.com/openssl/openssl/blob/openssl-3.0.13/crypto/bio/bss_sock.c)
- [OpenSSL 3.0.13 TLS 1.2 kTLS handoff](https://github.com/openssl/openssl/blob/openssl-3.0.13/ssl/t1_enc.c)
- [OpenSSL 3.0.13 kernel socket/record helpers](https://github.com/openssl/openssl/blob/openssl-3.0.13/include/internal/ktls.h)
- [Linux kTLS socket interface](https://docs.kernel.org/networking/tls.html)
