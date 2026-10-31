using Avalonia.Headless.XUnit;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

public class TerminalViewRetentionSettingsTests
{
    [AvaloniaFact]
    public void ApplyingRetentionSettings_PreservesActiveSnapshotRows_DuringOutput()
    {
        var buffer = new TerminalBuffer(20, 2);
        var parser = new AnsiParser(buffer);
        for (int i = 0; i < 200; i++) parser.Process($"before-{i}\r\n");
        var view = new TerminalView();
        view.SetBuffer(buffer);
        using var snapshotHeld = new ManualResetEventSlim();
        using var settingsWaiting = new ManualResetEventSlim();
        int uiThread = Environment.CurrentManagedThreadId;
        var previousScope = TerminalBuffer.BlockingWriteWaitScope;
        Task? output = null;
        var snapshot = Task.Factory.StartNew(() =>
        {
            buffer.Lock.EnterReadLock();
            try
            {
                int count = buffer.Scrollback.Count;
                var row = buffer.Scrollback.GetRow(0);
                char firstCell = row[0].Character;
                snapshotHeld.Set();
                Assert.True(settingsWaiting.Wait(TimeSpan.FromSeconds(10)), "Settings never reached a write-lock wait.");
                Assert.Equal(count, buffer.Scrollback.Count);
                Assert.Equal(firstCell, row[0].Character);
            }
            finally { buffer.Lock.ExitReadLock(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            Assert.True(snapshotHeld.Wait(TimeSpan.FromSeconds(10)));
            TerminalBuffer.BlockingWriteWaitScope = () =>
            {
                if (Environment.CurrentManagedThreadId == uiThread) settingsWaiting.Set();
                return previousScope?.Invoke();
            };
            output = Task.Factory.StartNew(() =>
            {
                // Let ApplySettings reach the retention write before output queues a writer:
                // its earlier cursor-policy reads must remain able to share the snapshot lock.
                Assert.True(settingsWaiting.Wait(TimeSpan.FromSeconds(10)));
                for (int i = 0; i < 250; i++) parser.Process($"after-{i}\r\n");
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            view.ApplySettings(new TerminalSettings { MaxHistory = 1 });
        }
        finally
        {
            settingsWaiting.Set();
            TerminalBuffer.BlockingWriteWaitScope = previousScope;
            // Surface worker assertions after their locks have been released.
            try { snapshot.GetAwaiter().GetResult(); }
            finally { output?.GetAwaiter().GetResult(); }
        }

        Assert.Equal(1, buffer.MaxHistory);
        buffer.Lock.EnterReadLock();
        try { Assert.Equal(0, buffer.Scrollback.Count); }
        finally { buffer.Lock.ExitReadLock(); }
    }
}
