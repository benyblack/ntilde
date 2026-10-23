using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Pty;
using Ntilde.Shell;
using static Ntilde.Tests.Core.WindowTestSupport;

namespace Ntilde.Tests.Core;

/// <summary>
/// Focus mode, task 7: the startup session file remembers focus mode; workspaces never do.
/// </summary>
public sealed class FocusModeSessionTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    private static string Shell => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    private static TabSession NewTab(string title) => new()
    {
        TabId = Guid.NewGuid().ToString(),
        Title = title,
        Root = new PaneNode
        {
            Type = NodeType.Leaf,
            PaneId = Guid.NewGuid().ToString(),
            Command = Shell,
            Arguments = string.Empty,
        },
    };

    private static string SessionFile(string root) => Path.Combine(root, "sessions", "last_session.json");

    private static void WriteSession(string root, bool? focusMode)
    {
        var session = new NtildeSession { ActiveTabIndex = 0, Tabs = { NewTab("One"), NewTab("Two") } };
        session.FocusMode = focusMode ?? false;
        string json = System.Text.Json.JsonSerializer.Serialize(session, SessionSerializationContext.Default.NtildeSession);
        if (focusMode is null)
        {
            // An older file: the property is simply absent.
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            Assert.True(node.Remove("FocusMode"));
            json = node.ToJsonString();
        }

        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        File.WriteAllText(SessionFile(root), json);
    }

    [AvaloniaFact]
    public void Save_with_focus_mode_round_trips()
    {
        using var appData = new TestAppDataRoot();
        try
        {
            MainWindow window = NewShownWindow();
            var tabs = window.FindControl<TabControl>("Tabs")!;

            SessionManager.SaveSession(window, tabs, null, focusMode: true);

            Assert.True(SessionManager.TryLoadSavedSession(out NtildeSession? s, out _));
            Assert.True(s!.FocusMode);

            SessionManager.SaveSession(window, tabs, null, focusMode: false);
            Assert.True(SessionManager.TryLoadSavedSession(out s, out _));
            Assert.False(s!.FocusMode);
        }
        finally
        {
            TryDelete(SessionFile(appData.RootPath));
        }
    }

    [AvaloniaFact]
    public void Workspace_captures_never_carry_focus_mode()
    {
        MainWindow window = NewShownWindow();
        window.EnterFocusMode();
        Assert.True(window.IsFocusMode);

        var tabs = window.FindControl<TabControl>("Tabs")!;
        Assert.False(SessionManager.CaptureSession(window, tabs).FocusMode);
    }

    [AvaloniaFact]
    public void An_old_session_file_restores_in_normal_mode()
    {
        using var appData = new TestAppDataRoot();
        try
        {
            WriteSession(appData.RootPath, focusMode: null);

            MainWindow window = NewShownWindow();

            Assert.False(window.IsFocusMode);
        }
        finally
        {
            TryDelete(SessionFile(appData.RootPath));
        }
    }

    [AvaloniaFact]
    public void A_focus_mode_session_restores_into_focus_mode()
    {
        using var appData = new TestAppDataRoot();
        try
        {
            WriteSession(appData.RootPath, focusMode: true);

            MainWindow window = NewShownWindow();

            Assert.True(window.IsFocusMode);
            Assert.True(window.FindControl<Grid>("FocusStrip")!.IsVisible);
            Assert.False(TemplatePart<Grid>(window, "PART_TabSidebar").IsVisible);
        }
        finally
        {
            TryDelete(SessionFile(appData.RootPath));
        }
    }

    [AvaloniaFact]
    public void Deferred_tabs_hydrated_after_a_focus_mode_restore_hide_their_pane_chrome()
    {
        using var appData = new TestAppDataRoot();
        try
        {
            WriteSession(appData.RootPath, focusMode: true);

            MainWindow window = NewShownWindow();
            for (int i = 0; i < 5; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            var panes = window.AllPanesForTest();
            Assert.NotEmpty(panes);
            Assert.All(panes, p => Assert.True(p.FocusChromeHidden));
        }
        finally
        {
            TryDelete(SessionFile(appData.RootPath));
        }
    }

    [AvaloniaFact]
    public void Loading_a_workspace_in_focus_mode_leaves_it_first()
    {
        MainWindow window = NewShownWindow();
        window.EnterFocusMode();
        Assert.True(window.IsFocusMode);

        var snapshot = new NtildeSession { Tabs = { NewTab("ws") }, FocusMode = true };
        Invoke(window, "ApplySessionSnapshot", snapshot);

        Assert.False(window.IsFocusMode);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
