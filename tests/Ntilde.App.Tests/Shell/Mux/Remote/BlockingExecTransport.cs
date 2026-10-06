using Ntilde.Platform.Ssh.Exec;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// An <see cref="ISshExecTransport"/> whose every start waits until its token is cancelled - a connect stuck on
/// a prompt the user never answers - and then throws <see cref="OperationCanceledException"/>. Nothing ever runs.
/// </summary>
internal sealed class BlockingExecTransport : ISshExecTransport
{
    private readonly TaskCompletionSource<CancellationToken> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string DisplayName => "nova@fake-host";

    /// <summary>Completes with the first start's token once it is waiting.</summary>
    public Task<CancellationToken> Entered => _entered.Task;

    public ISshExecChannel Start(string remoteCommand, CancellationToken ct)
    {
        _entered.TrySetResult(ct);
        ct.WaitHandle.WaitOne(); // SshExec calls Start on the pool, so blocking here is what a real start does
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("unreachable");
    }
}
