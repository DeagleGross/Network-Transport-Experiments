#define _GNU_SOURCE
#define MEMORY_BIO 0
#include <arpa/inet.h>
#include <errno.h>
#include <liburing.h>
#include <netinet/tcp.h>
#include <openssl/err.h>
#include <openssl/ssl.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/socket.h>
#include <unistd.h>

enum { INPUT_CAPACITY = 65536, HEADER_LIMIT = 16384, WIRE_CAPACITY = 16384 };
enum kind { ACCEPT, INPUT, OUTPUT, CANCEL };
enum phase { HANDSHAKE, READING, WRITING, SHUTTING_DOWN, DRAINING };
struct connection;
struct operation {
    enum kind kind;
    struct connection *connection;
};
struct connection {
    int fd, dead, references, input_active, output_active;
    int input_cancelled, output_cancelled, close_after, read_eof;
    enum phase phase;
    SSL *ssl;
    char input[INPUT_CAPACITY];
    size_t input_length, reply_length, reply_offset;
    const char *reply;
#if MEMORY_BIO
    BIO *read_bio, *write_bio; /* Owned by SSL after SSL_set_bio. */
    char wire_input[WIRE_CAPACITY], wire_output[WIRE_CAPACITY];
    size_t wire_length, wire_offset;
#endif
    struct operation input_op, output_op, cancel_input, cancel_output;
    struct connection *previous, *next;
};
struct request {
    size_t length;
    int close, http10, head;
};

#define RESPONSE(version, connection) \
    "HTTP/" version " 200 OK\r\nContent-Length: 12\r\nContent-Type: text/plain\r\nConnection: " connection "\r\n\r\nHello world\n"
static const char keep11[] = RESPONSE("1.1", "keep-alive");
static const char close11[] = RESPONSE("1.1", "close");
static const char keep10[] = RESPONSE("1.0", "keep-alive");
static const char close10[] = RESPONSE("1.0", "close");
static const char bad[] = "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
static struct io_uring ring;
static SSL_CTX *context;
static struct connection *connections;
static int listener, accept_active, accept_cancel_active;
static volatile sig_atomic_t stopping;
static struct operation accept_op = { ACCEPT, NULL }, cancel_accept = { CANCEL, NULL };
static unsigned long accepted, handshakes, resumed, responses, rejected, tls_errors, io_errors, peer_aborts;
static unsigned long input_submissions, output_submissions, want_read, want_write, partial_sends;
static unsigned long tls12, tls13;

static void fail(const char *message)
{
    perror(message);
    ERR_print_errors_fp(stderr);
    exit(EXIT_FAILURE);
}

static void signal_stop(int number)
{
    (void)number;
    stopping = 1;
}

static struct io_uring_sqe *get_sqe(void)
{
    struct io_uring_sqe *sqe = io_uring_get_sqe(&ring);
    if (sqe == NULL) {
        int result = io_uring_submit(&ring);
        if (result < 0) {
            errno = -result;
            fail("submit");
        }
        sqe = io_uring_get_sqe(&ring);
    }
    if (sqe == NULL) {
        errno = ENOBUFS;
        fail("SQ full");
    }
    return sqe;
}

static void release(struct connection *c)
{
    if (--c->references != 0)
        return;
    if (c->previous != NULL)
        c->previous->next = c->next;
    else
        connections = c->next;
    if (c->next != NULL)
        c->next->previous = c->previous;
    SSL_free(c->ssl);
    close(c->fd);
    free(c);
}

static void cancel_operation(struct connection *c, struct operation *target, struct operation *completion)
{
    struct io_uring_sqe *sqe = get_sqe();
    io_uring_prep_cancel(sqe, target, 0);
    io_uring_sqe_set_data(sqe, completion);
    c->references++;
}

