using System.Net;
using System.Net.Transport;
using System.Net.Transport.Epoll;
using System.Security.Cryptography.X509Certificates;

namespace NetworkTransportExamples;

public static class EpollTls
{
    public static void RunServer(
        X509Certificate2 certificate)
    {
        TransportProvider provider = TransportProviders.Epoll(
            new EpollTransportOptions
            {
                ReusePort = true,
                ReceiveBufferSize = 4096,
                WriteBufferSize = 4096,
            });

        using TransportEngine engine = provider.CreateEngine(
            new TransportEngineOptions
            {
                InitialWorkerCount = Environment.ProcessorCount,
                PinWorkerThreads = true,
            },
            new EchoApplication());

        // TLS is part of the listener. The epoll provider owns the fd before,
        // during, and after the handshake. The only early TLS bytes exposed to
        // user code are those supplied to the ClientHello callback.
        using TransportListener listener = engine.Listen(
            new TransportListenOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Any, 5443),
                Tls = ExampleTls.CreateServer(
                    certificate,
                    TlsOffloadPolicy.Prefer),
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
                Tls = ExampleTls.CreateClient(
                    targetHost,
                    TlsOffloadPolicy.Prefer),
                State = "epoll-tls-client",
            });
    }
}
