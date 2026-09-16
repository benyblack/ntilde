using System;
using System.Runtime.CompilerServices;
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
            RefitOpenWindows();
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
    /// scale slider included). When the primary screen's working area cannot hold the scaled
    /// window, the scale is reduced to what fits (never below 100%) and the window is pinned at
    /// that reduced scale via <see cref="PinScale"/> - capping only the outer size would bring the
    /// clipping straight back. Returns the scale the window ended up with. Call it once from the
    /// window's constructor, after InitializeComponent; a NaN dimension (SizeToContent) is left
    /// alone. A no-op at 100%.
    /// </summary>
    public static double FitWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        double applied = FitWindow(window, TryGetWorkingAreaDips(window));

        // The constructor only knows the primary screen; a CenterOwner dialog may land on a smaller
        // (or larger) monitor. Once it opens the real screen is known: refit from the design size.
        var fitted = Registry.GetValue(window, _ => new FittedWindow(window));
        if (!fitted.OpenedHooked)
        {
            fitted.OpenedHooked = true;
            window.Opened += static (sender, _) =>
            {
                if (sender is Window opened)
                {
                    FitWindow(opened, TryGetWorkingAreaDips(opened));
                }
            };
        }

        return applied;
    }

    internal static double FitWindow(Window window, Size? workingArea)
    {
        ArgumentNullException.ThrowIfNull(window);
        var fitted = Registry.GetValue(window, _ => new FittedWindow(window));

        // A caller's hold is the ceiling; otherwise the app scale is.
        double ceiling = fitted.HeldScale ?? Current;
        double scale = ceiling;

        if (workingArea is { } area && scale > Default)
        {
            double fit = scale;
            if (IsFixed(fitted.DesignWidth)) fit = Math.Min(fit, area.Width / fitted.DesignWidth);
            if (IsFixed(fitted.DesignHeight)) fit = Math.Min(fit, area.Height / fitted.DesignHeight);
            scale = Math.Max(fit, Default);
        }

        bool reduced = scale < ceiling - 0.0001;
        if (reduced)
        {
            // Screen-forced: shadow the app resource so the content renders at what fits. Not a
            // hold - the next fit with room (the real screen on open, a lower app scale) releases it.
            SetWindowTransform(window, scale);
            fitted.ScreenReduced = true;
        }
        else if (fitted.HeldScale is { } held)
        {
            SetWindowTransform(window, held);
            fitted.ScreenReduced = false;
        }
        else if (fitted.ScreenReduced)
        {
            window.Resources.Remove(TransformResourceKey);
            fitted.ScreenReduced = false;
        }

        // Always from the design size, never from the current one: fitting is idempotent, so a
        // refit on open or on a scale change does not compound the previous fit.
        window.Width = Scaled(fitted.DesignWidth, scale);
        window.Height = Scaled(fitted.DesignHeight, scale);
        window.MinWidth = Scaled(fitted.DesignMinWidth, scale);
        window.MinHeight = Scaled(fitted.DesignMinHeight, scale);
        fitted.AppliedScale = scale;
        return scale;
    }

    /// <summary>
    /// Holds <paramref name="window"/> at <paramref name="scale"/> regardless of later
    /// <see cref="Apply"/> calls: a window-level resource of the same key shadows the application
    /// one the Window theme binds to, the refit in <see cref="Apply"/> skips held windows, and the
    /// held scale becomes the ceiling for <see cref="FitWindow"/> (the screen may still reduce
    /// below it). Used by the Settings window so the slider does not run away from the pointer.
    /// </summary>
    public static void PinScale(Window window, double scale)
    {
        ArgumentNullException.ThrowIfNull(window);
        double clamped = Clamp(scale);
        var fitted = Registry.GetValue(window, _ => new FittedWindow(window));
        fitted.HeldScale = clamped;
        if (!fitted.ScreenReduced)
        {
            SetWindowTransform(window, clamped);
            fitted.AppliedScale = clamped;
        }
    }

    private static void SetWindowTransform(Window window, double scale)
        => window.Resources[TransformResourceKey] = new ScaleTransform(scale, scale);

    /// <summary>
    /// Every window that went through <see cref="FitWindow"/>, with the size its XAML asked for
    /// (captured on first fit) so later fits start from the design, not from the last result.
    /// Weakly keyed: a closed window is collected with its entry. The owner application is kept so
    /// a refit never touches a window from a previous headless test's isolated application.
    /// </summary>
    private sealed class FittedWindow
    {
        public FittedWindow(Window window)
        {
            DesignWidth = window.Width;
            DesignHeight = window.Height;
            DesignMinWidth = window.MinWidth;
            DesignMinHeight = window.MinHeight;
            OwnerApplication = Application.Current;
        }

        public double DesignWidth { get; }
        public double DesignHeight { get; }
        public double DesignMinWidth { get; }
        public double DesignMinHeight { get; }
        public Application? OwnerApplication { get; }
        public double AppliedScale { get; set; } = Default;
        /// <summary>Set by <see cref="PinScale"/>: the caller's hold, and the ceiling for fits.</summary>
        public double? HeldScale { get; set; }
        /// <summary>Set by <see cref="FitWindow"/> when the screen forced a scale below the ceiling.</summary>
        public bool ScreenReduced { get; set; }
        public bool OpenedHooked { get; set; }
    }

    private static readonly ConditionalWeakTable<Window, FittedWindow> Registry = new();

    /// <summary>
    /// Re-fits every open, unpinned fitted window after <see cref="Apply"/>: their content is
    /// rescaled by the theme, and without this their frames kept the old size, so an open Connection
    /// Manager taken from 100% to 200% offered half the room it was designed for.
    /// </summary>
    private static void RefitOpenWindows()
    {
        foreach ((Window window, FittedWindow fitted) in Registry)
        {
            if (fitted.HeldScale != null || !ReferenceEquals(fitted.OwnerApplication, Application.Current) || !window.IsVisible)
            {
                continue;
            }

            FitWindow(window, TryGetWorkingAreaDips(window));
        }
    }

    private static bool IsFixed(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;

    private static double Scaled(double value, double scale)
        => double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? value : value * scale;

    /// <summary>
    /// The working area of the screen the window is on, in DIPs. Before the window is shown only
    /// the primary screen is knowable; once open, the screen under the window wins (a CenterOwner
    /// dialog can land on a smaller or larger secondary monitor).
    /// </summary>
    private static Size? TryGetWorkingAreaDips(Window window)
    {
        try
        {
            var screens = window.Screens;
            if (screens == null)
            {
                return null;
            }

            var screen = (window.IsVisible ? screens.ScreenFromWindow(window) : null) ?? screens.Primary;
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
