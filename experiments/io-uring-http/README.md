# Plaintext io_uring: one-shot versus multishot receive

Two small C servers with exactly the same behavior: accept a TCP connection on loopback, receive any nonempty payload, send a fixed HTTP/1.1 `200 OK` response containing `Hello world\n`, and close the connection. No TLS, BIO, kTLS, HTTP parsing, keep-alive, or io_uring zero-copy send. These are teaching examples, not general-purpose HTTP servers.

## Files and reading order

- `oneshot/server.c`: entry point selecting one-shot receive.
- `multishot/server.c`: entry point selecting multishot receive.
- `common/server.c`: shared event loop, buffer pool, completion handling, and connection lifetime.
- `run.sh` / `run.py`: build, start four workers, wait for readiness, check responses, run wrk2, print results, and stop workers.
- `check.py`: socket-level response, pool-reuse, EOF, and idle-shutdown checks.
- `measurements/2026-09-25/`: recorded environment, full wrk2 output, worker counters, and summary JSON.

The shared implementation is deliberate: changing receive mode should not also change allocation policy, accept behavior, batching, response formatting, or thread topology.

## Run in WSL

Tested in Ubuntu WSL2 with Linux `6.18.33.2-microsoft-standard-WSL2`. This example requires Linux 6.0+ and support for provided buffer rings and multishot receive. Unsupported features produce errors, not a fallback to one-shot.

```bash
cd ~/code/Network-Transport-Experiments/experiments/io-uring-http
./run.sh --rates 60000 120000 --duration 30 --trials 3
```

The script uses four separate server processes, one pinned to each of four physical cores, sharing the listen port with `SO_REUSEPORT`. wrk2 uses four other physical cores. The selection uses Linux topology and excludes SMT siblings across the two sets. On the measured machine, server CPUs were `0,2,4,6`; client CPUs were `8,10,12,14`. Affinity is not exclusive machine reservation: Windows, WSL, kernel work, other applications, and shared caches still affect results.

Each run uses a fresh port, with one-shot first in odd-numbered trials and multishot first in even-numbered trials. Both accept and send remain one-shot; only receive is multishot. Defaults are 1,024 client connections and three 30-second trials at each of 30k, 60k, and 120k offered RPS. The measured command above selects two rates instead.

`run.sh` prints wrk2's summary and latency percentiles plus each server's counters. Full histograms, exact commands, CPU seconds, and environment are saved under ignored `results/`. RPS is wrk2's reported whole-run throughput, including its startup/calibration period; latency histograms are reset by wrk2 after calibration.

### Dependencies

The prepared WSL environment already has local builds:

| Dependency | Revision / location |
|---|---|
| liburing 2.9 | `08468cc3830185c75f9e7edefd88aa01e5c2f8ab`, installed under `~/.local` |
| wrk2 | `44a94c17d8e6a0bac8559b53da76848e430cb7a7`, `~/code/wrk2/wrk`, plus the included clock patch |
| Compiler | GCC 13.3.0 |

For another WSL installation, first provide a C toolchain, make, Git, Python 3, util-linux (`taskset`), and wrk2's OpenSSL/zlib build dependencies. Then run:

```bash
./setup.sh
make check
make sanitize
```

`setup.sh` builds pinned upstream sources under `~/code`, installs liburing to `~/.local`, applies `wrk2-monotonic.patch`, and builds the servers. It does not use sudo, modify APT configuration, or silently replace existing dependency checkouts at other revisions. `LIBURING_PREFIX` can override the Makefile's library location; it links liburing statically.

### Why patch wrk2?

The original client used wall-clock time for scheduling and latency. Pilot runs observed response timestamps earlier than the preceding send timestamps, and wrk2 aborted with a negative-latency histogram assertion. The included small patch uses `CLOCK_MONOTONIC` for wrk2 timing and its event-loop timers. It does not change HTTP behavior, rate targeting, or histogram recording. The Lua wall-clock helper is not used by these runs.

All recorded comparison runs use the same patched client. Crashed and preliminary unpatched runs are excluded from the comparison. This patch is local; nothing was submitted upstream.

## Follow the receive buffer

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
if (use_multishot)
    io_uring_prep_recv_multishot(sqe, connection->fd, NULL, 0, 0);
