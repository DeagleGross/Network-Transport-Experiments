**The most important thing to understand is this: a BIO is an object with “read”, “write”, and “control” functions. It is not necessarily a buffer.**

In our sample, OpenSSL calls those functions. Our custom BIO forwards them to another BIO, which knows how to communicate with the socket.

Let’s build the picture from the beginning.

## 1. There are three different things: the socket, the TLS connection, and the BIO

### The socket: a connection managed by the kernel

Our program calls:

```c
fd = accept4(listener, ...);
```

Suppose it returns `fd = 7`.

That integer means:

> “The kernel knows about a TCP connection. My program refers to it using number 7.”

It is **not a pointer to received bytes**. It is a handle to a connection.

Without TLS, we could call:

```c
recv(7, buffer, capacity, 0);
send(7, buffer, length, 0);
```

Those calls cross into the kernel.

### The TLS connection: an OpenSSL object

We create:

```c
SSL *ssl = SSL_new(ctx);
```

This object tracks one TLS connection: its handshake progress, negotiated settings, record-processing state, and buffers.

The application uses:

```c
SSL_read_ex(ssl, ...);
SSL_write_ex(ssl, ...);
```

rather than interpreting TLS records itself.

But OpenSSL needs to know:

> “Where do I obtain incoming bytes? Where do I put outgoing bytes?”

**That is what the BIO supplies.**

### The BIO: OpenSSL’s I/O interface

A BIO provides operations conceptually like:

```text
Read(destination, capacity)
Write(source, length)
Control(command, arguments)
```

Different BIO types implement those differently:

| BIO type | What its read/write functions do |
|---|---|
| Memory BIO | Read/write an internal memory buffer |
| Socket BIO | Read/write a socket |
| Our custom filter BIO | Call the next BIO’s read/write functions, while recording observations |

**Our filter does not itself store the message.**

---

## 2. A C# analogy for a BIO

This is an analogy, not the actual OpenSSL API:

```csharp
interface IBio
{
    int Read(Span<byte> destination);
    int Write(ReadOnlySpan<byte> source);
    object? Control(int command, object? argument);
}
```

A socket implementation might resemble:

```csharp
class SocketBio
{
    Socket socket;

    int Read(Span<byte> destination)
    {
        return socket.Receive(destination);
    }
}
```

Our custom filter is more like:

```csharp
class ObservingBio
{
    IBio next;
    int readCalls;

    int Read(Span<byte> destination)
    {
        readCalls++;
        return next.Read(destination);
    }
}
```

Notice that the filter forwards **the same destination**. It does not receive into its own temporary array and then copy into the caller’s array.

OpenSSL implements this extensibility with C function pointers rather than C# interfaces.

---

## 3. First we define the custom BIO’s functions

These lines create a **method table**:

```c
method = BIO_meth_new(
    type | BIO_TYPE_FILTER,
    "observed kTLS passthrough");
```

Think:

> “Create a description of my BIO type.”

Then we populate that description:

```c
BIO_meth_set_create(method, filter_create);
BIO_meth_set_destroy(method, filter_destroy);
BIO_meth_set_read_ex(method, filter_read);
BIO_meth_set_write_ex(method, filter_write);
BIO_meth_set_ctrl(method, filter_ctrl);
```

These calls **do not read or write anything**. They register functions to call later.

For example:

```c
BIO_meth_set_read_ex(method, filter_read);
```

means:

> “When someone asks an instance of this BIO type to read, call my `filter_read` function.”

`BIO_METHOD` is therefore similar to an interface’s implementation table. A `BIO` is an instance using that table.

---

## 4. Now we create the actual objects

Here is the important setup block, simplified:

```c
ssl = SSL_new(ctx);

filter = BIO_new(method);

socket_bio = BIO_new_socket(fd, BIO_NOCLOSE);

BIO_set_data(filter, &state);

BIO_push(filter, socket_bio);

SSL_set_bio(ssl, filter, filter);
```

Let’s explain every call.

### `SSL_new(ctx)`

Creates the TLS state for **one connection**.

`ctx` is the previously configured `SSL_CTX`: the template containing certificate configuration, allowed protocols, and options.

```text
SSL_CTX: shared configuration
SSL:     one connection's TLS state
```

### `BIO_new(method)`

Creates one BIO object using our custom method table.

It invokes our `filter_create()` initialization callback.

**It does not create a socket. It does not start a handshake. It does not allocate a message buffer in our implementation.**

At this point:

```text
filter → a custom BIO instance
```

### `BIO_new_socket(fd, BIO_NOCLOSE)`

Creates OpenSSL’s built-in socket BIO **around the already accepted socket**.

It does not create another TCP connection.

```text
socket_bio → wrapper around fd 7
```

`BIO_NOCLOSE` means:

> “When this BIO is freed, do not close fd 7. My application will close it.”

This makes descriptor ownership explicit.

### `BIO_set_data(filter, &state)`

Attaches an application-defined pointer to the filter.

Our `state` contains counters:

