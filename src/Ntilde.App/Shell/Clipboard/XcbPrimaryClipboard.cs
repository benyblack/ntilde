using System.Collections.Concurrent;

namespace Ntilde.Shell.Clipboard;

/// <summary>Linux X11 PRIMARY, on an independent connection and one process-lifetime worker.</summary>
internal sealed class XcbPrimaryClipboard(Func<ClipboardReadRequest, CancellationToken, Task<ClipboardReadConsent>> confirm) : IKittyClipboard
{
    internal static Action<string>? TraceForTest { get; set; }
    private static readonly Lazy<Worker> Shared = new(() => new Worker());
    public bool Available => OperatingSystem.IsLinux() && IsLocalDisplay(Environment.GetEnvironmentVariable("DISPLAY"));

    internal static bool IsLocalDisplay(string? display)
    {
        if (display == null) return false;
        string value = "";
        if (display.StartsWith("unix:", StringComparison.Ordinal)) value = display[5..];
        else if (display.StartsWith(':')) value = display[1..];
        string[] parts = value.Split('.');
        return parts.Length is 1 or 2 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit));
    }

    private Task<T> Run<T>(Func<XcbSelectionConnection, T> work) => Available
        ? Shared.Value.Run(work) : Task.FromException<T>(new NotSupportedException("X11 PRIMARY is unavailable."));

    internal Task<int> AdvertisedTargetCountForTestAsync() => Run(c => c.LastAdvertisedTargetCount);

    internal Task<uint> OwnerWindowForTestAsync() => Run(c => c.Owner());

    internal Task<(int Sent, int Received)> TransferCountsAsync() => Run(c => (c.IncrementalSends, c.IncrementalReceives));

    public async Task<IKittyClipboardSnapshot> OpenReadAsync(IReadOnlyList<string>? requestedTypes = null) =>
        await Run(c => c.CaptureSnapshot(requestedTypes));
    public async Task WriteAsync(IReadOnlyDictionary<string, byte[]> data) => await Run(c => { c.Write(data); return true; });
    public Task<ClipboardReadConsent> ConfirmReadAsync(ClipboardReadRequest request, CancellationToken cancellation) => confirm(request, cancellation);

    private sealed class Worker : IDisposable
    {
        private readonly BlockingCollection<Action<XcbSelectionConnection?, Exception?>> _work = new(1);
        private Exception? _failure;

        public Worker()
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = "Ntilde X11 PRIMARY" };
            thread.Start();
        }

        public Task<T> Run<T>(Func<XcbSelectionConnection, T> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = Volatile.Read(ref _failure);
            if (failure != null) return Task.FromException<T>(failure);
            try
            {
                if (!_work.TryAdd((connection, error) =>
                    {
                        try
                        {
                            if (error != null) throw error;
                            if (connection == null) throw new IOException("X11 PRIMARY connection was not initialized.");
                            done.TrySetResult(work(connection));
                        }
                        catch (Exception caught) { done.TrySetException(caught); }
                    })) done.TrySetException(new IOException("X11 PRIMARY is busy."));
            }
            catch (InvalidOperationException error)
            {
                done.TrySetException(Volatile.Read(ref _failure) ?? new IOException("X11 PRIMARY has stopped.", error));
            }
            // Initialization failure can race admission; the worker drains admitted work on failure.
            return done.Task;
        }

        private void Loop()
        {
            try
            {
                using var connection = new XcbSelectionConnection();
                while (!_work.IsCompleted)
                {
                    if (_work.TryTake(out var work, 10)) work(connection, null);
                    connection.Pump();
                }
            }
            catch (Exception error)
            {
                Volatile.Write(ref _failure, error is DllNotFoundException or EntryPointNotFoundException
                    ? new NotSupportedException("X11 PRIMARY native support is unavailable.", error) : error);
                // CompleteAdding closes the admission race without abandoning a queued caller.
                _work.CompleteAdding();
                while (_work.TryTake(out var work)) work(null, _failure);
            }
            finally { _work.Dispose(); }
        }

        public void Dispose() => _work.CompleteAdding();
    }
}

internal sealed record XcbSelectionTargets(uint Owner, Dictionary<string, uint> Formats, bool HasTimestamp);
