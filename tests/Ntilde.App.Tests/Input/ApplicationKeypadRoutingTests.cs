using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Moq;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Input;

public sealed class ApplicationKeypadRoutingTests
{
    private sealed class KeyView : TerminalView
    {
        public bool Down(Key key, KeyModifiers modifiers = KeyModifiers.None, PhysicalKey physical = PhysicalKey.None)
        {
            var e = new KeyEventArgs { Key = key, KeyModifiers = modifiers, PhysicalKey = physical, RoutedEvent = KeyDownEvent };
            RaiseEvent(e);
            return e.Handled;
        }
        public void Up(Key key, PhysicalKey physical = PhysicalKey.None) =>
            RaiseEvent(new KeyEventArgs { Key = key, PhysicalKey = physical, RoutedEvent = KeyUpEvent });
        public void Text(string text) => RaiseEvent(new TextInputEventArgs { Text = text, RoutedEvent = TextInputEvent });
    }

    private static (KeyView View, TerminalBuffer Buffer, List<string> Sent) Create(int kittyFlags = 0, bool running = true)
    {
        var sent = new List<string>();
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(running);
        session.Setup(x => x.SendInput(It.IsAny<string>())).Callback<string>(sent.Add);
        var buffer = new TerminalBuffer(80, 24);
        new AnsiParser(buffer).Process("\u001b=");
        buffer.Modes.KittyKeyboard.Push(kittyFlags);
        var view = new KeyView();
        view.SetBuffer(buffer);
        view.SetSession(session.Object);
        return (view, buffer, sent);
    }

    [AvaloniaTheory]
    [InlineData(Key.NumPad0, "\u001bOp")]
    [InlineData(Key.NumPad1, "\u001bOq")]
    [InlineData(Key.NumPad2, "\u001bOr")]
    [InlineData(Key.NumPad3, "\u001bOs")]
    [InlineData(Key.NumPad4, "\u001bOt")]
    [InlineData(Key.NumPad5, "\u001bOu")]
    [InlineData(Key.NumPad6, "\u001bOv")]
    [InlineData(Key.NumPad7, "\u001bOw")]
    [InlineData(Key.NumPad8, "\u001bOx")]
    [InlineData(Key.NumPad9, "\u001bOy")]
    [InlineData(Key.Multiply, "\u001bOj")]
    [InlineData(Key.Add, "\u001bOk")]
    [InlineData(Key.Separator, "\u001bOl")]
    [InlineData(Key.Subtract, "\u001bOm")]
    [InlineData(Key.Decimal, "\u001bOn")]
    [InlineData(Key.Divide, "\u001bOo")]
    public void Routed_auxiliary_keys_emit_exact_frames_once_even_with_a_text_callback(Key key, string expected)
    {
        var (view, _, sent) = Create();
        Assert.True(view.Down(key));
        view.Text("native keypad text");
        Assert.Equal(expected, Assert.Single(sent));
        view.Up(key);
        view.Text("new IME commit");
        Assert.Equal(new[] { expected, "new IME commit" }, sent);
    }

    [AvaloniaTheory]
    [InlineData(PhysicalKey.None, "\r")]
    [InlineData(PhysicalKey.Enter, "\r")]
    [InlineData(PhysicalKey.NumPadEnter, "\u001bOM")]
    public void Physical_keypad_enter_is_distinct_and_keeps_enter_observer_order(PhysicalKey physical, string expected)
    {
        var (view, _, sent) = Create();
        var observed = new List<int>();
        view.EnterObserving += () => observed.Add(sent.Count);
        view.EnterObserved += () => observed.Add(sent.Count);
        Assert.True(view.Down(Key.Enter, physical: physical));
        Assert.Equal(expected, Assert.Single(sent));
        Assert.Equal(new[] { 0, 1 }, observed);
    }

