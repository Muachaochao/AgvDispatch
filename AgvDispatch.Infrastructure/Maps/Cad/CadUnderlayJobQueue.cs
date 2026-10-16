using System.Threading.Channels;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Infrastructure.Maps;

public sealed class CadUnderlayJobQueue : ICadUnderlayJobQueue
{
    private readonly Channel<CadUnderlayJob> _channel = Channel.CreateUnbounded<CadUnderlayJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ValueTask QueueAsync(CadUnderlayJob job, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(job, cancellationToken);

    public IAsyncEnumerable<CadUnderlayJob> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
