using System.Net;
using System.Net.Transport;
using System.Net.Transport.Linux;
using System.Security.Cryptography.X509Certificates;

namespace NetworkTransportExamples;

public static class IoUringMemoryBioTls
{
    public static void RunServer(
        X509Certificate2 certificate)
    {
        TransportProvider provider = TransportProviders.IoUring(
            new IoUringTransportOptions
            {
                TlsStrategy = IoUringTlsStrategy.MemoryBio,
                RingEntryCount = 4096,
                ProvidedBufferCount = 512,
            });

        using TransportEngine engine = provider.CreateEngine(
            new TransportEngineOptions
            {
                InitialWorkerCount = Environment.ProcessorCount,
            },
            new EchoApplication());

        // io_uring receives ciphertext into provided buffers. The provider feeds
        // those bytes into the TLS engine, submits emitted ciphertext, and raises
        // OnReady only after the handshake completes. Multishot receive remains
        // available because OpenSSL does not own the fd.
        using TransportListener listener = engine.Listen(
            new TransportListenOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Any, 5443),
                Tls = ExampleTls.CreateServer(certificate),
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
                Tls = ExampleTls.CreateClient(targetHost),
                State = "io-uring-memory-bio-client",
            });
    }
}