```c
struct observations {
    unsigned read_calls;
    unsigned write_calls;
    // Other observations...
};
```

Inside a callback, we recover that pointer:

```c
struct observations *state = BIO_get_data(bio);
```

**`BIO_set_data` does not copy message bytes.** It stores a pointer to our bookkeeping.

### `BIO_push(filter, socket_bio)`

Connects the objects into a chain:

```text
filter → socket_bio
```

Despite its name, `BIO_push` is not sending bytes. It means:

> “Put this filter in front of that BIO.”

Inside our filter, this finds the next object:

```c
BIO *next = BIO_next(bio);
```

Here, `next` is the socket BIO.

### `SSL_set_bio(ssl, filter, filter)`

Tells OpenSSL:

> “For this TLS connection, use this chain for reading and writing.”

The arguments are:

```c
SSL_set_bio(ssl, read_bio, write_bio);
```

We pass the same chain for both.

Now the object graph is:

```text
SSL object
    ├─ read BIO  ─┐
    └─ write BIO ─┴→ our filter → socket BIO → fd 7
```

OpenSSL now owns the BIO chain. Our later assignments of `filter = NULL` are merely cleanup bookkeeping; they do not disconnect or destroy the objects.

---

## 5. Who calls whom when the application reads?

The application calls:

```c
SSL_read_ex(ssl, plaintext, sizeof(plaintext), &count);
```

That means:

> “Give me application plaintext. Put it in my `plaintext` array.”

The application does **not** directly call our BIO read function.

Instead, OpenSSL may need input, so the call stack becomes:

```text
main()
  → SSL_read_ex()
      → OpenSSL needs record input
          → BIO_read_ex(our filter, destination, capacity, ...)
              → our filter_read()
                  → BIO_read_ex(socket BIO, same destination, ...)
                      → socket I/O syscall
                          → kernel
```

Everything above the syscall executes in **our application’s thread**.

**The kernel does not invoke `filter_read()` or call back into OpenSSL.** The application calls down into the kernel; the syscall returns a result.

If OpenSSL already has enough buffered input, it may satisfy `SSL_read_ex()` without descending all the way to the socket.

---

## 6. What does `filter_read()` do?

Its essential behavior is:

```c
static int filter_read(
    BIO *bio,
    char *destination,
    size_t capacity,
    size_t *count)
{
    BIO *socket_bio = BIO_next(bio);

    return BIO_read_ex(
        socket_bio,
        destination,
        capacity,
        count);
}
```

Our real function also updates counters and propagates retry information.

### What do these arguments mean?

| Argument | Meaning |
|---|---|
| `bio` | The filter object being called |
| `destination` | Address where OpenSSL wants input placed |
| `capacity` | Maximum number of bytes that may be written there |
| `count` | Address of a variable in which to report the number of bytes read |

The `_ex` callback returns `1` for success or `0` for failure/retry. The number of bytes is returned through `*count`.

The filter passes `destination` through unchanged:

```text
OpenSSL supplies destination B
    → our filter forwards destination B
    → socket BIO fills destination B
```

**There is no filter-owned temporary buffer C.**

However, destination B is not necessarily the application’s `plaintext` array. OpenSSL can use its own record buffer and later deliver plaintext into the array passed to `SSL_read_ex()`.

That distinction explains why our output printed two different addresses.

---

## 7. What happens before kTLS is active?

Normally:

```text
Network sends encrypted TLS records
    ↓
Kernel TCP receives those bytes
    ↓
Socket BIO reads encrypted bytes into userspace
    ↓
OpenSSL processes/decrypts records
    ↓
Application receives plaintext
```

Our custom filter just sits between OpenSSL and the socket BIO. It does not decrypt anything.

The TLS handshake establishes the state required for later encrypted communication. OpenSSL performs that handshake using:

```c
SSL_do_handshake(ssl);
```

During this call, OpenSSL may both read and write through the BIO chain.

---

## 8. How does the kernel learn enough to perform TLS?

There are **three separate steps**.

### Step A: load the kernel capability

You ran:

```bash
sudo modprobe tls
```

This makes the kernel’s TLS implementation available.

It does not automatically change existing sockets.

### Step B: attach the TLS facility to our socket

The demo calls:

```c
setsockopt(fd, IPPROTO_TCP, TCP_ULP, "tls", sizeof("tls"));
```

This tells the kernel:

> “This TCP socket may use the TLS upper-layer facility.”

**It does not yet give the kernel the connection’s keys or activate data encryption/decryption.**

### Step C: OpenSSL configures the negotiated TLS state

We previously requested:

```c
SSL_CTX_set_options(ctx, SSL_OP_ENABLE_KTLS);
```

That tells OpenSSL:

> “Attempt to use kTLS when this connection’s configuration supports it.”

During the handshake, OpenSSL derives the TLS state. At the relevant transition, its kTLS integration asks the BIO to configure the kernel.

That request travels through:

```text
OpenSSL
    → BIO control operation
    → our filter_ctrl()
    → socket BIO's control implementation
    → kernel socket configuration
```

