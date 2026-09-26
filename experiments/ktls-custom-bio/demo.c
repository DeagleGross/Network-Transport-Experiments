#define _GNU_SOURCE
#include <arpa/inet.h>
#include <errno.h>
#include <netinet/tcp.h>
#include <openssl/bio.h>
#include <openssl/err.h>
#include <openssl/ssl.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

/*
 * One accepted TLS connection, one newline-terminated message, one echo.
 *
 * SSL -> our custom filter BIO -> OpenSSL socket BIO -> kernel TCP/kTLS.
 *
 * There is no BIO_s_mem, SSL_set_fd, io_uring, HTTP parser, or key export.
 * The socket BIO below our filter implements kTLS control/record conventions.
 */
struct observations {
    unsigned read_calls, write_calls, ctrl_calls;
    unsigned ktls_read_calls;
    size_t ktls_read_bytes, userspace_read_bytes;
    const void *last_read_destination;
};

static int filter_create(BIO *bio)
{
    BIO_set_init(bio, 1);
    BIO_set_data(bio, NULL);
    return 1;
}

static int filter_destroy(BIO *bio)
{
    /* Observations live on main's stack; SSL owns the BIO chain, not that state. */
    BIO_set_data(bio, NULL);
    BIO_set_init(bio, 0);
    return 1;
}

static int filter_read(BIO *bio, char *destination, size_t capacity, size_t *count)
{
    struct observations *state = BIO_get_data(bio);
    BIO *socket_bio = BIO_next(bio);
    int ktls = BIO_get_ktls_recv(socket_bio);
    state->read_calls++;
    state->last_read_destination = destination;
    BIO_clear_retry_flags(bio);

    /* Pass the very same destination through. Our filter makes no payload copy. */
    int result = BIO_read_ex(socket_bio, destination, capacity, count);
    int saved_errno = errno;
    BIO_copy_next_retry(bio);
    if (result == 1) {
        if (ktls) {
            state->ktls_read_calls++;
            state->ktls_read_bytes += *count;
        } else {
            state->userspace_read_bytes += *count;
        }
    }
    errno = saved_errno;
    return result;
}

static int filter_write(BIO *bio, const char *source, size_t length, size_t *count)
{
    struct observations *state = BIO_get_data(bio);
    state->write_calls++;
    BIO_clear_retry_flags(bio);
    int result = BIO_write_ex(BIO_next(bio), source, length, count);
    int saved_errno = errno;
    BIO_copy_next_retry(bio);
    errno = saved_errno;
    return result;
}

static long filter_ctrl(BIO *bio, int command, long argument, void *pointer)
{
    struct observations *state = BIO_get_data(bio);
    if (state != NULL)
        state->ctrl_calls++;
    BIO *next = BIO_next(bio);
    if (next == NULL)
        return 0;

    /*
     * Forward control operations, including fd queries, flush, kTLS setup,
     * RX/TX activation queries and TLS control-record handling.
     * Do not pretend to support these operations by returning success.
     */
    long result = BIO_ctrl(next, command, argument, pointer);
    int saved_errno = errno;
    BIO_copy_next_retry(bio);
    errno = saved_errno;
    return result;
}

