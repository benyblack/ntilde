using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;
using Ntilde.Shell.Theming;

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
    /// Marks a row's setting where an action button comes before it ("Clear history" before the "Remember commands"
    /// toggle): a jump to the row focuses this control, not the first one Tab reaches.
    /// </summary>
    public static readonly AttachedProperty<bool> IsRowFocusProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsRowFocus", typeof(SettingsNavigation));

    public static bool GetIsRowFocus(Control control) => control.GetValue(IsRowFocusProperty);

    public static void SetIsRowFocus(Control control, bool value) => control.SetValue(IsRowFocusProperty, value);

    /// <summary>
    /// Highlights <paramref name="target"/> with the palette's NtBlueDim, then fades back. The
    /// highlight is held at animation priority, so it neither replaces nor loses the control's own
    /// local or styled background. No-op for a control without a background.
    /// </summary>
    public static void Flash(Control target)
    {
        ArgumentNullException.ThrowIfNull(target);
        StyledProperty<IBrush?>? background = BackgroundPropertyOf(target);
        if (background is null || target.FindResource(ThemeKeys.AccentDim) is not IBrush highlight)
        {
            return;
        }

        // A second jump inside the first one's window ends the first before starting, so the new
        // flash captures the control's real Transitions and never the first flash's temporary fade.
        if (InFlight.TryGetValue(target, out FlashState? previous))
        {
            previous.End(target);
        }

        var state = new FlashState(target.Transitions);
        InFlight.AddOrUpdate(target, state);
        state.Held = target.SetValue(background, highlight, BindingPriority.Animation);
        state.Timer = DispatcherTimer.RunOnce(
            () =>
            {
                target.Transitions = new Transitions { new BrushTransition { Property = background, Duration = FadeDuration } };
                state.Held?.Dispose();
                state.Held = null;
                state.Timer = DispatcherTimer.RunOnce(() => state.End(target), FadeDuration);
            },
            FlashDuration - FadeDuration);
    }

    private static readonly ConditionalWeakTable<Control, FlashState> InFlight = new();

    private sealed class FlashState(Transitions? original)
    {
        public IDisposable? Held { get; set; }

        public IDisposable? Timer { get; set; }

        private bool _ended;

        public void End(Control target)
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            Timer?.Dispose();
            Held?.Dispose();
            target.Transitions = original;
            if (InFlight.TryGetValue(target, out FlashState? current) && ReferenceEquals(current, this))
            {
                InFlight.Remove(target);
            }
        }
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
