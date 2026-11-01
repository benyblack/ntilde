using Ntilde.Shell;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Ntilde.Controls;
using Ntilde.Platform;
using Ntilde.VT;
using Ntilde.Pty;

namespace Ntilde.Tests.Ssh;

public sealed class TerminalPaneRecordingTests
{
    [AvaloniaFact]
    public void ToggleRecording_StartsAndStopsWithoutWritingTerminalText_AndPublishesMetadata()
    {
        var pane = new TerminalPane(new TerminalProfile
        {
            Name = "PowerShell",
            Type = ConnectionType.Local,
            Command = "pwsh.exe"
        });

        try
        {
            var session = new StubTerminalSession();
            SetSessionForTest(pane, session);

            string before = GetVisiblePlainText(pane.Buffer!);
            var notifications = new List<RecordingNotificationEventArgs>();
            pane.RecordingNotification += notifications.Add;

            pane.ToggleRecording();
            pane.ToggleRecording();

            string after = GetVisiblePlainText(pane.Buffer!);

            Assert.Equal(before, after);
            Assert.Equal(2, notifications.Count);

            var started = notifications[0];
            Assert.Equal(RecordingNotificationKind.Started, started.Kind);
            Assert.True(started.IsRecording);
            Assert.Equal(session.StartedPath, started.FilePath);
            Assert.StartsWith(AppPaths.RecordingsDirectory, started.FilePath!, StringComparison.OrdinalIgnoreCase);
            Assert.Matches(@"^ntilde_rec_\d{8}_\d{6}_[0-9a-f]{6}\.rec$", Path.GetFileName(started.FilePath));

            var stopped = notifications[1];
            Assert.Equal(RecordingNotificationKind.Stopped, stopped.Kind);
            Assert.False(stopped.IsRecording);
            Assert.Equal(session.StartedPath, stopped.FilePath);
            Assert.Equal(AppPaths.RecordingsDirectory, stopped.RecordingsDirectory);
        }
        finally
        {
            pane.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task NotificationsWaitForReadinessAndFailedFlushNeverReportsSaved()
    {
        using var pane = new TerminalPane(new TerminalProfile { Name = "test", Type = ConnectionType.Local, Command = "test" });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new StubTerminalSession { Ready = ready.Task, Flushed = flushed.Task };
        SetSessionForTest(pane, session);
        var notifications = new List<RecordingNotificationEventArgs>();
        pane.RecordingNotification += notifications.Add;
        Task start = pane.ToggleRecordingAsync();
        Assert.Empty(notifications);
        ready.TrySetResult();
        await start;
        Assert.Equal(RecordingNotificationKind.Started, Assert.Single(notifications).Kind);
        Task stop = pane.ToggleRecordingAsync();
        Assert.Single(notifications);
        flushed.TrySetException(new IOException("disk full"));
        await stop;
        Assert.Equal(new[] { RecordingNotificationKind.Started, RecordingNotificationKind.Failed }, notifications.Select(n => n.Kind));
        Assert.Contains("disk full", notifications[1].ErrorMessage);
        Assert.False(pane.IsRecording);
    }

    [AvaloniaFact]
    public async Task MidRecordingFailurePublishesFailureAndClearsIndicator()
    {
        using var pane = new TerminalPane(new TerminalProfile { Name = "test", Type = ConnectionType.Local, Command = "test" });
        var session = new StubTerminalSession();
        SetSessionForTest(pane, session);
        var notifications = new List<RecordingNotificationEventArgs>();
        pane.RecordingNotification += notifications.Add;
        await pane.ToggleRecordingAsync();
        session.Fail();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.Equal(new[] { RecordingNotificationKind.Started, RecordingNotificationKind.Failed }, notifications.Select(n => n.Kind));
        Assert.False(pane.IsRecording);
    }

    [AvaloniaFact]
    public async Task FailedOpenNeverPublishesStarted()
    {
        using var pane = new TerminalPane(new TerminalProfile { Name = "test", Type = ConnectionType.Local, Command = "test" });
        var session = new StubTerminalSession { Ready = Task.FromException(new IOException("cannot open")) };
        SetSessionForTest(pane, session);
        var notifications = new List<RecordingNotificationEventArgs>();
        pane.RecordingNotification += notifications.Add;
        await pane.ToggleRecordingAsync();
        Assert.Equal(RecordingNotificationKind.Failed, Assert.Single(notifications).Kind);
        Assert.False(pane.IsRecording);
    }

    [Fact]
    public void BuildRecordingFileName_AppendsStableUniqueSuffix()
    {
        string first = TerminalPane.BuildRecordingFileName(
            new DateTime(2026, 5, 22, 10, 30, 45),
            "a1b2c3d4");
        string second = TerminalPane.BuildRecordingFileName(
            new DateTime(2026, 5, 22, 10, 30, 45),
            "f0e1d2c3");

        Assert.Equal("ntilde_rec_20260522_103045_a1b2c3.rec", first);
        Assert.Equal("ntilde_rec_20260522_103045_f0e1d2.rec", second);
        Assert.NotEqual(first, second);
    }

    private static void SetSessionForTest(TerminalPane pane, ITerminalSession session)
    {
        var property = typeof(TerminalPane).GetProperty("Session", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        property!.SetValue(pane, session);
    }

    private static string GetVisiblePlainText(TerminalBuffer buffer)
    {
        var field = typeof(TerminalBuffer).GetField("_viewport", BindingFlags.NonPublic | BindingFlags.Instance);
        var viewport = (TerminalRow[])field!.GetValue(buffer)!;
        return string.Join("\n", viewport.Select(GetRowText)).TrimEnd();
    }

    private static string GetRowText(TerminalRow row)
    {
        char[] chars = row.Cells.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray();
        return new string(chars).TrimEnd();
    }

    private sealed class StubTerminalSession : ITerminalSession
    {
        public Guid Id => Guid.NewGuid();
        public string ShellCommand => "stub";
        public string? ShellArguments => null;
        public bool IsProcessRunning => true;
        public bool HasActiveChildProcesses => false;
        public int? ExitCode => null;
        public bool IsRecording { get; private set; }
        public string? StartedPath { get; private set; }
        public Task? Ready { get; init; }
        public Task? Flushed { get; init; }
        public event Action<Ntilde.Replay.RecordingFailure>? RecordingFailed;
        public void Fail()
        {
            IsRecording = false;
            RecordingFailed?.Invoke(new Ntilde.Replay.RecordingFailure(Ntilde.Replay.RecordingFailureKind.Write, "disk full"));
        }
        public async Task StartRecordingAsync(string path)
        {
            if (Ready is { } ready) await ready;
            StartRecording(path);
        }
        public async Task StopRecordingAsync()
        {
            StopRecording();
            if (Flushed is { } flushed) await flushed;
        }

        public event Action<string>? OnOutputReceived
        {
            add { }
            remove { }
        }

        public event Action<int>? OnExit
        {
            add { }
            remove { }
        }

        public void SendInput(string input)
        {
        }

        public void Resize(int cols, int rows)
        {
        }

        public void StartRecording(string filePath)
        {
            StartedPath = filePath;
            IsRecording = true;
        }

        public void StopRecording()
        {
            IsRecording = false;
        }

        public bool IsFlightRecording => false;

        public void EnableFlightRecording(long maxTotalBytes)
        {
        }

        public void DisableFlightRecording()
        {
        }

        public bool TryExportFlightRecording(string filePath, out Ntilde.Replay.FlightExportInfo info)
        {
            info = default;
            return false;
        }

        public void AttachBuffer(TerminalBuffer buffer)
        {
        }

        public void TakeSnapshot()
        {
        }

        public void Dispose()
        {
        }
    }
}
