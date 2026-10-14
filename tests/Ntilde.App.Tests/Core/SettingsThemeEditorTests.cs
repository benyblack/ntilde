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

    /// <summary>Sets a field's text. TextBox raises TextChanged on a later dispatcher pass, so run it before going on.</summary>
    private static void Type(Control page, string textBox, string text)
    {
        page.FindControl<TextBox>(textBox)!.Text = text;
        Dispatcher.UIThread.RunJobs();
    }

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
            Type(page, "EditThemeNameInput", "Renamed");
            Type(page, "EditThemeFgInput", "#ff0000");

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
    /// Correction 7: saving repaints Settings with the theme just saved, edits included, not the
    /// theme settings.json named when the window opened.
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
            Type(page, "EditThemeBgInput", "#123456");
            Assert.Equal(Color.Parse("#ffffff"), ((SolidColorBrush)window.FindResource("NtWindowBg")!).Color); // an edit alone repaints only the editor's preview

            Click(page, "BtnSaveTheme");

            Assert.Equal(Color.Parse("#123456"), ((SolidColorBrush)window.FindResource("NtWindowBg")!).Color);
        }
    }

    /// <summary>Back drops the edits made since the last Save; the saved values stay saved.</summary>
    [AvaloniaFact]
    public void Back_AfterASave_DropsOnlyTheEditsMadeSinceTheSave()
    {
        var (root, _, page) = Open();
        using (root)
        {
            Click(page, "BtnEditTheme");
            Type(page, "EditThemeBgInput", "#123456");
            Click(page, "BtnSaveTheme");
            Type(page, "EditThemeBgInput", "#654321");

            Click(page, "BtnThemeEditorBack");
            Click(page, "BtnEditTheme");

            Assert.Equal("#123456", page.FindControl<TextBox>("EditThemeBgInput")!.Text);
        }
    }

    /// <summary>
    /// Review focus 5: at the minimum size both editor columns are fully on screen, and the swatch
    /// grid keeps its full width.
    /// </summary>
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
                var lastSwatch = page.FindControl<Button>("EditSwatch7")!;
                double previewLeft = preview.TranslatePoint(default, window)!.Value.X;
                double previewRight = preview.TranslatePoint(new Point(preview.Bounds.Width, 0), window)!.Value.X;
                double lastSwatchRight = lastSwatch.TranslatePoint(new Point(lastSwatch.Bounds.Width, 0), window)!.Value.X;

                // Layout ran: without it every bound is zero and the comparisons below pass vacuously.
                Assert.Equal(220, preview.Bounds.Width, 1);
                Assert.True(previewRight <= window.ClientSize.Width + 0.5, $"Preview ends at {previewRight}, past {window.ClientSize.Width}.");

                // A grid short of room squeezes its cells and overlaps its own swatches without ever
                // reaching the preview, so measure the grid: eight 28 px swatches with 2 px margins.
                Assert.True(swatches.Bounds.Width >= 8 * 32 - 0.5, $"The swatch grid is squeezed to {swatches.Bounds.Width}.");
                Assert.True(lastSwatchRight <= previewLeft, $"The last swatch ends at {lastSwatchRight}, under the preview at {previewLeft}.");
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
