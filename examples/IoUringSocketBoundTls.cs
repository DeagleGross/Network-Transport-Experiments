using System.Net;
using System.Net.Transport;
using System.Net.Transport.Linux;
using System.Security.Cryptography.X509Certificates;

namespace NetworkTransportExamples;

public static class IoUringSocketBoundTls
{
    public static void RunServer(
        X509Certificate2 certificate)
    {
        TransportProvider provider = TransportProviders.IoUring(
            new IoUringTransportOptions
            {
                TlsStrategy = IoUringTlsStrategy.SocketBoundPoll,
            });

        using TransportEngine engine = provider.CreateEngine(
            new TransportEngineOptions
            {
                InitialWorkerCount = Environment.ProcessorCount,
            },
            new EchoApplication());

        // OpenSSL owns a socket BIO. io_uring POLL completions drive
        // SSL_do_handshake and SSL_read/SSL_write retry states. If kTLS
        // activates, normal io_uring sends carry plaintext for kernel TX.
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
                State = "io-uring-socket-bound-client",
            });
    }
}
