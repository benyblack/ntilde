using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Shell.Mux.Remote;
using Ntilde.Tests.Shell.Mux.Remote; // RecordingExecTransport, FakeExecReply
using Ntilde.Views.Ssh;

namespace Ntilde.Tests.Core;

/// <summary>
/// The ntilde-mux install dialog (Phase 4 spec §9). Its installer is real; what it talks to is fake: a
/// <see cref="RecordingExecTransport"/> host and fake asset sources, so each button runs the whole flow.
/// </summary>
public sealed class RemoteMuxInstallDialogTests : IDisposable
{
    private const string UbuntuProbe = "Linux x86_64\nldd (Ubuntu GLIBC 2.35-0ubuntu3.8) 2.35\nHOME=/home/nova\n";
    private const string InstalledPath = "/home/nova/.local/share/ntilde/bin/ntilde-mux";
    private const string InstalledJson =
        "{\"version\":\"0.11.0\",\"protocolMin\":1,\"protocolMax\":2,\"rid\":\"linux-x64\",\"path\":\"" + InstalledPath + "\"}\n";

    private static readonly byte[] Binary = ElfX64(70 * 1024);
    private static readonly Guid Token = new("a1b2c3d4e5f60718293a4b5c6d7e8f90");

    private readonly List<IMuxDaemonAssetSource?> _sources = [];
    private readonly List<Window> _windows = [];

