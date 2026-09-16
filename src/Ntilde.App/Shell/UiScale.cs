using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace Ntilde.Shell;

/// <summary>
/// App-wide interface scale: the zoom for everything that is not terminal text - tabs, sidebars,
/// dialogs, the settings window, status bars. <see cref="TerminalSettings.FontSize"/> only ever
/// reached the terminal view, so on a dense display the chrome stayed small however large the
/// terminal font got. This is the same split Warp (terminal font size vs. Zoom) and VS Code
/// (editor font vs. window zoom) settled on.
/// </summary>
/// <remarks>
/// <para>
/// Mechanism: the Window control theme in <c>App.axaml</c> wraps every window's content presenter
/// in a <c>LayoutTransformControl</c> whose transform is the application resource
/// <see cref="TransformResourceKey"/>. <see cref="Apply"/> replaces that resource, and the
/// <c>DynamicResource</c> binding re-lays out every open window. One place, no per-window wiring,
/// and code-built dialogs get it for free.
/// </para>
/// <para>
/// A layout transform scales the terminal pane too - a terminal at font size 14 under 125% renders
/// like 17.5 - which is how display scaling behaves and keeps one mental model. The terminal
/// multiplies <see cref="Current"/> into its render scale (see <c>TerminalView</c>) so its glyph
/// atlas is rasterized at true device density rather than upscaled. Popups (context menus,
/// flyouts, tooltips) live in their own top-levels and are NOT reached by the transform; they
/// render at 100% for now. The Settings window pins the scale it opened with, so the slider that
/// drives this does not run away from the pointer.
/// </para>
/// </remarks>
public static class UiScale
{
    public const double Minimum = 0.5;
    public const double Maximum = 3.0;
    public const double Default = 1.0;

    /// <summary>Application resource key the Window theme binds its layout transform to.</summary>
    public const string TransformResourceKey = "NtUiScaleTransform";

    /// <summary>The scale currently applied to the application, always within range.</summary>
    public static double Current { get; private set; } = Default;

    /// <summary>
    /// Raised by <see cref="Apply"/> with the clamped scale. The Window theme does not need it (it
    /// binds the resource), but anything that rasterizes at device density does: the terminal
    /// bakes glyph sprites at its render scale, and under a layout transform those bitmaps would
    /// otherwise be upscaled into blur. Subscribers must unsubscribe when they leave the tree.
    /// </summary>
    public static event EventHandler<double>? Changed;

    /// <summary>
    /// NaN falls back to the default (a corrupt or hand-edited settings value must not blank the
    /// UI); everything else is clamped to [<see cref="Minimum"/>, <see cref="Maximum"/>].
    /// </summary>
    public static double Clamp(double scale)
    {
        if (double.IsNaN(scale))
        {
            return Default;
        }

        return Math.Clamp(scale, Minimum, Maximum);
    }

    /// <summary>
    /// Applies <paramref name="scale"/> (clamped) to every window, open or future. Safe to call
    /// before any window exists and safe without a running <see cref="Application"/> (unit tests).
    /// </summary>
    public static void Apply(double scale)
    {
        double clamped = Clamp(scale);
        Current = clamped;

        if (Application.Current is { } app)
        {
            // Resource replacement fans out to every window's layout transform, and the
            // subscribers below touch controls. Off the UI thread that does not fail here - it
            // poisons Avalonia's media context for every window that lays out afterwards (the
            // headless suite saw 29 unrelated tests die that way). Fail fast instead.
            Dispatcher.UIThread.VerifyAccess();

            // Replacing the resource (rather than mutating the existing transform) is what makes
            // the DynamicResource consumers re-evaluate.
            app.Resources[TransformResourceKey] = new ScaleTransform(clamped, clamped);
        }

        Changed?.Invoke(null, clamped);
    }
}
