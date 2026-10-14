using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;

namespace Ntilde.Settings;

/// <summary>
/// Row ids for deep links and search, and the short highlight a jump ends with.
/// </summary>
/// <remarks>
/// An attached property rather than a property of one row type, so any control can be a jump
/// target: the PR 2 move keeps today's markup, and SettingRow (PR 3) exposes this same property as
/// its RowId.
/// </remarks>
public static class SettingsNavigation
{
    public static readonly AttachedProperty<string?> RowIdProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("RowId", typeof(SettingsNavigation));

    /// <summary>How long a jumped-to row stays highlighted, fade included.</summary>
    public static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(400);

    public static string? GetRowId(Control control) => control.GetValue(RowIdProperty);

    public static void SetRowId(Control control, string? value) => control.SetValue(RowIdProperty, value);

    /// <summary>
    /// Highlights <paramref name="target"/> with the palette's NtBlueDim, then fades back. The
    /// highlight is held at animation priority, so it neither replaces nor loses the control's own
    /// local or styled background. No-op for a control without a background.
    /// </summary>
    public static void Flash(Control target)
    {
        ArgumentNullException.ThrowIfNull(target);
        StyledProperty<IBrush?>? background = BackgroundPropertyOf(target);
        if (background is null || target.FindResource("NtBlueDim") is not IBrush highlight)
        {
            return;
        }

        Transitions? original = target.Transitions;
        IDisposable? held = target.SetValue(background, highlight, BindingPriority.Animation);
        DispatcherTimer.RunOnce(
            () =>
            {
                target.Transitions = new Transitions { new BrushTransition { Property = background, Duration = FadeDuration } };
                held?.Dispose();
                DispatcherTimer.RunOnce(() => target.Transitions = original, FadeDuration);
            },
            FlashDuration - FadeDuration);
    }

    internal static StyledProperty<IBrush?>? BackgroundPropertyOf(Control control) => control switch
    {
        TemplatedControl => TemplatedControl.BackgroundProperty,
        Panel => Panel.BackgroundProperty,
        Border => Border.BackgroundProperty,
        TextBlock => TextBlock.BackgroundProperty,
        _ => null,
    };
}