    public void Dispose()
    {
        foreach (Window window in _windows) window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A linux-x64 executable as far as its header goes (<see cref="MuxDaemonRid.Of"/>).</summary>
    private static byte[] ElfX64(int size)
    {
        byte[] bytes = new byte[size];
        new Random(11).NextBytes(bytes);
        bytes[0] = 0x7F;
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'F';
        bytes[4] = 2; // 64-bit
        bytes[5] = 1; // little-endian
        bytes[18] = 62; // x86-64
        bytes[19] = 0;
        return bytes;
    }

    private static SshProfile Profile(string rid = "", bool persist = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = "fake",
        Host = "fake-host",
        User = "nova",
        MuxOptions = new SshMuxOptions { PersistRemoteSessions = persist, RemoteDaemonRid = rid },
    };

    /// <summary>A host that answers the probe with <paramref name="probe"/> and the upload with <paramref name="upload"/>.</summary>
    private static RecordingExecTransport Host(FakeExecReply upload, string probe = UbuntuProbe) =>
        new((command, _) => command == RemoteHostProbe.Command ? new FakeExecReply(probe) : upload);

    private static FakeSource Release() =>
        new(new MuxDaemonAsset(Binary, MuxDaemonAsset.Sha256Of(Binary), "https://example.test/ntilde-mux-linux-x64"));

    private RemoteMuxInstallDialog Open(
        SshProfile profile,
        ISshExecTransport host,
        IMuxDaemonAssetSource release,
        string appVersion = "0.11.0",
        IClipboard? clipboard = null,
        Func<Window, Task<string?>>? pickFile = null)
    {
        RemoteMuxInstallDialog dialog = RemoteMuxInstallDialog.Build(
            profile,
            (source, report, progress) =>
            {
                _sources.Add(source);
                return new RemoteMuxInstaller(host, source ?? release, report) { Progress = progress, NewUploadToken = () => Token };
            },
            appVersion,
            clipboard,
            pickFile);
        _windows.Add(dialog.Window);
        dialog.Window.Show();
        Dispatcher.UIThread.RunJobs();
        return dialog;
    }

    /// <summary>The headless platform's clipboard, through a window of its own, holding "before" (another test's text is gone).</summary>
    private IClipboard Clipboard()
    {
        var owner = new Window();
        _windows.Add(owner);
        owner.Show();
        Dispatcher.UIThread.RunJobs();
        IClipboard clipboard = owner.Clipboard!;
        Task before = clipboard.SetTextAsync("before");
        PumpUntil(() => before.IsCompleted, "the clipboard was written");
        return clipboard;
    }

    private static string? TextOn(IClipboard clipboard)
    {
        Task<string?> text = clipboard.TryGetTextAsync();
        PumpUntil(() => text.IsCompleted, "the clipboard was read");
        return text.Result;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void PressKey(Window dialog, PhysicalKey key)
    {
        Dispatcher.UIThread.RunJobs();
        dialog.KeyPressQwerty(key, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void PumpUntil(Func<bool> condition, string because, int ms = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Dispatcher.UIThread.RunJobs();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > ms) Assert.Fail($"Timed out: {because}");
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Install_success_records_the_version_and_can_turn_the_flag_on(bool keepRunning)
    {
        SshProfile profile = Profile();
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson));
        RemoteMuxInstallDialog dialog = Open(profile, host, Release());
        Assert.False(dialog.KeepRunningCheckBox.IsVisible);
        Assert.True(dialog.InstallButton.IsDefault);

        Click(dialog.InstallButton);
        PumpUntil(() => !dialog.IsRunning, "the install ended");

        Assert.Equal($"ntilde-mux 0.11.0 installed at {InstalledPath}", dialog.OutcomeText.Text);
        Assert.Equal(
            [RemoteHostProbe.Command, RemoteMuxInstallCommands.UploadForTrial(Binary.Length, Token), RemoteMuxInstallCommands.CommitUpload(Token)],
            host.Commands);
        Assert.Null(Assert.Single(_sources)); // Install is the release source's
        Assert.Equal(
            ("0.11.0", InstalledPath, "linux-x64", false),
            (profile.MuxOptions.RemoteDaemonVersion, profile.MuxOptions.RemoteDaemonPath, profile.MuxOptions.RemoteDaemonRid, profile.MuxOptions.PersistRemoteSessions));
        Assert.Contains(MuxDaemonAsset.Sha256Of(Binary), dialog.LogBox.Text, StringComparison.Ordinal);
        Assert.All(
            [RemoteMuxInstallStep.Probing, RemoteMuxInstallStep.Downloading, RemoteMuxInstallStep.Uploading, RemoteMuxInstallStep.Verifying],
            step => Assert.StartsWith(RemoteMuxInstallDialog.DoneMark, dialog.StepText(step), StringComparison.Ordinal));
        Assert.Equal(dialog.Progress.Maximum, dialog.Progress.Value);
        Assert.False(dialog.InstallButton.IsVisible);
        Assert.False(dialog.ChooseFileButton.IsVisible);
        Assert.Equal("Close", dialog.CloseButton.Content);
        Assert.True(dialog.KeepRunningCheckBox.IsVisible);
        Assert.True(dialog.KeepRunningCheckBox.IsChecked);

        dialog.KeepRunningCheckBox.IsChecked = keepRunning;
        Click(dialog.CloseButton);
        PumpUntil(() => dialog.Result.IsCompleted, "the dialog closed");

        Assert.True(dialog.Result.Result!.Success);
        Assert.Equal(keepRunning, profile.MuxOptions.PersistRemoteSessions);
    }

    /// <summary>
    /// Phase 5 spec R8: the keep-running choice an install offers says, under it, what a persistent tab lacks - in the
    /// connection editor's words.
    /// </summary>
    [AvaloniaFact]
    public void The_keep_running_choice_says_what_a_persistent_tab_lacks()
    {
        RemoteMuxInstallDialog dialog = Open(Profile(), Host(new FakeExecReply(InstalledJson)), Release());
        Assert.False(dialog.KeepRunningHint.IsVisible);

        Click(dialog.InstallButton);
        PumpUntil(() => !dialog.IsRunning, "the install ended");

        Assert.True(dialog.KeepRunningCheckBox.IsVisible);
        Assert.True(dialog.KeepRunningHint.IsVisible);
        Assert.Equal(Ntilde.Shell.Mux.RemoteMuxStatusText.PersistentTabLimits, dialog.KeepRunningHint.Text);
        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, dialog.KeepRunningHint.TextWrapping);
        Assert.Equal(0.7, dialog.KeepRunningHint.Opacity, precision: 3);
        var panel = Assert.IsType<StackPanel>(dialog.KeepRunningHint.Parent);
        Assert.Equal(panel.Children.IndexOf(dialog.KeepRunningCheckBox) + 1, panel.Children.IndexOf(dialog.KeepRunningHint));
    }

    [AvaloniaFact]
    public void A_profile_that_already_keeps_its_sessions_is_not_asked_again()
    {
        SshProfile profile = Profile(persist: true);
        RemoteMuxInstallDialog dialog = Open(profile, Host(new FakeExecReply(InstalledJson)), Release());

        Click(dialog.InstallButton);
        PumpUntil(() => !dialog.IsRunning, "the install ended");
        Assert.False(dialog.KeepRunningCheckBox.IsVisible);
        Assert.False(dialog.KeepRunningHint.IsVisible);
        Click(dialog.CloseButton);
        PumpUntil(() => dialog.Result.IsCompleted, "the dialog closed");

        Assert.True(profile.MuxOptions.PersistRemoteSessions);
        Assert.Equal("0.11.0", profile.MuxOptions.RemoteDaemonVersion);
    }

    [AvaloniaFact]
    public void Failure_shows_the_reason_and_keeps_the_dialog_open()
    {
        SshProfile profile = Profile();
        const string Denied = "mkdir: cannot create directory '/home/nova/.local/share/ntilde': Permission denied";
        RemoteMuxInstallDialog dialog = Open(profile, Host(new FakeExecReply(Stderr: Denied + "\n", ExitCode: 1)), Release());

        Click(dialog.InstallButton);
        PumpUntil(() => !dialog.IsRunning, "the install ended");

        string reason = $"The upload to nova@fake-host failed (exit 1): {Denied}";
        Assert.Equal(reason, dialog.OutcomeText.Text);
        Assert.Contains(reason, dialog.LogBox.Text, StringComparison.Ordinal);
        Assert.StartsWith(RemoteMuxInstallDialog.DoneMark, dialog.StepText(RemoteMuxInstallStep.Downloading), StringComparison.Ordinal);
        Assert.StartsWith(RemoteMuxInstallDialog.FailedMark, dialog.StepText(RemoteMuxInstallStep.Uploading), StringComparison.Ordinal);
        Assert.StartsWith(RemoteMuxInstallDialog.PendingMark, dialog.StepText(RemoteMuxInstallStep.Verifying), StringComparison.Ordinal);
        Assert.True(dialog.Window.IsVisible);
        Assert.False(dialog.Result.IsCompleted);
        Assert.True(dialog.InstallButton is { IsVisible: true, IsEnabled: true });
        Assert.True(dialog.ChooseFileButton.IsEnabled);
        Assert.False(dialog.KeepRunningCheckBox.IsVisible);
        Assert.Equal(string.Empty, profile.MuxOptions.RemoteDaemonVersion);

        Click(dialog.CloseButton);
        PumpUntil(() => dialog.Result.IsCompleted, "the dialog closed");
        Assert.False(dialog.Result.Result!.Success);
        Assert.Equal(string.Empty, profile.MuxOptions.RemoteDaemonVersion);
    }

    /// <summary>
    /// Fix round 2 (ruling): a version without a release points to Choose file only - the install command would
    /// download the same missing release, and so would Install again.
    /// </summary>
    [AvaloniaFact]
    public void A_version_without_a_release_points_to_the_file_only()
    {
        RemoteMuxInstallDialog dialog = Open(
            Profile(), Host(new FakeExecReply(InstalledJson)), new FakeSource(new MuxReleaseNotFoundException("0.12.0-dev")), appVersion: "0.12.0-dev", clipboard: Clipboard());
        Assert.True(dialog.CopyCommandButton.IsVisible);

        Click(dialog.InstallButton);
        PumpUntil(() => !dialog.IsRunning, "the install ended");

        Assert.Equal(RemoteMuxInstallDialog.NoReleaseText("0.12.0-dev"), dialog.OutcomeText.Text);
        Assert.Contains("Choose file\u2026", dialog.OutcomeText.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("command", dialog.OutcomeText.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(dialog.ChooseFileButton is { IsVisible: true, IsEnabled: true });
        Assert.False(dialog.CopyCommandButton.IsVisible);
        Assert.False(dialog.InstallButton.IsEnabled);
    }

    /// <summary>A failure that is not a missing release keeps both other ways in.</summary>
    [AvaloniaFact]
    public void Another_download_failure_keeps_install_and_the_command()
    {
        RemoteMuxInstallDialog dialog = Open(
            Profile(), Host(new FakeExecReply(InstalledJson)), new FakeSource(new InvalidDataException("checksum mismatch: x")), clipboard: Clipboard());

        Click(dialog.InstallButton);
        PumpUntil(() => !dialog.IsRunning, "the install ended");

        Assert.Equal("checksum mismatch: x", dialog.OutcomeText.Text);
        Assert.True(dialog.CopyCommandButton is { IsVisible: true, IsEnabled: true });
        Assert.True(dialog.InstallButton.IsEnabled);
    }

    /// <summary>
    /// Fix round 2: closing the window while Copy install command probes the host (a connect waiting on a prompt)
    /// cancels the probe, and <see cref="RemoteMuxInstallDialog.Result"/> still completes once it unwound.
    /// </summary>
    [AvaloniaFact]
    public void Closing_the_window_during_a_copy_probe_completes_the_result()
    {
        var host = new BlockingExecTransport();
        RemoteMuxInstallDialog dialog = Open(Profile(), host, Release(), clipboard: Clipboard());

        Click(dialog.CopyCommandButton);
        PumpUntil(() => host.Entered.IsCompleted, "the probe is connecting");
        Assert.True(dialog.IsRunning);

        dialog.Window.Close();

        Assert.True(host.Entered.Result.IsCancellationRequested);
        PumpUntil(() => dialog.Result.IsCompleted, "the result completed after the probe unwound");
        Assert.Null(dialog.Result.Result);
        Assert.False(dialog.IsRunning);
    }

    /// <summary>Fix round 2: nothing else starts while the file picker is open, and a second click opens no second picker.</summary>
    [AvaloniaFact]
    public void The_buttons_wait_while_the_file_picker_is_open()
    {
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson));
        var picked = new TaskCompletionSource<string?>();
        int pickers = 0;
        RemoteMuxInstallDialog dialog = Open(Profile(), host, Release(), clipboard: Clipboard(), pickFile: _ =>
        {
            pickers++;
            return picked.Task;
        });

        Click(dialog.ChooseFileButton);
        Dispatcher.UIThread.RunJobs();

        Assert.False(dialog.InstallButton.IsEnabled);
        Assert.False(dialog.CopyCommandButton.IsEnabled);
        Assert.False(dialog.ChooseFileButton.IsEnabled);
        Click(dialog.ChooseFileButton);
        Click(dialog.InstallButton);
        Click(dialog.CopyCommandButton);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, pickers);
        Assert.Empty(host.Commands);

        picked.SetResult(null);
        PumpUntil(() => dialog.InstallButton.IsEnabled, "the picker closed");

        Assert.True(dialog.ChooseFileButton.IsEnabled);
        Assert.True(dialog.CopyCommandButton.IsEnabled);
        Assert.False(dialog.IsRunning);
        Assert.Empty(host.Commands);
    }

