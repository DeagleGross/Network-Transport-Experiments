#define _GNU_SOURCE
#include "server.h"
#include <arpa/inet.h>
#include <errno.h>
#include <liburing.h>
#include <netinet/tcp.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

enum { QUEUE_DEPTH = 1024, BUFFER_COUNT = 4096, BUFFER_SIZE = 4096, BUFFER_GROUP = 1 };
enum operation_kind { ACCEPT, RECEIVE, SEND, CANCEL };

struct connection;

struct operation {
    enum operation_kind kind;
    struct connection *connection;
};

struct connection {
    int fd;
    unsigned references;
    int receive_active;
    int cancel_pending;
    int responding;
    size_t sent;
    struct operation receive;
    struct operation send;
    struct operation cancel;
    struct connection *previous;
    struct connection *next;
};

static const char response[] =
    "HTTP/1.1 200 OK\r\n"
    "Content-Length: 12\r\n"
    "Content-Type: text/plain\r\n"
    "Connection: close\r\n"
    "\r\n"
    "Hello world\n";

static struct io_uring ring;
static struct io_uring_buf_ring *buffers;
static unsigned char *storage;
static struct connection *connections;
static struct operation accept_operation = { ACCEPT, NULL };
static struct operation accept_cancel = { CANCEL, NULL };
static volatile sig_atomic_t stopping;
static int listener;
static int accept_active;
static int accept_cancel_active;
static int use_multishot;
static unsigned long accepted_count, response_count, receive_submissions, receive_completions;
static unsigned long cancel_count, error_count;

static void fail(const char *what, int error)
{
    fprintf(stderr, "%s: %s\n", what, strerror(error));
    exit(EXIT_FAILURE);
}

static void on_signal(int number)
{
    (void)number;
    stopping = 1;
}

static struct io_uring_sqe *get_sqe(void)
{
    struct io_uring_sqe *sqe = io_uring_get_sqe(&ring);
    if (sqe == NULL) {
        int result = io_uring_submit(&ring);
        if (result < 0)
            fail("io_uring_submit", -result);
        sqe = io_uring_get_sqe(&ring);
    }
    if (sqe == NULL)
        fail("submission queue exhausted", ENOBUFS);
    return sqe;
}

static void add_buffer(unsigned id)
{
    io_uring_buf_ring_add(buffers, storage + (size_t)id * BUFFER_SIZE,
                         BUFFER_SIZE, id, BUFFER_COUNT - 1, 0);
    io_uring_buf_ring_advance(buffers, 1);
}

static void submit_accept(void)
{
    struct io_uring_sqe *sqe = get_sqe();
    io_uring_prep_accept(sqe, listener, NULL, NULL, SOCK_CLOEXEC);
    io_uring_sqe_set_data(sqe, &accept_operation);
    accept_active = 1;
}

static void submit_receive(struct connection *connection)
{
    struct io_uring_sqe *sqe = get_sqe();

    /* Both modes use the same pool. Only the receive operation differs. */
    if (use_multishot)
        io_uring_prep_recv_multishot(sqe, connection->fd, NULL, 0, 0);
    else
        io_uring_prep_recv(sqe, connection->fd, NULL, 0, 0);

    sqe->flags |= IOSQE_BUFFER_SELECT;
    sqe->buf_group = BUFFER_GROUP;
    io_uring_sqe_set_data(sqe, &connection->receive);
    connection->receive_active = 1;
    connection->references++;
    receive_submissions++;
}

static void submit_send(struct connection *connection)
{
    struct io_uring_sqe *sqe = get_sqe();
    io_uring_prep_send(sqe, connection->fd, response + connection->sent,
                      sizeof(response) - 1 - connection->sent, MSG_NOSIGNAL);
    io_uring_sqe_set_data(sqe, &connection->send);
    connection->references++;
}

