#define _GNU_SOURCE
#include <arpa/inet.h>
#include <errno.h>
#include <liburing.h>
#include <netinet/tcp.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/socket.h>
#include <unistd.h>

enum { QUEUE_DEPTH = 1024, BUFFER_COUNT = 4096, BUFFER_SIZE = 4096, BUFFER_GROUP = 1, INPUT_CAPACITY = 65536, HEADER_LIMIT = 16384 };
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
    int sending;
    int close_after_send;
    int read_eof;
    int failed;
    size_t sent;
    const char *output;
    size_t output_length;
    size_t input_length;
    char input[INPUT_CAPACITY];
    struct operation receive;
    struct operation send;
    struct operation cancel;
    struct connection *previous;
    struct connection *next;
};

#define RESPONSE(version, connection) \
    "HTTP/" version " 200 OK\r\nContent-Length: 12\r\nContent-Type: text/plain\r\nConnection: " connection "\r\n\r\nHello world\n"
static const char response_keep[] = RESPONSE("1.1", "keep-alive");
static const char response_close[] = RESPONSE("1.1", "close");
static const char response_10_keep[] = RESPONSE("1.0", "keep-alive");
static const char response_10_close[] = RESPONSE("1.0", "close");
static const char bad_request[] = "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

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
static unsigned long accepted_count, response_count, receive_submissions, receive_completions;
static unsigned long cancel_count, error_count;
static unsigned long accept_submissions, accept_completions;
static unsigned long receive_data_completions, rejected_requests;

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
    io_uring_prep_multishot_accept(sqe, listener, NULL, NULL, SOCK_CLOEXEC);
    io_uring_sqe_set_data(sqe, &accept_operation);
    accept_active = 1;
    accept_submissions++;
}

static void submit_receive(struct connection *connection)
{
    struct io_uring_sqe *sqe = get_sqe();

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
    io_uring_prep_send(sqe, connection->fd, connection->output + connection->sent,
                      connection->output_length - connection->sent, MSG_NOSIGNAL);
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

static int equals(const char *text, size_t length, const char *expected)
{
    return length == strlen(expected) && strncasecmp(text, expected, length) == 0;
}

/* Returns 1 for a complete framed request, 0 for more bytes, -1 for rejection. */
static int parse_request(struct connection *connection, size_t *length, int *close_after, int *http10)
{
    const char *start = connection->input;
    size_t available = connection->input_length;
    const char *end = memmem(start, available, "\r\n\r\n", 4);
    if (end == NULL)
        return available >= HEADER_LIMIT ? -1 : 0;
    size_t header_length = (size_t)(end + 4 - start);
    if (header_length > HEADER_LIMIT)
        return -1;
    const char *line = memmem(start, header_length, "\r\n", 2);
    const char *space = memchr(start, ' ', (size_t)(line - start));
    if (space == NULL || space == start)
        return -1;
    const char *version = memchr(space + 1, ' ', (size_t)(line - space - 1));
    if (version == NULL || version == space + 1 || line - version != 9)
        return -1;
    *http10 = memcmp(version + 1, "HTTP/1.0", 8) == 0;
    if (!*http10 && memcmp(version + 1, "HTTP/1.1", 8) != 0)
        return -1;

    size_t body_length = 0;
    int has_length = 0, keep = 0, close_requested = 0;
    const char *header = line + 2;
    while (header < end) {
        line = memmem(header, (size_t)(end + 2 - header), "\r\n", 2);
        if (line == NULL)
            return -1;
        const char *colon = memchr(header, ':', (size_t)(line - header));
        if (colon == NULL || colon == header)
            return -1;
        const char *value = colon + 1;
        const char *value_end = line;
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
                if (body_length > (INPUT_CAPACITY - digit) / 10)
                    return -1;
                body_length = body_length * 10 + digit;
            }
        } else if (equals(header, (size_t)(colon - header), "Transfer-Encoding")) {
            return -1;
        }
        header = line + 2;
    }
    if (body_length > INPUT_CAPACITY - header_length)
        return -1;
    *length = header_length + body_length;
    *close_after = close_requested || (*http10 && !keep);
    return available >= *length ? 1 : 0;
}

