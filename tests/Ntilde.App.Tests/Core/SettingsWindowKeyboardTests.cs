using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.Settings;
using Ntilde.Settings.Pages;
using Ntilde.Shell.Shortcuts;

namespace Ntilde.Tests.Core;

public sealed class SettingsWindowKeyboardTests
{
    private static RawInputModifiers Primary => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    [AvaloniaFact]
    public void PrimaryS_SavesAndCloses()
    {
        using var root = SettingsTestRoot.WithSettings("""{"FontSize":14}""");
        var window = new Ntilde.SettingsWindow();
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            SettingsWindowFrameTests.FontSizeInput(window).Value = 19;

            window.KeyPress(Key.S, Primary, PhysicalKey.S, "s");
            Dispatcher.UIThread.RunJobs();

            Assert.True(closed);
            using var saved = root.ReadSettings();
            Assert.Equal(19, saved.RootElement.GetProperty("FontSize").GetInt32());
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    /// <summary>
    /// Review focus 2: a number typed but not yet committed is what gets saved. Note: with Avalonia
    /// 12.1.3 a NumericUpDown commits its Value on every text change, so no pending edit exists to
    /// lose here and removing the frame's _btnSave.Focus() does not fail this test (verified).
    /// </summary>
    [AvaloniaFact]
    public void PrimaryS_SavesAnEditStillPendingInANumberBox()
    {
        using var root = SettingsTestRoot.WithSettings("""{"FontSize":14}""");
        // Open on Terminal, where Font size lives; a hidden page never applies its template, so the test must open the page that owns the box.
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Terminal));
        try
        {
            window.Show();
            TextBox text = SettingsWindowFrameTests.FontSizeInput(window).GetVisualDescendants().OfType<TextBox>().First();
            text.Focus();
            text.SelectAll();
            window.KeyTextInput("23");
            Dispatcher.UIThread.RunJobs();

            window.KeyPress(Key.S, Primary, PhysicalKey.S, "s");
            Dispatcher.UIThread.RunJobs();

            using var saved = root.ReadSettings();
            Assert.Equal(23, saved.RootElement.GetProperty("FontSize").GetInt32());
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    /// <summary>
    /// The chord goes to the focused binding editor, which records it, or reports a clash. Either
    /// way the window must not save: had the editor been skipped, Save would have closed the window.
    /// </summary>
    [AvaloniaFact]
    public void PrimaryS_InAShortcutEditor_IsLeftToTheEditor()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            TextBox editor = SettingsPageTests.BindingEditorFor(window.GetPage<ShortcutsPage>(), "Settings");
            editor.Focus();

            window.KeyPress(Key.S, Primary, PhysicalKey.S, "s");
            Dispatcher.UIThread.RunJobs();

            Assert.False(closed);
            Assert.Equal(SettingsPage.Shortcuts, window.CurrentPage);
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    /// <summary>
    /// Tab in a binding editor moves on, as everywhere else. It used to be recorded as the binding
    /// and marked handled, so a keyboard user tabbing through the page bound the first command to
    /// Tab and could not leave the field.
    /// </summary>
    [AvaloniaFact]
    public void Tab_InAShortcutEditor_MovesToTheResetButton_AndRecordsNothing()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            TextBox editor = SettingsPageTests.BindingEditorFor(window.GetPage<ShortcutsPage>(), "Settings");
            Button reset = editor.GetLogicalParent()!.GetLogicalChildren().OfType<Button>().Single(b => (string?)b.Content == "Reset");
            string before = editor.Text!;
            editor.Focus();

            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            Dispatcher.UIThread.RunJobs();

            Assert.Same(reset, window.FocusManager!.GetFocusedElement());
            Assert.Equal(before, editor.Text);
            Assert.False(window.Session.ShortcutDraft.ContainsKey("settings"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ShiftTab_InAShortcutEditor_MovesBack_AndRecordsNothing()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            TextBox editor = SettingsPageTests.BindingEditorFor(window.GetPage<ShortcutsPage>(), "Settings");
            string before = editor.Text!;
            editor.Focus();

            window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, "\t");
            Dispatcher.UIThread.RunJobs();

            // The previous stop is the Reset button of the row above.
            Button previous = Assert.IsType<Button>(window.FocusManager!.GetFocusedElement());
            Assert.Equal("Reset", previous.Content);
            Assert.NotSame(editor.GetLogicalParent(), previous.GetLogicalParent());
            Assert.Equal(before, editor.Text);
            Assert.False(window.Session.ShortcutDraft.ContainsKey("settings"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Tab with a modifier is still a binding: the tab-switching defaults are Ctrl+Tab and Ctrl+Shift+Tab.</summary>
    [AvaloniaFact]
    public void CtrlTab_InAShortcutEditor_IsStillRecorded()
    {
        using var root = SettingsTestRoot.WithSettings("""{"Keybindings":{"next_tab":"Ctrl+F12"}}""");
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            TextBox editor = SettingsPageTests.BindingEditorFor(window.GetPage<ShortcutsPage>(), "Tab: Next (MRU)");
            editor.Focus();

            window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, "\t");
            Dispatcher.UIThread.RunJobs();

            // Ctrl+Tab is this row's default, so recording it removes the override.
            Assert.Equal(ShortcutMatcher.Format(Key.Tab, KeyModifiers.Control), editor.Text);
            Assert.False(window.Session.ShortcutDraft.ContainsKey("next_tab"));
            Assert.Same(editor, window.FocusManager!.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Review focus 3: arrows pass over group headers in both directions.</summary>
    [AvaloniaFact]
    public void ArrowKeysInTheSidebar_PassOverGroupHeaders()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow(new SettingsTarget(SettingsPage.Shortcuts));
        try
        {
            window.Show();
            window.NavItem(SettingsPage.Shortcuts).Focus();

            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(SettingsPage.CommandAssist, window.CurrentPage);

            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(SettingsPage.Shortcuts, window.CurrentPage);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Avalonia's own ListBox navigation already skips disabled headers mid-list and marks those
    /// keys handled, so only the ends tell the frame's handler apart: it handles the key and stays
    /// put, whereas the built-in navigation leaves a boundary key unhandled.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArrowKeysAtTheSidebarEnds_AreHandled_AndStayOnThePage(bool atTop)
    {
        using var root = SettingsTestRoot.Empty();
        SettingsPage edge = atTop ? SettingsPageRegistry.Pages[0].Page : SettingsPageRegistry.Pages[^1].Page;
        var window = new Ntilde.SettingsWindow(new SettingsTarget(edge));
        try
        {
            window.Show();
            window.NavItem(edge).Focus();
            var nav = window.FindControl<ListBox>("SideNav")!;
            KeyEventArgs? seen = null;
            nav.AddHandler(InputElement.KeyDownEvent, (_, e) => seen = e, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

            window.KeyPress(atTop ? Key.Up : Key.Down, RawInputModifiers.None, atTop ? PhysicalKey.ArrowUp : PhysicalKey.ArrowDown, null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(edge, window.CurrentPage);
            Assert.NotNull(seen);
            Assert.True(seen!.Handled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CancelButton_SaysWhatItDoesNotUndo()
    {
        using var root = SettingsTestRoot.Empty();
        var window = new Ntilde.SettingsWindow();

        Assert.Equal(
            "Reverts preview changes. Themes, snippets, history and backups are saved as you change them.",
            ToolTip.GetTip(window.FindControl<Button>("BtnCancel")!));
    }
}
