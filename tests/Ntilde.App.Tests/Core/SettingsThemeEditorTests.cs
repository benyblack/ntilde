using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    /// <summary>
    /// A failed write must not look like a save: the draft stays dirty, so the window's Save still
    /// refuses to drop it, and the status says what went wrong. A directory parked on the theme
    /// file's path makes the write fail on every OS.
    /// </summary>
    [AvaloniaFact]
    public void Save_WhenTheThemeFileCannotBeWritten_KeepsTheDraftDirty_AndSaysSo()
    {
        var (root, _, page) = Open();
        using (root)
        {
            string themeFile = Path.Combine(root.Root, "themes", "GitHubLight.json");
            File.Delete(themeFile);
            Directory.CreateDirectory(themeFile);
            Click(page, "BtnEditTheme");
            Type(page, "EditThemeBgInput", "#123456");
            var themeListBefore = page.FindControl<ComboBox>("ThemeList")!.Items.Count;

            Click(page, "BtnSaveTheme");

            Assert.Equal(
                "Couldn't save the theme. Check that the themes folder can be written.",
                page.FindControl<TextBlock>("ThemeEditorStatus")!.Text);
            Assert.Equal("Save the theme or go back first.", page.Validate()?.Message);
            Assert.Equal("#123456", page.FindControl<TextBox>("EditThemeBgInput")!.Text);
            Assert.Equal(themeListBefore, page.FindControl<ComboBox>("ThemeList")!.Items.Count);
            Assert.True(Directory.Exists(themeFile));
        }
    }

    /// <summary>
    /// Waits, yielding to the headless session, until <paramref name="done"/> or the deadline. A DispatcherTimer fires only
    /// while the session's loop runs - during an await - and not from RunJobs.
    /// </summary>
    private static async Task YieldUntilAsync(Func<bool> done, TimeSpan within)
    {
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(within.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        while (!done() && System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// "Saved!" clears itself after 2 s, and its timer clears only that message: a failed save shown meanwhile stays
    /// (Greptile on #527). The second half proves the timer fires here, so the first cannot pass by its never firing.
    /// </summary>
    [AvaloniaFact]
    public async Task TheSavedTimer_LeavesALaterSaveFailureShowing_AndClearsItsOwnMessage()
    {
        var (root, _, page) = Open();
        using (root)
        {
            var status = page.FindControl<TextBlock>("ThemeEditorStatus")!;
            string themeFile = Path.Combine(root.Root, "themes", "GitHubLight.json");
            Click(page, "BtnEditTheme");
            Type(page, "EditThemeBgInput", "#123456");
            Click(page, "BtnSaveTheme");
            Assert.Equal("Saved!", status.Text);   // its timer is running now
            long saved = System.Diagnostics.Stopwatch.GetTimestamp();

            File.Delete(themeFile);
            Directory.CreateDirectory(themeFile);   // the next write fails
            Type(page, "EditThemeBgInput", "#654321");
            Click(page, "BtnSaveTheme");
            const string Failed = "Couldn't save the theme. Check that the themes folder can be written.";
            Assert.Equal(Failed, status.Text);
            await YieldUntilAsync(() => System.Diagnostics.Stopwatch.GetElapsedTime(saved) > TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(5));

            Assert.Equal(Failed, status.Text);

            Directory.Delete(themeFile);
            Click(page, "BtnSaveTheme");
            Assert.Equal("Saved!", status.Text);
            await YieldUntilAsync(() => status.Text == "", TimeSpan.FromSeconds(5));
            Assert.Equal("", status.Text);
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

    private const string TranslucentForeground = "#80E6EDF3";
    private const string TranslucentRed = "#80D73A49";

    /// <summary>Settings on a theme with one translucent colour (#AARRGGBB), as a hand-edited theme file can hold.</summary>
    private static (SettingsTestRoot Root, Ntilde.SettingsWindow Window, AppearancePage Page) OpenTranslucent(string property, string colour)
    {
        var root = SettingsTestRoot.WithSettings("""{"ThemeName":"Translucent"}""");
        root.Tree.WriteFile(Path.Combine("themes", "Translucent.json"), $$"""{"Name":"Translucent","{{property}}":"{{colour}}"}""");
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Appearance));
        return (root, window, window.GetPage<AppearancePage>());
    }

    /// <summary>
    /// The fields show a translucent colour with its alpha, so the text they read back on the next pass is
    /// the colour as loaded, not an opaque copy that reads as an edit and blocks Save.
    /// </summary>
    [AvaloniaFact]
    public void Edit_OnATranslucentColour_ShowsItsAlpha_AndLeavesNoUnsavedEdits()
    {
        var (root, _, page) = OpenTranslucent("Foreground", TranslucentForeground);
        using (root)
        {
            Click(page, "BtnEditTheme");
            Dispatcher.UIThread.RunJobs(); // the editor's own fill-in reaches the change handlers

            Assert.Equal(TranslucentForeground, page.FindControl<TextBox>("EditThemeFgInput")!.Text);
            Assert.Null(page.Validate());
        }
    }

    [AvaloniaFact]
    public void ASwatchFlyout_OnATranslucentColour_LeavesNoUnsavedEdits()
    {
        var (root, window, page) = OpenTranslucent("Red", TranslucentRed);
        using (root)
        {
            try
            {
                window.Show();
                Click(page, "BtnEditTheme");
                Dispatcher.UIThread.RunJobs();
                Assert.Null(page.Validate());

                Click(page, "EditSwatch1"); // red
                Dispatcher.UIThread.RunJobs();

                Assert.Null(page.Validate());
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>A colour changed in a swatch's pop-up is an unsaved edit: Save is refused until it is saved or dropped (#528).</summary>
    [AvaloniaFact]
    public void ASwatchFlyoutEdit_IsAnUnsavedEdit()
    {
        var (root, window, page) = Open();
        using (root)
        {
            try
            {
                window.Show();
                Click(page, "BtnEditTheme");
                Dispatcher.UIThread.RunJobs();
                Assert.Null(page.Validate()); // setup: the editor opens with no edits

                Click(page, "EditSwatch1");
                Dispatcher.UIThread.RunJobs();
                // The pop-up's colour field: the one text box on the page without a name.
                TextBox hex = window.GetVisualDescendants().OfType<TextBox>().Single(box => string.IsNullOrEmpty(box.Name));
                hex.Text = "#123456";
                Dispatcher.UIThread.RunJobs();

                Assert.NotNull(page.Validate());
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>Delete leaves the editor, and focus goes back to Edit as it does after Back (#528).</summary>
    [AvaloniaFact]
    public void Delete_ReturnsFocusToEdit()
    {
        var (root, window, page) = Open();
        using (root)
        {
            try
            {
                window.Show();
                Click(page, "BtnEditTheme");
                Dispatcher.UIThread.RunJobs();
                var delete = page.FindControl<Button>("BtnDeleteTheme")!;
                delete.Focus(NavigationMethod.Tab); // Delete chosen from the keyboard
                Dispatcher.UIThread.RunJobs();
                Assert.Same(delete, TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement()); // setup: Delete itself has focus

                Click(page, "BtnDeleteTheme");
                Dispatcher.UIThread.RunJobs();

                Assert.False(page.IsThemeEditorOpen);
                Assert.Same(page.FindControl<Button>("BtnEditTheme"), TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement());
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>Saving an edit to another colour writes the translucent one back as it was, not opaque.</summary>
    [AvaloniaFact]
    public void Save_AfterEditingAnotherColour_KeepsATranslucentColoursAlpha()
    {
        var (root, _, page) = OpenTranslucent("Foreground", TranslucentForeground);
        using (root)
        {
            Click(page, "BtnEditTheme");
            Type(page, "EditThemeBgInput", "#123456");

            Click(page, "BtnSaveTheme");

            string saved = File.ReadAllText(Path.Combine(root.Root, "themes", "Translucent.json"));
            Assert.Contains($"\"Foreground\": \"{TranslucentForeground}\"", saved);
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

    private static RawInputModifiers Primary => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static string SettingsText(SettingsTestRoot root) => File.ReadAllText(Path.Combine(root.Root, "settings.json"));

    /// <summary>Ctrl/Cmd+F, the query, Enter: a search jump to the first result.</summary>
    private static void SearchAndJump(Ntilde.SettingsWindow window, string query)
    {
        window.KeyPress(Key.F, Primary, PhysicalKey.F, "f");
        window.KeyTextInput(query);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>PR 5 ruling: a search jump into Appearance never drops an unsaved draft. The editor stays, and says why.</summary>
    [AvaloniaFact]
    public void ASearchJump_WithUnsavedThemeEdits_KeepsTheEditorAndItsDraft()
    {
        var (root, window, page) = Open();
        using (root)
        {
            try
            {
                window.Show();
                Click(page, "BtnEditTheme");
                Type(page, "EditThemeBgInput", "#123456");

                SearchAndJump(window, "opacity");

                Assert.Equal(SettingsPage.Appearance, window.CurrentPage);
                Assert.Equal(string.Empty, window.FindControl<TextBox>("SettingsSearchBox")!.Text);
                Assert.True(page.IsSubPageOpen);
                Assert.True(page.FindControl<ScrollViewer>("ThemeEditorView")!.IsEffectivelyVisible);
                Assert.Equal("Save the theme or go back first.", page.FindControl<TextBlock>("ThemeEditorStatus")!.Text);

                Click(page, "BtnSaveTheme"); // the draft survived: saving it writes the edit
                Assert.Equal("#123456", window.Session.Settings.ThemeManager.GetTheme("GitHub Light").Background.ToString());
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>
    /// A refused jump moves focus out of the emptied search box into the editor, to Save theme: the reason asks
    /// to save or go back, and Back would discard the draft.
    /// </summary>
    [AvaloniaFact]
    public void ASearchJump_WithUnsavedThemeEdits_FocusesSaveTheme()
    {
        var (root, window, page) = Open();
        using (root)
        {
            try
            {
                window.Show();
                Click(page, "BtnEditTheme");
                Type(page, "EditThemeBgInput", "#123456");

                SearchAndJump(window, "opacity");

                Assert.Same(page.FindControl<Button>("BtnSaveTheme"), window.FocusManager!.GetFocusedElement());
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>The editor's status ("Saved!", a failed save, a refusal) is read out when it changes.</summary>
    [AvaloniaFact]
    public void TheEditorStatus_IsAPoliteLiveRegion()
    {
        var (root, _, page) = Open();
        using (root)
        {
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(page.FindControl<TextBlock>("ThemeEditorStatus")!));
        }
    }

    /// <summary>A live region speaks when its text changes, so a second refusal sets the same message afresh.</summary>
    [AvaloniaFact]
    public void ASecondRefusedJump_SetsTheMessageAgain()
    {
        var (root, window, page) = Open();
        using (root)
        {
            try
            {
                window.Show();
                Click(page, "BtnEditTheme");
                Type(page, "EditThemeBgInput", "#123456");
                SearchAndJump(window, "opacity");
                var status = page.FindControl<TextBlock>("ThemeEditorStatus")!;
                int changes = 0;
                status.PropertyChanged += (_, e) => changes += e.Property == TextBlock.TextProperty ? 1 : 0;

                SearchAndJump(window, "opacity");

                Assert.True(changes > 0, "the second refusal left the status text untouched");
                Assert.Equal("Save the theme or go back first.", status.Text);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void ASearchJump_WithTheEditorUntouched_ClosesIt_AndLandsOnTheRow()
    {
        var (root, window, page) = Open();
        using (root)
        {
            try
            {
                window.Show();
                Click(page, "BtnEditTheme");
                Dispatcher.UIThread.RunJobs(); // the editor's own fill-in must not read as an edit

                SearchAndJump(window, "opacity");

                Assert.False(page.IsSubPageOpen);
                var row = page.GetLogicalDescendants().OfType<SettingRow>().Single(r => r.RowId == "window-opacity");
                Assert.Same(window.FindResource("NtBlueDim"), row.Background);

                // Leaving the editor returns focus to Edit only when the editor had it; here the setting takes it.
                var slider = page.FindControl<Slider>("WindowOpacitySlider")!;
                var focused = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() as Visual;
                Assert.True(focused is not null && (ReferenceEquals(focused, slider) || slider.IsVisualAncestorOf(focused)), $"Focus is on {focused?.GetType().Name ?? "nothing"}.");
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void PrimaryS_WithUnsavedThemeEdits_DoesNotDiscardThem()
    {
        var (root, window, page) = Open();
        using (root)
        {
            try
            {
                window.Show();
                Click(page, "BtnEditTheme");
                Type(page, "EditThemeBgInput", "#123456");
                string before = SettingsText(root);

                window.KeyPress(Key.S, Primary, PhysicalKey.S, "s");
                Dispatcher.UIThread.RunJobs();

                Assert.True(window.IsVisible);
                Assert.True(page.IsThemeEditorOpen);
                Assert.Equal("#123456", page.FindControl<TextBox>("EditThemeBgInput")!.Text);
                Assert.Equal("Save the theme or go back first.", page.FindControl<TextBlock>("ThemeEditorStatus")!.Text);
                Assert.Equal(before, SettingsText(root));
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
            }
        }
    }

    [AvaloniaFact]
    public void PrimaryS_WithTheEditorOpenAndUntouched_SavesAndCloses()
    {
        var (root, window, page) = Open();
        using (root)
        {
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                window.Show();
                Click(page, "BtnEditTheme");
                Dispatcher.UIThread.RunJobs(); // the editor's own fill-in must not read as an edit

                window.KeyPress(Key.S, Primary, PhysicalKey.S, "s");
                Dispatcher.UIThread.RunJobs();

                Assert.True(closed);
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
            }
        }
    }

    [AvaloniaFact]
    public void FooterSave_WithUnsavedThemeEdits_IsRefused_AndASavedThemeSavesAgain()
    {
        var (root, window, page) = Open();
        using (root)
        {
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                window.Show();
                Click(page, "BtnEditTheme");
                Type(page, "EditThemeNameInput", "Renamed");
                Click(window, "BtnSave");
                Assert.False(closed);

                Click(page, "BtnSaveTheme"); // the edit is now saved, nothing is pending
                Click(window, "BtnSave");
                Dispatcher.UIThread.RunJobs();
                Assert.True(closed);
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
            }
        }
    }

    [AvaloniaFact]
    public void Edit_MovesFocusIntoTheEditor_AndBackReturnsItToEdit()
    {
        var (root, window, page) = Open();
        using (root)
        {
            try
            {
                window.Show();
                Click(page, "BtnEditTheme");
                Dispatcher.UIThread.RunJobs();
                var editor = page.FindControl<ScrollViewer>("ThemeEditorView")!;
                var focused = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() as Visual;
                Assert.True(focused is not null && editor.IsVisualAncestorOf(focused), "Focus is not inside the editor.");

                Click(page, "BtnThemeEditorBack");
                Dispatcher.UIThread.RunJobs();

                Assert.Same(page.FindControl<Button>("BtnEditTheme"), TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement());
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void Delete_IsDisabledOnAnUnsavedNewThemeDraft_UntilItIsSaved()
    {
        var (root, _, page) = Open();
        using (root)
        {
            Click(page, "BtnNewTheme");
            var delete = page.FindControl<Button>("BtnDeleteTheme")!;
            Assert.False(delete.IsEnabled);

            Click(page, "BtnDeleteTheme"); // a disabled button's click must not delete anything either
            Assert.True(page.IsThemeEditorOpen);

            Click(page, "BtnSaveTheme");
            Assert.True(delete.IsEnabled);
        }
    }

    [AvaloniaFact]
    public void Load_ClosesTheEditorAndDropsItsDraft()
    {
        var (root, window, page) = Open();
        using (root)
        {
            Click(page, "BtnEditTheme");
            Type(page, "EditThemeBgInput", "#123456");

            page.Load(window.Session);

            Assert.False(page.IsThemeEditorOpen);
            Assert.True(page.FindControl<ScrollViewer>("AppearanceMainView")!.IsVisible);
            Assert.Null(page.Validate());
        }
    }
}