The socket BIO knows how to translate that operation into the necessary socket calls.

**Our filter does not derive keys or implement the kernel TLS setup structures. It forwards the request.**

---

## 9. What is `BIO_ctrl()`?

Read and write transfer data. **Control operations configure or query behavior.**

Conceptually:

```text
Read:    “Get bytes.”
Write:   “Send bytes.”
Control: “Flush,” “what fd do you use?”,
         “configure kTLS,” “is kTLS RX active?”
```

The interface is generic:

```c
long BIO_ctrl(
    BIO *bio,
    int command,
    long argument,
    void *pointer);
```

`command` determines what the other arguments mean.

Our implementation forwards the request:

```c
return BIO_ctrl(BIO_next(bio), command, argument, pointer);
```

This is important. If we instead returned “success” for every command, OpenSSL could believe something had been configured when nothing happened.

After the handshake, we ask:

```c
BIO_get_ktls_recv(SSL_get_rbio(ssl));
BIO_get_ktls_send(SSL_get_wbio(ssl));
```

These are queries through the same chain.

- `SSL_get_rbio()` returns the TLS connection’s read BIO—our filter.
- The query reaches `filter_ctrl()`.
- It is forwarded to the socket BIO.
- The socket BIO reports its actual kTLS state.

Our verified result was:

```text
ktls_rx=1
ktls_tx=1
```

---

## 10. What changes when we read with kTLS active?

Now the kernel performs the configured receive-side record decryption:

```text
Encrypted network record
    ↓
Kernel TCP + kTLS
    → decrypt/check record
    ↓
Socket BIO receives decrypted record data
    ↓
Our filter returns it to OpenSSL
    ↓
SSL_read_ex delivers application plaintext
```

**OpenSSL does not blindly decrypt that plaintext a second time.** Its record layer knows that kTLS is active.

There is one implementation detail in OpenSSL 3.0.13: the kernel returns decrypted data and record-type information. The socket BIO reconstructs a small record header expected by OpenSSL’s internal interface.

That is why our output showed:

```text
Application plaintext: 29 bytes
Custom BIO read:        34 bytes
```

The extra five bytes are synthetic record framing added by the socket BIO. They are not extra application data.

### Where is the memory?

In our observed run:

```text
B: OpenSSL's BIO-read destination
   contains synthetic header + decrypted record data

A: application's plaintext[] array
   receives the 29 application bytes from SSL_read_ex()
```

Our filter does not allocate a third payload buffer between them.

**“Filter copy bytes = 0” means only that our filter adds no payload copy. It does not mean A and B are the same allocation.**

---

## 11. What happens if there is no data yet?

The sockets are nonblocking.

The socket BIO cannot produce data immediately, so it marks the operation as retryable. Our filter copies those retry flags upward:

```c
BIO_copy_next_retry(bio);
```

OpenSSL then returns an error classification such as:

```text
SSL_ERROR_WANT_READ
```

This means:

> “The TLS operation is not finished. Retry after input becomes available.”

Our outer application waits with:

```c
poll(..., POLLIN, ...);
```

Then retries `SSL_read_ex()`.

Likewise, `WANT_WRITE` means wait for writable progress.

**The BIO callback does not start a background thread or independently wait for data.** It reports what happened. The application’s loop decides when to retry.

---

## 12. How do we send the echo?

After receiving the line, the application already has:

```c
unsigned char plaintext[16384];
```

We reuse that storage:

```c
SSL_write_ex(ssl, plaintext, used, &written);
```

The data flows down through OpenSSL and the BIO chain.

With kTLS TX active, the socket integration sends data using the kernel’s configured TLS record path. The kernel performs the corresponding encryption.

We then call:

```c
SSL_shutdown(ssl);
```

to send TLS `close_notify`, free the SSL/BIO objects, and close the fd.

No separate response payload allocation is necessary for this echo.

---

## 13. What the sample is actually teaching

The result is **not**:

> “We replaced OpenSSL’s socket BIO with a special buffer and achieved zero-copy TLS.”

The result is:

> **“We inserted our own BIO into OpenSSL’s I/O chain, preserved the socket BIO’s behavior and control interface, and verified that kernel TLS still works.”**

Our custom layer currently adds observation, not a new transport architecture.

The shortest mental model is:

```text
BIO_METHOD = which functions implement this BIO type
BIO        = one instance using those functions
BIO_new    = create an instance
BIO_set_data = attach our bookkeeping pointer
BIO_push   = connect it to the next BIO
SSL_set_bio = tell OpenSSL which chain to use
BIO_ctrl   = configure/query that chain
```

And the ownership of the main work is:

```text
Application: starts operations and supplies application buffers
OpenSSL:    manages TLS protocol/record state
Custom BIO: observes and forwards I/O/control calls
Socket BIO: translates those calls to socket/kTLS operations
Kernel:     manages TCP and performs enabled kTLS record work
```

**The kernel never knows that our custom filter exists. It only sees socket operations and configuration arriving from userspace.**
