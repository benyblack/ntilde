using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Views.Ssh;

/// <summary>
/// Builds the installer for one run of <see cref="RemoteMuxInstallDialog"/>, over the profile's own exec
/// transport (Phase 4 spec §9: the same askpass and prompts as its tabs). The dialog calls it off the UI
/// thread, since building a transport may block (OpenSSH plans its config file).
/// </summary>
/// <param name="source">Where the binary comes from: a picked file, or null for this app version's release.</param>
/// <param name="report">The dialog's step-and-log sink; pass it to the installer.</param>
/// <param name="progress">The dialog's progress bar; set it as the installer's <see cref="RemoteMuxInstaller.Progress"/>.</param>
internal delegate RemoteMuxInstaller RemoteMuxInstallerFactory(
    IMuxDaemonAssetSource? source,
    Action<RemoteMuxInstallStep, string> report,
    IProgress<RemoteMuxInstallProgress>? progress);

/// <summary>
/// The "Install ntilde-mux" dialog (Phase 4 spec §9), code-built like the session picker: a step list, a
/// read-only log, a progress bar, and four buttons -
/// <list type="bullet">
/// <item><b>Install</b> (the default): this app version's release from GitHub;</item>
/// <item><b>Choose file…</b>: a binary the user has, picked with the storage provider;</item>
/// <item><b>Copy install command</b>: the offline one-liner (spec §9 step 2(c)) for the host's RID, put on
/// the clipboard;</item>
/// <item><b>Cancel / Close</b> (the cancel button, so Escape): cancels a running probe or install, and
/// closes the dialog when nothing runs.</item>
/// </list>
/// On success it records what was installed in the profile's <see cref="SshProfile.MuxOptions"/>
/// (<see cref="RemoteMuxInstaller.Record"/>) and offers "Keep remote sessions running", checked by
/// default; the flag is applied when the dialog closes. The caller saves the profile.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here blocks the UI thread: each run is on the pool, and its reports, its progress and its end
/// are posted back to the UI thread in that order (one priority, so the log is complete when the run's
/// end is shown). Only one run at a time.
/// </para>
/// <para>
/// Closing the window cancels a running install or probe. <see cref="Result"/> then completes when that run
/// has unwound - every click's work ends there, whichever way it returned - so an install that succeeded as
/// the window closed is still recorded (without the flag, which the user did not see offered). While the file
/// picker is open nothing else can start.
/// </para>
/// <para>
/// A version the release source has no release for (<see cref="MuxReleaseNotFoundException"/>) leaves only
/// Choose file: Install is disabled and the install command hidden, since both would fetch that release.
/// </para>
/// <para>
/// The RID for the install command is the profile's recorded <see cref="SshMuxOptions.RemoteDaemonRid"/>
/// when it is a published one; otherwise the button probes the host first (one exec, which may prompt).
/// The command is offered only when the app version is a plain release name and there is a clipboard;
/// a build without a version has no release to download either, so Install is disabled too.
/// </para>
/// </remarks>
internal sealed class RemoteMuxInstallDialog
{
    internal const string PendingMark = "\u25CB";   // white circle
    internal const string ActiveMark = "\u25CF";    // black circle
    internal const string DoneMark = "\u2713";      // check mark
    internal const string FailedMark = "\u2717";    // ballot x
    internal const string CopiedText = "Copied";
    internal const string CancelledText = "Cancelled";
    private const string CopyText = "Copy install command";

    private static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(2);

    private static readonly (RemoteMuxInstallStep Step, string Label)[] StepLabels =
    [
        (RemoteMuxInstallStep.Probing, "Probe the host"),
        (RemoteMuxInstallStep.Downloading, "Get ntilde-mux"),
        (RemoteMuxInstallStep.Uploading, "Upload it"),
        (RemoteMuxInstallStep.Verifying, "Verify it"),
    ];

