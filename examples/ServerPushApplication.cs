using System.Collections.Concurrent;
using System.Net.Transport;

namespace NetworkTransportExamples;

public sealed class ServerPushApplication : TransportApplication
{
    private readonly ConcurrentDictionary<long, ServerPeer> _peers = new();

    protected override void OnAccepting(ref TransportAcceptingContext context)
    {
        context.Listener.Accept();
    }

    protected override void OnReady(ref TransportReadyContext context)
    {
        var peer = new ServerPeer(context.Connection);
        context.Connection.State = peer;
        _peers.TryAdd(context.Connection.Id, peer);
    }

    protected override void OnClosed(ref TransportClosedContext context)
    {
        if (_peers.TryRemove(context.Connection.Id, out ServerPeer? peer))
        {
            peer.MarkClosed();
        }
    }

    public void Broadcast(ReadOnlySpan<byte> message)
    {
        foreach (ServerPeer peer in _peers.Values)
        {
            peer.Send(message);
        }
    }

    private sealed class ServerPeer
    {
        private readonly object _sync = new();
        private readonly TransportConnection _connection;
        private bool _closed;

        public ServerPeer(TransportConnection connection)
        {
            _connection = connection;
        }

        public bool Send(ReadOnlySpan<byte> message)
        {
            lock (_sync)
            {
                if (_closed)
                {
                    return false;
                }

                try
                {
                    _connection.Send(message);
                    return true;
                }
                catch (ObjectDisposedException)
                {
                    _closed = true;
                    return false;
                }
            }
        }

        public void MarkClosed()
        {
            lock (_sync)
            {
                _closed = true;
            }
        }
    }
}
