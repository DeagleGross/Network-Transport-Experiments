using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;

namespace System.Net.Transport.Pipelines;

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportPipeOptions
{
    public TransportPipeOptions()
    {
    }

    public PipeOptions InputOptions { get; set; } = PipeOptions.Default;
    public PipeOptions OutputOptions { get; set; } = PipeOptions.Default;
    public bool LeaveOpen { get; set; }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public sealed class TransportDuplexPipe : IDuplexPipe, IAsyncDisposable
{
    internal TransportDuplexPipe()
    {
    }

    public PipeReader Input => throw new NotImplementedException();
    public PipeWriter Output => throw new NotImplementedException();
    public Task Completion => throw new NotImplementedException();

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

[Experimental("SYSLIBXXXX", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
public static class TransportPipelines
{
    public static TransportDuplexPipe Create(
        TransportConnection connection,
        TransportPipeOptions? options = null)
    {
        throw new NotImplementedException();
    }
}