static void finish(struct connection *c)
{
    if (c->dead)
        return;
    c->dead = 1;
    /* Wake any socket operation even if cancellation races with completion. */
    shutdown(c->fd, SHUT_RDWR);
    if (c->input_active && !c->input_cancelled) {
        c->input_cancelled = 1;
        cancel_operation(c, &c->input_op, &c->cancel_input);
    }
    if (c->output_active && !c->output_cancelled) {
        c->output_cancelled = 1;
        cancel_operation(c, &c->output_op, &c->cancel_output);
    }
    release(c); /* Drop owner; kernel operations retain their own references. */
}

static void io_failure(struct connection *c, int result)
{
    if (result == -ECONNRESET || result == -EPIPE) {
        peer_aborts++;
        if (peer_aborts <= 4)
            fprintf(stderr, "peer abort: %s\n", strerror(-result));
    } else {
        io_errors++;
        fprintf(stderr, "socket operation: %s\n", strerror(-result));
    }
    finish(c);
}

static int equals(const char *text, size_t length, const char *expected)
{
    return length == strlen(expected) && strncasecmp(text, expected, length) == 0;
}

/* This intentionally small HTTP subset rejects unsupported framing explicitly. */
static int parse_request(struct connection *c, struct request *request)
{
    const char *start = c->input;
    const char *end = memmem(start, c->input_length, "\r\n\r\n", 4);
    if (end == NULL)
        return c->input_length >= HEADER_LIMIT ? -1 : 0;
    size_t headers = (size_t)(end + 4 - start);
    if (headers > HEADER_LIMIT)
        return -1;
    const char *line = memmem(start, headers, "\r\n", 2);
    const char *space = memchr(start, ' ', (size_t)(line - start));
    if (space == NULL || space == start)
        return -1;
    request->head = space - start == 4 && memcmp(start, "HEAD", 4) == 0;
    const char *version = memchr(space + 1, ' ', (size_t)(line - space - 1));
    if (version == NULL || version == space + 1 || line - version != 9)
        return -1;
    request->http10 = memcmp(version + 1, "HTTP/1.0", 8) == 0;
    if (!request->http10 && memcmp(version + 1, "HTTP/1.1", 8) != 0)
        return -1;
    size_t body = 0;
    int has_length = 0, keep = 0, close_requested = 0;
    for (const char *header = line + 2; header < end; header = line + 2) {
        line = memmem(header, (size_t)(end + 2 - header), "\r\n", 2);
        if (line == NULL)
            return -1;
        const char *colon = memchr(header, ':', (size_t)(line - header));
        if (colon == NULL || colon == header)
            return -1;
        const char *value = colon + 1, *value_end = line;
        while (value < value_end && (*value == ' ' || *value == '\t'))
            value++;
        while (value_end > value && (value_end[-1] == ' ' || value_end[-1] == '\t'))
            value_end--;
        if (equals(header, (size_t)(colon - header), "Connection")) {
            while (value < value_end) {
                const char *comma = memchr(value, ',', (size_t)(value_end - value));
                const char *token_end = comma != NULL ? comma : value_end;
                while (value < token_end && (*value == ' ' || *value == '\t'))
                    value++;
                while (token_end > value && (token_end[-1] == ' ' || token_end[-1] == '\t'))
                    token_end--;
                close_requested |= equals(value, (size_t)(token_end - value), "close");
                keep |= equals(value, (size_t)(token_end - value), "keep-alive");
                value = comma != NULL ? comma + 1 : value_end;
            }
        } else if (equals(header, (size_t)(colon - header), "Content-Length")) {
            if (has_length || value == value_end)
                return -1;
            has_length = 1;
            for (; value < value_end; value++) {
                if (*value < '0' || *value > '9')
                    return -1;
                size_t digit = (size_t)(*value - '0');
                if (body > (INPUT_CAPACITY - digit) / 10)
                    return -1;
                body = body * 10 + digit;
            }
        } else if (equals(header, (size_t)(colon - header), "Transfer-Encoding")
                   || equals(header, (size_t)(colon - header), "Expect")
                   || equals(header, (size_t)(colon - header), "Upgrade")) {
            return -1;
        }
    }
    if (body > INPUT_CAPACITY - headers)
        return -1;
    request->length = headers + body;
    request->close = close_requested || (request->http10 && !keep);
    return c->input_length >= request->length ? 1 : 0;
}

