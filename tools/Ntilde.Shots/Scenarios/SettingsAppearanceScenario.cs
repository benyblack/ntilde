using Avalonia.Controls;
using Avalonia.Media;
using Ntilde.Settings;

namespace Ntilde.Shots.Scenarios;

/// <summary>
/// The Appearance page: the sidebar, theme selection, and the live preview swatch that reflects
/// whichever theme is actually selected.
/// </summary>
internal sealed class SettingsAppearanceScenario : IScenario
{
    /// <summary>DemoWorld.SeedSettings pins this theme; see its own remarks for why.</summary>
    private const string SeededThemeName = "GitHub Dark";

    /// <summary>GitHub Dark's Background, straight out of themes/GitHubDark.json.</summary>
    private static readonly Color SeededThemeBackground = Color.Parse("#0d1117");

    /// <summary>GitHub Dark's Foreground, straight out of themes/GitHubDark.json.</summary>
    private static readonly Color SeededThemeForeground = Color.Parse("#c9d1d9");

    public ShotSpec Spec { get; } = new(
        Name: "settings-appearance",
        Tier: 2,
        LogicalWidth: 1000,
        LogicalHeight: 700,
        Intent: "The settings window on the Appearance page: the sidebar of pages, the selected " +
                "theme, and a live preview painted in that theme's colours.");

    public Task RunAsync(ShotContext context) =>
        SettingsWindowScenario.RunAsync(
            context,
            SettingsPage.Appearance,
            Spec.LogicalWidth,
            Spec.LogicalHeight,
            AssertAndCapture);

    private static void AssertAndCapture(ShotContext context, SettingsWindow settingsWindow)
    {
        // Each page is its own UserControl and so its own name scope: the window cannot find the
        // page's named controls, the page can.
        Control page = settingsWindow.GetPage(SettingsPage.Appearance).View;

        // Proof that the combo reflects the seeded theme rather than sitting on whatever the first
        // item happens to be.
        var themeList = context.Driver.RequireIn<ComboBox>(page, "ThemeList");
        if (themeList.SelectedItem is not ComboBoxItem themeItem ||
            !string.Equals(themeItem.Content?.ToString(), SeededThemeName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ThemeList's selection is '{(themeList.SelectedItem as ComboBoxItem)?.Content}', not " +
                $"'{SeededThemeName}'. DemoWorld.SeedSettings pins the theme; either the seed drifted or " +
                "the Appearance page stopped matching it.");
        }

        // The live preview is repainted from the selected theme, not a static XAML placeholder.
        var sampleBorder = context.Driver.RequireIn<Border>(page, "SampleTextBorder");
        var sampleText = context.Driver.RequireIn<TextBlock>(page, "SampleTextBlock");

        if (sampleBorder.Background is not ISolidColorBrush borderBrush || borderBrush.Color != SeededThemeBackground)
        {
            throw new InvalidOperationException(
                $"SampleTextBorder's background is {(sampleBorder.Background as ISolidColorBrush)?.Color}, " +
                $"not GitHub Dark's {SeededThemeBackground}. The live preview would not be showing the " +
                "actually-selected theme.");
        }

        if (sampleText.Foreground is not ISolidColorBrush textBrush || textBrush.Color != SeededThemeForeground)
        {
            throw new InvalidOperationException(
                $"SampleTextBlock's foreground is {(sampleText.Foreground as ISolidColorBrush)?.Color}, " +
                $"not GitHub Dark's {SeededThemeForeground}. The live preview would not be showing the " +
                "actually-selected theme.");
        }

        context.CaptureOther(settingsWindow, "tab");
    }
}
