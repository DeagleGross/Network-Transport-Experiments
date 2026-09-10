using System.Net;
using System.Net.Transport;
using System.Net.Transport.Rio;
using System.Security.Cryptography.X509Certificates;

namespace NetworkTransportExamples;

public static class WindowsRioTls
{
    public static void RunServer(
        X509Certificate2 certificate)
    {
        // RIO is explicit and never selected by CreateDefault.
        TransportProvider provider = TransportProviders.WindowsRio(
            new RioTransportOptions
            {
                CompletionQueueSize = 4096,
                AcceptConcurrency = 32,
                ReceiveBufferSize = 4096,
                SendBufferSize = 65536,
                RegisteredSendBufferCount = 256,
            });

        using TransportEngine engine = provider.CreateEngine(
            new TransportEngineOptions
            {
                InitialWorkerCount = Environment.ProcessorCount,
            },
            new EchoApplication());

        // RIO drives the registered TCP data path. TLS remains provider-owned,
        // and application callbacks see only authenticated plaintext.
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
                State = "rio-tls-client",
            });
    }
}
