using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Mux.Cli;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Services.Ssh;
using Ntilde.Shell;
using Ntilde.Shell.Mux.Remote;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory
using Ntilde.ViewModels.Ssh;

namespace Ntilde.Tests.Core;

/// <summary>
/// The two ways into the ntilde-mux install dialog (Phase 4 spec §9): the connection editor's
/// "Install ntilde-mux on this host…" and the unavailable toast's action. The dialog itself is a seam
/// here (<see cref="MainWindow.ShowRemoteMuxInstall"/>); its own tests are RemoteMuxInstallDialogTests.
/// </summary>
/// <remarks>
/// <see cref="TestAppDataRoot"/> is taken for its lifetime: both paths save the profile to the SSH store,
/// which must not be the developer's own.
/// </remarks>
public sealed class NewSshConnectionInstallFlowTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private const string InstalledPath = "/home/nova/.local/share/ntilde/bin/ntilde-mux";
    private static readonly MuxVersionInfo Installed = new("0.11.0", 1, 2, "linux-x64", InstalledPath);

    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    private static MainWindow CreateWindow()
    {
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = new RecordingSessionFactory(new FakeTerminalSession()),
        });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>What the store holds now, read by a store of its own.</summary>
    private static SshProfile? Stored(Guid id) => new SshConnectionService().GetStoredProfile(id);

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

    /// <summary>A dialog that installs, records it as the real one does, and leaves "Keep remote sessions running" as <paramref name="keepRunning"/>.</summary>
    private static Func<Window, SshProfile, Task<RemoteMuxInstallResult?>> InstallingDialog(List<(Window Owner, SshProfile Profile)> shown, bool keepRunning) =>
        (owner, profile) =>
        {
            shown.Add((owner, profile));
            RemoteMuxInstaller.Record(profile.MuxOptions, Installed);
            if (keepRunning) profile.MuxOptions.PersistRemoteSessions = true;
            return Task.FromResult<RemoteMuxInstallResult?>(new RemoteMuxInstallResult(true, $"ntilde-mux 0.11.0 installed at {InstalledPath}", Installed));
        };

    [AvaloniaFact]
    public void Install_command_is_enabled_for_a_saveable_profile_and_refreshes_the_status_after_an_install()
    {
        MainWindow window = CreateWindow();
        var vm = new NewSshConnectionViewModel { Name = "install-flow", BackendKind = SshBackendKind.OpenSsh, AppVersion = "0.11.0" };
        window.WireRemoteMuxInstall(vm, window);
        Assert.False(vm.InstallRemoteMuxCommand.CanExecute(null));
        vm.HostName = "fake-host";
        Assert.False(vm.InstallRemoteMuxCommand.CanExecute(null)); // no user yet
        int changed = 0;
        vm.InstallRemoteMuxCommand.CanExecuteChanged += (_, _) => changed++;

        vm.UserName = "nova";

        Assert.True(vm.InstallRemoteMuxCommand.CanExecute(null));
        Assert.True(changed > 0);
        Assert.Equal("ntilde-mux not installed", vm.RemoteDaemonStatusText);

        var shown = new List<(Window Owner, SshProfile Profile)>();
        window.ShowRemoteMuxInstall = InstallingDialog(shown, keepRunning: true);
        vm.InstallRemoteMuxCommand.Execute(null);
        PumpUntil(() => vm.RemoteDaemonStatusText == "ntilde-mux 0.11.0 installed", "the status line refreshed");

        (Window owner, SshProfile profile) = Assert.Single(shown);
        Assert.Same(window, owner);
        Assert.Equal(("fake-host", "nova"), (profile.Host, profile.User)); // the pending edits were saved first
        Assert.Equal(profile.Id, vm.ProfileId); // and the editor's own save updates that profile, not a copy
        Assert.True(vm.PersistRemoteSessions);
        SshProfile stored = Stored(profile.Id)!;
        Assert.Equal(
            ("0.11.0", InstalledPath, "linux-x64", true),
            (stored.MuxOptions.RemoteDaemonVersion, stored.MuxOptions.RemoteDaemonPath, stored.MuxOptions.RemoteDaemonRid, stored.MuxOptions.PersistRemoteSessions));
        SshProfile edited = vm.ToSshProfile();
        Assert.Equal((InstalledPath, "linux-x64"), (edited.MuxOptions.RemoteDaemonPath, edited.MuxOptions.RemoteDaemonRid));
        PumpUntil(() => vm.InstallRemoteMuxCommand.CanExecute(null), "the command is enabled again");
    }

    [AvaloniaFact]
    public void An_install_that_did_not_succeed_changes_no_status_but_the_edits_are_saved()
    {
        MainWindow window = CreateWindow();
        var vm = new NewSshConnectionViewModel { HostName = "fake-host", UserName = "nova", BackendKind = SshBackendKind.OpenSsh, AppVersion = "0.11.0" };
        window.WireRemoteMuxInstall(vm, window);
        int shown = 0;
        window.ShowRemoteMuxInstall = (_, _) =>
        {
            shown++;
            return Task.FromResult<RemoteMuxInstallResult?>(null); // closed before any install ended
        };

        vm.InstallRemoteMuxCommand.Execute(null);
        PumpUntil(() => shown == 1 && vm.InstallRemoteMuxCommand.CanExecute(null), "the flow ended");

        Assert.Equal("ntilde-mux not installed", vm.RemoteDaemonStatusText);
        Assert.False(vm.PersistRemoteSessions);
        Assert.Equal(string.Empty, Stored(vm.ProfileId!.Value)!.MuxOptions.RemoteDaemonVersion);
    }

    [AvaloniaFact]
    public void An_editor_that_does_not_validate_opens_no_dialog()
    {
        MainWindow window = CreateWindow();
        var vm = new NewSshConnectionViewModel
        {
            HostName = "fake-host",
            UserName = "nova",
            BackendKind = SshBackendKind.OpenSsh,
            AuthMode = NewSshAuthMode.IdentityFile, // with no identity file
        };
        window.WireRemoteMuxInstall(vm, window);
        int shown = 0;
        window.ShowRemoteMuxInstall = (_, _) =>
        {
            shown++;
            return Task.FromResult<RemoteMuxInstallResult?>(null);
        };

        vm.InstallRemoteMuxCommand.Execute(null);
        PumpUntil(() => vm.InstallRemoteMuxCommand.CanExecute(null), "the flow ended");

        Assert.Equal(0, shown);
        Assert.Equal("Identity file is required when using IdentityFile auth.", vm.ValidationError);
        Assert.Null(vm.ProfileId);
    }

    [AvaloniaFact]
    public void The_toast_action_installs_for_the_stored_profile_and_saves_what_it_recorded()
    {
        SshProfile saved = new SshConnectionService().SaveProfile(new NewSshConnectionViewModel
        {
            Name = "toast-flow",
            HostName = "toast-host",
            UserName = "nova",
            Port = 2222,
            BackendKind = SshBackendKind.OpenSsh,
            PersistRemoteSessions = true,
        });
        MainWindow window = CreateWindow();
        var shown = new List<(Window Owner, SshProfile Profile)>();
        window.ShowRemoteMuxInstall = InstallingDialog(shown, keepRunning: false);

        window.OpenRemoteMuxInstall!(saved.Id);
        PumpUntil(() => Stored(saved.Id)?.MuxOptions.RemoteDaemonVersion == "0.11.0", "the install was saved");

        (Window owner, SshProfile profile) = Assert.Single(shown);
        Assert.Same(window, owner);
        Assert.Equal(saved.Id, profile.Id);
        SshProfile stored = Stored(saved.Id)!;
        Assert.Equal((InstalledPath, "linux-x64", true), (stored.MuxOptions.RemoteDaemonPath, stored.MuxOptions.RemoteDaemonRid, stored.MuxOptions.PersistRemoteSessions));
        Assert.Equal(("toast-flow", "toast-host", "nova", 2222), (stored.Name, stored.Host, stored.User, stored.Port));
    }

    [AvaloniaFact]
    public void The_toast_action_for_a_deleted_profile_opens_no_dialog()
    {
        MainWindow window = CreateWindow();
        int shown = 0;
        window.ShowRemoteMuxInstall = (_, _) =>
        {
            shown++;
            return Task.FromResult<RemoteMuxInstallResult?>(null);
        };

        window.OpenRemoteMuxInstall!(Guid.NewGuid());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, shown);
    }
}
