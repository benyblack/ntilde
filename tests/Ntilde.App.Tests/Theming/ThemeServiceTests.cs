using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Ntilde.Shell.Theming;
using Xunit;

namespace Ntilde.Tests.Theming;

/// <summary>
/// Every test touches Application.Current resources, so every test is an [AvaloniaFact]. A plain
/// [Fact] here would run off the session thread and poison the headless run.
/// </summary>
public class ThemeServiceTests
{
    [AvaloniaFact]
    public void Apply_mutates_the_installed_brushes_in_place()
    {
        ThemeService.Apply(TestThemes.Dracula, 1.0);
        var panel = AppBrush(ThemeKeys.Panel);

        ThemeService.Apply(TestThemes.GitHubLight, 1.0);

        Assert.Same(panel, AppBrush(ThemeKeys.Panel));
        Assert.Equal(ThemeTokens.Derive(TestThemes.GitHubLight, 1.0)[ThemeKeys.Panel], panel.Color);
    }

    [AvaloniaFact]
    public void Apply_sets_the_application_variant_from_the_background()
    {
        ThemeService.Apply(TestThemes.GitHubLight, 1.0);
        Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);

        ThemeService.Apply(TestThemes.Dracula, 1.0);
        Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
    }

    [AvaloniaFact]
    public void Apply_raises_Changed_once_with_the_new_tokens()
    {
        ThemeTokens? seen = null;
        int calls = 0;
        void Handler(object? sender, ThemeTokens tokens) { calls++; seen = tokens; }
        ThemeService.Changed += Handler;
        try
        {
            ThemeService.Apply(TestThemes.Nord, 1.0);
        }
        finally
        {
            ThemeService.Changed -= Handler;
        }

        Assert.Equal(1, calls);
        Assert.Same(ThemeService.Current, seen);
        Assert.Equal(ThemeTokens.Derive(TestThemes.Nord, 1.0)[ThemeKeys.Accent], seen![ThemeKeys.Accent]);
    }

    [AvaloniaFact]
    public async Task Apply_off_the_ui_thread_throws()
    {
        var error = await Task.Run(() => Record.Exception(() => ThemeService.Apply(TestThemes.Nord, 1.0)));
        Assert.IsType<InvalidOperationException>(error);
    }

    [AvaloniaFact]
    public void ApplyTo_writes_only_the_given_scope()
    {
        ThemeService.Apply(TestThemes.Dracula, 1.0);
        var appPanel = AppBrush(ThemeKeys.Panel).Color;
        var scope = new ResourceDictionary();
        var light = ThemeTokens.Derive(TestThemes.GitHubLight, 1.0);

        ThemeService.ApplyTo(scope, light);

        Assert.Equal(light[ThemeKeys.Panel], Assert.IsType<SolidColorBrush>(scope[ThemeKeys.Panel]).Color);
        Assert.Equal(appPanel, AppBrush(ThemeKeys.Panel).Color);
    }

    /// <summary>
    /// Stock controls follow a same-variant switch (dark → dark) through the control-level keys
    /// FluentControlOverrides aliases to token brushes. Fluent's own system brushes cannot: they read
    /// the palette once ({StaticResource}), see FluentControlOverrides.
    /// </summary>
    [AvaloniaFact]
    public void Stock_menu_background_follows_a_same_variant_theme_switch()
    {
        var border = new Border();
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("MenuFlyoutPresenterBackground"));
        var window = new Window { Content = border };
        window.Show();
        try
        {
            ThemeService.Apply(TestThemes.Dracula, 1.0);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeTokens.Derive(TestThemes.Dracula, 1.0)[ThemeKeys.Overlay], ((ISolidColorBrush)border.Background!).Color);

            ThemeService.Apply(TestThemes.Nord, 1.0);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeTokens.Derive(TestThemes.Nord, 1.0)[ThemeKeys.Overlay], ((ISolidColorBrush)border.Background!).Color);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_stock_text_box_follows_a_same_variant_theme_switch()
    {
        string token = FluentControlOverrides.Map.Single(m => m.FluentKey == "TextControlBackground").TokenKey;
        var box = new TextBox();
        var window = new Window { Content = box };
        window.Show();
        try
        {
            ThemeService.Apply(TestThemes.Dracula, 1.0);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeTokens.Derive(TestThemes.Dracula, 1.0)[token], ((ISolidColorBrush)box.Background!).Color);

            ThemeService.Apply(TestThemes.Nord, 1.0);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeTokens.Derive(TestThemes.Nord, 1.0)[token], ((ISolidColorBrush)box.Background!).Color);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Fluent paints a checked ToggleButton's text from ChromeWhite, which the palette sets to the
    /// theme foreground, so a light theme put dark ink on the accent (the connection manager's
    /// "All" chip). The checked state takes the accent and its own ink instead.
    /// </summary>
    [AvaloniaFact]
    public void A_checked_toggle_button_on_a_light_theme_has_the_accent_ink_on_the_accent()
    {
        var toggle = new ToggleButton { Content = "All", IsChecked = true };
        var window = new Window { Content = toggle };
        window.Show();
        try
        {
            ThemeService.Apply(TestThemes.GitHubLight, 1.0);
            Dispatcher.UIThread.RunJobs();

            var expected = ThemeTokens.Derive(TestThemes.GitHubLight, 1.0);
            var presenter = toggle.GetVisualDescendants().OfType<ContentPresenter>().First();
            Assert.Equal(expected[ThemeKeys.Accent], ((ISolidColorBrush)presenter.Background!).Color);
            Assert.Equal(expected[ThemeKeys.AccentFg], ((ISolidColorBrush)presenter.Foreground!).Color);
        }
        finally
        {
            window.Close();
            ThemeService.Apply(new Ntilde.VT.TerminalTheme(), 1.0);
        }
    }

    [AvaloniaFact]
    public void Fluent_overrides_alias_the_token_brush_instance()
    {
        ThemeService.Apply(TestThemes.Nord, 1.0);
        foreach (var (fluentKey, tokenKey) in FluentControlOverrides.Map)
        {
            Assert.Same(Application.Current!.FindResource(tokenKey), Application.Current.FindResource(fluentKey));
        }
    }

    /// <summary>
    /// Pins every override key to a resource Fluent actually defines, so an Avalonia upgrade that
    /// renames one fails here instead of silently leaving that control on stock colors.
    /// </summary>
    [AvaloniaFact]
    public void Every_fluent_override_key_is_a_real_fluent_resource()
    {
        var fluent = new FluentTheme();
        var missing = FluentControlOverrides.Map
            .Select(m => m.FluentKey)
            .Where(key => !fluent.TryGetResource(key, ThemeVariant.Dark, out _) && !fluent.TryGetResource(key, ThemeVariant.Light, out _))
            .ToList();
        Assert.True(missing.Count == 0, "Not defined by the Fluent theme in use: " + string.Join(", ", missing));
    }

    [AvaloniaFact]
    public void Override_map_is_well_formed()
    {
        Assert.Equal(FluentControlOverrides.Map.Count, FluentControlOverrides.Map.Select(m => m.FluentKey).Distinct(StringComparer.Ordinal).Count());
        Assert.All(FluentControlOverrides.Map, m => Assert.Contains(m.TokenKey, ThemeKeys.All));
        Assert.True(FluentControlOverrides.Map.Count >= 40, $"only {FluentControlOverrides.Map.Count} overrides - coverage table not applied");
    }

    [AvaloniaFact]
    public void Fluent_accent_is_the_theme_accent()
    {
        ThemeService.Apply(TestThemes.Nord, 1.0);

        Assert.True(Application.Current!.TryFindResource("SystemAccentColor", ThemeVariant.Dark, out var value));
        Assert.Equal(ThemeTokens.Derive(TestThemes.Nord, 1.0)[ThemeKeys.Accent], Assert.IsType<Color>(value));
    }

    // Review Focus 5
    [AvaloniaFact]
    public void Light_theme_stock_text_is_the_theme_foreground()
    {
        ThemeService.Apply(TestThemes.GitHubLight, 1.0);

        Assert.True(Application.Current!.TryFindResource("SystemBaseHighColor", ThemeVariant.Light, out var value));
        Assert.Equal(ThemeTokens.Derive(TestThemes.GitHubLight, 1.0)[ThemeKeys.Fg], Assert.IsType<Color>(value));
    }

    // Review Focus 2
    [AvaloniaFact]
    public void Reapplying_with_only_a_new_window_opacity_leaves_the_fluent_palette_alone()
    {
        var fluent = Application.Current!.Styles.OfType<FluentTheme>().Single();
        ThemeService.Apply(TestThemes.Dracula, 1.0);
        var palette = fluent.Palettes[ThemeVariant.Dark];
        int accentChanges = 0;
        void Handler(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ColorPaletteResources.AccentProperty) accentChanges++;
        }

        palette.PropertyChanged += Handler;
        try
        {
            ThemeService.Apply(TestThemes.Dracula, 0.6);
        }
        finally
        {
            palette.PropertyChanged -= Handler;
        }

        Assert.Equal(0, accentChanges);
        Assert.Equal(153, AppBrush(ThemeKeys.SidebarBg).Color.A);
    }

    private static SolidColorBrush AppBrush(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out var value), key);
        return Assert.IsType<SolidColorBrush>(value);
    }
}
