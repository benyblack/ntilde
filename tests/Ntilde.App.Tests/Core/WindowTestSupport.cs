using System;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Ntilde.AgentHost;
using Ntilde.Controls;
using Ntilde.Pty;
using Ntilde.Shell;

namespace Ntilde.Tests.Core;

/// <summary>
/// Helpers shared by the tests that drive a real, shown <see cref="MainWindow"/>. Callers take
/// <c>IClassFixture&lt;TestAppDataRoot&gt;</c> and dispose through
/// <see cref="TestMainWindowFactory.DisposeCreatedWindows"/>.
/// </summary>
internal static class WindowTestSupport
{
    private const BindingFlags NonPublicInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>Creates, shows and lays out a window with default services.</summary>
    public static MainWindow NewShownWindow()
    {
        MainWindow window = TestMainWindowFactory.Create();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>
    /// Builds a tab holding a real two-pane split — the shape
    /// <c>SessionManager.CreateRestoredTabItem</c> produces from a
    /// <c>NodeType.Split</c> root — and runs it through the production
    /// <c>InitializeRestoredTabs</c> so the panes are wired to the window and
    /// their registrations are associated with the tab.
    /// </summary>
    public static TabItem AddSplitTab(MainWindow window, out TerminalPane first, out TerminalPane second)
    {
        var settings = (TerminalSettings)typeof(MainWindow)
            .GetField("_settings", NonPublicInstance)!
            .GetValue(window)!;

        var session = new TabSession
        {
            Title = "Split",
            Root = new PaneNode
            {
                Type = NodeType.Split,
                SplitOrientation = 0,
                Children =
                {
                    new PaneNode { Type = NodeType.Leaf, Command = "cmd.exe", Arguments = string.Empty, PaneId = Guid.NewGuid().ToString() },
                    new PaneNode { Type = NodeType.Leaf, Command = "cmd.exe", Arguments = string.Empty, PaneId = Guid.NewGuid().ToString() }
                },
                Sizes = { "1*", "1*" }
            }
        };

        var tab = SessionManager.CreateRestoredTabItem(session, settings)!;
        var tabs = window.FindControl<TabControl>("Tabs")!;
        tabs.Items.Add(tab);

        typeof(MainWindow)
            .GetMethod("InitializeRestoredTabs", NonPublicInstance)!
            .Invoke(window, new object[] { tabs });

        // RestorePaneTree builds [child0, GridSplitter, child1]; OfType filters
        // the splitter out and preserves order.
        var panes = ((Grid)tab.Content!).Children.OfType<TerminalPane>().ToList();
        Assert.Equal(2, panes.Count);
        first = panes[0];
        second = panes[1];
        return tab;
    }

    public static AgentSessionRegistration RegistrationFor(TerminalPane pane)
        => Assert.Single(AgentSessionRegistry.Instance.GetRegistrations().Where(r => r.PaneId == pane.PaneId));

    /// <summary>Finds a named part of the TabControl's template (sidebar, spacer, ...).</summary>
    public static T TemplatePart<T>(MainWindow window, string name) where T : Control
    {
        var tabs = window.FindControl<TabControl>("Tabs")!;
        return Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(tabs)
            .OfType<T>()
            .First(c => c.Name == name);
    }

    /// <summary>
    /// Calls a private instance method by name. Callers cast a returned <see cref="System.Threading.Tasks.Task"/>
    /// and await it.
    /// </summary>
    public static object? Invoke(MainWindow window, string privateMethod, params object[] args)
        => typeof(MainWindow)
            .GetMethod(privateMethod, NonPublicInstance | BindingFlags.Public)!
            .Invoke(window, args);
}
