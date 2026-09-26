# io_uring HTTP and TLS experiments

The original `oneshot` and `multishot` folders contain plaintext C servers with the same bounded HTTP request framing and `Hello world\n` response. HTTP/1.1 connections persist unless a `Connection: close` token is present. HTTP/1.0 closes by default unless `keep-alive` is requested. Header names and connection tokens are case-insensitive; `close` wins over `keep-alive`. These are teaching examples, not general-purpose HTTP servers.

The separate `tls-*` folders add OpenSSL and compare different integration/event-loop architectures without replacing the plaintext examples. See [TLS architectures and results](#tls-architectures-and-results) for the measured comparisons, limitations, and commands. The cross-ecosystem implementation research is in [transport-framework-comparison.md](../../transport-framework-comparison.md).

## Files and reading order

- `oneshot/server.c`: complete standalone one-shot server.
- `multishot/server.c`: complete standalone multishot server.
- `oneshot.sh` / `multishot.sh`: build and run four pinned workers; Ctrl+C stops them.
- `wrk.sh`: connection-close load, on different cores from the server.
- `wrk-keepalive.sh`: persistent-connection load; no `Connection: close` header.
- `results/`: local raw load output and counters. This directory is gitignored and is not included in a fresh clone.
- `tls-bio/`, `tls-fd/`, and the `tls-fd-*` folders: independent TLS server variants described below.
- `wrk-tls.sh`: TLS close/keep-alive client.
- `tls-check.mjs`: TLS correctness checks, including forced retry/partial-send paths.

The two plaintext C implementations are duplicated so each can be read independently. Both now use multishot accept; receive mode is their only intentional behavior difference. `STATS` includes accept submissions and CQEs to show that a single accept submission services many connections.

## Run the plaintext servers in WSL

Tested in Ubuntu WSL2 with Linux `6.18.33.2-microsoft-standard-WSL2`. This example requires Linux 6.0+ and support for provided buffer rings and multishot receive. Unsupported features produce errors, not a fallback to one-shot.

```bash
cd ~/code/Network-Transport-Experiments/experiments/io-uring-http
./oneshot.sh 18080
# Or, after stopping it:
./multishot.sh 18080
```

In another WSL terminal:

```bash
cd ~/code/Network-Transport-Experiments/experiments/io-uring-http
./wrk.sh 60000 30s 1024 18080
# Long-lived HTTP/1.1 connections:
./wrk-keepalive.sh 60000 30s 1024 18080
```

The launchers use four separate server processes sharing the listen port with `SO_REUSEPORT`, pinned to CPUs `0,2,4,6`. wrk2 uses `8,10,12,14`. These are disjoint physical cores on this WSL machine. Override the comma-separated `SERVER_CPUS` and `CLIENT_CPUS` variables for other machines, consistently in both terminals; `wrk.sh` rejects overlap including SMT siblings. Affinity is not exclusive machine reservation: Windows, WSL, kernel work, other applications, and shared caches still affect results.

Both wrk scripts take offered RPS, duration, connections, and port, defaulting to `30000 30s 1024 18080`. Set `WRK2` to override the client executable. They print the complete wrk2 results without writing new measurement files. RPS is whole-run throughput, including startup/calibration; latency histograms are reset by wrk2 after calibration.

### Dependencies

The prepared WSL environment already has local builds:

| Dependency | Revision / location |
|---|---|
| liburing 2.9 | `08468cc3830185c75f9e7edefd88aa01e5c2f8ab`, installed under `~/.local` |
| wrk2 | `44a94c17d8e6a0bac8559b53da76848e430cb7a7`, `~/code/wrk2/wrk`, plus the included clock patch |
| Compiler | GCC 13.3.0 |

For another WSL installation, first provide a C toolchain, make, Git, curl, util-linux (`taskset`), and wrk2's OpenSSL/zlib build dependencies. Then run:

```bash
./setup.sh
make sanitize
```

`make sanitize` builds separate AddressSanitizer/UndefinedBehaviorSanitizer binaries under `bin/sanitize`; it does not run load.

`setup.sh` builds pinned upstream sources under `~/code`, installs liburing to `~/.local`, applies `wrk2-monotonic.patch`, and builds the servers. It does not use sudo, modify APT configuration, or silently replace existing dependency checkouts at other revisions. `LIBURING_PREFIX` can override the Makefile's library location; it links liburing statically.

### Why patch wrk2?

The original client used wall-clock time for scheduling and latency. Pilot runs observed response timestamps earlier than the preceding send timestamps, and wrk2 aborted with a negative-latency histogram assertion. The included small patch uses `CLOCK_MONOTONIC` for wrk2 timing and its event-loop timers. It does not change HTTP behavior, rate targeting, or histogram recording. The Lua wall-clock helper is not used by these runs.

All recorded comparison runs use the same patched client. Crashed and preliminary unpatched runs are excluded from the comparison. This patch is local; nothing was submitted upstream.

## Plaintext receive-buffer ownership

At startup, each worker allocates 4,096 buffers of 4,096 bytes (16 MiB) and publishes them in an io_uring provided-buffer ring:

```text
application-owned pool
    -> io_uring selects a buffer
    -> receive completion identifies its ID and byte count
    -> application can read that buffer
    -> application republishes it only when finished
```

Both modes use this same pool. One-shot does not require provided buffers in general, but using them here isolates the multishot difference.

The relevant code is in `submit_receive`:

```c
// multishot/server.c:
io_uring_prep_recv_multishot(sqe, connection->fd, NULL, 0, 0);
// oneshot/server.c instead uses:
// io_uring_prep_recv(sqe, connection->fd, NULL, 0, 0);

sqe->flags |= IOSQE_BUFFER_SELECT;
sqe->buf_group = BUFFER_GROUP;
```

After a successful CQE:

```c
unsigned id = flags >> IORING_CQE_BUFFER_SHIFT;
/* storage + id * BUFFER_SIZE contains result received bytes. */
```

The server copies received bytes into a bounded 64 KiB per-connection input array, then calls `add_buffer(id)` to recycle the receive page. This intentionally simple framing storage handles split headers, fixed-length bodies, and multiple requests per receive. Both variants perform the same copy. A future ownership-aware parser could avoid that copy by retaining receive pages instead. Responses use static storage.

These are ordinary, non-incremental provided buffers. This is not zero-copy or ArcBuffer.

## Plaintext multishot lifetime handling

A one-shot receive ends at its CQE and must be rearmed for further input. A multishot receive can keep producing CQEs while `IORING_CQE_F_MORE` is set. It remains active across keep-alive responses. Receives are cancelled for explicit close, shutdown, errors, or to pause a backed-up pipelined reader. A terminal receive is rearmed when more input is wanted.

Each connection counts outstanding receive, send, and cancel operations. A cancel completion does not alone authorize freeing the connection: the original receive must also produce its terminal CQE. The socket and connection object are released only after all outstanding operations complete. Partial sends are resubmitted. SIGTERM stops accepting, cancels idle receives, and drains completions before freeing the pool.

On keep-alive traffic, compare `responses`, `accepted`, `recv_submissions`, and `recv_data_cqes` in the shutdown counters. Many responses per accepted connection prove reuse. Fewer receive submissions than data completions show multishot amortization; one CQE is not necessarily one HTTP request. Fewer submissions are not by themselves proof of higher throughput: compare both modes at identical load and CPU settings, including latency.

## Historical plaintext results: 2026-09-25

AMD Ryzen 9 7950X3D, Ubuntu WSL2, loopback, four server workers, four wrk2 threads on disjoint physical CPU sets, 1,024 connections, 30 seconds per run, three trials per mode/load point. No TLS. Every response closes its TCP connection.

These archived results used one-shot accept and the earlier close-after-first-receive implementation, before HTTP framing and keep-alive. They are not measurements of the current servers. The former Python runner alternated mode order and used a fresh port for every run; that runner and its Python checks have now been removed.

| Offered RPS | Mode | Trial 1 achieved RPS | Trial 2 | Trial 3 | Median |
|---|---|---:|---:|---:|---:|
| 60,000 | One-shot | 58,715.53 | 58,257.71 | 58,488.98 | 58,488.98 |
| 60,000 | Multishot | 58,296.68 | 58,049.21 | 58,234.98 | 58,234.98 |
| 120,000 | One-shot | 66,355.19 | 65,892.51 | 66,686.42 | 66,355.19 |
| 120,000 | Multishot | 65,910.22 | 69,476.24 | 63,939.55 | 65,910.22 |

No reported socket errors, non-success responses, or server I/O errors occurred in these twelve runs. At the 120k offered rate, however, corrected latency grew to seconds: that is overload, not sustainable 120k service. The achieved rate must not be presented as meeting the offered rate.

**No consistent multishot throughput advantage was demonstrated.** Its median was approximately 0.4% lower at 60k offered and 0.7% lower at 120k, with overlapping/noisy results. The test includes TCP setup, teardown, scheduling, and client overhead, not just receive processing.

**The high-rate test is substantially client-constrained.** At 120k offered, wrk2 used roughly 112-114 CPU seconds per 30-second run (about 3.7-3.8 of its four cores), while the four server processes used roughly 53-56 CPU seconds combined (about 1.8 cores). These are process CPU times, not all kernel/background work. The result is not the maximum capability of four server cores, nor proof that either receive mode is intrinsically faster. A server-capacity experiment needs more client capacity or separate load-generator machines.

## Plaintext boundaries

- Responses follow complete request framing. Headers are limited to 16 KiB, with 64 KiB total request/buffered input. `Content-Length` bodies and pipelining are supported; transfer encodings (including chunked), duplicate/invalid content lengths, and unsupported HTTP versions are rejected with `400` and close. No `100-continue`, upgrades, or general HTTP compliance.
- Pipelined input is bounded: receives are paused above half capacity while sending. A burst of already-completed receives can still exceed capacity, in which case the server logs an error and closes rather than allocating unbounded storage.
- The receive pool is bounded, but idle connections have no application timeout/admission policy. This is an instructional loopback server.
- Functional sanitizer checks cover sequential reuse, fragmented headers/bodies, pipelining, HTTP/1.0 persistence, explicit close, rejected framing, peer EOF, and idle shutdown. They do not prove every possible kernel race or force partial sends of this tiny response.
- No SQPOLL, registered files, busy polling, incremental buffers, SEND_ZC, or TLS. Accept is now multishot in both samples.
- The comparison changes only receive mode, not the transport architecture or application parsing strategy. It cannot predict Kestrel, physical-NIC, or TLS performance.

## TLS architectures and results

### What each server implements

All TLS variants are standalone copies so their mechanisms can be inspected independently. They use the same HTTP subset, response, certificate/cipher configuration, and normal TLS close-notify behavior. They support short and persistent HTTP connections. Both baseline variants use multishot accept; this does not mean their data receives or readiness watches are multishot.

| Folder | Architecture / incremental change |
|---|---|
| `tls-bio` | OpenSSL memory BIOs. One-shot io_uring receives obtain ciphertext, `BIO_write` feeds OpenSSL, and `BIO_read` drains ciphertext into buffers sent with io_uring. |
| `tls-fd` | `SSL_set_fd` socket BIO. OpenSSL performs socket I/O; one-shot io_uring polls wait for `WANT_READ`/`WANT_WRITE`. |
| `tls-fd-ring` | Original fd path plus `SINGLE_ISSUER`, `DEFER_TASKRUN`, and combined submit-and-wait. These changes were tested together, not independently attributed. |
| `tls-fd-ahead` | The ring variant plus OpenSSL read-ahead and explicit `SSL_MODE_AUTO_RETRY`. |
| `tls-fd-poll` | The ring/read-ahead configuration plus persistent multishot read-readiness polling; write readiness is armed separately when needed. |
| `tls-fd-read` | Read-ahead with the original ring configuration, isolating it from the ring changes. |
| `tls-fd-hybrid` | Read-ahead plus persistent epoll registrations; io_uring watches the epoll fd, then `epoll_wait(0)` collects socket readiness. Inspired by Monoio's compatibility driver. |
| `tls-fd-ready` | `tls-fd-read` plus a readiness-directed shortcut: after a completed response, arm read readiness rather than speculatively call `SSL_read` when neither HTTP nor OpenSSL has buffered input. Best confirmed keep-alive result in these experiments. |

`tls-fd-ready` checks `input_length == 0`, `!read_eof`, and `!SSL_has_pending(ssl)` before taking that shortcut. Buffered HTTP/TLS input still makes immediate progress. It does not discard data or skip TLS processing.

None of these configurations submits `SSL_read` as an SQE. OpenSSL always runs in userspace. The fd variants are not zero-copy networking; the BIO variant is not representative of every possible custom BIO implementation.

### Common measurement setup

- Date: 2026-09-25; Ubuntu WSL2, Linux `6.18.33.2-microsoft-standard-WSL2`, AMD Ryzen 9 7950X3D.
- Four single-threaded server processes on logical CPUs `0,2,4,6`, each owning its connections and ring.
- Twelve wrk2 threads on `8,10,12,14,16,18,20,22,24,26,28,30`: different physical cores from the server according to WSL's topology.
- 1,200 client connections; 15 seconds per run; patched monotonic-clock wrk2 described above.
- OpenSSL 3.0.13; local ECDSA P-256 certificate; TLS 1.3 in the load runs, AES-128-GCM configuration. Functional checks also cover TLS 1.2.
- Session resumption disabled for both modes; `resumed=0` in server counters. Short connections pay for a full handshake.
- Offered load: 1,000,000 RPS for keep-alive and 50,000 RPS for short connections.

**These offered rates deliberately exceeded capacity.** The tables show completed/achieved RPS, not sustained service at the offered rate. Corrected latency grew to seconds. Short 15-second runs include wrk2 startup/calibration, leaving only a short post-calibration latency window.

Baseline throughput changed across batches. Compare configurations within a matched batch, not the best value from one batch against the worst from another.

### Confirmed keep-alive comparison

Three runs per configuration, reversing mode order in the middle trial:

| Server | Trial 1 RPS | Trial 2 RPS | Trial 3 RPS | Median RPS |
|---|---:|---:|---:|---:|
| `tls-bio` | 296,339.29 | 296,020.26 | 301,276.57 | 296,339.29 |
| Original `tls-fd` | 294,197.75 | 302,808.32 | 297,285.10 | 297,285.10 |
| `tls-fd-read` | 313,381.20 | 302,612.12 | 314,336.90 | 313,381.20 |
| **`tls-fd-ready`** | **332,517.67** | **345,091.05** | **333,963.83** | **333,963.83** |

The readiness-directed variant's median was **12.7% above BIO** and **12.3% above the original fd implementation**. This is a repeatable improvement in this small experiment, not proof of the best possible implementation on other machines or workloads.

The per-process read-syscall accounting supports the mechanism:

| Fd variant | Approximate reported read syscalls per response |
|---|---:|
| Original fd | 3 |
| Read-ahead | 2 |
| Read-ahead + readiness-directed retry | 1 |

Read-ahead reduces small reads of the next required part of a TLS record. The final shortcut removes the common immediate `SSL_read` attempt that would return `WANT_READ` after a response. This syscall pattern is workload-specific, not an OpenSSL API guarantee.

Do not interpret the BIO processes' low `/proc/.../io` read-syscall count as zero I/O: io_uring receive/send operations use different accounting. Native submissions and completions must also be counted.

Local evidence: `results/tls-architecture-confirm-20260925-220150/`, including `summary.json`, wrk2 output, worker counters, and CPU/I/O accounting.

### Confirmed short-connection comparison

The same paired/reversed-order approach, with a new TLS connection for each request:

| Server | Trial 1 RPS | Trial 2 RPS | Trial 3 RPS | Median RPS |
|---|---:|---:|---:|---:|
| `tls-bio` | 5,692.49 | 5,774.93 | 5,973.86 | 5,774.93 |
| `tls-fd-ready` | 5,764.16 | 5,854.15 | 5,869.89 | 5,854.15 |

The approximately **1.4% median difference is not a meaningful demonstrated short-connection win**. The ranges overlap, and the client consumed most of its allocated CPU budget doing full handshakes. The readiness-directed optimization targets the transition to another request, which short connections do not take.

Local evidence: `results/tls-architecture-close-confirm-20260925-220934/`.

### Exploratory architecture screens

These were single runs used to choose candidates, not repeated performance claims.

**Screen A:**

| Server | Keep-alive achieved RPS | Short-connection achieved RPS |
|---|---:|---:|
| BIO baseline | 263,620.78 | 5,306.14 |
| Fd baseline | 244,369.10 | 5,481.66 |
| `tls-fd-ring` | 229,824.35 | 5,297.79 |
| `tls-fd-ahead` | 279,105.91 | 5,166.99 |
| `tls-fd-poll` | 244,980.52 | 5,298.44 |

The ring changes and persistent polling did not establish a win. Persistent polling reduced registrations/submissions, but that did not translate into higher RPS. Read-ahead was worth isolating.

Local evidence: `results/tls-architecture-screen-20260925-214407/`.

**Screen B, with fresh baselines:**

| Server | Keep-alive achieved RPS |
|---|---:|
| BIO baseline | 310,861.60 |
| Fd baseline | 303,220.63 |
| `tls-fd-read` | 323,256.76 |
| `tls-fd-hybrid` | 271,354.76 |

The hybrid sharply reduced per-socket ring submissions, but did not win this screen. Its inclusion is still useful as an example of integrating readiness consumers into an io_uring runtime. These values must not be mixed with Screen A to calculate improvements.

Local evidence: `results/tls-architecture-refine-20260925-215506/`. The earlier initial BIO/fd comparison is retained separately under `results/tls-comparison-20260925-193326/`.

### Correctness and interpretation

The variants passed TLS 1.2/1.3 functional checks under AddressSanitizer and UndefinedBehaviorSanitizer, covering persistent/close behavior, fragmented headers and bodies, pipelining, rejected framing, EOF, and shutdown with pending handshakes. A deliberately small socket send buffer exercised real OpenSSL `WANT_WRITE` retries and partial ciphertext sends. These checks do not prove every TLS extension, kernel race, or production HTTP behavior.

The recorded load runs reported no wrk2 socket/HTTP errors and zero server `tls_errors`/`io_errors`. They did record peer aborts separately, such as EOF/reset without a completed TLS shutdown. They were not entirely clean TLS-shutdown runs; those counters were not suppressed.

No kTLS, networking zero-copy, TLS session-resumption comparison, physical NIC, or production application logic is involved. The six optimized fd folders were added independently; the original BIO and fd sources were preserved. No framework implementation was benchmarked by these C tests.

### Run a TLS variant

Start only one variant on a given port:

```bash
cd ~/code/Network-Transport-Experiments/experiments/io-uring-http
./tls-fd-ready/run.sh 18443
# Or: ./tls-bio/run.sh 18443
# Or another tls-* folder's run.sh.
```

The TLS Makefiles require OpenSSL development headers/libraries and link `-lssl -lcrypto` alongside the locally installed liburing. The launcher builds its own server and uses `tls-cert.sh` to create a local test certificate/key if absent. Those PEM files are gitignored. The four worker processes remain attached to the launcher; Ctrl+C stops them.

In another WSL terminal, reproduce the measured client CPU allocation:

```bash
cd ~/code/Network-Transport-Experiments/experiments/io-uring-http
export CLIENT_CPUS=8,10,12,14,16,18,20,22,24,26,28,30
./wrk-tls.sh keepalive 1000000 15s 1200 18443
./wrk-tls.sh close 50000 15s 1200 18443
```

For latency at a sustainable rate, lower the offered RPS rather than interpreting the overload histograms as normal request latency.
