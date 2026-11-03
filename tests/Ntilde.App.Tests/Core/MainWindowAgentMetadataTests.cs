using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;
using Ntilde.Controls;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Pty;
using Ntilde.Tests.Controls;

namespace Ntilde.Tests.Core;

/// <summary>Actual parser-to-host metadata and UI readback; no native clipboard, URL opening or external notification recipient.</summary>
public sealed class MainWindowAgentMetadataTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    private static (MainWindow Window, TerminalPane Pane, TerminalSettings Settings) CreateWindow(bool observeEnabled = false, string orientation = "Horizontal")
    {
        var profile = new TerminalProfile { Name = "Owned metadata local", Type = ConnectionType.Local };
        var settings = new TerminalSettings
        {
            SessionPersistence = SessionPersistenceMode.Off,
            AgentAccessObserveEnabled = observeEnabled,
            TabStripOrientation = orientation,
            AgentAccessActEnabled = false,
            CommandAssistShellIntegrationEnabled = false,
            Osc9NotificationsEnabled = true,
            Osc9ProgressReportingEnabled = true,
            Profiles = [profile],
            DefaultProfileId = profile.Id,
        };
        var window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            Settings = settings,
            SessionFactory = new RecordingSessionFactory(new FakeTerminalSession()),
            CommandAssist = TestCommandAssistServices.Instance,
        });
        window.Show(); Dispatcher.UIThread.RunJobs();
        return (window, Assert.Single(window.AllPanesForTest()), settings);
    }

    private static AgentSessionRegistration Registration(TerminalPane pane)
    {
        var registration = new AgentSessionRegistration(pane.PaneId, pane.Buffer!, "initial", "local", "local", true);
        registration.SetLifecycle(pane.Session); return registration;
    }

    private static async Task<PaneMetadataResult?> Read(MainWindow window, AgentSessionRegistration registration)
    {
        var task = ((IAgentPaneObserver)window).ReadPaneMetadataAsync(registration, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        return await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [AvaloniaFact]
    public async Task Initial_window_startup_publishes_the_read_observer_without_act_access()
    {
        var registry = new AgentSessionRegistry();
        using var registryScope = AgentSessionRegistry.OverrideInstanceForTesting(registry);
        string root = Ntilde.AppTests.AgentHost.AgentHostTestEndpoint.CreateTempDir("metadata");
        try
        {
            using var service = new AgentHostService(registry, Ntilde.AppTests.AgentHost.AgentHostTestEndpoint.CreateEndpoint(root), root);
            using var scope = AgentHostService.OverrideInstanceForTesting(service);
            var (window, pane, _) = CreateWindow(observeEnabled: true);
            Assert.False(service.ActEnabled);
            Assert.Equal(pane.PaneId, Assert.Single(registry.GetRegistrations()).PaneId);
            string request = "{\"v\":1,\"id\":1,\"method\":\"readPaneMetadata\",\"params\":{\"paneId\":\"" + pane.PaneId + "\"}}";
            var pending = service.HandleRequestLineAsync(request, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Null(response.Error);
            Assert.Equal(pane.PaneId.ToString(), response.Result!.Value.GetProperty("paneId").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public async Task Parser_title_cwd_links_and_prompt_anchor_reach_independent_host_readback_without_activation()
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, pane, _) = CreateWindow(); var registration = Registration(pane);
        string cwd = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ntilde-metadata-fixture"));
        var tabs = window.FindControl<TabControl>("Tabs")!; var selected = tabs.SelectedItem;
        var size = window.ClientSize;
        pane.Parser!.Process("\u001b]2;owned-title\a\u001b]7;" + new Uri(cwd).AbsoluteUri + "\a\u001b[2;4H\u001b]133;B\a\u001b]8;id=owned;https://example.invalid/owned\aLINK\u001b]8;;\a");
        Dispatcher.UIThread.RunJobs();
        var observed = Assert.IsType<PaneMetadataResult>(await Read(window, registration));
        Assert.Equal("owned-title", observed.OscTitle); Assert.Equal("owned-title", observed.DisplayTitle);
        Assert.Equal(cwd, observed.WorkingDirectory); Assert.Contains("owned-title", observed.Presentation.TabText);
        Assert.Equal(pane.PaneId, observed.PaneId); Assert.True(observed.Buffer.AcceptingCommandInput);
        var mark = Assert.IsType<PaneShellMark>(observed.Buffer.CommandStart); Assert.Equal((1, 3), (mark.Row, mark.Col));
        var link = Assert.Single(observed.Buffer.Links);
        Assert.Equal((1, 3, 4, "owned", "https://example.invalid/owned"), (link.Row, link.Col, link.Length, link.Id, link.Uri));
        Assert.Same(selected, tabs.SelectedItem); Assert.Equal(size, window.ClientSize);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stored_progress_and_actual_tab_text_distinguish_the_effective_presentation_setting(bool enabled)
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, pane, settings) = CreateWindow(); var registration = Registration(pane);
        settings.Osc9ProgressReportingEnabled = enabled;
        pane.Parser!.Process("\u001b]9;4;1;42\a"); Dispatcher.UIThread.RunJobs();
        var observed = Assert.IsType<PaneMetadataResult>(await Read(window, registration));
        Assert.Equal("Normal", observed.Presentation.ProgressKind); Assert.Equal((byte)42, observed.Presentation.ProgressPercent);
        Assert.Equal(pane.PaneId, observed.Presentation.ProgressOwner); Assert.Equal(enabled, observed.Presentation.ProgressEnabled);
        Assert.Equal(enabled, observed.Presentation.TabText.Contains("42%", StringComparison.Ordinal));
        pane.Parser.Process("\u001b]9;4;0\a"); Dispatcher.UIThread.RunJobs();
        var cleared = Assert.IsType<PaneMetadataResult>(await Read(window, registration));
        Assert.Null(cleared.Presentation.ProgressKind); Assert.Null(cleared.Presentation.ProgressOwner);
        Assert.DoesNotContain("42%", cleared.Presentation.TabText);
    }

    [AvaloniaTheory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Vertical_progress_readback_observes_actual_visibility_indeterminate_and_value(bool enabled, bool indeterminate)
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, pane, settings) = CreateWindow(orientation: "Vertical");
        Assert.True(window.IsVerticalTabStripActive);
        var registration = Registration(pane);
        settings.Osc9ProgressReportingEnabled = enabled;
        pane.Parser!.Process(indeterminate ? "\u001b]9;4;3\a" : "\u001b]9;4;1;42\a");
        Dispatcher.UIThread.RunJobs();
        var observed = Assert.IsType<PaneMetadataResult>(await Read(window, registration));
        Assert.Equal(indeterminate ? "Indeterminate" : "Normal", observed.Presentation.ProgressKind);
        Assert.Equal(pane.PaneId, observed.Presentation.ProgressOwner);
        Assert.Equal(enabled, observed.Presentation.ProgressEnabled);
        var bar = Assert.IsType<PaneProgressBarMetadata>(observed.Presentation.ProgressBar);
        Assert.Equal(enabled, bar.Visible);
        Assert.Equal(enabled && indeterminate, bar.Indeterminate);
        Assert.Equal(enabled && !indeterminate ? 42d : 0d, bar.Value);
        Assert.DoesNotContain("42%", observed.Presentation.TabText);

        pane.Parser.Process("\u001b]9;4;0\a");
        Dispatcher.UIThread.RunJobs();
        var cleared = Assert.IsType<PaneMetadataResult>(await Read(window, registration));
        Assert.Null(cleared.Presentation.ProgressKind);
        Assert.Null(cleared.Presentation.ProgressOwner);
        var hidden = Assert.IsType<PaneProgressBarMetadata>(cleared.Presentation.ProgressBar);
        Assert.False(hidden.Visible);
        // Hiding the actual control preserves its last geometry/animation values.
        Assert.Equal(bar.Indeterminate, hidden.Indeterminate); Assert.Equal(bar.Value, hidden.Value);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Notification_readback_observes_actual_toast_controls_and_focus_policy(bool enabled)
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, pane, settings) = CreateWindow(); var registration = Registration(pane);
        settings.Osc9NotificationsEnabled = enabled;
        window.FindControl<Border>("RecordingToast")!.IsVisible = false;
        pane.Parser!.Process("\u001b]9;owned-notification\a"); Dispatcher.UIThread.RunJobs();
        var observed = Assert.IsType<PaneMetadataResult>(await Read(window, registration));
        Assert.Equal(enabled, observed.Presentation.NotificationsEnabled);
        Assert.Equal(window.IsActive, observed.Presentation.WindowActive);
        bool expected = enabled && !window.IsActive; // The fixture has exactly one current pane.
        Assert.Equal(expected, observed.Presentation.ToastVisible);
        if (expected) Assert.Equal("owned-notification", observed.Presentation.ToastMessage);
        Assert.Equal(window.FindControl<TextBlock>("RecordingToastMessage")!.Text ?? "", observed.Presentation.ToastMessage);
    }

    [AvaloniaTheory]
    [InlineData("unknown")]
    [InlineData("hidden")]
    [InlineData("session")]
    [InlineData("buffer")]
    [InlineData("remote")]
    [InlineData("mux")]
    public async Task Unavailable_or_changed_presenting_identity_is_refused(string changed)
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, pane, _) = CreateWindow(); var registration = Registration(pane);
        switch (changed)
        {
            case "unknown":
                registration = new AgentSessionRegistration(Guid.NewGuid(), pane.Buffer!, "", "", "local", true);
                registration.SetLifecycle(pane.Session);
                break;
            case "hidden": pane.IsVisible = false; break;
            case "session": registration.SetLifecycle(new Ntilde.AppTests.AgentHost.InputStubSession()); break;
            case "buffer": registration = new AgentSessionRegistration(pane.PaneId, new Ntilde.VT.TerminalBuffer(80, 4), "", "", "local", true); registration.SetLifecycle(pane.Session); break;
            case "remote": pane.Profile!.Type = ConnectionType.SSH; break;
            case "mux": pane.TermView.DefersBufferResizeToSession = true; break;
        }
        Assert.Null(await Read(window, registration));
    }

    [AvaloniaFact]
    public async Task Overlong_host_metadata_is_refused_without_truncating_or_rewriting_it()
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, pane, _) = CreateWindow(); var registration = Registration(pane);
        string title = new('x', 4097);
        pane.Parser!.Process("\u001b]2;" + title + "\a"); Dispatcher.UIThread.RunJobs();
        Assert.Null(await Read(window, registration)); Assert.Equal(title, pane.CurrentOscTitle);
    }
}
