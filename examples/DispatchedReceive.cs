using System.Buffers;
using System.Threading.Channels;
using System.Net.Transport;

namespace NetworkTransportExamples;

public sealed class DispatchedReceiveApplication : TransportApplication, IAsyncDisposable
{
    private readonly Channel<ReceivedRequest> _requests;
    private readonly Func<ReadOnlySequence<byte>, ValueTask<byte[]>> _handler;
    private readonly Task _processor;

    public DispatchedReceiveApplication(Func<ReadOnlySequence<byte>, ValueTask<byte[]>> handler)
    {
        _handler = handler;
        _requests = Channel.CreateBounded<ReceivedRequest>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _processor = ProcessRequestsAsync();
    }

    protected override void OnReceive(ref TransportReceiveContext context)
    {
        ReceivedRequest request;
        if (context.Payload.IsEmpty)
        {
            request = ReceivedRequest.Completed(context.Connection);
        }
        else if (context.TryRetainPayload(out TransportReceiveLease? lease))
        {
            request = ReceivedRequest.FromLease(context.Connection, lease, context.IsCompleted);
        }
        else
        {
            request = ReceivedRequest.FromCopy(context.Connection, context.Payload.ToArray(), context.IsCompleted);
        }

        if (!_requests.Writer.TryWrite(request))
        {
            request.Dispose();
            context.Connection.Abort(new InvalidOperationException("The application queue is full."));
        }
    }

    protected override void OnWriteCompleted(ref TransportWriteCompletedContext context)
    {
        if (context.Error is not null)
        {
            context.Connection.Abort(context.Error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _requests.Writer.TryComplete();
        await _processor.ConfigureAwait(false);
    }

    private async Task ProcessRequestsAsync()
    {
        await foreach (ReceivedRequest request in _requests.Reader.ReadAllAsync())
        {
            using (request)
            {
                if (!request.Buffer.IsEmpty)
                {
                    byte[] response = await _handler(request.Buffer).ConfigureAwait(false);

                    request.Connection.Send(response);
                }

                if (request.IsCompleted)
                {
                    request.Connection.ShutdownWrite();
                }
            }
        }
    }

    private sealed class ReceivedRequest : IDisposable
    {
        private readonly TransportReceiveLease? _lease;
        private readonly byte[]? _copy;

        private ReceivedRequest(TransportConnection connection, TransportReceiveLease? lease, byte[]? copy, bool isCompleted)
        {
            Connection = connection;
            _lease = lease;
            _copy = copy;
            IsCompleted = isCompleted;
        }

        public TransportConnection Connection { get; }

        public ReadOnlySequence<byte> Buffer => _lease is not null
            ? _lease.Buffer
            : _copy is not null
                ? new ReadOnlySequence<byte>(_copy)
                : ReadOnlySequence<byte>.Empty;

        public bool IsCompleted { get; }

        public static ReceivedRequest Completed(TransportConnection connection) => new(connection, null, null, true);

        public static ReceivedRequest FromLease(TransportConnection connection, TransportReceiveLease lease, bool isCompleted) => new(connection, lease, null, isCompleted);

        public static ReceivedRequest FromCopy(TransportConnection connection, byte[] copy, bool isCompleted) => new(connection, null, copy, isCompleted);

        public void Dispose() => _lease?.Dispose();
    }
}