static void cancel_receive(struct connection *connection)
{
    if (!connection->receive_active || connection->cancel_pending)
        return;

    struct io_uring_sqe *sqe = get_sqe();
    io_uring_prep_cancel(sqe, &connection->receive, 0);
    io_uring_sqe_set_data(sqe, &connection->cancel);
    connection->cancel_pending = 1;
    connection->references++;
    cancel_count++;
}

static void release_connection(struct connection *connection)
{
    if (--connection->references != 0)
        return;

    if (connection->previous != NULL)
        connection->previous->next = connection->next;
    else
        connections = connection->next;
    if (connection->next != NULL)
        connection->next->previous = connection->previous;
    close(connection->fd);
    free(connection);
}

static void report_io_error(const char *operation, int result)
{
    error_count++;
    /* Log a bounded sample; the final counter includes every failure. */
    if (error_count <= 8)
        fprintf(stderr, "%s: %s\n", operation, strerror(-result));
}

static void complete(struct operation *operation, int result, unsigned flags)
{
    struct connection *connection = operation->connection;
    if (operation->kind == ACCEPT) {
        accept_active = 0;
        if (result >= 0) {
            if (stopping) {
                close(result);
            } else {
                connection = calloc(1, sizeof(*connection));
                if (connection == NULL)
                    fail("calloc", ENOMEM);
                connection->fd = result;
                connection->receive = (struct operation){ RECEIVE, connection };
                connection->send = (struct operation){ SEND, connection };
                connection->cancel = (struct operation){ CANCEL, connection };
                connection->next = connections;
                if (connections != NULL)
                    connections->previous = connection;
                connections = connection;
                int enabled = 1;
                if (setsockopt(result, IPPROTO_TCP, TCP_NODELAY, &enabled, sizeof(enabled)) != 0)
                    fail("TCP_NODELAY", errno);
                accepted_count++;
                submit_receive(connection);
            }
        } else if (result != -ECANCELED) {
            report_io_error("accept", result);
            stopping = 1;
        }
        if (!stopping)
            submit_accept();
        return;
    }

    if (operation->kind == CANCEL) {
        if (result < 0 && result != -ENOENT && result != -EALREADY)
            report_io_error("cancel", result);
        if (connection == NULL)
            accept_cancel_active = 0;
        else
            release_connection(connection);
        return;
    }

    if (operation->kind == RECEIVE) {
        receive_completions++;
        int terminal = !(flags & IORING_CQE_F_MORE);
        if (terminal)
            connection->receive_active = 0;

        if (result > 0) {
            if (!(flags & IORING_CQE_F_BUFFER))
                fail("receive did not select a buffer", EIO);
            unsigned id = flags >> IORING_CQE_BUFFER_SHIFT;
            if (id >= BUFFER_COUNT || result > BUFFER_SIZE)
                fail("invalid receive buffer", EIO);

            /*
             * The request content is intentionally ignored. At this point
             * storage + id * BUFFER_SIZE contains result readable bytes.
             * Real parsing must finish using them before add_buffer().
             */
            if (!connection->responding && !stopping) {
                connection->responding = 1;
                submit_send(connection);
            }
        } else if (result < 0 && result != -ECANCELED) {
            report_io_error("receive", result);
        }

        /* Ordinary (not incremental) provided buffers transfer per CQE. */
        if (flags & IORING_CQE_F_BUFFER)
            add_buffer(flags >> IORING_CQE_BUFFER_SHIFT);

        /* One response per connection: do not leave a multishot read alive. */
        if (!terminal)
            cancel_receive(connection);
        else
            release_connection(connection);
        return;
    }

    if (result > 0) {
        connection->sent += (size_t)result;
        if (connection->sent < sizeof(response) - 1)
            submit_send(connection);
        else
            response_count++;
    } else {
        report_io_error("send", result == 0 ? -EPIPE : result);
    }
    release_connection(connection);
}

