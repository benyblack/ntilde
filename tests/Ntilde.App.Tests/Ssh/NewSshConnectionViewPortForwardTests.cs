using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.Platform.Ssh.Models;
using Ntilde.ViewModels.Ssh;
using Ntilde.Views.Ssh;

namespace Ntilde.Tests.Ssh;

/// <summary>
/// The SSH connection editor's Port Forwards tab: the add form (kind-dependent rows, default
/// ports, validation) and the list (per-row Remove, empty state).
/// </summary>
public sealed class NewSshConnectionViewPortForwardTests
{
    private const int PortForwardsTab = 3;

    private static (NewSshConnectionView View, NewSshConnectionViewModel Vm) Show()
    {
        var vm = new NewSshConnectionViewModel();
        var view = new NewSshConnectionView(vm) { Width = 760, Height = 680 };
        view.Show();
        view.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = PortForwardsTab;
        Dispatcher.UIThread.RunJobs();
        view.UpdateLayout();
        return (view, vm);
    }

    private static T Named<T>(NewSshConnectionView view, string name) where T : Control =>
        view.FindControl<T>(name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    private static void SelectKind(NewSshConnectionView view, PortForwardKind kind)
    {
        Named<ComboBox>(view, "ForwardKindCombo").SelectedItem = kind;
        Dispatcher.UIThread.RunJobs();
    }

    private static void ClickAdd(NewSshConnectionView view)
    {
        Named<Button>(view, "AddForwardButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static decimal? SourcePort(NewSshConnectionView view) => Named<NumericUpDown>(view, "ForwardSourcePortInput").Value;

    [AvaloniaFact]
    public void EmptyDestinationHost_AddsAForwardToLocalhost()
    {
        var (view, vm) = Show();
        try
        {
            Named<NumericUpDown>(view, "ForwardDestPortInput").Value = 5432;
            ClickAdd(view);

            PortForward forward = Assert.Single(vm.Forwards);
            Assert.Equal(PortForwardKind.Local, forward.Kind);
            Assert.Equal(8080, forward.SourcePort);
            Assert.Equal("localhost", forward.DestinationHost);
            Assert.Equal(5432, forward.DestinationPort);
            Assert.True(string.IsNullOrEmpty(vm.ValidationError));
        }
        finally
        {
            view.Close();
        }
    }

    [AvaloniaFact]
    public void Dynamic_HidesTheDestinationRow_AndDropsAnyDestinationTyped()
    {
        var (view, vm) = Show();
        try
        {
            Named<TextBox>(view, "ForwardDestHostInput").Text = "db.internal";
            SelectKind(view, PortForwardKind.Dynamic);

            Assert.False(Named<TextBox>(view, "ForwardDestHostInput").IsVisible);
            Assert.False(Named<NumericUpDown>(view, "ForwardDestPortInput").IsVisible);
            Assert.Contains("SOCKS", Named<TextBlock>(view, "ForwardKindHint").Text, StringComparison.Ordinal);

            ClickAdd(view);

            PortForward forward = Assert.Single(vm.Forwards);
            Assert.Equal(PortForwardKind.Dynamic, forward.Kind);
            Assert.Equal(1080, forward.SourcePort);
            Assert.Equal(string.Empty, forward.DestinationHost);
            Assert.Equal(0, forward.DestinationPort);
        }
        finally
        {
            view.Close();
        }
    }

    [AvaloniaFact]
    public void Remote_RelabelsTheListenRow()
    {
        var (view, _) = Show();
        try
        {
            Assert.Equal("Listen on", Named<TextBlock>(view, "ForwardListenLabel").Text);
            SelectKind(view, PortForwardKind.Remote);
            Assert.Equal("Listen on server", Named<TextBlock>(view, "ForwardListenLabel").Text);
            Assert.True(Named<TextBox>(view, "ForwardDestHostInput").IsVisible);
        }
        finally
        {
            view.Close();
        }
    }

    [AvaloniaFact]
    public void SwitchingKind_SwapsTheDefaultListenPort_UntilTheUserSetsOne()
    {
        var (view, _) = Show();
        try
        {
            Assert.Equal(8080, SourcePort(view));
            SelectKind(view, PortForwardKind.Dynamic);
            Assert.Equal(1080, SourcePort(view));
            SelectKind(view, PortForwardKind.Local);
            Assert.Equal(8080, SourcePort(view));

            // A deliberate 8080 for a SOCKS proxy: neither kind switch may rewrite it.
            SelectKind(view, PortForwardKind.Dynamic);
            Named<NumericUpDown>(view, "ForwardSourcePortInput").Value = 8080;
            SelectKind(view, PortForwardKind.Local);
            Assert.Equal(8080, SourcePort(view));
            SelectKind(view, PortForwardKind.Dynamic);
            Assert.Equal(8080, SourcePort(view));
        }
        finally
        {
            view.Close();
        }
    }

    [AvaloniaFact]
    public void EmptyListenPort_ShowsAValidationError_InsteadOfDoingNothing()
    {
        var (view, vm) = Show();
        try
        {
            Named<NumericUpDown>(view, "ForwardSourcePortInput").Value = null;
            ClickAdd(view);

            Assert.Empty(vm.Forwards);
            Assert.Contains("listen port", vm.ValidationError, StringComparison.Ordinal);
        }
        finally
        {
            view.Close();
        }
    }

    [AvaloniaFact]
    public void RowRemoveButton_RemovesItsOwnForward_AndNamesIt()
    {
        var (view, vm) = Show();
        try
        {
            var first = new PortForward { Kind = PortForwardKind.Local, SourcePort = 8080, DestinationHost = "a", DestinationPort = 80 };
            var second = new PortForward { Kind = PortForwardKind.Dynamic, SourcePort = 1080 };
            vm.Forwards.Add(first);
            vm.Forwards.Add(second);
            Dispatcher.UIThread.RunJobs();
            view.UpdateLayout();

            Button[] removes = Named<ListBox>(view, "ForwardsList").GetVisualDescendants()
                .OfType<Button>()
                .Where(b => Equals(b.Content, "Remove"))
                .ToArray();
            Assert.Equal(2, removes.Length);

            Button secondRemove = removes.Single(b => ReferenceEquals(b.DataContext, second));
            Assert.Equal($"Remove forward {second}", AutomationProperties.GetName(secondRemove));

            secondRemove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Same(first, Assert.Single(vm.Forwards));
        }
        finally
        {
            view.Close();
        }
    }

    [AvaloniaFact]
    public void EmptyState_ShowsOnlyWhileThereAreNoForwards()
    {
        var (view, vm) = Show();
        try
        {
            TextBlock empty = view.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Text == "No port forwards yet. Add one above.");
            ListBox list = Named<ListBox>(view, "ForwardsList");
            Assert.True(empty.IsVisible);
            Assert.False(list.IsVisible);

            vm.Forwards.Add(new PortForward { Kind = PortForwardKind.Dynamic, SourcePort = 1080 });
            Dispatcher.UIThread.RunJobs();
            Assert.False(empty.IsVisible);
            Assert.True(list.IsVisible);

            vm.Forwards.Clear();
            Dispatcher.UIThread.RunJobs();
            Assert.True(empty.IsVisible);
            Assert.False(list.IsVisible);
        }
        finally
        {
            view.Close();
        }
    }
}
