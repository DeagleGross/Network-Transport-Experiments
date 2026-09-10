using System.Buffers;
using System.Net.Security;
using System.Net.Transport;
using System.Security.Cryptography.X509Certificates;

namespace NetworkTransportExamples;

internal static class ExampleTls
{
    public static TransportServerTlsOptions CreateServer(
        X509Certificate2 certificate,
        TlsOffloadPolicy offload = TlsOffloadPolicy.Disabled)
    {
        return new TransportServerTlsOptions
        {
            AuthenticationOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ApplicationProtocols =
                [
                    SslApplicationProtocol.Http2,
                    SslApplicationProtocol.Http11,
                ],
            },
            ClientHelloCallback =
                static (ref TransportClientHelloContext context) =>
                {
                    // Borrowed bytes: parse now or copy what must survive.
                    ObserveFingerprintInput(
                        context.FirstRecordBytes,
                        context.ContainsCompleteClientHello);
                },
            OptionsSelectionCallback =
                static (context, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Illustrative: return the preconfigured per-listener options.
                    // Real code can select a certificate from ClientHelloInfo.ServerName.
                    return ValueTask.FromResult(context.DefaultOptions);
                },
            Offload = new TransportTlsOffloadOptions
            {
                Transmit = offload,
                Receive = offload,
            },
        };
    }

    public static TransportClientTlsOptions CreateClient(
        string targetHost,
        TlsOffloadPolicy offload = TlsOffloadPolicy.Disabled)
    {
        return new TransportClientTlsOptions
        {
            AuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
            },
            Offload = new TransportTlsOffloadOptions
            {
                Transmit = offload,
                Receive = offload,
            },
        };
    }

    private static void ObserveFingerprintInput(
        ReadOnlySequence<byte> firstRecord,
        bool containsCompleteClientHello)
    {
    }
}
