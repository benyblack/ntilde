using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.AgentHost;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Storage;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls;

namespace Ntilde.Tests.Core;

public sealed class MainWindowAgentSpawnTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task UnnamedSpawnWithDeferredLegacySshDefaultUsesFirstLocalProfile(string? name)
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, settings, factory) = CreateWindow();
        TerminalProfile local = settings.Profiles.First(p => p.Type == ConnectionType.Local);
        Assert.Equal(ConnectionType.SSH, settings.Profiles.First(p => p.Id == settings.DefaultProfileId).Type);

        var (result, error) = await ((IAgentActionExecutor)window).SpawnAsync(name);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal("local", result.Value.Kind);
        Assert.Equal(local.Name, result.Value.ProfileName);
        Assert.Null(factory.LastRequest?.Ssh);
    }

    [AvaloniaFact]
    public async Task UnnamedSpawnWithoutLocalProfileReturnsNotFoundWithoutSpawning()
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, settings, factory) = CreateWindow();
        settings.Profiles.RemoveAll(p => p.Type == ConnectionType.Local);
        var previousRequest = factory.LastRequest;

        var (result, error) = await ((IAgentActionExecutor)window).SpawnAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(result);
        Assert.Equal(AgentSpawnError.ProfileNotFound, error);
        Assert.Same(previousRequest, factory.LastRequest);
    }

    [AvaloniaFact]
    public async Task UnnamedSpawnPreservesConfiguredLocalDefault()
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var (window, settings, _) = CreateWindow();
        var preferred = new TerminalProfile { Name = "Preferred local", Type = ConnectionType.Local };
        settings.Profiles.Add(preferred);
        settings.DefaultProfileId = preferred.Id;

        var (result, error) = await ((IAgentActionExecutor)window).SpawnAsync(null);

        Assert.Null(error);
        Assert.Equal(preferred.Name, result?.ProfileName);
        Assert.Equal("local", result?.Kind);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedSshSpawnRequiresStoredAllowlist(bool allowed)
    {
        using var service = new AgentHostService(new AgentSessionRegistry());
        using var scope = AgentHostService.OverrideInstanceForTesting(service);
        var store = new JsonSshProfileStore();
        var ssh = new SshProfile { Name = $"Agent SSH {Guid.NewGuid():N}", Host = "test.invalid", User = "test", AllowAgentAccess = allowed };
        store.SaveProfile(ssh);
        try
        {
            var (window, _, factory) = CreateWindow();
            var previousRequest = factory.LastRequest;
            var (result, error) = await ((IAgentActionExecutor)window).SpawnAsync(ssh.Name);
            Dispatcher.UIThread.RunJobs();
            if (allowed)
            {
                Assert.Null(error);
                Assert.Equal("ssh", result?.Kind);
                Assert.NotNull(factory.LastRequest?.Ssh);
            }
            else
            {
                Assert.Null(result);
                Assert.Equal(AgentSpawnError.ProfileNotAllowed, error);
                Assert.Same(previousRequest, factory.LastRequest);
            }
        }
        finally
        {
            store.DeleteProfile(ssh.Id);
        }
    }

    private static (MainWindow Window, TerminalSettings Settings, RecordingSessionFactory Factory) CreateWindow()
    {
        var legacy = new TerminalProfile { Name = "Unapproved legacy SSH", Type = ConnectionType.SSH, SshHost = "test.invalid" };
        var local = new TerminalProfile { Name = "Local fallback", Type = ConnectionType.Local };
        var settings = new TerminalSettings
        {
            SessionPersistence = SessionPersistenceMode.Off,
            AgentAccessActEnabled = true,
            CommandAssistShellIntegrationEnabled = false,
            Profiles = [legacy, local],
            DefaultProfileId = legacy.Id,
        };
        // Locked credential storage leaves legacy profiles in settings during startup migration.
        typeof(TerminalSettings).GetProperty(nameof(TerminalSettings.CredentialMigrationPending))!.SetValue(settings, true);
        var factory = new RecordingSessionFactory(new FakeTerminalSession());
        var window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            Settings = settings,
            SessionFactory = factory,
            CommandAssist = TestCommandAssistServices.Instance,
        });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(legacy, settings.Profiles);
        Assert.Equal(legacy.Id, settings.DefaultProfileId);
        return (window, settings, factory);
    }
}
