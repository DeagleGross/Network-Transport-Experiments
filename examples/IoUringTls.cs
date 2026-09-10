using System.Net;
using System.Net.Transport;
using System.Net.Transport.IoUring;
using System.Security.Cryptography.X509Certificates;

namespace NetworkTransportExamples;

public static class IoUringTls
{
    public static void RunServer(
        X509Certificate2 certificate)
    {
        TransportProvider provider = TransportProviders.IoUring(
            new IoUringTransportOptions
            {
                RingEntryCount = 4096,
                ProvidedBufferCount = 512,
                ReceiveBufferSize = 4096,
                WriteBufferSize = 16384,
            });

        using TransportEngine engine = provider.CreateEngine(
            new TransportEngineOptions
            {
                InitialWorkerCount = Environment.ProcessorCount,
                PinWorkerThreads = true,
            },
            new EchoApplication());

        // TLS has one public meaning. The provider may internally use a
        // memory-BIO, fd-bound polling, SslStream, or kTLS-capable path, but
        // that choice does not change the application callback API.
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
                State = "io-uring-tls-client",
            });
    }
}
