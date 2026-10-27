using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace Ntilde.Tests.Core;

/// <summary>
/// Focus mode swaps the window's decorations theme, and Avalonia 12.1.3 rebuilds the drawn caption buttons on a theme
/// change (the template goes null and back). TopLevelHost hands the new content to a plain layer control beside the
/// window, which nothing re-measures, so on Windows the buttons were invisible until a resize. Headless windows draw no
/// decorations, so these tests rebuild that shape: a host whose children are the "window" and a layer whose one child
/// is swapped.
/// </summary>
public sealed class FocusModeDecorationsLayoutTests
{
    // TopLevelHost's shape: every child laid out over the whole host.
    private sealed class Host : Control
    {
        public void Add(Control child) => VisualChildren.Add(child);
    }

    // TopLevelHost.LayerWrapper's shape: a plain Control whose Inner setter swaps its one visual child.
    private sealed class Layer : Control
    {
        public Control? Inner
        {
            get => VisualChildren.Count > 0 ? (Control)VisualChildren[0] : null;
            set
            {
                VisualChildren.Clear();
                if (value != null) VisualChildren.Add(value);
            }
        }
    }

    private static (Window Window, Border Self, Layer Layer) ShowHost()
    {
        var self = new Border();
        var layer = new Layer { Inner = new Button { Width = 45, Height = 30 } };
        var host = new Host();
        host.Add(self);
        host.Add(layer);
        var window = new Window { Width = 400, Height = 300, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, self, layer);
    }

    [AvaloniaFact]
    public void A_child_swapped_into_a_layer_stays_unlaid_out_until_its_layer_is_invalidated()
    {
        var (window, _, layer) = ShowHost();
        var replacement = new Button { Width = 45, Height = 24 };

        layer.Inner = replacement;
        window.UpdateLayout();

        // The Avalonia behaviour the workaround is for. If this starts failing, Avalonia lays out swapped layer content
        // itself and InvalidateLayersBeside can go.
        Assert.False(replacement.IsMeasureValid);
        Assert.Equal(0, replacement.Bounds.Height);
        window.Close();
    }

    [AvaloniaFact]
    public void Invalidating_the_layers_beside_a_control_lays_out_the_swapped_child()
    {
        var (window, self, layer) = ShowHost();
        var replacement = new Button { Width = 45, Height = 24 };
        layer.Inner = replacement;

        MainWindow.InvalidateLayersBeside(self);
        window.UpdateLayout();

        Assert.True(replacement.IsArrangeValid);
        Assert.Equal(45, replacement.Bounds.Width);
        Assert.Equal(24, replacement.Bounds.Height);
        window.Close();
    }

    [AvaloniaFact]
    public void Invalidating_the_layers_beside_a_control_leaves_the_control_itself_alone()
    {
        var (window, self, _) = ShowHost();

        MainWindow.InvalidateLayersBeside(self);

        Assert.True(self.IsMeasureValid);
        window.Close();
    }
}
