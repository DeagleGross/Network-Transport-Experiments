using System.Net.Transport;

namespace NetworkTransportExamples;

public sealed class EchoApplication : TransportApplication
{
    protected override void OnAccepting(ref TransportAcceptingContext context)
    {
        context.Connection.State = $"accepted:{context.Connection.Id}";
    }

    protected override void OnReady(ref TransportReadyContext context)
    {
        Console.WriteLine(
            $"ready id={context.Connection.Id} " +
            $"origin={context.Origin} " +
            $"tls={context.Connection.TlsInfo is not null}");
    }

    protected override void OnConnectFailed(ref TransportConnectFailedContext context)
    {
        Console.Error.WriteLine(
            $"connect operation {context.Operation.Id} failed: {context.Error.Message}");
    }

    protected override void OnReceive(ref TransportReceiveContext context)
    {
        if (!context.Payload.IsEmpty)
        {
            Span<byte> response = context.GetResponseSpan(context.Payload.Length);
            context.Payload.CopyTo(response);
            context.ResponseBytes = context.Payload.Length;
        }

        if (context.IsCompleted)
        {
            context.Connection.ShutdownWrite();
        }
    }

    protected override void OnWriteCompleted(ref TransportWriteCompletedContext context)
    {
        if (context.Error is not null)
        {
            context.Connection.Abort(context.Error);
        }
    }

    protected override void OnClosed(ref TransportClosedContext context)
    {
        Console.WriteLine(
            $"closed id={context.Connection.Id} " +
            $"phase={context.Phase} " +
            $"reason={context.Reason} " +
            $"error={context.Error?.Message}");
    }
}