int run_server(int argc, char **argv, int multishot)
{
    char *end;
    long port = argc == 2 ? strtol(argv[1], &end, 10) : 0;
    if (argc != 2 || *end != '\0' || port < 1 || port > 65535) {
        fprintf(stderr, "Usage: %s PORT\n", argv[0]);
        return EXIT_FAILURE;
    }
    use_multishot = multishot;
    struct sigaction action = { .sa_handler = on_signal };
    sigemptyset(&action.sa_mask);
    if (sigaction(SIGINT, &action, NULL) || sigaction(SIGTERM, &action, NULL))
        fail("sigaction", errno);
    signal(SIGPIPE, SIG_IGN);

    int result = io_uring_queue_init(QUEUE_DEPTH, &ring, 0);
    if (result < 0)
        fail("io_uring_queue_init", -result);
    if (posix_memalign((void **)&storage, 4096, (size_t)BUFFER_COUNT * BUFFER_SIZE) != 0)
        fail("allocate receive pool", ENOMEM);
    buffers = io_uring_setup_buf_ring(&ring, BUFFER_COUNT, BUFFER_GROUP, 0, &result);
    if (buffers == NULL)
        fail("io_uring_setup_buf_ring (Linux 6.0+ required)", -result);
    for (unsigned id = 0; id < BUFFER_COUNT; id++)
        add_buffer(id);

    listener = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (listener < 0)
        fail("socket", errno);
    int enabled = 1;
    if (setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, &enabled, sizeof(enabled)) ||
        setsockopt(listener, SOL_SOCKET, SO_REUSEPORT, &enabled, sizeof(enabled)))
        fail("reuse socket options", errno);
    struct sockaddr_in address = {
        .sin_family = AF_INET,
        .sin_port = htons((uint16_t)port),
        .sin_addr.s_addr = htonl(INADDR_LOOPBACK)
    };
    if (bind(listener, (struct sockaddr *)&address, sizeof(address)) || listen(listener, 4096))
        fail("bind/listen", errno);
    submit_accept();
    printf("READY mode=%s pid=%ld port=%ld\n", multishot ? "multishot" : "oneshot", (long)getpid(), port);
    fflush(stdout);

    int shutdown_started = 0;
    while (accept_active || accept_cancel_active || connections != NULL) {
        if (stopping && !shutdown_started) {
            shutdown_started = 1;
            if (accept_active) {
                struct io_uring_sqe *sqe = get_sqe();
                io_uring_prep_cancel(sqe, &accept_operation, 0);
                io_uring_sqe_set_data(sqe, &accept_cancel);
                accept_cancel_active = 1;
            }
            for (struct connection *c = connections; c != NULL; c = c->next)
                cancel_receive(c);
        }
        result = io_uring_submit(&ring);
        if (result < 0 && result != -EINTR)
            fail("io_uring_submit", -result);

        struct io_uring_cqe *cqe;
        struct __kernel_timespec timeout = { .tv_sec = 0, .tv_nsec = 100000000 };
        result = io_uring_wait_cqe_timeout(&ring, &cqe, &timeout);
        if (result == -EINTR || result == -ETIME)
            continue;
        if (result < 0)
            fail("io_uring_wait_cqe", -result);

        unsigned head, count = 0;
        io_uring_for_each_cqe(&ring, head, cqe) {
            complete(io_uring_cqe_get_data(cqe), cqe->res, cqe->flags);
            count++;
        }
        io_uring_cq_advance(&ring, count);
    }

    close(listener);
    result = io_uring_free_buf_ring(&ring, buffers, BUFFER_COUNT, BUFFER_GROUP);
    if (result < 0)
        fail("io_uring_free_buf_ring", -result);
    io_uring_queue_exit(&ring);
    free(storage);
    printf("STATS accepted=%lu responses=%lu recv_submissions=%lu recv_cqes=%lu cancels=%lu errors=%lu\n",
           accepted_count, response_count, receive_submissions, receive_completions, cancel_count, error_count);
    return error_count == 0 ? EXIT_SUCCESS : EXIT_FAILURE;
}
