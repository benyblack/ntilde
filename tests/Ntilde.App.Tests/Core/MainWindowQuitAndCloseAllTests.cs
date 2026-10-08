using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory

namespace Ntilde.Tests.Core;

/// <summary>
/// Task 17: "Quit and close all shells" ends every shell the local daemon runs (this window's, a shared one, a
/// detached one), shuts the daemon down and closes the window, and the Settings link that starts it.
/// </summary>
public sealed class MainWindowQuitAndCloseAllTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private const string CommandTitle = "Session: Quit and Close All Shells";

    private readonly MuxTestHost _mux = new();
    private readonly List<MuxConnectionHost> _hosts = [];
    private int _shutdowns;

    public MainWindowQuitAndCloseAllTests(TestAppDataRoot appData)
    {
        _ = appData;
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
        _mux.Server.ShutdownRequested += () => Interlocked.Increment(ref _shutdowns);
    }

    public void Dispose()
    {
        TestMainWindowFactory.DisposeCreatedWindows();
        foreach (MuxConnectionHost host in _hosts) host.Dispose();
        _mux.Dispose();
    }

    private MainWindow CreateWindow(Func<int, Task<bool>> confirm, string persistence = SessionPersistenceMode.KeepOnClose)
    {
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct), "test", null);
        _hosts.Add(host);
        var factory = new MuxTerminalSessionFactory(
            new MuxConnectionHosts(host, _ => null), new RecordingSessionFactory(new FakeTerminalSession()), null);
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
            Settings = new TerminalSettings { SessionPersistence = persistence },
        });
        window.ConfirmQuitAndCloseAll = confirm;
        // The real probe would look for the machine's daemon; the test's daemon is the one to shut down.
        window.MuxProbeForUpdate = async ct => await _mux.ConnectClientAsync();
        window.MuxReadDescriptorForUpdate = () => null;
        window.Show();
        PumpUntil(() => LocalSession(window) is { IsAttached: true }, "the first pane attached");
        return window;
    }

    private static MuxClientSession? LocalSession(MainWindow window) =>
        window.AllPanesForTest().Select(p => p.Session).OfType<MuxClientSession>().FirstOrDefault();

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

    /// <summary>The window's own shell, a second one it attached, and a third nobody shows (detached).</summary>
    private (Guid Own, Guid Attached, Guid Detached) ThreeShells(MainWindow window)
    {
        Guid own = LocalSession(window)!.Id;
        (Guid attached, Guid detached) = Task.Run(async () =>
        {
            MuxClient c = await _mux.ConnectClientAsync();
            return (await MuxTestHost.SpawnAsync(c), await MuxTestHost.SpawnAsync(c));
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        window.PickMuxSession = _ => Task.FromResult<Guid?>(attached);
        Task attach = window.AttachToMuxSessionAsync();
        PumpUntil(() => attach.IsCompleted, "the attach command finished");
        PumpUntil(() => window.AllPanesForTest().Any(p => p.Session is MuxClientSession { IsAttached: true } m && m.Id == attached), "the second shell attached");
        return (own, attached, detached);
    }

    [AvaloniaFact]
    public void Confirmed_ends_every_shell_shuts_the_daemon_down_and_closes_the_window()
    {
        var asked = new List<int>();
        MainWindow window = CreateWindow(count =>
        {
            asked.Add(count);
            return Task.FromResult(true);
        });
        (Guid own, Guid attached, Guid detached) = ThreeShells(window);

        Task quit = window.QuitAndCloseAllShellsAsync();
        PumpUntil(() => quit.IsCompleted && !window.IsVisible, "the window closed");

        Assert.Equal([3], asked);
        PumpUntil(() => !_mux.Server.GetSessionIds().Contains(own) && !_mux.Server.GetSessionIds().Contains(attached) && !_mux.Server.GetSessionIds().Contains(detached),
            "all three shells were killed");
        PumpUntil(() => Volatile.Read(ref _shutdowns) == 1, "the daemon received shutdown");
        // None is named for reattach: the next launch must not report them lost.
        string saved = File.ReadAllText(AppPaths.SessionFilePath);
        Assert.DoesNotContain(own.ToString(), saved);
        Assert.DoesNotContain(attached.ToString(), saved);
    }

    [AvaloniaFact]
    public void Declined_does_nothing()
    {
        var asked = new List<int>();
        MainWindow window = CreateWindow(count =>
        {
            asked.Add(count);
            return Task.FromResult(false);
        });
        (Guid own, Guid attached, Guid detached) = ThreeShells(window);

        Task quit = window.QuitAndCloseAllShellsAsync();
        PumpUntil(() => quit.IsCompleted, "the command returned");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([3], asked);
        Assert.True(window.IsVisible);
        Assert.Equal(0, Volatile.Read(ref _shutdowns));
        foreach (Guid id in new[] { own, attached, detached })
        {
            Assert.Contains(id, _mux.Server.GetSessionIds());
            Assert.False(_mux.Mux(id).IsExited);
        }
    }

    [AvaloniaTheory]
    [InlineData(SessionPersistenceMode.KeepOnClose, true)]
    [InlineData(SessionPersistenceMode.Off, false)]
    public void The_palette_command_exists_only_while_persistence_is_on(string persistence, bool registered)
    {
        MainWindow window = CreateWindow(_ => Task.FromResult(false));
        if (persistence == SessionPersistenceMode.Off)
        {
            // The factory follows the setting at runtime; the window's host outlives the flip.
            ((TerminalSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!)
                .SessionPersistence = SessionPersistenceMode.Off;
            typeof(MainWindow).GetMethod("ApplySessionPersistenceSetting", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        }

        typeof(MainWindow).GetMethod("SetupCommandPalette", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);

        Assert.Equal(registered, CommandRegistry.GetCommands().Any(c => c.Title == CommandTitle));
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_settings_link_follows_whether_persistence_is_active(bool active)
    {
        var settings = new SettingsWindow { SessionPersistenceActive = active };

        Button link = settings.FindControl<Button>("QuitAndCloseAllShellsLink")!;

        Assert.Equal(active, link.IsVisible);
        Assert.Equal("Quit and close all shells…", link.Content);
    }

    [AvaloniaFact]
    public void The_settings_link_asks_the_owner_and_closes_without_saving()
    {
        var settings = new SettingsWindow { SessionPersistenceActive = true };
        var owner = new Window();
        owner.Show();
        Task<bool> result = settings.ShowDialog<bool>(owner);
        Dispatcher.UIThread.RunJobs();

        settings.FindControl<Button>("QuitAndCloseAllShellsLink")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        PumpUntil(() => result.IsCompleted, "settings closed");
        Assert.True(settings.QuitAndCloseAllRequested);
        Assert.False(result.Result);
    }
}
