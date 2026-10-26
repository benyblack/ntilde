using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Ntilde.VT;

namespace Ntilde.Shell.Theming;

/// <summary>
/// App-wide UI theming. It installs one dictionary of token brushes into
/// <c>Application.Resources</c>, and on every theme change mutates those brushes in place, writes
/// Fluent's palette and sets the application's theme variant. Every window, dialog and control
/// resolves <c>{DynamicResource Nt…}</c> up the tree to that dictionary, so none of them wire
/// anything per window. (Before v2 only Settings and Connection Manager followed the theme, each
/// through its own private copy.) Same shape as <see cref="UiScale"/>.
/// </summary>
public static class ThemeService
{
    private static readonly ThemeVariant[] Variants = [ThemeVariant.Light, ThemeVariant.Dark];

    /// <summary>The tokens most recently applied (Default theme before the first apply).</summary>
    public static ThemeTokens Current { get; private set; } = ThemeTokens.Derive(new TerminalTheme(), 1.0);

    /// <summary>Raised after every <see cref="Apply"/> with the new tokens.</summary>
    public static event EventHandler<ThemeTokens>? Changed;

    /// <summary>
    /// Creates the token dictionary and both Fluent palettes. Call from
    /// <c>Application.Initialize</c>, after the XAML load (so the FluentTheme exists) and before
    /// any window is constructed, so every token resolves from the first layout pass.
    /// </summary>
    public static void Install(Application app, TerminalTheme theme, double windowOpacity)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(theme);

        var dictionary = FindTokenDictionary(app);
        if (dictionary is null)
        {
            dictionary = new ThemeTokenDictionary();
            app.Resources.MergedDictionaries.Add(dictionary);
        }

        if (FindFluent(app) is { } fluent)
        {
            // Both variants get a palette up front, so later applies only mutate an existing one.
            foreach (var variant in Variants)
            {
                if (!fluent.Palettes.ContainsKey(variant))
                {
                    fluent.Palettes[variant] = new ColorPaletteResources();
                }
            }
        }

        var tokens = ThemeTokens.Derive(theme, windowOpacity);
        Write(app, dictionary, previous: null, tokens);
        Current = tokens;
    }

    /// <summary>
    /// Applies <paramref name="theme"/> to every open and future window. Safe without a running
    /// <see cref="Application"/>: then only <see cref="Current"/> and <see cref="Changed"/> update.
    /// </summary>
    public static void Apply(TerminalTheme theme, double windowOpacity)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var tokens = ThemeTokens.Derive(theme, windowOpacity);

        if (Application.Current is { } app)
        {
            // Same fail-fast as UiScale.Apply. Off the UI thread, mutating application resources does
            // not throw here; it poisons Avalonia's media context for every window that lays out
            // afterwards (the headless suite once lost 29 unrelated tests that way).
            Dispatcher.UIThread.VerifyAccess();

            if (FindTokenDictionary(app) is { } dictionary)
            {
                Write(app, dictionary, Current, tokens);
            }
            else
            {
                Install(app, theme, windowOpacity);
            }
        }

        Current = tokens;
        Changed?.Invoke(null, tokens);
    }

    /// <summary>
    /// Writes <paramref name="tokens"/> into <paramref name="scope"/>: an existing brush is
    /// recolored in place (so every holder of it updates), a missing one is added. Used for the app
    /// dictionary and for local scopes (a theme preview, a profile-themed pane). Also aliases every
    /// <see cref="FluentControlOverrides"/> key to its token's brush instance, so Fluent's own
    /// control themes (MenuFlyoutPresenter, TextBox, ComboBox, ...) pick up the same recolor: they
    /// resolve those keys with {DynamicResource}, and a key here in Application.Resources wins that
    /// lookup over Fluent's own {StaticResource}-cached stock brushes.
    /// </summary>
    public static void ApplyTo(IResourceDictionary scope, ThemeTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(tokens);

        foreach (var (key, color) in tokens.All)
        {
            if (scope.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
            {
                brush.Color = color;
            }
            else
            {
                scope[key] = new SolidColorBrush(color);
            }
        }

        // Fluent control keys share the token's brush instance, so the recolor above covers them.
        foreach (var (fluentKey, tokenKey) in FluentControlOverrides.Map)
        {
            var brush = scope[tokenKey];
            if (!scope.TryGetValue(fluentKey, out var current) || !ReferenceEquals(current, brush))
            {
                scope[fluentKey] = brush;
            }
        }
    }

    private static void Write(Application app, ThemeTokenDictionary dictionary, ThemeTokens? previous, ThemeTokens tokens)
    {
        ApplyTo(dictionary, tokens);

        var variant = tokens.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (FindFluent(app) is { } fluent
            && fluent.Palettes.TryGetValue(variant, out var palette)
            && (previous is null || !FluentPaletteMapping.SameInputs(previous, tokens)))
        {
            FluentPaletteMapping.Write(palette, tokens);
        }

        // Palette first, variant second: a variant flip re-resolves every Fluent resource and must
        // find the new palette already in place.
        app.RequestedThemeVariant = variant;
    }

    private static ThemeTokenDictionary? FindTokenDictionary(Application app)
        => app.Resources.MergedDictionaries.OfType<ThemeTokenDictionary>().FirstOrDefault();

    private static FluentTheme? FindFluent(Application app)
        => app.Styles.OfType<FluentTheme>().FirstOrDefault();

    /// <summary>Marker type so the service finds its own dictionary among the merged ones.</summary>
    private sealed class ThemeTokenDictionary : ResourceDictionary
    {
    }
}
