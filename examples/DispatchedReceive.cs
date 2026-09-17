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
        if (context.Payload.IsEmpty)
        {
            if (context.IsCompleted)
            {
                context.Connection.ShutdownWrite();
            }

            return;
        }

        context.StopReceiving();

        ReceivedRequest request;
        if (context.TryRetainPayload(out TransportReceiveLease? lease))
        {
            request = ReceivedRequest.FromLease(context.Connection, lease);
        }
        else
        {
            request = ReceivedRequest.FromCopy(context.Connection, context.Payload.ToArray());
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
                byte[] response = await _handler(request.Buffer).ConfigureAwait(false);

                request.Connection.Send(response);
                request.Connection.ShutdownWrite();
            }
        }
    }

    private sealed class ReceivedRequest : IDisposable
    {
        private readonly TransportReceiveLease? _lease;
        private readonly byte[]? _copy;

        private ReceivedRequest(TransportConnection connection, TransportReceiveLease lease)
        {
            Connection = connection;
            _lease = lease;
        }

        private ReceivedRequest(TransportConnection connection, byte[] copy)
        {
            Connection = connection;
            _copy = copy;
        }

        public TransportConnection Connection { get; }

        public ReadOnlySequence<byte> Buffer => _lease is not null ? _lease.Buffer : new ReadOnlySequence<byte>(_copy!);

        public static ReceivedRequest FromLease(TransportConnection connection, TransportReceiveLease lease) => new(connection, lease);

        public static ReceivedRequest FromCopy(TransportConnection connection, byte[] copy) => new(connection, copy);

        public void Dispose() => _lease?.Dispose();
    }
}
