using Ntilde.Settings;
using Ntilde.Shell;
using Ntilde.Shell.TitleBar;

namespace Ntilde.Tests.Core;

public sealed class SettingsSessionTests
{
    private static readonly Func<IEnumerable<string>> NoSystemFonts = () => Array.Empty<string>();

    [Fact]
    public void Constructing_SeedsBothDraftsFromTheSettings_WithoutSharingThem()
    {
        var settings = new TerminalSettings
        {
            Keybindings = new() { ["settings"] = "Ctrl+Alt+Shift+F11" },
            TitleBarItems = new() { ["find"] = "Pinned" },
        };

        var session = new SettingsSession(settings, NoSystemFonts);

        Assert.Equal("Ctrl+Alt+Shift+F11", session.ShortcutDraft["SETTINGS"]); // case-insensitive
        Assert.NotSame(settings.Keybindings, session.ShortcutDraft);
        Assert.Equal(TitleBarItemState.Pinned, session.TitleBarDraft.GetState("find"));
    }

    [Fact]
    public void CommitDrafts_WritesKeybindingsAndTitleBarDeltas()
    {
        var session = new SettingsSession(new TerminalSettings(), NoSystemFonts);
        session.ShortcutDraft["settings"] = "Ctrl+Alt+Shift+F11";
        Assert.True(session.TitleBarDraft.TrySetState("find", TitleBarItemState.Pinned));
        var target = new TerminalSettings();

        session.CommitDrafts(target);

        Assert.Equal("Ctrl+Alt+Shift+F11", target.Keybindings["settings"]);
        Assert.Equal("Pinned", target.TitleBarItems["find"]);
        Assert.Contains("find", target.TitleBarOrder);
    }

    [Fact]
    public void Reload_SwapsTheSettings_AndDiscardsPendingDraftEdits()
    {
        var session = new SettingsSession(new TerminalSettings(), NoSystemFonts);
        session.ShortcutDraft["settings"] = "Ctrl+Alt+Shift+F11";
        var fresh = new TerminalSettings { FontSize = 21 };

        session.Reload(fresh);

        Assert.Same(fresh, session.Settings);
        Assert.False(session.ShortcutDraft.ContainsKey("settings"));
    }

    [Fact]
    public void KeepShellsRunning_StartsFromTheSettings_AndAReloadTakesItFromTheNewOnes()
    {
        var session = new SettingsSession(new TerminalSettings { SessionPersistence = "KeepOnClose" }, NoSystemFonts);
        Assert.True(session.KeepShellsRunning);

        session.Reload(new TerminalSettings { SessionPersistence = "Off" });

        Assert.False(session.KeepShellsRunning);
    }

    [Fact]
    public void SetKeepShellsRunning_RaisesTheEvent_OnlyWhenTheValueChanges()
    {
        var session = new SettingsSession(new TerminalSettings { SessionPersistence = "Off" }, NoSystemFonts);
        int changes = 0;
        session.KeepShellsRunningChanged += () => changes++;

        session.SetKeepShellsRunning(false);
        Assert.Equal(0, changes);

        session.SetKeepShellsRunning(true);
        session.SetKeepShellsRunning(true);
        Assert.Equal(1, changes);
        Assert.True(session.KeepShellsRunning);
    }

    [Fact]
    public void FontChoices_IncludeTheGlobalFontAndEveryLocalProfileOverride_EvenWhenNotInstalled()
    {
        var settings = new TerminalSettings { FontFamily = "Global Mono" };
        settings.Profiles.Add(new TerminalProfile { Name = "a", FontFamily = "Profile One" });
        settings.Profiles.Add(new TerminalProfile { Name = "b", FontFamily = "Profile Two" });
        settings.Profiles.Add(new TerminalProfile { Name = "ssh", Type = ConnectionType.SSH, FontFamily = "Remote Only" });

        var session = new SettingsSession(settings, () => ["Installed Mono"]);

        Assert.Contains("Installed Mono", session.FontChoices);
        Assert.Contains("Global Mono", session.FontChoices);
        Assert.Contains("Profile One", session.FontChoices);
        Assert.Contains("Profile Two", session.FontChoices);
        Assert.DoesNotContain("Remote Only", session.FontChoices);
    }

    [Fact]
    public void RaiseMethods_ReachTheirSubscribers()
    {
        var session = new SettingsSession(new TerminalSettings(), NoSystemFonts);
        int shortcuts = 0, themes = 0, history = 0, snippets = 0;
        session.ShortcutsChanged += () => shortcuts++;
        session.ThemesChanged += () => themes++;
        session.HistoryCleared += () => history++;
        session.SnippetsChanged += () => snippets++;

        session.RaiseShortcutsChanged();
        session.RaiseThemesChanged();
        session.RaiseHistoryCleared();
        session.RaiseSnippetsChanged();

        Assert.Equal((1, 1, 1, 1), (shortcuts, themes, history, snippets));
    }

    [Fact]
    public async Task ReloadAfterExternalChangeAsync_RunsTheHandlerTheFrameRegistered()
    {
        var session = new SettingsSession(new TerminalSettings(), NoSystemFonts);
        int reloads = 0;
        session.SetExternalReloadHandler(() => { reloads++; return Task.CompletedTask; });

        await session.ReloadAfterExternalChangeAsync();

        Assert.Equal(1, reloads);
    }

    [Fact]
    public void PreviewSink_RaisesTheMatchingEvent()
    {
        var sink = new SettingsPreviewSink();
        string? theme = null;
        double? scale = null;
        sink.ThemeChanged += name => theme = name;
        sink.UiScaleChanged += value => scale = value;

        sink.Theme("Nord");
        sink.UiScale(1.5);

        Assert.Equal("Nord", theme);
        Assert.Equal(1.5, scale);
    }
}