static void drive_connection(struct connection *connection)
{
    if (stopping || connection->failed || connection->close_after_send)
        return;

    if (!connection->sending && connection->input_length != 0) {
        size_t length = 0;
        int close_after = 0, http10 = 0;
        int parsed = parse_request(connection, &length, &close_after, &http10);
        if (parsed == 0 && connection->read_eof)
            parsed = -1;
        if (parsed != 0) {
            if (parsed < 0) {
                rejected_requests++;
                connection->output = bad_request;
                connection->input_length = 0;
                close_after = 1;
            } else {
                connection->input_length -= length;
                memmove(connection->input, connection->input + length, connection->input_length);
                if (connection->read_eof && connection->input_length == 0)
                    close_after = 1;
                connection->output = http10
                    ? (close_after ? response_10_close : response_10_keep)
                    : (close_after ? response_close : response_keep);
            }
            connection->close_after_send = close_after;
            connection->output_length = strlen(connection->output);
            connection->sent = 0;
            connection->sending = 1;
            submit_send(connection);
            if (close_after) {
                cancel_receive(connection);
                return;
            }
        }
    }

    /*
     * Bound pipelined input. Pause/rearm the receive, not the send lane.
     * Cancellation CQEs can race with already completed receives.
     */
    if (connection->sending && connection->input_length > INPUT_CAPACITY / 2) {
        cancel_receive(connection);
    } else if (!connection->read_eof && !connection->receive_active && !connection->cancel_pending) {
        submit_receive(connection);
    }
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
        accept_completions++;
        int terminal = !(flags & IORING_CQE_F_MORE);
        if (terminal)
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
        if (!stopping && terminal)
            submit_accept();
        return;
    }

    if (operation->kind == CANCEL) {
        if (result < 0 && result != -ENOENT && result != -EALREADY)
            report_io_error("cancel", result);
        if (connection == NULL)
            accept_cancel_active = 0;
        else {
            connection->cancel_pending = 0;
            drive_connection(connection);
            release_connection(connection);
        }
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
             * Copy into bounded per-connection framing storage so fragmented
             * and pipelined requests survive recycling this receive buffer.
             */
            receive_data_completions++;
            if (!connection->failed && !connection->close_after_send && !stopping) {
                if ((size_t)result > INPUT_CAPACITY - connection->input_length) {
                    report_io_error("pipelined input limit", -EMSGSIZE);
                    connection->failed = 1;
                } else {
                    memcpy(connection->input + connection->input_length, storage + (size_t)id * BUFFER_SIZE, (size_t)result);
                    connection->input_length += (size_t)result;
                }
            }
        } else if (result == 0) {
            connection->read_eof = 1;
        } else if (result < 0 && result != -ECANCELED) {
            report_io_error("receive", result);
            connection->failed = 1;
        }

        /* Ordinary (not incremental) provided buffers transfer per CQE. */
        if (flags & IORING_CQE_F_BUFFER)
            add_buffer(flags >> IORING_CQE_BUFFER_SHIFT);

        if (connection->failed || stopping)
            cancel_receive(connection);
        else
            drive_connection(connection);
        if (terminal)
            release_connection(connection);
        return;
    }

    if (result > 0) {
        connection->sent += (size_t)result;
        if (connection->sent < connection->output_length)
            submit_send(connection);
        else {
            response_count++;
            connection->sending = 0;
            if (connection->close_after_send)
                cancel_receive(connection);
            else
                drive_connection(connection);
        }
    } else {
        report_io_error("send", result == 0 ? -EPIPE : result);
        connection->failed = 1;
        cancel_receive(connection);
    }
    release_connection(connection);
}

int main(int argc, char **argv)
{
    char *end;
    long port = argc == 2 ? strtol(argv[1], &end, 10) : 0;
    if (argc != 2 || *end != '\0' || port < 1 || port > 65535) {
        fprintf(stderr, "Usage: %s PORT\n", argv[0]);
        return EXIT_FAILURE;
    }
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
    printf("READY mode=oneshot accept=multishot pid=%ld port=%ld\n", (long)getpid(), port);
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
    printf("STATS accepted=%lu responses=%lu recv_submissions=%lu recv_cqes=%lu cancels=%lu errors=%lu accept_submissions=%lu accept_cqes=%lu recv_data_cqes=%lu rejected=%lu\n",
           accepted_count, response_count, receive_submissions, receive_completions, cancel_count, error_count,
           accept_submissions, accept_completions, receive_data_completions, rejected_requests);
    return error_count == 0 ? EXIT_SUCCESS : EXIT_FAILURE;
}
