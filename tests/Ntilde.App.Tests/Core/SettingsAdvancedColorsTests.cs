using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.Settings;
using Ntilde.Settings.Pages;
using Ntilde.Shell.Theming;

namespace Ntilde.Tests.Core;

/// <summary>
/// The theme editor's Advanced colors section (spec §6): each optional color is derived (dimmed
/// computed swatch, no Reset) or overridden (its own swatch, Reset), and the preview resolves the
/// draft's tokens in a scope of its own.
/// </summary>
public sealed class SettingsAdvancedColorsTests
{
    private static void Click(Control page, string button)
        => page.FindControl<Button>(button)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static T Find<T>(Control page, string name) where T : Control
        => page.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static (SettingsTestRoot Root, Ntilde.SettingsWindow Window, AppearancePage Page) OpenEditor()
    {
        var root = SettingsTestRoot.WithSettings("""{"ThemeName":"GitHub Light"}""");
        root.CopyShippedTheme("GitHubLight.json");
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Appearance));
        var page = window.GetPage<AppearancePage>();
        window.Show();
        Click(page, "BtnEditTheme");
        page.FindControl<Expander>("AdvancedColorsExpander")!.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        return (root, window, page);
    }

    /// <summary>Overrides a color the way a user does: its swatch, then the pop-up's hex field.</summary>
    private static void Override(Ntilde.SettingsWindow window, AppearancePage page, string key, string hex)
    {
        Find<Button>(page, $"Advanced{key}Swatch").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        // The pop-up's colour field: the one text box on the page without a name.
        window.GetVisualDescendants().OfType<TextBox>().Single(box => string.IsNullOrEmpty(box.Name)).Text = hex;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void A_theme_without_optional_colors_shows_every_row_derived()
    {
        var (root, window, page) = OpenEditor();
        using (root)
        {
            try
            {
                foreach (var key in new[] { "SelectionBackground", "CursorText", "Accent", "Info" })
                {
                    Assert.Equal("Derived", Find<TextBlock>(page, $"Advanced{key}State").Text);
                    Assert.False(Find<Button>(page, $"Advanced{key}Reset").IsVisible);
                    Assert.Equal(0.5, Find<Button>(page, $"Advanced{key}Swatch").Opacity);
                }

                Assert.Null(page.Validate());
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void The_derived_swatch_shows_the_computed_color_and_follows_other_edits()
    {
        var (root, window, page) = OpenEditor();
        using (root)
        {
            try
            {
                var swatch = Find<Button>(page, "AdvancedCursorTextSwatch");
                // Derived cursor text is the background.
                Assert.Equal(Color.Parse("#ffffff"), ((ISolidColorBrush)swatch.Background!).Color);

                page.FindControl<TextBox>("EditThemeBgInput")!.Text = "#202020";
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(Color.Parse("#202020"), ((ISolidColorBrush)swatch.Background!).Color);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void An_override_is_an_unsaved_edit_that_saves_to_the_theme_file()
    {
        var (root, window, page) = OpenEditor();
        using (root)
        {
            try
            {
                Override(window, page, "Accent", "#8040C0");

                Assert.Equal("Override", Find<TextBlock>(page, "AdvancedAccentState").Text);
                Assert.True(Find<Button>(page, "AdvancedAccentReset").IsVisible);
                Assert.Equal(1.0, Find<Button>(page, "AdvancedAccentSwatch").Opacity);
                Assert.NotNull(page.Validate());

                Click(page, "BtnSaveTheme");

                using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.Root, "themes", "GitHubLight.json")));
                Assert.Equal("#8040C0", saved.RootElement.GetProperty("Accent").GetString());
                Assert.False(saved.RootElement.TryGetProperty("SelectionBackground", out _));
                Assert.Null(page.Validate());
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void Reset_returns_an_override_to_derived()
    {
        var (root, window, page) = OpenEditor();
        using (root)
        {
            try
            {
                Override(window, page, "SelectionBackground", "#336699");
                Assert.NotNull(page.Validate());

                Find<Button>(page, "AdvancedSelectionBackgroundReset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("Derived", Find<TextBlock>(page, "AdvancedSelectionBackgroundState").Text);
                Assert.False(Find<Button>(page, "AdvancedSelectionBackgroundReset").IsVisible);
                Assert.Null(page.Validate()); // back to what was opened: nothing to save
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>
    /// The preview's chrome samples resolve the draft's tokens from a scope on the preview card,
    /// so an override shows there while the rest of Settings keeps the app theme.
    /// </summary>
    [AvaloniaFact]
    public void The_preview_resolves_the_drafts_tokens_and_the_window_keeps_the_apps()
    {
        var (root, window, page) = OpenEditor();
        using (root)
        {
            try
            {
                var appAccent = ((ISolidColorBrush)window.FindResource(ThemeKeys.Accent)!).Color;

                Override(window, page, "Accent", "#8040C0");

                var sample = page.FindControl<Button>("EditorAccentSample")!;
                Assert.Equal(Color.Parse("#8040C0"), ((ISolidColorBrush)sample.FindResource(ThemeKeys.Accent)!).Color);
                Assert.Equal(appAccent, ((ISolidColorBrush)window.FindResource(ThemeKeys.Accent)!).Color);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void The_terminal_sample_draws_selection_search_and_cursor_from_the_draft()
    {
        var (root, window, page) = OpenEditor();
        using (root)
        {
            try
            {
                Override(window, page, "SelectionBackground", "#336699");

                var runs = page.FindControl<TextBlock>("EditorTerminalSample")!.Inlines!.OfType<Avalonia.Controls.Documents.Run>().ToList();
                var selected = runs.Single(run => run.Text == "selected");
                Assert.Equal(Color.Parse("#336699"), ((ISolidColorBrush)selected.Background!).Color);
                Assert.Contains(runs, run => run.Text == "match");
                Assert.Contains(runs, run => run.Text == "current");
                Assert.Contains(runs, run => run.Text == "x"); // the glyph under a block cursor
            }
            finally
            {
                window.Close();
            }
        }
    }
}
