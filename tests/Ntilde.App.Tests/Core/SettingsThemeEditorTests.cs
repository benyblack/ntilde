using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Ntilde.Settings;
using Ntilde.Settings.Pages;

namespace Ntilde.Tests.Core;

/// <summary>The theme editor as a sub-page of Appearance (spec §7).</summary>
public sealed class SettingsThemeEditorTests
{
    private static void Click(Control page, string button)
        => page.FindControl<Button>(button)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static (SettingsTestRoot Root, Ntilde.SettingsWindow Window, AppearancePage Page) Open(string themeName = "GitHub Light")
    {
        var root = SettingsTestRoot.WithSettings($$"""{"ThemeName":"{{themeName}}"}""");
        root.CopyShippedTheme("GitHubLight.json");
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Appearance));
        return (root, window, window.GetPage<AppearancePage>());
    }

    [AvaloniaFact]
    public void Edit_OpensTheSubPageForTheSelectedTheme_WithAppearanceStillSelected()
    {
        var (root, window, page) = Open();
        using (root)
        {
            Click(page, "BtnEditTheme");

            Assert.True(page.IsThemeEditorOpen);
            Assert.False(page.FindControl<ScrollViewer>("AppearanceMainView")!.IsVisible);
            Assert.Equal("GitHub Light", page.FindControl<TextBox>("EditThemeNameInput")!.Text);
            Assert.Equal("GitHub Light", page.FindControl<TextBlock>("ThemeEditorTitle")!.Text);
            Assert.Equal(SettingsPage.Appearance, window.CurrentPage);
            Assert.Same(window.NavItem(SettingsPage.Appearance), window.FindControl<ListBox>("SideNav")!.SelectedItem);
        }
    }

    [AvaloniaFact]
    public void Back_DropsUnsavedEdits()
    {
        var (root, _, page) = Open();
        using (root)
        {
            Click(page, "BtnEditTheme");
            string originalForeground = page.FindControl<TextBox>("EditThemeFgInput")!.Text!;
            page.FindControl<TextBox>("EditThemeNameInput")!.Text = "Renamed";
            page.FindControl<TextBox>("EditThemeFgInput")!.Text = "#ff0000";

            Click(page, "BtnThemeEditorBack");
            Assert.False(page.IsThemeEditorOpen);
            Click(page, "BtnEditTheme");

            Assert.Equal("GitHub Light", page.FindControl<TextBox>("EditThemeNameInput")!.Text);
            Assert.Equal(originalForeground, page.FindControl<TextBox>("EditThemeFgInput")!.Text);
        }
    }

    [AvaloniaFact]
    public void New_OpensTheEditorOnANewThemeDraft()
    {
        var (root, _, page) = Open();
        using (root)
        {
            Click(page, "BtnNewTheme");

            Assert.True(page.IsThemeEditorOpen);
            Assert.Equal("New Theme", page.FindControl<TextBox>("EditThemeNameInput")!.Text);
        }
    }

    [AvaloniaFact]
    public void Delete_RemovesTheTheme_AndReturnsToAppearance()
    {
        var (root, _, page) = Open();
        using (root)
        {
            Click(page, "BtnEditTheme");
            Click(page, "BtnDeleteTheme");

            Assert.False(page.IsThemeEditorOpen);
            Assert.DoesNotContain(
                page.FindControl<ComboBox>("ThemeList")!.Items.OfType<ComboBoxItem>(),
                item => (string?)item.Content == "GitHub Light");
        }
    }

    /// <summary>
    /// Correction 7: saving repaints Settings with the theme just saved, not the theme
    /// settings.json named when the window opened.
    /// </summary>
    [AvaloniaFact]
    public void Save_RepaintsSettingsWithTheSavedTheme()
    {
        var (root, window, page) = Open(themeName: "Default"); // stored theme is dark
        using (root)
        {
            var themes = page.FindControl<ComboBox>("ThemeList")!;
            themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(i => (string?)i.Content == "GitHub Light");
            Click(page, "BtnEditTheme");

            Click(page, "BtnSaveTheme");

            Assert.Equal(Color.Parse("#ffffff"), ((SolidColorBrush)window.FindResource("NtWindowBg")!).Color);
        }
    }

    /// <summary>Review focus 5: at the minimum width both editor columns are fully on screen.</summary>
    [AvaloniaFact]
    public void AtTheMinimumWidth_BothEditorColumnsFit()
    {
        var (root, window, page) = Open();
        using (root)
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            try
            {
                window.Show();
                Click(page, "BtnEditTheme");
                Dispatcher.UIThread.RunJobs();

                var preview = page.FindControl<Border>("EditorPreviewArea")!;
                var swatches = page.FindControl<UniformGrid>("EditorSwatchGrid")!;
                double previewLeft = preview.TranslatePoint(default, window)!.Value.X;
                double previewRight = preview.TranslatePoint(new Point(preview.Bounds.Width, 0), window)!.Value.X;
                double swatchesRight = swatches.TranslatePoint(new Point(swatches.Bounds.Width, 0), window)!.Value.X;

                Assert.True(previewRight <= window.ClientSize.Width + 0.5, $"Preview ends at {previewRight}, past {window.ClientSize.Width}.");
                Assert.True(swatchesRight <= previewLeft, "The swatch grid runs under the preview column.");
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void JumpingToAnAppearanceRow_ClosesTheEditorFirst()
    {
        var (root, window, page) = Open();
        using (root)
        {
            try
            {
                window.Show();
                Click(page, "BtnEditTheme");

                Assert.True(window.RevealRow(SettingsPage.Appearance, "window-opacity"));
                Dispatcher.UIThread.RunJobs();

                Assert.False(page.IsThemeEditorOpen);
                var row = page.GetLogicalDescendants().OfType<SettingRow>().Single(r => r.RowId == "window-opacity");
                Assert.Same(window.FindResource("NtBlueDim"), row.Background);
            }
            finally
            {
                window.Close();
            }
        }
    }
}
