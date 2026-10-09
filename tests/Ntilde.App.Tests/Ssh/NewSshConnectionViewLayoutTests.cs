using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.ViewModels.Ssh;
using Ntilde.Views.Ssh;

namespace Ntilde.Tests.Ssh;

/// <summary>
/// The New / Edit SSH Connection dialog has more form rows than fit its default height, and it is
/// the same window whether it is opened from the title bar or from the Connection Manager. Before
/// this test the Basic tab was a bare Grid, so the rows below the fold were simply clipped and the
/// only way to reach Notes or Favorite was to resize the window by hand. Every tab now scrolls.
/// </summary>
public sealed class NewSshConnectionViewLayoutTests
{
    private static NewSshConnectionView ShowAt(double height)
    {
        var view = new NewSshConnectionView(new NewSshConnectionViewModel())
        {
            Width = 760,
            Height = height,
        };
        view.Show();
        Dispatcher.UIThread.RunJobs();
        view.UpdateLayout();
        return view;
    }

    [AvaloniaFact]
    public void BasicTab_ScrollsInsteadOfClipping_WhenTheWindowIsShort()
    {
        var view = ShowAt(height: 360);
        try
        {
            var scroll = view.FindControl<ScrollViewer>("BasicTabScroll");
            Assert.NotNull(scroll);
            Assert.Equal(ScrollBarVisibility.Auto, scroll!.VerticalScrollBarVisibility);
            Assert.True(
                scroll.Extent.Height > scroll.Viewport.Height + 1,
                $"Basic tab should overflow a {view.Height}px window and scroll; extent {scroll.Extent.Height} vs viewport {scroll.Viewport.Height}.");
        }
        finally
        {
            view.Close();
        }
    }

    [AvaloniaFact]
    public void InstallButton_tooltip_names_the_install_dir_the_scripts_use()
    {
        var view = ShowAt(height: 620);
        try
        {
            var button = view.FindControl<Button>("InstallMuxButton");
            Assert.NotNull(button);
            string tip = Assert.IsType<string>(ToolTip.GetTip(button!));
            Assert.Contains(Ntilde.Shell.Mux.Remote.RemoteInstallDir.Display, tip, StringComparison.Ordinal);
        }
        finally
        {
            view.Close();
        }
    }

    [AvaloniaFact]
    public void EveryTab_HostsItsFormInAScrollViewer()
    {
        var view = ShowAt(height: 620);
        try
        {
            var tabs = view.GetVisualDescendantsOfType<TabControl>().Single();
            var items = tabs.Items.Cast<TabItem>().ToArray();
            Assert.Equal(6, items.Length);
            Assert.All(items, item => Assert.IsType<ScrollViewer>(item.Content));
        }
        finally
        {
            view.Close();
        }
    }

    /// <summary>
    /// Phase 5 spec R8: under "Keep remote sessions running", what a persistent tab does not have - this connection's port
    /// forwards, and with OpenSSH the Remote Files sidebar - in the same words as the install dialog's.
    /// </summary>
    [AvaloniaFact]
    public void The_keep_running_checkbox_says_what_a_persistent_tab_lacks()
    {
        var view = ShowAt(height: 620);
        try
        {
            var hint = view.FindControl<TextBlock>("PersistentTabLimitsHint");
            Assert.NotNull(hint);
            Assert.Equal(Ntilde.Shell.Mux.RemoteMuxStatusText.PersistentTabLimits, hint!.Text);
            Assert.Contains("port forwards", hint.Text, StringComparison.Ordinal);
            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, hint.TextWrapping);
            Assert.Equal(0.7, hint.Opacity, precision: 3);
            var panel = Assert.IsType<StackPanel>(hint.Parent);
            int checkbox = panel.Children.IndexOf(panel.Children.OfType<CheckBox>().Single(box => Equals(box.Content, "Keep remote sessions running (ntilde-mux)")));
            Assert.Equal(checkbox + 1, panel.Children.IndexOf(hint));
        }
        finally
        {
            view.Close();
        }
    }
}

internal static class VisualTreeTestExtensions
{
    public static System.Collections.Generic.IEnumerable<T> GetVisualDescendantsOfType<T>(this Avalonia.Visual root)
        where T : Avalonia.Visual
        => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>();
}
