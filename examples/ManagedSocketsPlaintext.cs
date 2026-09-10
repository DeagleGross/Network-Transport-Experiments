using System.Net;
using System.Net.Transport;
using System.Net.Transport.Sockets;

namespace NetworkTransportExamples;

public static class ManagedSocketsPlaintext
{
    public static void RunServer()
    {
        TransportProvider provider = TransportProviders.ManagedSockets(
            new ManagedSocketTransportOptions
            {
                ReceiveBufferSize = 4096,
                WriteBufferSize = 4096,
                WaitForDataBeforeAllocatingBuffer = true,
            });

        using TransportEngine engine = provider.CreateEngine(
            new TransportEngineOptions(),
            new EchoApplication());

        using TransportListener listener = engine.Listen(
            new TransportListenOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Any, 5000),
                Backlog = 512,
                NoDelay = true,
            });

        Console.ReadLine();
    }

    public static TransportConnectOperation Connect(
        TransportEngine engine)
    {
        return engine.Connect(
            new TransportConnectOptions
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 5000),
                State = "managed-client",
            });
    }
}