else
    io_uring_prep_recv(sqe, connection->fd, NULL, 0, 0);

sqe->flags |= IOSQE_BUFFER_SELECT;
sqe->buf_group = BUFFER_GROUP;
```

After a successful CQE:

```c
unsigned id = flags >> IORING_CQE_BUFFER_SHIFT;
/* storage + id * BUFFER_SIZE contains result received bytes. */
```

This server ignores the input content, so it can immediately call `add_buffer(id)` to make that buffer available again. The response uses separate static storage. A real parser or a future C# PipeReader adapter would keep the receive buffer until consumption; it must not republish storage that a reader still references.

These are ordinary, non-incremental provided buffers. No payload is copied by our C code between receiving and discarding it. This is not kernel-to-userspace zero-copy, and it is not ArcBuffer: there is no need for independent retained slices when the application ignores the input.

## Why multishot needs more lifetime handling

A one-shot receive ends at its CQE. A multishot receive can keep producing CQEs while `IORING_CQE_F_MORE` is set. After the first data completion, this server queues its response and cancels the still-active multishot receive because it must close after one response.

Each connection counts outstanding receive, send, and cancel operations. A cancel completion does not alone authorize freeing the connection: the original receive must also produce its terminal CQE. The socket and connection object are released only after all outstanding operations complete. Partial sends are resubmitted. SIGTERM stops accepting, cancels idle receives, and drains completions before freeing the pool.

Consequently, this workload gives multishot little opportunity to amortize receive submissions. Typical counters show one receive submission and one data completion per response for both versions, plus a cancellation submission, cancellation completion, and terminal receive completion for multishot. A keep-alive/streaming comparison would be a different experiment.

## Results: 2026-09-25

AMD Ryzen 9 7950X3D, Ubuntu WSL2, loopback, four server workers, four wrk2 threads on disjoint physical CPU sets, 1,024 connections, 30 seconds per run, three trials per mode/load point. No TLS. Every response closes its TCP connection.

| Offered RPS | Mode | Trial 1 achieved RPS | Trial 2 | Trial 3 | Median |
|---|---|---:|---:|---:|---:|
| 60,000 | One-shot | 58,715.53 | 58,257.71 | 58,488.98 | 58,488.98 |
| 60,000 | Multishot | 58,296.68 | 58,049.21 | 58,234.98 | 58,234.98 |
| 120,000 | One-shot | 66,355.19 | 65,892.51 | 66,686.42 | 66,355.19 |
| 120,000 | Multishot | 65,910.22 | 69,476.24 | 63,939.55 | 65,910.22 |

No reported socket errors, non-success responses, or server I/O errors occurred in these twelve runs. At the 120k offered rate, however, corrected latency grew to seconds: that is overload, not sustainable 120k service. The achieved rate must not be presented as meeting the offered rate.

**No consistent multishot throughput advantage was demonstrated.** Its median was approximately 0.4% lower at 60k offered and 0.7% lower at 120k, with overlapping/noisy results. The test includes TCP setup, teardown, scheduling, and client overhead, not just receive processing.

**The high-rate test is substantially client-constrained.** At 120k offered, wrk2 used roughly 112-114 CPU seconds per 30-second run (about 3.7-3.8 of its four cores), while the four server processes used roughly 53-56 CPU seconds combined (about 1.8 cores). These are process CPU times, not all kernel/background work. The result is not the maximum capability of four server cores, nor proof that either receive mode is intrinsically faster. A server-capacity experiment needs more client capacity or separate load-generator machines.

## Boundaries

- The response is triggered by the first positive receive, not by parsing a complete HTTP request. Tests use small non-pipelined GET requests. Split/large uploads can be closed with unread data and may observe a reset; this is intentionally not a general HTTP parser.
- The receive pool is bounded, but idle connections have no application timeout/admission policy. This is an instructional loopback server.
- Sanitizer runs cover exact response framing/body, arbitrary one-byte input, peer EOF, more than one pool's worth of connections, and shutdown with pending receives. They do not prove every possible kernel race or force partial sends of this tiny response.
- No multishot accept, SQPOLL, registered files, busy polling, incremental buffers, SEND_ZC, or TLS.
- The comparison changes only receive mode, not the transport architecture or application parsing strategy. It cannot predict Kestrel, physical-NIC, or TLS performance.