    [AvaloniaTheory]
    [InlineData(Key.Separator)]
    [InlineData(Key.Decimal)]
    [InlineData(Key.OemComma)]
    public void Physical_keypad_comma_uses_the_same_auxiliary_frame(Key key)
    {
        var (view, _, sent) = Create();
        Assert.True(view.Down(key, physical: PhysicalKey.NumPadComma));
        view.Text(",");
        Assert.Equal("\u001bOl", Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Numeric_mode_and_main_keyboard_keep_plain_text_and_navigation()
    {
        var (view, buffer, sent) = Create();
        Assert.False(view.Down(Key.D7)); view.Text("7");
        Assert.True(view.Down(Key.Home, physical: PhysicalKey.NumPad7));
        new AnsiParser(buffer).Process("\u001b>");
        Assert.False(view.Down(Key.NumPad7)); view.Text("7");
        Assert.Equal(new[] { "7", "\u001b[H", "7" }, sent);
    }

    [AvaloniaTheory]
    [InlineData(KeyModifiers.Shift)]
    [InlineData(KeyModifiers.Alt)]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Meta)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Alt)]
    public void Modified_keypad_is_not_silently_encoded_as_unmodified(KeyModifiers modifiers)
    {
        var (view, buffer, sent) = Create();
        Assert.Null(TerminalInputModeEncoder.EncodeApplicationKeypad(Key.NumPad1, modifiers, PhysicalKey.NumPad1, buffer.Modes));
        Assert.False(view.Down(Key.NumPad1, modifiers, PhysicalKey.NumPad1));
        Assert.Empty(sent);
        // If a backend supplies text, it stays on the legacy TextInput path.
        // This synthetic callback makes no native layout or modifier-output claim.
        view.Text("1");
        Assert.Equal("1", Assert.Single(sent));
    }

    [AvaloniaTheory]
    [InlineData(8, "\u001b[57406u")]
    [InlineData(24, "\u001b[57406;1;55u")]
    public void Kitty_all_key_and_associated_text_keep_precedence(int flags, string expected)
    {
        var (view, _, sent) = Create(flags);
        bool handled = view.Down(Key.NumPad7, physical: PhysicalKey.NumPad7);
        Assert.Equal(flags == 8, handled);
        view.Text("7");
        Assert.Equal(expected, Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Mode_changes_after_keydown_do_not_duplicate_or_discard_the_captured_text_decision()
    {
        var (view, buffer, sent) = Create();
        view.Down(Key.NumPad7);
        new AnsiParser(buffer).Process("\u001b>");
        buffer.Modes.KittyKeyboard.Push(24);
        view.Text("7");
        Assert.Equal("\u001bOw", Assert.Single(sent));
        buffer.Modes.KittyKeyboard.Pop(1);
        Assert.False(view.Down(Key.NumPad8));
        new AnsiParser(buffer).Process("\u001b=");
        view.Text("8");
        Assert.Equal(new[] { "\u001bOw", "8" }, sent);
    }

    [AvaloniaFact]
    public void A_reentrant_text_callback_is_consumed_without_rearming_suppression()
    {
        var (view, _, sent) = Create();
        var session = new Mock<ITerminalSession>();
        session.Setup(x => x.SendInput(It.IsAny<string>())).Callback<string>(sequence =>
        {
            sent.Add(sequence);
            if (sequence == "\u001bOw") view.Text("7");
        });
        view.SetSession(session.Object);
        view.Down(Key.NumPad7);
        view.Text("new IME commit");
        Assert.Equal(new[] { "\u001bOw", "new IME commit" }, sent);
    }
    [AvaloniaTheory]
    [InlineData("release")]
    [InlineData("buffer")]
    [InlineData("session")]
    public void A_missing_text_callback_cannot_swallow_a_new_context_commit(string reset)
    {
        var (view, _, sent) = Create();
        view.Down(Key.NumPad7);
        switch (reset)
        {
            case "release": view.Up(Key.NumPad7); break;
            case "buffer": view.SetBuffer(new TerminalBuffer(80, 24)); break;
            case "session":
                var session = new Mock<ITerminalSession>();
                session.Setup(x => x.SendInput(It.IsAny<string>())).Callback<string>(sent.Add);
                view.SetSession(session.Object);
                break;
        }
        view.Text("new IME commit");
        Assert.Equal(new[] { "\u001bOw", "new IME commit" }, sent);
    }

    [AvaloniaFact]
    public void A_reentrant_new_key_owns_its_text_decision_after_the_outer_send_returns()
    {
        var (view, buffer, sent) = Create();
        var session = new Mock<ITerminalSession>();
        session.Setup(x => x.SendInput(It.IsAny<string>())).Callback<string>(sequence =>
        {
            sent.Add(sequence);
            if (sequence != "\u001bOw") return;
            new AnsiParser(buffer).Process("\u001b>");
            Assert.False(view.Down(Key.NumPad8));
        });
        view.SetSession(session.Object);
        view.Down(Key.NumPad7);
        view.Text("8");
        Assert.Equal(new[] { "\u001bOw", "8" }, sent);
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(16)]
    public void Kitty_flags_that_do_not_claim_keypad_text_keep_the_legacy_application_frame(int flags)
    {
        var (view, _, sent) = Create(flags);
        Assert.True(view.Down(Key.NumPad7));
        view.Text("7");
        view.Up(Key.NumPad7);
        Assert.Equal("\u001bOw", Assert.Single(sent));
    }

    [AvaloniaTheory]
    [InlineData(PhysicalKey.None, "\r")]
    [InlineData(PhysicalKey.NumPadEnter, "\u001bOM")]
    public void Enter_keeps_legacy_text_only_recovery_without_reserving_an_IME_commit(PhysicalKey physical, string expected)
    {
        var (view, _, sent) = Create();
        Assert.True(view.Down(Key.Enter, physical: physical));
        view.Text("new IME commit");
        Assert.Equal(new[] { expected, "new IME commit" }, sent);
    }

    [AvaloniaTheory]
    [InlineData(0, PhysicalKey.None)]
    [InlineData(0, PhysicalKey.NumPadEnter)]
    [InlineData(1, PhysicalKey.None)]
    [InlineData(1, PhysicalKey.NumPadEnter)]
    [InlineData(8, PhysicalKey.None)]
    [InlineData(8, PhysicalKey.NumPadEnter)]
    [InlineData(24, PhysicalKey.None)]
    [InlineData(24, PhysicalKey.NumPadEnter)]
    public void Enter_on_an_exited_session_stays_unhandled_for_the_panes_reconnect_gesture(int flags, PhysicalKey physical)
    {
        var (view, _, sent) = Create(flags, running: false);
        int before = 0, after = 0;
        view.EnterObserving += () => before++;
        view.EnterObserved += () => after++;
        Assert.False(view.Down(Key.Enter, physical: physical));
        Assert.Empty(sent);
        Assert.Equal((0, 0), (before, after));
    }

}
