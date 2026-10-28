using System;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Platform.Ssh.Launch;
using Ntilde.Pty;
using Ntilde.Shell;
using static Ntilde.Tests.Core.WindowTestSupport;

namespace Ntilde.Tests.Core;

/// <summary>
/// #587: opening a profile with a tab-template rule adds the template's tab and leaves the rest of the window
/// alone. It used to capture the window, append the tab and re-apply the whole session, which disposed and
/// rebuilt every tab - every running shell was restarted - and handed an agent the user's pane as its spawn.
/// </summary>
public sealed class TemplateRuleTabTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    private static string Shell => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    private static TabControl TabsOf(MainWindow window) => window.FindControl<TabControl>("Tabs")!;

    private static TerminalSettings SettingsOf(MainWindow window) =>
        (TerminalSettings)typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static PaneNode Leaf(string paneId) =>
        new() { Type = NodeType.Leaf, Command = Shell, Arguments = string.Empty, PaneId = paneId };

    /// <summary>A template whose first tab is a two-pane split with its second pane active.</summary>
    private static (string Name, TabSession Tab) SaveSplitTemplate()
    {
        var tab = new TabSession
        {
            TabId = Guid.NewGuid().ToString(),
            Title = "Template",
            Root = new PaneNode
            {
                Type = NodeType.Split,
                Children = { Leaf(Guid.NewGuid().ToString()), Leaf(Guid.NewGuid().ToString()) },
                Sizes = { "1*", "1*" },
            },
        };
        tab.ActivePaneId = tab.Root.Children[1].PaneId;

        string name = $"rule_tpl_{Guid.NewGuid():N}";
        Assert.True(WorkspaceManager.SaveWorkspaceTemplate(name, new NtildeSession { Tabs = { tab } }));
        return (name, tab);
    }

    private static TerminalProfile ProfileWithRule(MainWindow window, string templateName)
    {
        TerminalSettings settings = SettingsOf(window);
        TerminalProfile profile = settings.Profiles[0];
        settings.TabTemplateRules.Add(new TabTemplateRule { ProfileId = profile.Id, TemplateName = templateName });
        return profile;
    }

    private static TerminalPane? OpenTab(MainWindow window, TerminalProfile profile, bool select = true)
    {
        var pane = (TerminalPane?)Invoke(window, "AddTab", profile, SshDiagnosticsLevel.None, select, true);
        Dispatcher.UIThread.RunJobs();
        return pane;
    }

    [AvaloniaFact]
    public void A_template_rule_adds_its_tab_and_leaves_the_other_tabs_running()
    {
        MainWindow window = NewShownWindow();
        var tabs = TabsOf(window);
        var firstTab = (TabItem)tabs.Items[0]!;
        var firstPane = (TerminalPane)firstTab.Content!;
        TabItem split = AddSplitTab(window, out TerminalPane left, out TerminalPane right);
        var (templateName, template) = SaveSplitTemplate();

        TerminalPane? opened = OpenTab(window, ProfileWithRule(window, templateName));

        // The tabs that were there are the same tabs, holding the same panes: nothing was rebuilt.
        Assert.Equal(3, tabs.Items.Count);
        Assert.Same(firstTab, tabs.Items[0]);
        Assert.Same(split, tabs.Items[1]);
        Assert.Same(firstPane, firstTab.Content);
        Assert.Equal(new[] { left, right }, ((Grid)split.Content!).Children.OfType<TerminalPane>());

        // The template's tab is new, selected, and it is what AddTab hands back - its active pane.
        var added = (TabItem)tabs.Items[2]!;
        Assert.Same(added, tabs.SelectedItem);
        var addedPanes = ((Grid)added.Content!).Children.OfType<TerminalPane>().ToList();
        Assert.Equal(2, addedPanes.Count);
        Assert.NotNull(opened);
        Assert.Same(addedPanes[1], opened);
        Assert.Same(added, Invoke(window, "ResolveOwningTabForPane", opened!));

        // A template is a layout: its tab gets panes of its own, not the template's ids.
        Assert.DoesNotContain(addedPanes, p => template.Root!.Children.Any(c => c.PaneId == p.PaneId.ToString()));
    }

    [AvaloniaFact]
    public void A_zoomed_tab_stays_zoomed_when_a_template_rule_opens_a_tab()
    {
        MainWindow window = NewShownWindow();
        TabItem split = AddSplitTab(window, out TerminalPane zoomed, out TerminalPane hidden);
        TabsOf(window).SelectedItem = split;
        Dispatcher.UIThread.RunJobs();
        Assert.True((bool)Invoke(window, "EnterPaneZoom", split, zoomed, true)!);
        var (templateName, _) = SaveSplitTemplate();

        OpenTab(window, ProfileWithRule(window, templateName));

        // The rebuild used to dispose the zoomed tab's content - the zoomed pane alone - and drop the map that held
        // the rest of its tree, abandoning the hidden pane's shell.
        Assert.Equal(zoomed.PaneId, (Guid?)Invoke(window, "GetZoomedPaneIdForTab", split));
        Assert.Same(zoomed, split.Content);
        Assert.Same(split, Invoke(window, "ResolveOwningTabForPane", hidden));
    }

    [AvaloniaFact]
    public void A_background_open_ignores_the_rule_and_opens_the_profile()
    {
        MainWindow window = NewShownWindow();
        var tabs = TabsOf(window);
        var userTab = (TabItem)tabs.Items[0]!;
        var (templateName, _) = SaveSplitTemplate();

        TerminalPane? opened = OpenTab(window, ProfileWithRule(window, templateName), select: false);

        Assert.NotNull(opened);
        Assert.Equal(2, tabs.Items.Count);
        Assert.Same(opened, ((TabItem)tabs.Items[1]!).Content);
        Assert.Same(userTab, tabs.SelectedItem);
    }

    [Fact]
    public void An_instantiated_template_tab_gets_fresh_ids_and_keeps_its_layout()
    {
        string first = Guid.NewGuid().ToString();
        string second = Guid.NewGuid().ToString();
        string sshProfile = Guid.NewGuid().ToString();
        var template = new TabSession
        {
            TabId = Guid.NewGuid().ToString(),
            Title = "Template",
            UserTitle = "Work",
            ActivePaneId = second,
            ZoomedPaneId = first,
            Root = new PaneNode
            {
                Type = NodeType.Split,
                SplitOrientation = 1,
                Sizes = { "2*", "1*" },
                Children =
                {
                    Leaf(first),
                    new PaneNode { Type = NodeType.Leaf, PaneId = second, SshProfileId = sshProfile, MuxSessionId = "live-session" },
                },
            },
        };

        TabSession a = MainWindow.InstantiateTemplateTab(template);
        TabSession b = MainWindow.InstantiateTemplateTab(template);

        Assert.Null(a.TabId);
        Assert.Equal("Work", a.UserTitle);
        Assert.Equal(1, a.Root!.SplitOrientation);
        Assert.Equal(["2*", "1*"], a.Root.Sizes);

        // Fresh pane ids, different each time, and the active and zoomed ids follow their panes.
        string[] ids = { a.Root.Children[0].PaneId!, a.Root.Children[1].PaneId!, b.Root!.Children[0].PaneId!, b.Root.Children[1].PaneId! };
        Assert.Equal(4, ids.Distinct().Count());
        Assert.DoesNotContain(first, ids);
        Assert.DoesNotContain(second, ids);
        Assert.Equal(a.Root.Children[1].PaneId, a.ActivePaneId);
        Assert.Equal(a.Root.Children[0].PaneId, a.ZoomedPaneId);

        // An SSH pane stays an SSH pane; a live multiplexer session is not carried into a new tab.
        Assert.Equal(sshProfile, a.Root.Children[1].SshProfileId);
        Assert.Null(a.Root.Children[1].MuxSessionId);
    }
}
