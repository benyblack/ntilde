using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ntilde.CommandAssist.ShellIntegration.Runtime;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Its only disposable is a SemaphoreSlim gate, which holds no handle unless AvailableWaitHandle is read " +
                    "(nothing here reads it), and disposing it under a caller still waiting would fault that caller.")]
public sealed class OrderedAsyncEventDispatcher
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task EnqueueAsync(Func<Task> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
