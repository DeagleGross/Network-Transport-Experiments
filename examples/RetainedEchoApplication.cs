using System.Net.Transport;

namespace NetworkTransportExamples;

public sealed class RetainedEchoApplication : TransportApplication
{
    protected override void OnReceive(ref TransportReceiveContext context)
    {
        if (!context.Payload.IsEmpty)
        {
            if (context.TryRetainPayload(out TransportReceiveLease? lease))
            {
                try
                {
                    context.Connection.SendBorrowed(lease.Buffer, state: lease);
                }
                catch
                {
                    lease.Dispose();
                    throw;
                }
            }
            else
            {
                context.Connection.Send(context.Payload);
            }
        }

        if (context.IsCompleted)
        {
            context.Connection.ShutdownWrite();
        }
    }

    protected override void OnWriteCompleted(ref TransportWriteCompletedContext context)
    {
        if (context.Operation.State is TransportReceiveLease lease)
        {
            lease.Dispose();
        }

        if (context.Error is not null)
        {
            context.Connection.Abort(context.Error);
        }
    }
}
