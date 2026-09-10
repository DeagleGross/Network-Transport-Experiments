using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;

namespace System.Net.Transport.Sockets;

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class SocketTransportProvider : TransportProvider
{
    public SocketTransportProvider()
    {
    }

    public override string Name => "sockets";

    public TransportConnection CreateConnection(
        Socket socket,
        bool ownsSocket)
    {
        throw new NotImplementedException();
    }

    public TransportListener CreateListener(
        Socket socket,
        bool ownsSocket)
    {
        throw new NotImplementedException();
    }

    public override ValueTask<TransportListener> ListenAsync(
        TransportListenOptions options,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public override ValueTask<TransportConnection> ConnectAsync(
        TransportConnectOptions options,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public override ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