static void wait_for_io(struct connection *c, int ssl_error)
{
    if (c->input_active)
        return;
    struct io_uring_sqe *sqe = get_sqe();
#if MEMORY_BIO
    if (ssl_error != SSL_ERROR_WANT_READ) {
        errno = EIO;
        fail("memory BIO unexpectedly wants write without pending output");
    }
    io_uring_prep_recv(sqe, c->fd, c->wire_input, sizeof(c->wire_input), 0);
#else
    io_uring_prep_poll_add(sqe, c->fd, ssl_error == SSL_ERROR_WANT_WRITE ? POLLOUT : POLLIN);
#endif
    io_uring_sqe_set_data(sqe, &c->input_op);
    c->input_active = 1;
    c->references++;
    input_submissions++;
}

#if MEMORY_BIO
static void submit_output(struct connection *c)
{
    struct io_uring_sqe *sqe = get_sqe();
    io_uring_prep_send(sqe, c->fd, c->wire_output + c->wire_offset,
                      c->wire_length - c->wire_offset, MSG_NOSIGNAL);
    io_uring_sqe_set_data(sqe, &c->output_op);
    c->output_active = 1;
    c->references++;
    output_submissions++;
}

static void flush_output(struct connection *c)
{
    if (c->output_active || BIO_ctrl_pending(c->write_bio) == 0)
        return;
    int count = BIO_read(c->write_bio, c->wire_output, sizeof(c->wire_output));
    if (count <= 0)
        fail("BIO_read(output)");
    c->wire_length = (size_t)count;
    c->wire_offset = 0;
    submit_output(c);
}
#endif

static void ssl_failure(struct connection *c, int error, int saved_errno)
{
    unsigned long detail = ERR_peek_error();
    if ((error == SSL_ERROR_SYSCALL && (saved_errno == ECONNRESET || saved_errno == EPIPE))
        || (error == SSL_ERROR_SSL && ERR_GET_LIB(detail) == ERR_LIB_SSL
            && ERR_GET_REASON(detail) == SSL_R_UNEXPECTED_EOF_WHILE_READING)) {
        peer_aborts++;
        if (peer_aborts <= 4)
            fprintf(stderr, "TLS peer aborted phase=%d ssl_error=%d errno=%d\n", c->phase, error, saved_errno);
        ERR_clear_error();
    } else {
        tls_errors++;
        fprintf(stderr, "TLS failure phase=%d ssl_error=%d errno=%d\n", c->phase, error, saved_errno);
        ERR_print_errors_fp(stderr);
    }
    /* Do not call SSL_shutdown after a fatal SSL error. */
    finish(c);
}