    /// <summary>The window closed while the picker was open: the result completes at once, and a file picked after runs nothing.</summary>
    [AvaloniaFact]
    public void A_file_picked_after_the_window_closed_runs_nothing()
    {
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson));
        var picked = new TaskCompletionSource<string?>();
        RemoteMuxInstallDialog dialog = Open(Profile(), host, Release(), pickFile: _ => picked.Task);

        Click(dialog.ChooseFileButton);
        dialog.Window.Close();
        PumpUntil(() => dialog.Result.IsCompleted, "the dialog closed");
        picked.SetResult("/tmp/ntilde-mux");
        Dispatcher.UIThread.RunJobs();

        Assert.Null(dialog.Result.Result);
        Assert.Empty(host.Commands);
        Assert.Empty(_sources);
    }

    [AvaloniaFact]
    public void Escape_cancels_a_running_install()
    {
        SshProfile profile = Profile();
        var release = new BlockingSource();
        RemoteMuxInstallDialog dialog = Open(profile, Host(new FakeExecReply(InstalledJson)), release);

        Click(dialog.InstallButton);
        PumpUntil(() => release.Entered.IsCompleted, "the download started");
        Assert.True(dialog.IsRunning);
        Assert.Equal("Cancel", dialog.CloseButton.Content);
        Assert.False(dialog.InstallButton.IsEnabled);
        CancellationToken token = release.Entered.Result;
        Assert.False(token.IsCancellationRequested);

        PressKey(dialog.Window, PhysicalKey.Escape);

        Assert.True(token.IsCancellationRequested);
        PumpUntil(() => !dialog.IsRunning, "the install unwound");
        Assert.Equal(RemoteMuxInstallDialog.CancelledText, dialog.OutcomeText.Text);
        Assert.True(dialog.Window.IsVisible); // the log stays readable; the next Escape closes
        Assert.False(dialog.Result.IsCompleted);
        Assert.Equal(string.Empty, profile.MuxOptions.RemoteDaemonVersion);
        Assert.True(dialog.InstallButton.IsEnabled);

        PressKey(dialog.Window, PhysicalKey.Escape);
        PumpUntil(() => dialog.Result.IsCompleted, "the dialog closed");
        Assert.Null(dialog.Result.Result);
    }

    [AvaloniaFact]
    public void Closing_the_window_cancels_a_running_install()
    {
        var release = new BlockingSource();
        RemoteMuxInstallDialog dialog = Open(Profile(), Host(new FakeExecReply(InstalledJson)), release);
        Click(dialog.InstallButton);
        PumpUntil(() => release.Entered.IsCompleted, "the download started");

        dialog.Window.Close();

        Assert.True(release.Entered.Result.IsCancellationRequested);
        PumpUntil(() => dialog.Result.IsCompleted, "the run ended after the window closed");
        Assert.Null(dialog.Result.Result);
    }

    [AvaloniaFact]
    public void Copy_install_command_puts_the_one_liner_on_the_clipboard()
    {
        IClipboard clipboard = Clipboard();
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson));
        RemoteMuxInstallDialog dialog = Open(Profile(rid: "linux-arm64"), host, Release(), clipboard: clipboard);
        Assert.True(dialog.CopyCommandButton.IsVisible);

        Click(dialog.CopyCommandButton);
        PumpUntil(() => Equals(dialog.CopyCommandButton.Content, RemoteMuxInstallDialog.CopiedText), "the button says Copied");

        string expected = RemoteMuxInstallCommands.OfflineOneLiner("0.11.0", "linux-arm64");
        Assert.Equal(expected, TextOn(clipboard));
        Assert.Contains(expected, dialog.LogBox.Text, StringComparison.Ordinal);
        Assert.Empty(host.Commands); // the recorded platform needs no probe
        Assert.Empty(_sources);
    }

    [AvaloniaFact]
    public void Copy_install_command_probes_a_host_whose_platform_is_not_recorded()
    {
        IClipboard clipboard = Clipboard();
        SshProfile profile = Profile();
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson));
        RemoteMuxInstallDialog dialog = Open(profile, host, Release(), clipboard: clipboard);

        Click(dialog.CopyCommandButton);
        PumpUntil(() => Equals(dialog.CopyCommandButton.Content, RemoteMuxInstallDialog.CopiedText), "the button says Copied");

        Assert.Equal(RemoteMuxInstallCommands.OfflineOneLiner("0.11.0", "linux-x64"), TextOn(clipboard));
        Assert.Equal([RemoteHostProbe.Command], host.Commands);
        Assert.StartsWith(RemoteMuxInstallDialog.DoneMark, dialog.StepText(RemoteMuxInstallStep.Probing), StringComparison.Ordinal);
        Assert.Equal((string.Empty, string.Empty), (profile.MuxOptions.RemoteDaemonRid, profile.MuxOptions.RemoteDaemonVersion)); // copying records nothing
        Assert.False(dialog.IsRunning);
    }

    [AvaloniaFact]
    public void Copy_install_command_for_a_refused_host_copies_nothing_and_says_why()
    {
        IClipboard clipboard = Clipboard();
        RemoteMuxInstallDialog dialog = Open(
            Profile(), Host(new FakeExecReply(InstalledJson), probe: "Linux x86_64\nmusl libc (x86_64)\nHOME=/root\n"), Release(), clipboard: clipboard);

        Click(dialog.CopyCommandButton);
        PumpUntil(() => !dialog.IsRunning && dialog.OutcomeText.Text is { Length: > 0 }, "the probe ended");

        Assert.Equal("musl libc is not supported", dialog.OutcomeText.Text);
        Assert.StartsWith(RemoteMuxInstallDialog.FailedMark, dialog.StepText(RemoteMuxInstallStep.Probing), StringComparison.Ordinal);
        Assert.NotEqual(RemoteMuxInstallDialog.CopiedText, dialog.CopyCommandButton.Content);
        Assert.Equal("before", TextOn(clipboard));
    }

    [AvaloniaFact]
    public void Copy_install_command_is_hidden_without_a_release_version()
    {
        RemoteMuxInstallDialog dialog = Open(Profile(rid: "linux-x64"), Host(new FakeExecReply(InstalledJson)), Release(), appVersion: string.Empty, clipboard: Clipboard());

        Assert.False(dialog.CopyCommandButton.IsVisible);
        Assert.False(dialog.InstallButton.IsEnabled); // nothing to download either
        Assert.True(dialog.ChooseFileButton is { IsVisible: true, IsEnabled: true });
    }

    [AvaloniaFact]
    public void Choose_file_installs_the_picked_file()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ntilde-mux-dialog-{Guid.NewGuid():N}");
        File.WriteAllBytes(path, Binary);
        try
        {
            SshProfile profile = Profile();
            RecordingExecTransport host = Host(new FakeExecReply(InstalledJson));
            Window? pickedFor = null;
            RemoteMuxInstallDialog dialog = Open(profile, host, new BlockingSource(), pickFile: window =>
            {
                pickedFor = window;
                return Task.FromResult<string?>(path);
            });

            Click(dialog.ChooseFileButton);
            PumpUntil(() => !dialog.IsRunning && dialog.OutcomeText.Text is { Length: > 0 }, "the install ended");

            Assert.Same(dialog.Window, pickedFor);
            Assert.IsType<LocalFileMuxAssetSource>(Assert.Single(_sources));
            Assert.Equal($"ntilde-mux 0.11.0 installed at {InstalledPath}", dialog.OutcomeText.Text);
            Assert.Equal(Binary, host.Runs[1].Stdin);
            Assert.Contains(MuxDaemonAsset.Sha256Of(Binary), dialog.LogBox.Text, StringComparison.Ordinal); // a picked file's own SHA-256 is shown
            Assert.Equal("0.11.0", profile.MuxOptions.RemoteDaemonVersion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void A_cancelled_file_picker_runs_nothing()
    {
        RecordingExecTransport host = Host(new FakeExecReply(InstalledJson));
        RemoteMuxInstallDialog dialog = Open(Profile(), host, Release(), pickFile: _ => Task.FromResult<string?>(null));

        Click(dialog.ChooseFileButton);
        Dispatcher.UIThread.RunJobs();

        Assert.False(dialog.IsRunning);
        Assert.Empty(host.Commands);
        Assert.Empty(_sources);
    }

    private sealed class FakeSource : IMuxDaemonAssetSource
    {
        private readonly MuxDaemonAsset? _asset;
        private readonly Exception? _failure;

        public FakeSource(MuxDaemonAsset asset) => _asset = asset;

        public FakeSource(Exception failure) => _failure = failure;

        public Task<MuxDaemonAsset> GetAsync(string rid, IProgress<long>? progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_failure is not null) throw _failure;
            progress?.Report(_asset!.Bytes.Length);
            return Task.FromResult(_asset!);
        }
    }

    /// <summary>A download that never ends on its own: it waits for its token.</summary>
    private sealed class BlockingSource : IMuxDaemonAssetSource
    {
        private readonly TaskCompletionSource<CancellationToken> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes with the download's token once it started.</summary>
        public Task<CancellationToken> Entered => _entered.Task;

        public async Task<MuxDaemonAsset> GetAsync(string rid, IProgress<long>? progress, CancellationToken ct)
        {
            _entered.TrySetResult(ct);
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }
    }
}
