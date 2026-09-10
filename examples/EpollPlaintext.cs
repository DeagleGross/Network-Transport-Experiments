using System.Net;
using System.Net.Transport;
using System.Net.Transport.Epoll;

namespace NetworkTransportExamples;

public static class EpollPlaintext
{
    public static void RunServer()
    {
        TransportProvider provider = TransportProviders.Epoll(
            new EpollTransportOptions
            {
                MaximumEventsPerWait = 256,
                ReadBurstLimit = 8,
                WriteBurstLimit = 16,
                ReceiveBufferSize = 4096,
                WriteBufferSize = 4096,
                ReusePort = true,
            });

        using TransportEngine engine = provider.CreateEngine(
            new TransportEngineOptions
            {
                InitialWorkerCount = Environment.ProcessorCount,
                MaximumConnectionsPerWorker = 4096,
                PinWorkerThreads = true,
            },
            new EchoApplication());

        using TransportListener listener = engine.Listen(
            new TransportListenOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Any, 5000),
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
                RequiredWorkerIndex = TransportExecutionContext.CurrentWorkerIndex is >= 0
                    ? TransportExecutionContext.CurrentWorkerIndex
                    : null,
                State = "epoll-client",
            });
    }
}