static void drive(struct connection *c)
{
    if (c->dead)
        return;
    for (;;) {
#if MEMORY_BIO
        flush_output(c);
        if (BIO_ctrl_pending(c->write_bio) > WIRE_CAPACITY)
            return; /* Resume on send completion; bound queued ciphertext. */
#endif
        if (c->phase == DRAINING) {
#if MEMORY_BIO
            if (c->output_active || BIO_ctrl_pending(c->write_bio) != 0)
                return;
#endif
            finish(c);
            return;
        }
        if (c->phase == READING) {
            struct request request = {0};
            int parsed = parse_request(c, &request);
            if (parsed == 0 && c->read_eof) {
                if (c->input_length == 0) {
                    c->phase = SHUTTING_DOWN;
                    continue;
                }
                parsed = -1;
            }
            if (parsed != 0) {
                if (parsed < 0) {
                    rejected++;
                    c->reply = bad;
                    c->input_length = 0;
                    c->close_after = 1;
                } else {
                    c->input_length -= request.length;
                    memmove(c->input, c->input + request.length, c->input_length);
                    c->close_after = request.close || (c->read_eof && c->input_length == 0);
                    c->reply = request.http10
                        ? (c->close_after ? close10 : keep10)
                        : (c->close_after ? close11 : keep11);
                }
                c->reply_length = strlen(c->reply) - (parsed > 0 && request.head ? 12 : 0);
                c->reply_offset = 0;
                c->phase = WRITING;
            }
        }

        /* SSL_get_error must see this operation's error queue on this thread. */
        ERR_clear_error();
        errno = 0;
        size_t count = 0;
        int result;
        enum phase performed = c->phase;
        switch (performed) {
        case HANDSHAKE:
            result = SSL_do_handshake(c->ssl);
            break;
        case READING:
            result = SSL_read_ex(c->ssl, c->input + c->input_length, INPUT_CAPACITY - c->input_length, &count);
            break;
        case WRITING:
            result = SSL_write_ex(c->ssl, c->reply + c->reply_offset, c->reply_length - c->reply_offset, &count);
            break;
        case SHUTTING_DOWN:
            result = SSL_shutdown(c->ssl);
            break;
        default:
            abort();
        }
        int saved_errno = errno;
        /* Shutdown's zero means close_notify sent, not an SSL error. */
        int success = result > 0 || (performed == SHUTTING_DOWN && result == 0);
        int error = success ? SSL_ERROR_NONE : SSL_get_error(c->ssl, result);
        if (!success && error != SSL_ERROR_WANT_READ && error != SSL_ERROR_WANT_WRITE
            && error != SSL_ERROR_ZERO_RETURN) {
            ssl_failure(c, error, saved_errno);
            return;
        }
#if MEMORY_BIO
        flush_output(c); /* Handshake/read/shutdown can also produce ciphertext. */
#endif
        if (success) {
            if (performed == HANDSHAKE) {
                handshakes++;
                resumed += SSL_session_reused(c->ssl) != 0;
                tls12 += SSL_version(c->ssl) == TLS1_2_VERSION;
                tls13 += SSL_version(c->ssl) == TLS1_3_VERSION;
                c->phase = READING;
            } else if (performed == READING) {
                c->input_length += count;
            } else if (performed == WRITING) {
                c->reply_offset += count;
                if (c->reply_offset == c->reply_length) {
                    responses++;
                    c->phase = c->close_after ? SHUTTING_DOWN : READING;
#if !MEMORY_BIO
                    /*
                     * No application or TLS-buffered input remains. Let the
                     * readiness operation test the socket instead of another
                     * speculative SSL_read that usually returns WANT_READ.
                     */
                    if (c->phase == READING && c->input_length == 0
                        && !c->read_eof && !SSL_has_pending(c->ssl)) {
                        wait_for_io(c, SSL_ERROR_WANT_READ);
                        return;
                    }
#endif
                }
            } else {
                c->phase = DRAINING;
            }
            continue;
        }
        if (error == SSL_ERROR_ZERO_RETURN) {
            c->read_eof = 1;
            if (performed != READING)
                c->phase = SHUTTING_DOWN;
            continue;
        }
        if (error == SSL_ERROR_WANT_READ)
            want_read++;
        else
            want_write++;
#if MEMORY_BIO
        if (error == SSL_ERROR_WANT_WRITE && c->output_active)
            return;
#endif
        wait_for_io(c, error);
        return;
    }
}

static void submit_accept(void)
{
    struct io_uring_sqe *sqe = get_sqe();
    io_uring_prep_multishot_accept(sqe, listener, NULL, NULL, SOCK_CLOEXEC | SOCK_NONBLOCK);
    io_uring_sqe_set_data(sqe, &accept_op);
    accept_active = 1;
}