static int64_t milliseconds(void)
{
    struct timespec now;
    if (clock_gettime(CLOCK_MONOTONIC, &now) != 0) {
        perror("clock_gettime");
        exit(EXIT_FAILURE);
    }
    return (int64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

static int wait_socket(int fd, short events, int64_t deadline)
{
    for (;;) {
        int64_t remaining = deadline - milliseconds();
        if (remaining <= 0) {
            fprintf(stderr, "Timed out waiting for socket progress.\n");
            return 0;
        }
        struct pollfd pfd = { .fd = fd, .events = events };
        int result = poll(&pfd, 1, (int)remaining);
        if (result > 0) {
            if (pfd.revents & POLLNVAL) {
                fprintf(stderr, "Invalid socket while waiting.\n");
                return 0;
            }
            return 1;
        }
        if (result < 0 && errno != EINTR) {
            perror("poll");
            return 0;
        }
    }
}

static int retry_ssl(int fd, int error, int saved_errno, int64_t deadline)
{
    if (error == SSL_ERROR_WANT_READ)
        return wait_socket(fd, POLLIN, deadline);
    if (error == SSL_ERROR_WANT_WRITE)
        return wait_socket(fd, POLLOUT, deadline);
    fprintf(stderr, "TLS operation failed: ssl_error=%d errno=%d (%s)\n",
            error, saved_errno, strerror(saved_errno));
    ERR_print_errors_fp(stderr);
    return 0;
}

int main(int argc, char **argv)
{
    char *end;
    long port = argc >= 4 ? strtol(argv[1], &end, 10) : 0;
    int userspace = argc == 5 && strcmp(argv[4], "--userspace") == 0;
    if ((argc != 4 && !userspace) || port < 1 || port > 65535 || *end != '\0') {
        fprintf(stderr, "Usage: %s PORT CERT.pem KEY.pem [--userspace]\n", argv[0]);
        return 1;
    }

    int status = 1, listener = -1, fd = -1;
    SSL_CTX *ctx = NULL;
    SSL *ssl = NULL;
    BIO_METHOD *method = NULL;
    BIO *filter = NULL, *socket_bio = NULL;
    struct observations state = {0};
    signal(SIGPIPE, SIG_IGN);
    setvbuf(stdout, NULL, _IOLBF, 0);

    ctx = SSL_CTX_new(TLS_server_method());
    if (ctx == NULL || !SSL_CTX_set_min_proto_version(ctx, TLS1_2_VERSION)
        || !SSL_CTX_set_max_proto_version(ctx, TLS1_2_VERSION)
        || !SSL_CTX_set_cipher_list(ctx, "ECDHE-ECDSA-AES128-GCM-SHA256")
        || !SSL_CTX_use_certificate_chain_file(ctx, argv[2])
        || !SSL_CTX_use_PrivateKey_file(ctx, argv[3], SSL_FILETYPE_PEM)
        || !SSL_CTX_check_private_key(ctx)) {
        fprintf(stderr, "Failed to configure the TLS context.\n");
        goto cleanup;
    }
    SSL_CTX_set_session_cache_mode(ctx, SSL_SESS_CACHE_OFF);
    SSL_CTX_set_options(ctx, SSL_OP_NO_TICKET);
    /* Keep the kTLS handoff simple: no read-ahead before activation. */
    SSL_CTX_set_read_ahead(ctx, 0);
    if (!userspace)
        SSL_CTX_set_options(ctx, SSL_OP_ENABLE_KTLS);

    int type = BIO_get_new_index();
    if (type < 0)
        goto cleanup;
    method = BIO_meth_new(type | BIO_TYPE_FILTER, "observed kTLS passthrough");
    if (method == NULL || !BIO_meth_set_create(method, filter_create)
        || !BIO_meth_set_destroy(method, filter_destroy)
        || !BIO_meth_set_read_ex(method, filter_read)
        || !BIO_meth_set_write_ex(method, filter_write)
        || !BIO_meth_set_ctrl(method, filter_ctrl))
        goto cleanup;

    listener = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
    int one = 1;
    if (listener < 0 || setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one))) {
        perror("socket/setup");
        goto cleanup;
    }
    struct sockaddr_in address = {
        .sin_family = AF_INET, .sin_port = htons((uint16_t)port),
        .sin_addr.s_addr = htonl(INADDR_LOOPBACK)
    };
    if (bind(listener, (struct sockaddr *)&address, sizeof(address)) || listen(listener, 1)) {
        perror("bind/listen");
        goto cleanup;
    }
    printf("READY port=%ld mode=%s OpenSSL=%s\n", port, userspace ? "userspace-control" : "require-ktls-rx",
           OpenSSL_version(OPENSSL_VERSION));
    int64_t deadline = milliseconds() + 15000;
    for (;;) {
        if (!wait_socket(listener, POLLIN, deadline))
            goto cleanup;
        fd = accept4(listener, NULL, NULL, SOCK_CLOEXEC | SOCK_NONBLOCK);
        if (fd >= 0)
            break;
        if (errno != EAGAIN && errno != EINTR) {
            perror("accept4");
            goto cleanup;
        }
    }
    close(listener);
    listener = -1;
    if (!userspace && setsockopt(fd, IPPROTO_TCP, TCP_ULP, "tls", sizeof("tls")) != 0) {
        perror("TCP_ULP tls (kernel module/support)");
        goto cleanup;
    }
    ssl = SSL_new(ctx);
    filter = BIO_new(method);
    socket_bio = BIO_new_socket(fd, BIO_NOCLOSE);
    if (ssl == NULL || filter == NULL || socket_bio == NULL)
        goto cleanup;
    BIO_set_data(filter, &state);
    BIO_push(filter, socket_bio);
    socket_bio = NULL; /* Owned by the chain now. */
    SSL_set_bio(ssl, filter, filter);
    filter = NULL; /* SSL owns this chain, once, for both directions. */
    SSL_set_accept_state(ssl);

    for (;;) {
        ERR_clear_error();
        errno = 0;
        int result = SSL_do_handshake(ssl);
        int saved_errno = errno;
        if (result == 1)
            break;
        int error = SSL_get_error(ssl, result);
        if (!retry_ssl(fd, error, saved_errno, deadline))
            goto cleanup;
    }
    int rx = BIO_get_ktls_recv(SSL_get_rbio(ssl));
    int tx = BIO_get_ktls_send(SSL_get_wbio(ssl));
    printf("TLS protocol=%s cipher=%s resumed=%d ktls_rx=%d ktls_tx=%d\n",
           SSL_get_version(ssl), SSL_get_cipher_name(ssl), SSL_session_reused(ssl), rx, tx);
    if ((!userspace && !rx) || (userspace && (rx || tx))) {
        fprintf(stderr, "Activation gate failed. No userspace fallback is accepted as a kTLS result.\n");
        goto cleanup;
    }
    unsigned before_calls = state.ktls_read_calls;
    size_t before_bytes = state.ktls_read_bytes;
    unsigned char plaintext[16384];
    size_t used = 0;
    printf("APPLICATION destination=%p capacity=%zu\n", (void *)plaintext, sizeof(plaintext));
    for (;;) {
        size_t count = 0;
        ERR_clear_error();
        errno = 0;
        int result = SSL_read_ex(ssl, plaintext + used, sizeof(plaintext) - used, &count);
        int saved_errno = errno;
        if (result == 1) {
            used += count;
            if (memchr(plaintext, '\n', used) != NULL)
                break;
            if (used == sizeof(plaintext)) {
                fprintf(stderr, "Message must end with newline within 16 KiB.\n");
                goto cleanup;
            }
        } else {
            int error = SSL_get_error(ssl, result);
            if (!retry_ssl(fd, error, saved_errno, deadline))
                goto cleanup;
        }
    }
    printf("READ plaintext_bytes=%zu custom_bio_ktls_calls=%u custom_bio_ktls_bytes=%zu last_bio_destination=%p filter_copy_bytes=0\n",
           used, state.ktls_read_calls - before_calls, state.ktls_read_bytes - before_bytes,
           state.last_read_destination);
    if (!userspace && state.ktls_read_calls == before_calls) {
        fprintf(stderr, "Did not observe a post-handshake kTLS read through our custom BIO.\n");
        goto cleanup;
    }
    /* Reuse application storage for the echo. No separate response allocation. */
    for (size_t sent = 0; sent < used;) {
        size_t count = 0;
        ERR_clear_error();
        errno = 0;
        int result = SSL_write_ex(ssl, plaintext + sent, used - sent, &count);
        int saved_errno = errno;
        if (result == 1)
            sent += count;
        else {
            int error = SSL_get_error(ssl, result);
            if (!retry_ssl(fd, error, saved_errno, deadline))
                goto cleanup;
        }
    }
    for (;;) {
        ERR_clear_error();
        errno = 0;
        int result = SSL_shutdown(ssl);
        int saved_errno = errno;
        if (result >= 0)
            break; /* Sent close_notify; no need to await peer for this demo. */
        int error = SSL_get_error(ssl, result);
        if (!retry_ssl(fd, error, saved_errno, deadline))
            goto cleanup;
    }
    printf("PASS mode=%s echoed_bytes=%zu filter_reads=%u filter_writes=%u filter_ctrls=%u\n",
           userspace ? "userspace-control" : "ktls-rx", used, state.read_calls, state.write_calls, state.ctrl_calls);
    status = 0;

cleanup:
    if (status != 0)
        ERR_print_errors_fp(stderr);
    BIO_free_all(filter);
    BIO_free(socket_bio);
    SSL_free(ssl);
    BIO_meth_free(method);
    SSL_CTX_free(ctx);
    if (fd >= 0)
        close(fd);
    if (listener >= 0)
        close(listener);
    return status;
}
