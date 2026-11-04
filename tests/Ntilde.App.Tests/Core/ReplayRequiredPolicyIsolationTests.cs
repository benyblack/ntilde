using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Ntilde.Replay;
using Ntilde.UI.Replay;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

public sealed class ReplayRequiredPolicyIsolationTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_UI_restoration_preserves_state_and_releases_the_reader(bool checkpoint)
    {
        string path = Path.GetTempFileName();
        ReplayWindow? window = null;
        using var cts = new CancellationTokenSource();
        try
        {
            var source = new TerminalBuffer(20, 8);
            var item = checkpoint
                ? new ReplayEvent { Type = "snapshot", TimeOffsetMs = 100, Snapshot = new ReplaySnapshot { State = source.ExportState(0) } }
                : new ReplayEvent { Type = "data", TimeOffsetMs = 100, Data = "WA==" };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new ReplayHeader { Type = ReplayHeader.TypeToken }, ReplayJsonContext.Default.ReplayHeader) + "\n" +
                JsonSerializer.Serialize(item, ReplayJsonContext.Default.ReplayEvent) + "\n", TestContext.Current.CancellationToken);
            window = new ReplayWindow(path);
            await ((ReplayViewModel)window.DataContext!).Initialization;
            var buffer = Field<TerminalBuffer>(window, "_buffer");
            var parser = Field<AnsiParser>(window, "_parser");
            parser.Process("preserved\x1b[?25l\x1b[31;");
            byte[] before = Pair(buffer, parser);
            window.BeforeSeekRestoreForTest = cts.Cancel;
            await Invoke(window, "PerformSeek", 100L, cts.Token);
            Assert.True(cts.IsCancellationRequested);
            Assert.Equal(before, Pair(buffer, parser));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { window?.Close(); File.Delete(path); }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Path_replacement_between_validation_and_restore_cannot_switch_the_replay_source(bool checkpoint, bool playback)
    {
        string path = Path.GetTempFileName();
        string replacement = Path.GetTempFileName();
        ReplayWindow? window = null;
        try
        {
            string header = JsonSerializer.Serialize(new ReplayHeader { Type = ReplayHeader.TypeToken, Cols = 20, Rows = 8 }, ReplayJsonContext.Default.ReplayHeader);
            var source = new TerminalBuffer(20, 8);
            new AnsiParser(source).Process("checkpoint");
            string snapshot = checkpoint ? JsonSerializer.Serialize(new ReplayEvent
            {
                Type = "snapshot",
                TimeOffsetMs = 50,
                Snapshot = new ReplaySnapshot { Cols = 20, Rows = 8, State = source.ExportState(0) }
            }, ReplayJsonContext.Default.ReplayEvent) + "\n" : "";
            await File.WriteAllTextAsync(path, header + "\n" + snapshot + "{\"t\":100,\"type\":\"data\",\"d\":\"Tw==\"}\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(replacement, header[..^1] + ",\"required_sixel_raster\":1}\n{\"t\":100,\"type\":\"data\",\"d\":\"WA==\"}\n", TestContext.Current.CancellationToken);
            byte[] originalBytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            byte[] replacementBytes = await File.ReadAllBytesAsync(replacement, TestContext.Current.CancellationToken);
            window = new ReplayWindow(path);
            var model = (ReplayViewModel)window.DataContext!;
            await model.Initialization;
            bool attempted = false;
            bool sharingBlocked = false;
            Exception? replacementError = null;
            window.BeforeSeekRestoreForTest = () =>
            {
                attempted = true;
                try
                {
                    try { File.Move(replacement, path, overwrite: true); }
                    catch (Exception ex) when (IsWindowsHeldReaderDenial(ex))
                    {
                        // Windows reports access denied (observed 0x80070005) or a sharing
                        // violation for replacement while the original no-delete reader is open.
                        sharingBlocked = true;
                        Assert.Equal(originalBytes, File.ReadAllBytes(path));
                        Assert.Equal(replacementBytes, File.ReadAllBytes(replacement));
                    }
                }
                catch (Exception ex) { replacementError = ex; throw; }
            };
            var buffer = Field<TerminalBuffer>(window, "_buffer");
            if (playback)
            {
                model.CurrentTimeMs = 100;
                int session = Field<PlaybackSessionGate>(window, "_playbackSessionGate").BeginSession();
                await Invoke(window, "PlaybackLoop", session, CancellationToken.None);
            }
            else await Invoke(window, "PerformSeek", 100L, CancellationToken.None);
            Assert.True(attempted);
            Assert.True(replacementError is null, $"Replacement control failed before restoration: {replacementError?.GetType().Name}, HResult=0x{replacementError?.HResult:X8}: {replacementError}");
            Assert.Equal((20, 8), (buffer.Cols, buffer.Rows));
            Assert.Equal(checkpoint ? "checkpointO" : "O", BufferSnapshot.Capture(buffer).Lines[0]);
            if (sharingBlocked)
            {
                // The exact same operation must succeed once the run releases the reader;
                // permanent ACL/path failures are not an accepted sharing control.
                File.Move(replacement, path, overwrite: true);
                Assert.Equal(replacementBytes, File.ReadAllBytes(path));
            }
        }
        finally { window?.Close(); File.Delete(path); File.Delete(replacement); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsupported_policy_is_rejected_before_snapshots_are_indexed(bool leadingBlank)
    {
        string path = Path.GetTempFileName();
        try
        {
            var header = new ReplayHeader { Type = ReplayHeader.TypeToken, RequiredSixelRasterPolicy = 1 };
            var snapshot = new ReplayEvent { Type = "snapshot", TimeOffsetMs = 100, Snapshot = new ReplaySnapshot { State = new TerminalBuffer(20, 8).ExportState(0) } };
            string prefix = leadingBlank ? "\n \r\n" : "";
            await File.WriteAllTextAsync(path, prefix + JsonSerializer.Serialize(header, ReplayJsonContext.Default.ReplayHeader) + "\n" +
                JsonSerializer.Serialize(snapshot, ReplayJsonContext.Default.ReplayEvent), TestContext.Current.CancellationToken);
            var model = new ReplayViewModel(path);
            await model.Initialization;
            Assert.Equal(0, model.DurationMs);
            Assert.Null(model.GetNearestSnapshotEntry(100));
            Assert.Null(model.GetNearestSeekCheckpoint(100));
        }
        finally { File.Delete(path); }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Unsupported_policy_is_rechecked_before_cached_checkpoint_or_initial_state_restore(bool checkpoint, bool playback)
    {
        string path = Path.GetTempFileName();
        ReplayWindow? window = null;
        try
        {
            string header = JsonSerializer.Serialize(new ReplayHeader { Type = ReplayHeader.TypeToken, Cols = 20, Rows = 8 }, ReplayJsonContext.Default.ReplayHeader);
            var saved = new TerminalBuffer(20, 8);
            new AnsiParser(saved).Process("checkpoint");
            var item = checkpoint
                ? new ReplayEvent { Type = "snapshot", TimeOffsetMs = 100, Snapshot = new ReplaySnapshot { Cols = 20, Rows = 8, State = saved.ExportState(0) } }
                : new ReplayEvent { Type = "data", TimeOffsetMs = 100, Data = "WA==" };
            string data = JsonSerializer.Serialize(item, ReplayJsonContext.Default.ReplayEvent);
            await File.WriteAllTextAsync(path, header + "\n" + data + "\n", TestContext.Current.CancellationToken);
            window = new ReplayWindow(path);
            var model = (ReplayViewModel)window.DataContext!;
            await Ntilde.Mux.Tests.Support.TestWait.UntilAsync(() => model.DurationMs == 100, "the valid original recording was indexed");
            await File.WriteAllTextAsync(path, header[..^1] + ",\"required_sixel_raster\":1}\n" + data + "\n", TestContext.Current.CancellationToken);
            var buffer = Field<TerminalBuffer>(window, "_buffer");
            var parser = Field<AnsiParser>(window, "_parser");
            parser.Process("preserved\x1b[?25l\x1b[31;");
            byte[] before = Pair(buffer, parser);
            Task operation;
            if (playback)
            {
                model.CurrentTimeMs = 100;
                int session = Field<PlaybackSessionGate>(window, "_playbackSessionGate").BeginSession();
                operation = Invoke(window, "PlaybackLoop", session, CancellationToken.None);
            }
            else operation = Invoke(window, "PerformSeek", 100L, CancellationToken.None);
            await operation;
            Assert.Equal(before, Pair(buffer, parser));
        }
        finally { window?.Close(); File.Delete(path); }
    }

    private static T Field<T>(ReplayWindow window, string name) =>
        (T)typeof(ReplayWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static bool IsWindowsHeldReaderDenial(Exception error) => OperatingSystem.IsWindows() &&
        ((error is UnauthorizedAccessException && error.HResult == unchecked((int)0x80070005)) ||
         (error is IOException && error.HResult == unchecked((int)0x80070020)));

    private static Task Invoke(ReplayWindow window, string name, params object[] args) =>
        (Task)typeof(ReplayWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args)!;

    private static byte[] Pair(TerminalBuffer buffer, AnsiParser parser)
    {
        var state = buffer.ExportState(0);
        state.Parser = parser.ExportState();
        return TerminalStateSerializer.ToBytes(state);
    }
}