    private readonly SshProfile _profile;
    private readonly RemoteMuxInstallerFactory _createInstaller;
    private readonly string _appVersion;
    private readonly IClipboard? _clipboard;
    private readonly Func<Window, Task<string?>> _pickFile;
    private readonly string _host;
    private readonly bool _flagWasOn;
    private readonly TaskCompletionSource<RemoteMuxInstallResult?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<RemoteMuxInstallStep, TextBlock> _steps = [];

    private CancellationTokenSource? _run; // the running probe or install; null while idle (UI thread)
    private bool _picking;                 // the file picker is open
    private bool _releaseMissing;          // the release download said this version has none
    private RemoteMuxInstallStep? _currentStep;
    private RemoteMuxInstallResult? _last;
    private bool _succeeded;
    private bool _closed;
    private IDisposable? _copiedReset;

    private RemoteMuxInstallDialog(
        SshProfile profile,
        RemoteMuxInstallerFactory createInstaller,
        string appVersion,
        IClipboard? clipboard,
        Func<Window, Task<string?>>? pickFile)
    {
        _profile = profile;
        _createInstaller = createInstaller;
        _appVersion = appVersion ?? string.Empty;
        _clipboard = clipboard;
        _pickFile = pickFile ?? PickFileAsync;
        _host = RemoteMuxConnector.DisplayNameOf(profile);
        _flagWasOn = profile.MuxOptions.PersistRemoteSessions;
        bool hasRelease = MuxDaemonAsset.IsPlainName(_appVersion);

        Window = new Window
        {
            Title = "Install ntilde-mux",
            Width = 640,
            Height = 540,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        UiScale.FitWindow(Window);

        InstallButton = new Button { Content = "Install", MinWidth = 92, IsDefault = true, IsEnabled = hasRelease };
        ChooseFileButton = new Button { Content = "Choose file\u2026" };
        CopyCommandButton = new Button { Content = CopyText, IsVisible = hasRelease && clipboard is not null };
        CloseButton = new Button { Content = "Close", MinWidth = 92, IsCancel = true };
        KeepRunningCheckBox = new CheckBox { Content = "Keep remote sessions running (ntilde-mux)", IsChecked = true, IsVisible = false };
        LogBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 150,
            Text = string.Empty,
        };
        Progress = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Height = 6 };
        OutcomeText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold, Text = string.Empty };

        var stepList = new StackPanel { Spacing = 2 };
        foreach ((RemoteMuxInstallStep step, string label) in StepLabels)
        {
            var line = new TextBlock { Tag = label };
            _steps[step] = line;
            stepList.Children.Add(line);
        }

        ResetSteps();

        InstallButton.Click += (_, _) => _ = GuardAsync(() => InstallAsync(source: null));
        ChooseFileButton.Click += (_, _) => _ = GuardAsync(ChooseFileAndInstallAsync);
        CopyCommandButton.Click += (_, _) => _ = GuardAsync(CopyInstallCommandAsync);
        CloseButton.Click += (_, _) => CancelOrClose();
        Window.Closing += (_, _) => _run?.Cancel();
        Window.Closed += (_, _) => OnClosed();

        string version = hasRelease ? $"ntilde-mux {_appVersion}" : "ntilde-mux";
        string intro = hasRelease
            ? "Install downloads this app version's release from GitHub and checks its SHA-256. "
              + "Choose file\u2026 uploads a binary you have; Copy install command gives a command to run on the host yourself."
            : "This build has no release to download: choose an ntilde-mux binary for the host.";

        Window.Content = new Border
        {
            Padding = new Thickness(16),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    Docked(new StackPanel
                    {
                        Spacing = 8,
                        Margin = new Thickness(0, 0, 0, 10),
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"Install {version} on {_host}",
                                FontWeight = FontWeight.SemiBold,
                                TextWrapping = TextWrapping.Wrap,
                            },
                            new TextBlock
                            {
                                Text = $"{RemoteMuxStatusText.Describe(profile.MuxOptions.RemoteDaemonVersion, _appVersion)}. It goes to ~/.local/share/ntilde/bin; nothing needs root.",
                                Opacity = 0.75,
                                TextWrapping = TextWrapping.Wrap,
                            },
                            new TextBlock { Text = intro, Opacity = 0.75, TextWrapping = TextWrapping.Wrap },
                            stepList,
                            Progress,
                        },
                    }, Dock.Top),
                    Docked(new DockPanel
                    {
                        Margin = new Thickness(0, 10, 0, 0),
                        LastChildFill = false,
                        Children =
                        {
                            Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { CopyCommandButton, ChooseFileButton } }, Dock.Left),
                            Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { InstallButton, CloseButton } }, Dock.Right),
                        },
                    }, Dock.Bottom),
                    Docked(new StackPanel
                    {
                        Spacing = 8,
                        Margin = new Thickness(0, 10, 0, 0),
                        Children = { OutcomeText, KeepRunningCheckBox },
                    }, Dock.Bottom),
                    LogBox,
                },
            },
        };
    }

    /// <summary>The dialog's window, not yet shown.</summary>
    internal Window Window { get; }

    /// <summary>
    /// Completes once the window has closed and no run is left: with the successful install, else the last
    /// install that failed, else null (nothing ran to its end).
    /// </summary>
    internal Task<RemoteMuxInstallResult?> Result => _result.Task;

    /// <summary>A probe or an install is running.</summary>
    internal bool IsRunning => _run is not null;

    internal Button InstallButton { get; }

    internal Button ChooseFileButton { get; }

    internal Button CopyCommandButton { get; }

    /// <summary>"Cancel" while something runs, "Close" otherwise; the window's cancel button either way.</summary>
    internal Button CloseButton { get; }

    internal CheckBox KeepRunningCheckBox { get; }

    internal TextBox LogBox { get; }

    internal ProgressBar Progress { get; }

    /// <summary>How the last run ended: what was installed, why it failed, or <see cref="CancelledText"/>.</summary>
    internal TextBlock OutcomeText { get; }

    /// <summary>
    /// What the dialog says when <paramref name="version"/> has no release (<see cref="MuxReleaseNotFoundException"/>):
    /// only Choose file is left, since the install command would download the same missing release.
    /// </summary>
    internal static string NoReleaseText(string version) =>
        $"There is no ntilde-mux release for {version}. Choose file\u2026 uploads an ntilde-mux binary you have.";

    /// <summary>The step's line: its mark (<see cref="PendingMark"/>, <see cref="ActiveMark"/>, <see cref="DoneMark"/>, <see cref="FailedMark"/>) and its label.</summary>
    internal string StepText(RemoteMuxInstallStep step) => _steps[step].Text ?? string.Empty;

    /// <summary>
    /// Shows the dialog over <paramref name="owner"/> and returns <see cref="Result"/>. On success the
    /// profile's <see cref="SshProfile.MuxOptions"/> holds the install (and the flag, if the user kept it
    /// ticked); the caller saves the profile.
    /// </summary>
    /// <param name="appVersion">This app's version without build metadata (<see cref="AppVersionInfo.Version"/>).</param>
    /// <param name="clipboard">For "Copy install command"; null hides it.</param>
    /// <param name="prepare">Themes the window before it is shown.</param>
    internal static async Task<RemoteMuxInstallResult?> ShowAsync(
        Window owner,
        SshProfile profile,
        RemoteMuxInstallerFactory createInstaller,
        string appVersion,
        IClipboard? clipboard,
        Action<Window>? prepare = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        RemoteMuxInstallDialog dialog = Build(profile, createInstaller, appVersion, clipboard);
        prepare?.Invoke(dialog.Window);
        await dialog.Window.ShowDialog(owner);
        return await dialog.Result;
    }

    /// <summary>The dialog, not yet shown; separate from <see cref="ShowAsync"/> so a headless test can show it and press its buttons.</summary>
    /// <param name="pickFile">Picks a binary for "Choose file…" (null: cancelled); the storage provider's file picker when null.</param>
    internal static RemoteMuxInstallDialog Build(
        SshProfile profile,
        RemoteMuxInstallerFactory createInstaller,
        string appVersion,
        IClipboard? clipboard,
        Func<Window, Task<string?>>? pickFile = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(createInstaller);
        profile.MuxOptions ??= new SshMuxOptions(); // a store written before the options existed
        return new RemoteMuxInstallDialog(profile, createInstaller, appVersion, clipboard, pickFile);
    }

    private static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    /// <summary>
    /// A click's work. Every run starts from one, so its end is where <see cref="Result"/> completes when the window
    /// closed meanwhile (<see cref="CompleteIfClosed"/>) - whichever way the work returned. The click handlers
    /// discard the task: it never faults, since a throw nothing expected goes to the log, not the dispatcher.
    /// </summary>
    private async Task GuardAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[RemoteMuxInstallDialog] {_host}: {ex}");
            AppendLog($"Something went wrong: {ex.Message}");
        }
        finally
        {
            CompleteIfClosed();
        }
    }

    /// <summary>Whether a release can be downloaded: a plain version that has not been found missing.</summary>
    private bool HasRelease => MuxDaemonAsset.IsPlainName(_appVersion) && !_releaseMissing;

    /// <summary>Nothing new may start while a run is going, the picker is open, or after the install or the close.</summary>
    private bool Busy => _run is not null || _picking || _closed;

    private async Task InstallAsync(IMuxDaemonAssetSource? source)
    {
        if (Busy || _succeeded) return;

        CancellationTokenSource run = BeginRun();
        RemoteMuxInstallResult? result;
        try
        {
            var progress = new UiProgress(this);
            result = await OffUiThreadAsync(ct => _createInstaller(source, Report, progress).InstallAsync(ct), run.Token);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            result = null;
        }
        catch (Exception ex)
        {
            // The factory itself failed (no launch plan for the profile, say); the installer never throws else.
            result = new RemoteMuxInstallResult(false, ex.Message, null);
        }
        finally
        {
            EndRun(run);
        }

        if (result is null)
        {
            ShowCancelled();
        }
        else if (result.Success && result.Installed is { } installed)
        {
            RemoteMuxInstaller.Record(_profile.MuxOptions, installed);
            _succeeded = true;
            _last = result;
            ShowSucceeded(result.Message);
        }
        else if (result.ReleaseMissing)
        {
            // Install again and the install command would both download the same missing release.
            _last = result;
            _releaseMissing = true;
            CopyCommandButton.IsVisible = false;
            SetActionsEnabled(true);
            ShowFailed(NoReleaseText(_appVersion));
        }
        else
        {
            _last = result;
            ShowFailed(result.Message);
        }
    }

    private async Task ChooseFileAndInstallAsync()
    {
        if (Busy || _succeeded) return;

        // The picker may not be modal (a portal's): nothing else starts meanwhile, and a second click opens no second one.
        _picking = true;
        SetActionsEnabled(false);
        string? path;
        try
        {
            path = await _pickFile(Window);
        }
        catch (Exception ex)
        {
            AppendLog($"Choosing a file failed: {ex.Message}");
            path = null;
        }
        finally
        {
            _picking = false;
            if (_run is null) SetActionsEnabled(true);
        }

        if (string.IsNullOrWhiteSpace(path) || _closed) return;
        AppendLog($"Using {path}");
        await InstallAsync(new LocalFileMuxAssetSource(path));
    }

    /// <summary>
    /// Puts the offline one-liner for the host's RID on the clipboard: the recorded RID when it is a published
    /// one, otherwise the probe's. A refused host gets its reason instead, and nothing is copied.
    /// </summary>
    private async Task CopyInstallCommandAsync()
    {
        if (Busy || _clipboard is null || !HasRelease) return;

        string? rid = MuxDaemonRid.IsKnown(_profile.MuxOptions.RemoteDaemonRid) ? _profile.MuxOptions.RemoteDaemonRid : null;
        if (rid is null)
        {
            CancellationTokenSource run = BeginRun();
            RemoteHostProbeOutcome? outcome;
            try
            {
                outcome = await OffUiThreadAsync(ct => _createInstaller(null, Report, null).ProbeAsync(ct), run.Token);
            }
            catch (OperationCanceledException) when (run.IsCancellationRequested)
            {
                outcome = null;
            }
            catch (Exception ex)
            {
                outcome = new RemoteHostRefusal(ex.Message);
            }
            finally
            {
                EndRun(run);
            }

            switch (outcome)
            {
                case null:
                    ShowCancelled();
                    return;
                case RemoteHostRefusal refusal:
                    ShowFailed(refusal.Reason);
                    return;
                case RemoteHostFacts facts:
                    MarkStep(RemoteMuxInstallStep.Probing, DoneMark);
                    rid = facts.Rid;
                    break;
            }

            if (_closed) return;
        }

        try
        {
            string command = RemoteMuxInstallCommands.OfflineOneLiner(_appVersion, rid!);
            await _clipboard.SetTextAsync(command);
            AppendLog($"Copied the install command for {rid}; run it on {_host}:{Environment.NewLine}{command}");
            ShowCopied();
        }
        catch (Exception ex)
        {
            AppendLog($"Copying the install command failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the pool. Its end is posted to the UI thread after every report it
    /// made (one priority), so the awaiting code sees the whole log first.
    /// </summary>
    private static Task<T> OffUiThreadAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        var ended = new TaskCompletionSource<T>();
        _ = Task.Run(async () =>
        {
            T value = default!;
            Exception? failure = null;
            try
            {
                value = await work(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (failure is null) ended.TrySetResult(value);
                else ended.TrySetException(failure);
            });
        }, CancellationToken.None);
        return ended.Task;
    }

    /// <summary>The installer's report: posted to the UI thread (the flow is on the pool).</summary>
    private void Report(RemoteMuxInstallStep step, string message) => Dispatcher.UIThread.Post(() => OnReport(step, message));

    private void OnReport(RemoteMuxInstallStep step, string message)
    {
        AppendLog(message);
        if (step == RemoteMuxInstallStep.Done)
        {
            foreach ((RemoteMuxInstallStep each, _) in StepLabels) MarkStep(each, DoneMark);
            _currentStep = null;
            return;
        }

        if (_currentStep == step) return;
        foreach ((RemoteMuxInstallStep each, _) in StepLabels)
        {
            if (each == step) break;
            MarkStep(each, DoneMark);
        }

        MarkStep(step, ActiveMark);
        _currentStep = step;
        // A new step starts without a size; the upload's first byte count makes the bar determinate.
        Progress.IsIndeterminate = true;
    }

    private void OnProgress(RemoteMuxInstallProgress progress)
    {
        if (_run is null) return;
        if (progress.Total > 0)
        {
            Progress.IsIndeterminate = false;
            Progress.Maximum = progress.Total;
            Progress.Value = Math.Min(progress.Done, progress.Total);
        }
        else
        {
            Progress.IsIndeterminate = true;
        }
    }

    private CancellationTokenSource BeginRun()
    {
        var run = new CancellationTokenSource();
        _run = run;
        _currentStep = null;
        ResetSteps();
        OutcomeText.Text = string.Empty;
        Progress.IsIndeterminate = true;
        Progress.Maximum = 1;
        Progress.Value = 0;
        SetActionsEnabled(false);
        CloseButton.Content = "Cancel";
        return run;
    }

    private void EndRun(CancellationTokenSource run)
    {
        if (ReferenceEquals(_run, run)) _run = null;
        run.Dispose();
        Progress.IsIndeterminate = false;
        CloseButton.Content = "Close";
        SetActionsEnabled(true);
    }

    /// <summary>Install, Choose file and Copy: all off while something runs or the picker is open; Install only with a release.</summary>
    private void SetActionsEnabled(bool enabled)
    {
        InstallButton.IsEnabled = enabled && HasRelease;
        ChooseFileButton.IsEnabled = enabled;
        CopyCommandButton.IsEnabled = enabled;
    }

    private void ShowSucceeded(string message)
    {
        foreach ((RemoteMuxInstallStep each, _) in StepLabels) MarkStep(each, DoneMark);
        Progress.Maximum = 1;
        Progress.Value = 1;
        OutcomeText.Text = message;
        InstallButton.IsVisible = false;
        ChooseFileButton.IsVisible = false;
        CopyCommandButton.IsVisible = false;
        InstallButton.IsDefault = false;
        CloseButton.IsDefault = true;
        // Offered only when it is off: the install flow never changes it on its own (spec §9 step 4).
        KeepRunningCheckBox.IsVisible = !_flagWasOn;
    }

    private void ShowFailed(string reason)
    {
        if (_currentStep is { } step) MarkStep(step, FailedMark);
        else MarkStep(RemoteMuxInstallStep.Probing, FailedMark);
        Progress.Value = 0;
        OutcomeText.Text = reason;
        AppendLog(reason);
    }

    private void ShowCancelled()
    {
        if (_currentStep is { } step) MarkStep(step, FailedMark);
        Progress.Value = 0;
        OutcomeText.Text = CancelledText;
        AppendLog(CancelledText);
    }

    private void ShowCopied()
    {
        CopyCommandButton.Content = CopiedText;
        _copiedReset?.Dispose();
        _copiedReset = DispatcherTimer.RunOnce(() => CopyCommandButton.Content = CopyText, CopiedFor);
    }

    private void CancelOrClose()
    {
        if (_run is { } run)
        {
            run.Cancel();
            OutcomeText.Text = "Cancelling\u2026";
            return;
        }

        Window.Close();
    }

    private void OnClosed()
    {
        _closed = true;
        _copiedReset?.Dispose();
        if (_succeeded && KeepRunningCheckBox.IsVisible && KeepRunningCheckBox.IsChecked == true)
        {
            _profile.MuxOptions.PersistRemoteSessions = true;
        }

        CompleteIfClosed();
    }

    /// <summary>Completes <see cref="Result"/> once the window is closed and nothing runs any more.</summary>
    private void CompleteIfClosed()
    {
        if (_closed && _run is null) _result.TrySetResult(_last);
    }

    private void ResetSteps()
    {
        foreach ((RemoteMuxInstallStep step, _) in StepLabels) MarkStep(step, PendingMark);
    }

    private void MarkStep(RemoteMuxInstallStep step, string mark)
    {
        TextBlock line = _steps[step];
        line.Text = $"{mark}  {line.Tag}";
        line.Opacity = mark == PendingMark ? 0.6 : 1.0;
    }

    private void AppendLog(string line)
    {
        string text = LogBox.Text ?? string.Empty;
        text = text.Length == 0 ? line : $"{text}{Environment.NewLine}{line}";
        LogBox.Text = text;
        LogBox.CaretIndex = text.Length;
    }

    /// <summary>The storage provider's open-file picker; the chosen file's local path, or null.</summary>
    private static async Task<string?> PickFileAsync(Window window)
    {
        IStorageProvider? storage = TopLevel.GetTopLevel(window)?.StorageProvider;
        if (storage is null || !storage.CanOpen) return null;

        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an ntilde-mux binary for the host",
            AllowMultiple = false,
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>The installer's byte counts, posted to the UI thread in order with its reports.</summary>
    private sealed class UiProgress(RemoteMuxInstallDialog dialog) : IProgress<RemoteMuxInstallProgress>
    {
        public void Report(RemoteMuxInstallProgress value) => Dispatcher.UIThread.Post(() => dialog.OnProgress(value));
    }
}
