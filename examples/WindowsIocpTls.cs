using System.Net;
using System.Net.Transport;
using System.Net.Transport.Iocp;
using System.Security.Cryptography.X509Certificates;

namespace NetworkTransportExamples;

public static class WindowsIocpTls
{
    public static void RunServer(
        X509Certificate2 certificate)
    {
        TransportProvider provider = TransportProviders.WindowsIocp(
            new IocpTransportOptions
            {
                CompletionBatchSize = 128,
                AcceptConcurrency = 32,
            });

        using TransportEngine engine = provider.CreateEngine(
            new TransportEngineOptions
            {
                InitialWorkerCount = Environment.ProcessorCount,
            },
            new EchoApplication());

        // IOCP owns network I/O. The provider chooses its internal Windows TLS
        // implementation and raises OnReady only after authentication.
        using TransportListener listener = engine.Listen(
            new TransportListenOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Any, 5443),
                Tls = ExampleTls.CreateServer(certificate),
            });

        listener.Accept();
        Console.ReadLine();
    }

    public static TransportConnectOperation ConnectClient(
        TransportEngine engine,
        string targetHost,
        int port)
    {
        return engine.Connect(
            new TransportConnectOptions
            {
                RemoteEndPoint = new DnsEndPoint(targetHost, port),
                Tls = ExampleTls.CreateClient(targetHost),
                State = "iocp-tls-client",
            });
    }
}
