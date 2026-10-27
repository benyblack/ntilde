using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Ntilde.Tests.Core;

/// <summary>
/// The window's drawn min/max/close buttons (NtildeDrawnCaptionButton) live in the decorations layer, which is the
/// window's parent, so a key aimed at a focused one never passes the window's shortcut handler. Clicking maximize used
/// to leave it focused: every key then went to that button, and Enter with any modifiers clicked it again, so
/// Ctrl+Shift+Enter restored the window instead of toggling focus mode. Headless windows draw no decorations, so these
/// put a button with the theme in an ordinary window.
/// </summary>
public sealed class CaptionButtonFocusTests
{
    private static (Window Window, TextBox Typing, Button Caption, int[] Clicks) ShowCaptionButton()
    {
        var typing = new TextBox { Width = 200 };
        var caption = new Button
        {
            Theme = (ControlTheme)Application.Current!.FindResource("NtildeDrawnCaptionButton")!,
            Content = "□",
        };
        int[] clicks = [0];
        caption.Click += (_, _) => clicks[0]++;
        var window = new Window { Width = 400, Height = 200, Content = new StackPanel { Children = { typing, caption } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        typing.Focus();
        return (window, typing, caption, clicks);
    }

    private static void Click(Window window, Control control)
    {
        Point at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Clicking_a_caption_button_works_and_leaves_keyboard_focus_where_it_was()
    {
        var (window, typing, caption, clicks) = ShowCaptionButton();

        Click(window, caption);

        Assert.Equal(1, clicks[0]);
        Assert.False(caption.IsFocused);
        Assert.True(typing.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void Ctrl_Shift_Enter_after_clicking_a_caption_button_does_not_click_it_again()
    {
        var (window, _, caption, clicks) = ShowCaptionButton();
        Click(window, caption);

        window.KeyPress(Key.Enter, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, clicks[0]);
        window.Close();
    }
}
