using System.Net;
using System.Net.Transport;
using System.Net.Transport.Linux;

namespace NetworkTransportExamples;

public static class IoUringPlaintext
{
    public static void RunServer()
    {
        TransportProvider provider = TransportProviders.IoUring(
            new IoUringTransportOptions
            {
                RingEntryCount = 4096,
                ProvidedBufferCount = 512,
                ReceiveBufferSize = 4096,
                WriteBufferSize = 16384,
                WriteBufferCount = 1024,
                MaximumBorrowedReceiveBuffers = 128,
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
                State = "io-uring-client",
            });
    }
}
