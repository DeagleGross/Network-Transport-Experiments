using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Transport;
using System.Net.Transport.Pipelines;
using System.Net.Transport.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ProposalSurface.Validation;

internal static class Examples
{
    public static async Task PlainServerAsync(
        CancellationToken shutdownToken)
    {
        await using TransportProvider provider = new SocketTransportProvider();
        await using TransportListener listener = await provider.ListenAsync(
            new TransportListenOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Any, 5000),
                Backlog = 512,
                NoDelay = true,
            },
            shutdownToken);

        while (!shutdownToken.IsCancellationRequested)
        {
            TransportConnection connection = await listener.AcceptAsync(shutdownToken);
            await EchoAsync(connection, shutdownToken);
        }
    }

    private static async Task EchoAsync(
        TransportConnection connection,
        CancellationToken cancellationToken)
    {
        await using (connection)
        {
            while (true)
            {
                TransportReadResult result = await connection.ReadAsync(cancellationToken);
                ReadOnlySequence<byte> buffer = result.Buffer;

                try
                {
                    if (!buffer.IsEmpty)
                    {
                        await connection.WriteAsync(buffer, cancellationToken);
                    }
                }
                finally
                {
                    connection.AdvanceRead(buffer.End, buffer.End);
                }

                if (result.IsCompleted)
                {
                    await connection.ShutdownWriteAsync(cancellationToken);
                    break;
                }
            }
        }
    }

    public static async Task<TransportTlsInfo> AuthenticateServerAsync(
        TransportConnection connection,
        SslStreamCertificateContext defaultCertificateContext,
        CertificateStore certificates,
        CancellationToken clientHelloTimeoutToken,
        CancellationToken cancellationToken)
    {
        var defaultOptions = new SslServerAuthenticationOptions
        {
            ServerCertificateContext = defaultCertificateContext,
            EnabledSslProtocols = SslProtocols.None,
            ApplicationProtocols =
            [
                SslApplicationProtocol.Http2,
                SslApplicationProtocol.Http11,
            ],
        };

        TransportClientHelloCallback observeClientHello =
            static (context, callbackCancellationToken) =>
            {
                callbackCancellationToken.ThrowIfCancellationRequested();
                ObserveJa4Input(
                    context.FirstRecordBytes,
                    context.ContainsCompleteClientHello);
                return ValueTask.CompletedTask;
            };

        var tlsOptions = new TransportServerAuthenticationOptions
        {
            AuthenticationOptions = defaultOptions,
            OptionsSelectionCallback = (context, options, callbackCancellationToken) =>
            {
                callbackCancellationToken.ThrowIfCancellationRequested();
                options.ServerCertificateContext =
                    certificates.GetRequired(context.ClientHelloInfo.ServerName);
                return ValueTask.FromResult(options);
            },
            Offload = new TransportTlsOffloadOptions
            {
                Transmit = TlsOffloadPolicy.Prefer,
                Receive = TlsOffloadPolicy.Prefer,
            },
        };

        await connection.ObserveTlsClientHelloAsync(
            observeClientHello,
            clientHelloTimeoutToken);

        return await connection.AuthenticateAsServerAsync(
            tlsOptions,
            cancellationToken);
    }

    public static async Task<TransportConnection> ConnectClientAsync(
        TransportProvider provider,
        CancellationToken cancellationToken)
    {
        TransportConnection connection = await provider.ConnectAsync(
            new TransportConnectOptions
            {
                RemoteEndPoint = new DnsEndPoint("cache.example.net", 6380),
                NoDelay = true,
            },
            cancellationToken);

        await connection.AuthenticateAsClientAsync(
            new TransportClientAuthenticationOptions
            {
                AuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "cache.example.net",
                    ApplicationProtocols = [new SslApplicationProtocol("resp3")],
                    CertificateRevocationCheckMode = X509RevocationMode.Online,
                },
                Offload = new TransportTlsOffloadOptions
                {
                    Transmit = TlsOffloadPolicy.Prefer,
                    Receive = TlsOffloadPolicy.Prefer,
                },
            },
            cancellationToken);

        return connection;
    }

    public static TransportDuplexPipe CreateKestrelPipe(
        TransportConnection connection,
        MemoryPool<byte> memoryPool,
        PipeScheduler applicationScheduler,
        PipeScheduler transportScheduler,
        long maxReadBufferSize,
        long maxWriteBufferSize)
    {
        var inputOptions = new PipeOptions(
            pool: memoryPool,
            readerScheduler: applicationScheduler,
            writerScheduler: transportScheduler,
            pauseWriterThreshold: maxReadBufferSize,
            resumeWriterThreshold: maxReadBufferSize / 2,
            useSynchronizationContext: false);

        var outputOptions = new PipeOptions(
            pool: memoryPool,
            readerScheduler: transportScheduler,
            writerScheduler: applicationScheduler,
            pauseWriterThreshold: maxWriteBufferSize,
            resumeWriterThreshold: maxWriteBufferSize / 2,
            useSynchronizationContext: false);

        return TransportPipelines.Create(
            connection,
            new TransportPipeOptions
            {
                InputOptions = inputOptions,
                OutputOptions = outputOptions,
                LeaveOpen = false,
            });
    }

    private static void ObserveJa4Input(
        ReadOnlySequence<byte> firstRecordBytes,
        bool containsCompleteClientHello)
    {
    }
}

internal sealed class CertificateStore
{
    public SslStreamCertificateContext GetRequired(
        string serverName)
    {
        throw new NotImplementedException();
    }
}

internal sealed class RespConnectionFactory
{
    private readonly TransportProvider _provider;
    private readonly EndPoint _endpoint;
    private readonly string _targetHost;

    public RespConnectionFactory(
        TransportProvider provider,
        EndPoint endpoint,
        string targetHost)
    {
        _provider = provider;
        _endpoint = endpoint;
        _targetHost = targetHost;
    }

    public async ValueTask<TransportDuplexPipe> ConnectAsync(
        CancellationToken cancellationToken)
    {
        TransportConnection connection = await _provider.ConnectAsync(
            new TransportConnectOptions
            {
                RemoteEndPoint = _endpoint,
                NoDelay = true,
            },
            cancellationToken);

        try
        {
            await connection.AuthenticateAsClientAsync(
                new TransportClientAuthenticationOptions
                {
                    AuthenticationOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = _targetHost,
                    },
                },
                cancellationToken);

            return TransportPipelines.Create(
                connection,
                new TransportPipeOptions
                {
                    LeaveOpen = false,
                });
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
