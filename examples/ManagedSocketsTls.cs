using System.Net;
using System.Net.Transport;
using System.Net.Transport.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace NetworkTransportExamples;

public static class ManagedSocketsTls
{
    public static void RunServer(
        X509Certificate2 certificate)
    {
        TransportProvider provider = TransportProviders.ManagedSockets(
            new ManagedSocketTransportOptions
            {
                WaitForDataBeforeAllocatingBuffer = true,
            });

        using TransportEngine engine = provider.CreateEngine(
            new TransportEngineOptions(),
            new EchoApplication());

        // The managed provider uses ordinary Socket/SAEA for transport I/O.
        // TLS is configured on the listener and completed before OnReady.
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
                State = "managed-tls-client",
            });
    }
}