static void start_connection(int fd)
{
    struct connection *c = calloc(1, sizeof(*c));
    if (c == NULL)
        fail("calloc");
    c->fd = fd;
    c->references = 1;
    c->phase = HANDSHAKE;
    c->input_op = (struct operation){ INPUT, c };
    c->output_op = (struct operation){ OUTPUT, c };
    c->cancel_input = (struct operation){ CANCEL, c };
    c->cancel_output = (struct operation){ CANCEL, c };
    c->next = connections;
    if (connections != NULL)
        connections->previous = c;
    connections = c;
    int enabled = 1;
    if (setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &enabled, sizeof(enabled)))
        fail("TCP_NODELAY");
    const char *test_buffer = getenv("TLS_TEST_SNDBUF");
    if (test_buffer != NULL) {
        int size = atoi(test_buffer);
        if (size < 1024 || setsockopt(fd, SOL_SOCKET, SO_SNDBUF, &size, sizeof(size)))
            fail("TLS_TEST_SNDBUF");
    }
    c->ssl = SSL_new(context);
    if (c->ssl == NULL)
        fail("SSL_new");
#if MEMORY_BIO
    c->read_bio = BIO_new(BIO_s_mem());
    c->write_bio = BIO_new(BIO_s_mem());
    if (c->read_bio == NULL || c->write_bio == NULL)
        fail("BIO_new");
    BIO_set_mem_eof_return(c->read_bio, -1); /* Empty is WANT_READ, not EOF. */
    SSL_set_bio(c->ssl, c->read_bio, c->write_bio);
#else
    if (SSL_set_fd(c->ssl, fd) != 1)
        fail("SSL_set_fd");
#endif
    SSL_set_accept_state(c->ssl);
    accepted++;
    drive(c);
}

static void complete(struct operation *op, int result, unsigned flags)
{
    struct connection *c = op->connection;
    if (op->kind == ACCEPT) {
        if (!(flags & IORING_CQE_F_MORE))
            accept_active = 0;
        if (result >= 0) {
            if (stopping)
                close(result);
            else
                start_connection(result);
        } else if (result != -ECANCELED) {
            fprintf(stderr, "accept: %s\n", strerror(-result));
            io_errors++;
            stopping = 1;
        }
        if (!accept_active && !stopping)
            submit_accept();
        return;
    }
    if (op->kind == CANCEL) {
        if (result < 0 && result != -ENOENT && result != -EALREADY) {
            fprintf(stderr, "cancel: %s\n", strerror(-result));
            io_errors++;
        }
        if (c != NULL)
            release(c);
        else
            accept_cancel_active = 0;
        return;
    }
    if (op->kind == INPUT) {
        c->input_active = 0;
        if (!c->dead) {
            if (result < 0) {
                io_failure(c, result);
            } else {
#if MEMORY_BIO
                if (result == 0)
                    BIO_set_mem_eof_return(c->read_bio, 0);
                else if (BIO_write(c->read_bio, c->wire_input, result) != result)
                    fail("BIO_write(input)");
#endif
                drive(c);
            }
        }
    } else {
        c->output_active = 0;
#if MEMORY_BIO
        if (!c->dead) {
            if (result <= 0) {
                io_failure(c, result == 0 ? -EPIPE : result);
            } else {
                c->wire_offset += (size_t)result;
                if (c->wire_offset < c->wire_length) {
                    partial_sends++;
                    submit_output(c);
                } else {
                    c->wire_offset = c->wire_length = 0;
                    drive(c);
                }
            }
        }
#endif
    }
    release(c);
}

