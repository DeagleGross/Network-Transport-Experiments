using System.Net;
using System.Net.Transport;
using System.Net.Transport.Linux;
using System.Security.Cryptography.X509Certificates;

namespace NetworkTransportExamples;

public static class EpollSocketBoundTls
{
    public static void RunServer(
        X509Certificate2 certificate)
    {
        TransportProvider provider = TransportProviders.Epoll(
            new EpollTransportOptions
            {
                TlsStrategy = EpollTlsStrategy.SocketBoundOpenSsl,
                ReusePort = true,
            });

        using TransportEngine engine = provider.CreateEngine(
            new TransportEngineOptions
            {
                InitialWorkerCount = Environment.ProcessorCount,
                PinWorkerThreads = true,
            },
            new EchoApplication());

        // SSL_do_handshake runs against the nonblocking fd. WANT_READ/WANT_WRITE
        // update epoll interest. The ClientHello callback is raised from that
        // handshake; no memory BIO and no public ObserveClientHello call is used.
        using TransportListener listener = engine.Listen(
            new TransportListenOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Any, 5443),
                Tls = ExampleTls.CreateServer(
                    certificate,
                    TlsOffloadPolicy.Prefer),
            });

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
