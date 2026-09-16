using System;
using Avalonia;
using Avalonia.Controls;
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
/// Mechanism: the Window control theme in <c>UI/UiScaleWindowTheme.axaml</c> (merged by
/// <c>App.axaml</c>, installed into the askpass helper's Application by
/// <see cref="InstallWindowTheme"/>) wraps every window's content presenter in a
/// <c>LayoutTransformControl</c> whose transform is the application resource
/// <see cref="TransformResourceKey"/>. <see cref="Apply"/> replaces that resource, and the
/// <c>DynamicResource</c> binding re-lays out every open window. One place, no per-window wiring,
/// and code-built dialogs get it for free. Fixed-size windows also call <see cref="FitWindow"/>
/// so their logical room survives the transform.
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

    /// <summary>
    /// The shared resource dictionary carrying the scale transform and the Window control theme
    /// that applies it (<c>UI/UiScaleWindowTheme.axaml</c>). <c>App.axaml</c> merges it in XAML;
    /// a secondary <see cref="Application"/> such as the standalone askpass helper calls this in
    /// its <c>Initialize</c>, or its windows never scale.
    /// </summary>
    public static void InstallWindowTheme(Application app)
    {
        ArgumentNullException.ThrowIfNull(app);
        // Compiled XAML via the dictionary's x:Class, not ResourceInclude(Uri): the latter is the
        // runtime loader and trips IL2026 under the NativeAOT release build.
        app.Resources.MergedDictionaries.Add(new Ntilde.UI.UiScaleWindowTheme());
    }

    /// <summary>
    /// Grows a fixed-size window's requested size and minimums by <see cref="Current"/> so the
    /// logical room its XAML was designed for survives the layout transform: an 880-DIP-wide
    /// window under 200% would otherwise offer 440 DIPs and clip its own content (the Interface
    /// scale slider included). Clamped to the primary screen's working area when that is known.
    /// Call it once from the window's constructor, after InitializeComponent; a NaN dimension
    /// (SizeToContent) is left alone. A no-op at 100%.
    /// </summary>
    public static void FitWindow(Window window) => FitWindow(window, TryGetWorkingAreaDips(window));

    internal static void FitWindow(Window window, Size? workingArea)
    {
        ArgumentNullException.ThrowIfNull(window);
        double scale = Current;
        if (Math.Abs(scale - Default) < 0.0001)
        {
            return;
        }

        window.Width = Scaled(window.Width, scale);
        window.Height = Scaled(window.Height, scale);
        window.MinWidth = Scaled(window.MinWidth, scale);
        window.MinHeight = Scaled(window.MinHeight, scale);

        if (workingArea is { } area)
        {
            if (!double.IsNaN(window.Width)) window.Width = Math.Min(window.Width, area.Width);
            if (!double.IsNaN(window.Height)) window.Height = Math.Min(window.Height, area.Height);
            // Minimums shrink with the cap, or the platform refuses the smaller size.
            window.MinWidth = Math.Min(window.MinWidth, area.Width);
            window.MinHeight = Math.Min(window.MinHeight, area.Height);
        }
    }

    private static double Scaled(double value, double scale)
        => double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? value : value * scale;

    private static Size? TryGetWorkingAreaDips(Window window)
    {
        try
        {
            var screen = window.Screens?.Primary;
            if (screen == null || screen.Scaling <= 0)
            {
                return null;
            }

            var area = screen.WorkingArea;
            return new Size(area.Width / screen.Scaling, area.Height / screen.Scaling);
        }
        catch (Exception)
        {
            // No platform screens (headless, or a window not yet attached to one): skip the clamp.
            return null;
        }
    }
}