int main(int argc, char **argv)
{
    char *end;
    long port = argc == 4 ? strtol(argv[1], &end, 10) : 0;
    if (argc != 4 || *end != '\0' || port < 1 || port > 65535) {
        fprintf(stderr, "Usage: %s PORT CERTIFICATE PRIVATE_KEY\n", argv[0]);
        return 1;
    }
    struct sigaction action = { .sa_handler = signal_stop };
    sigemptyset(&action.sa_mask);
    if (sigaction(SIGINT, &action, NULL) || sigaction(SIGTERM, &action, NULL))
        fail("sigaction");
    signal(SIGPIPE, SIG_IGN);
    context = SSL_CTX_new(TLS_server_method());
    if (context == NULL
        || SSL_CTX_set_min_proto_version(context, TLS1_2_VERSION) != 1
        || SSL_CTX_set_max_proto_version(context, TLS1_3_VERSION) != 1
        || SSL_CTX_set_ciphersuites(context, "TLS_AES_128_GCM_SHA256") != 1
        || SSL_CTX_set_cipher_list(context, "ECDHE-ECDSA-AES128-GCM-SHA256") != 1
        || SSL_CTX_set1_groups_list(context, "X25519:P-256") != 1
        || SSL_CTX_use_certificate_chain_file(context, argv[2]) != 1
        || SSL_CTX_use_PrivateKey_file(context, argv[3], SSL_FILETYPE_PEM) != 1
        || SSL_CTX_check_private_key(context) != 1
        || SSL_CTX_set_num_tickets(context, 0) != 1)
        fail("TLS context");
    SSL_CTX_set_session_cache_mode(context, SSL_SESS_CACHE_OFF);
    SSL_CTX_set_options(context, SSL_OP_NO_TICKET);
    SSL_CTX_set_read_ahead(context, 1);
    SSL_CTX_set_mode(context, SSL_MODE_AUTO_RETRY);
    int result = io_uring_queue_init(2048, &ring, 0);
    if (result < 0) {
        errno = -result;
        fail("io_uring_queue_init");
    }
    listener = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (listener < 0)
        fail("socket");
    int enabled = 1;
    if (setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, &enabled, sizeof(enabled))
        || setsockopt(listener, SOL_SOCKET, SO_REUSEPORT, &enabled, sizeof(enabled)))
        fail("reuse options");
    struct sockaddr_in address = {
        .sin_family = AF_INET, .sin_port = htons((uint16_t)port),
        .sin_addr.s_addr = htonl(INADDR_LOOPBACK)
    };
    if (bind(listener, (struct sockaddr *)&address, sizeof(address)) || listen(listener, 4096))
        fail("bind/listen");
    submit_accept();
    printf("READY mode=tls-fd-ready pid=%ld port=%ld openssl=%s\n", (long)getpid(), port, OpenSSL_version(OPENSSL_VERSION));
    fflush(stdout);
    int shutdown_started = 0;
    while (accept_active || accept_cancel_active || connections != NULL) {
        if (stopping && !shutdown_started) {
            shutdown_started = 1;
            if (accept_active) {
                struct io_uring_sqe *sqe = get_sqe();
                io_uring_prep_cancel(sqe, &accept_op, 0);
                io_uring_sqe_set_data(sqe, &cancel_accept);
                accept_cancel_active = 1;
            }
            for (struct connection *c = connections, *next; c != NULL; c = next) {
                next = c->next;
                finish(c);
            }
        }
        result = io_uring_submit(&ring);
        if (result < 0 && result != -EINTR) {
            errno = -result;
            fail("submit");
        }
        struct io_uring_cqe *cqe;
        struct __kernel_timespec timeout = { .tv_sec = 0, .tv_nsec = 100000000 };
        result = io_uring_wait_cqe_timeout(&ring, &cqe, &timeout);
        if (result == -EINTR || result == -ETIME)
            continue;
        if (result < 0) {
            errno = -result;
            fail("wait");
        }
        unsigned head, count = 0;
        io_uring_for_each_cqe(&ring, head, cqe) {
            complete(io_uring_cqe_get_data(cqe), cqe->res, cqe->flags);
            count++;
        }
        io_uring_cq_advance(&ring, count);
    }
    close(listener);
    io_uring_queue_exit(&ring);
    SSL_CTX_free(context);
    printf("STATS accepted=%lu handshakes=%lu resumed=%lu responses=%lu rejected=%lu tls_errors=%lu io_errors=%lu peer_aborts=%lu input_sqes=%lu output_sqes=%lu want_read=%lu want_write=%lu partial_sends=%lu tls12=%lu tls13=%lu\n",
           accepted, handshakes, resumed, responses, rejected, tls_errors, io_errors, peer_aborts,
           input_submissions, output_submissions, want_read, want_write, partial_sends, tls12, tls13);
    return tls_errors || io_errors ? 1 : 0;
}
