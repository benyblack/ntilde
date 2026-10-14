using System.Collections.Concurrent;

namespace Ntilde.Mux.Transport;

/// <summary>In-process listener: <see cref="Connect"/> hands the client end back and queues the server end for <see cref="Accept"/>.</summary>
public sealed class InMemoryMuxListener : IMuxListener
{
    private readonly BlockingCollection<Stream> _pending = new();
    private readonly int _pipeCapacityBytes;

    public InMemoryMuxListener(int pipeCapacityBytes = 1 << 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pipeCapacityBytes);
        _pipeCapacityBytes = pipeCapacityBytes;
    }

    public Stream Connect()
    {
        (Stream client, Stream server) = InMemoryDuplexPipe.Create(_pipeCapacityBytes);
        _pending.Add(server);
        return client;
    }

    public Stream? Accept(CancellationToken cancellationToken)
    {
        try
        {
            return _pending.Take(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null; // CompleteAdding: listener closed
        }
    }

    public void Dispose()
    {
        _pending.CompleteAdding();
        while (_pending.TryTake(out Stream? orphan)) orphan.Dispose();
    }
}
