using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Moq;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Input;

public sealed class KittyAllKeyRoutingTests
{
    private sealed class KeyView : TerminalView
    {
        public KeyEventArgs Down(Key key, KeyModifiers modifiers = KeyModifiers.None, PhysicalKey physical = PhysicalKey.None, string? symbol = null)
        {
            var e = new KeyEventArgs { Key = key, KeyModifiers = modifiers, PhysicalKey = physical, KeySymbol = symbol, RoutedEvent = KeyDownEvent };
            OnKeyDown(e);
            return e;
        }
        public KeyEventArgs Up(Key key, KeyModifiers modifiers = KeyModifiers.None, PhysicalKey physical = PhysicalKey.None)
        {
            var e = new KeyEventArgs { Key = key, KeyModifiers = modifiers, PhysicalKey = physical, RoutedEvent = KeyUpEvent };
            OnKeyUp(e);
            return e;
        }
        public void Text(string text) => OnTextInput(new TextInputEventArgs { Text = text, RoutedEvent = TextInputEvent });
        public void LoseFocus() => OnLostFocus(new FocusChangedEventArgs(LostFocusEvent));
    }

    private static (KeyView View, TerminalBuffer Buffer, List<string> Sent) Create(int flags = 8)
    {
        var sent = new List<string>();
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(true);
        session.Setup(x => x.SendInput(It.IsAny<string>())).Callback<string>(sent.Add);
        var buffer = new TerminalBuffer(80, 24);
        buffer.Modes.KittyKeyboard.Push(flags);
        var view = new KeyView();
        view.SetBuffer(buffer);
        view.SetSession(session.Object);
        return (view, buffer, sent);
    }

    [AvaloniaTheory]
    [InlineData(Key.A, KeyModifiers.None, "a", "\u001b[97u")]
    [InlineData(Key.A, KeyModifiers.Shift, "A", "\u001b[97;2u")]
    [InlineData(Key.A, KeyModifiers.Control, null, "\u001b[97;5u")]
    [InlineData(Key.Q, KeyModifiers.Control | KeyModifiers.Alt, "@", "\u001b[113;7u")]
    [InlineData(Key.C, KeyModifiers.None, "с", "\u001b[1089u")]
    [InlineData(Key.None, KeyModifiers.None, "あ", "\u001b[12354u")]
    [InlineData(Key.None, KeyModifiers.None, "𐐨", "\u001b[66600u")]
    [InlineData(Key.Enter, KeyModifiers.None, null, "\u001b[13u")]
    [InlineData(Key.Tab, KeyModifiers.None, null, "\u001b[9u")]
    [InlineData(Key.Back, KeyModifiers.None, null, "\u001b[127u")]
    [InlineData(Key.Escape, KeyModifiers.None, null, "\u001b[27u")]
    [InlineData(Key.Up, KeyModifiers.None, null, "\u001b[A")]
    [InlineData(Key.F3, KeyModifiers.None, null, "\u001b[13~")]
    [InlineData(Key.LeftShift, KeyModifiers.Shift, null, "\u001b[57441;2u")]
    [InlineData(Key.RightShift, KeyModifiers.Shift, null, "\u001b[57447;2u")]
    [InlineData(Key.LeftCtrl, KeyModifiers.Control, null, "\u001b[57442;5u")]
    [InlineData(Key.RightCtrl, KeyModifiers.Control, null, "\u001b[57448;5u")]
    [InlineData(Key.LeftAlt, KeyModifiers.Alt, null, "\u001b[57443;3u")]
    [InlineData(Key.RightAlt, KeyModifiers.Alt | KeyModifiers.Control, null, "\u001b[57449;7u")]
    [InlineData(Key.LWin, KeyModifiers.Meta, null, "\u001b[57444;9u")]
    [InlineData(Key.RWin, KeyModifiers.Meta, null, "\u001b[57450;9u")]
    [InlineData(Key.NumPad0, KeyModifiers.None, null, "\u001b[57399u")]
    [InlineData(Key.NumPad9, KeyModifiers.None, null, "\u001b[57408u")]
    [InlineData(Key.Add, KeyModifiers.None, null, "\u001b[57413u")]
    [InlineData(Key.MediaPlayPause, KeyModifiers.None, null, "\u001b[57430u")]
    [InlineData(Key.VolumeMute, KeyModifiers.None, null, "\u001b[57440u")]
    public void All_key_flag_alone_replaces_legacy_and_text_encodings(Key key, KeyModifiers modifiers, string? symbol, string expected)
    {
        var (view, _, sent) = Create();
        Assert.True(view.Down(key, modifiers, symbol: symbol).Handled);
        Assert.Equal(expected, Assert.Single(sent));
        Assert.False(view.Up(key, modifiers).Handled);
    }

    [AvaloniaTheory]
    [InlineData(Key.Enter, PhysicalKey.NumPadEnter, "\u001b[57414u")]
    [InlineData(Key.OemPlus, PhysicalKey.NumPadEqual, "\u001b[57415u")]
    [InlineData(Key.Home, PhysicalKey.NumPad7, "\u001b[57423u")]
    [InlineData(Key.Up, PhysicalKey.NumPad8, "\u001b[57419u")]
    [InlineData(Key.Clear, PhysicalKey.NumPad5, "\u001b[E")]
    [InlineData(Key.Delete, PhysicalKey.NumPadDecimal, "\u001b[57426u")]
    [InlineData(Key.Clear, PhysicalKey.NumPadClear, "\u001b[E")]
    [InlineData(Key.OemComma, PhysicalKey.NumPadComma, "\u001b[57416u")]
    public void Keypad_physical_identity_is_distinct_from_the_main_keyboard(Key key, PhysicalKey physical, string expected)
    {
        var (view, _, sent) = Create();
        Assert.True(view.Down(key, physical: physical).Handled);
        Assert.Equal(expected, Assert.Single(sent));
    }

    [AvaloniaTheory]
    [InlineData(Key.ImeProcessed)]
    [InlineData(Key.DeadCharProcessed)]
    public void Composition_markers_are_not_physical_key_presses(Key key)
    {
        var (view, _, sent) = Create();
        Assert.False(view.Down(key, symbol: "a").Handled);
        view.Text("á");
        Assert.Empty(sent);
    }

    [AvaloniaFact]
    public void Composition_marker_in_a_held_physical_slot_is_not_a_repeat()
    {
        var (view, _, sent) = Create(10);
        view.Down(Key.A, physical: PhysicalKey.A, symbol: "a");
        Assert.False(view.Down(Key.ImeProcessed, physical: PhysicalKey.A, symbol: "a").Handled);
        Assert.Equal("\u001b[97u", Assert.Single(sent));
    }

    [AvaloniaFact]
    public void All_key_text_callbacks_do_not_emit_raw_or_duplicate_input()
    {
        var (view, _, sent) = Create();
        int observed = 0;
        view.TextInputObserved += _ => observed++;
        view.Down(Key.A, symbol: "a");
        view.Text("a");
        view.Text("かな"); // Text-only IME commits carry no reported key identity in flag 8.
        Assert.Equal("\u001b[97u", Assert.Single(sent));
        Assert.Equal(0, observed);
    }

    [AvaloniaFact]
    public void Without_event_reporting_repeats_are_presses_and_releases_are_silent()
    {
        var (view, _, sent) = Create();
        view.Down(Key.A);
        view.Down(Key.A);
        Assert.False(view.Up(Key.A).Handled);
        Assert.Equal(new[] { "\u001b[97u", "\u001b[97u" }, sent);
    }

    [AvaloniaFact]
    public void Event_reporting_retains_layout_identity_even_without_alternate_fields()
    {
        var (view, _, sent) = Create(10);
        view.Down(Key.C, physical: PhysicalKey.C, symbol: "с");
        view.Down(Key.Z, physical: PhysicalKey.C, symbol: "z");
        Assert.True(view.Up(Key.Z, physical: PhysicalKey.C).Handled);
        Assert.Equal(new[] { "\u001b[1089u", "\u001b[1089;1:2u", "\u001b[1089;1:3u" }, sent);
    }

    [AvaloniaFact]
    public void Unknown_keys_without_physical_identity_do_not_share_a_release_or_repeat_slot()
    {
        var (view, _, sent) = Create(10);
        view.Down(Key.None, symbol: "あ");
        view.Down(Key.None, symbol: "い");
        Assert.False(view.Up(Key.None).Handled);
        Assert.Equal(new[] { "\u001b[12354u", "\u001b[12356u" }, sent);
    }

    [AvaloniaFact]
    public void All_key_mode_can_enrich_ordinary_keys_with_optional_alternates()
    {
        var (view, _, sent) = Create(14);
        view.Down(Key.C, KeyModifiers.Shift, PhysicalKey.C, "С");
        view.Up(Key.C, KeyModifiers.None, PhysicalKey.C);
        Assert.Equal(new[] { "\u001b[1089:1057:99;2u", "\u001b[1089::99;1:3u" }, sent);
    }

    [AvaloniaFact]
    public void Modifier_release_uses_current_modifiers_when_the_other_side_remains_held()
    {
        var (view, _, sent) = Create(10);
        view.Down(Key.LeftCtrl, KeyModifiers.Control, PhysicalKey.ControlLeft);
        view.Down(Key.RightCtrl, KeyModifiers.Control, PhysicalKey.ControlRight);
        view.Up(Key.LeftCtrl, KeyModifiers.Control, PhysicalKey.ControlLeft);
        view.Up(Key.RightCtrl, KeyModifiers.None, PhysicalKey.ControlRight);
        Assert.Equal(new[] { "\u001b[57442;5u", "\u001b[57448;5u", "\u001b[57442;5:3u", "\u001b[57448;1:3u" }, sent);
    }

    [AvaloniaTheory]
    [InlineData(Key.Enter, 13)]
    [InlineData(Key.Tab, 9)]
    [InlineData(Key.Back, 127)]
    public void All_key_recovery_keys_have_paired_releases(Key key, int code)
    {
        var (view, _, sent) = Create(10);
        view.Down(key);
        view.Down(key);
        Assert.True(view.Up(key).Handled);
        Assert.Equal(new[] { $"\u001b[{code}u", $"\u001b[{code};1:2u", $"\u001b[{code};1:3u" }, sent);
    }

    [AvaloniaFact]
    public void Keypad_enter_retains_its_identity_through_recovery_observers()
    {
        var (view, _, sent) = Create(10);
        view.Down(Key.Enter, physical: PhysicalKey.NumPadEnter);
        view.Down(Key.Enter, physical: PhysicalKey.NumPadEnter);
        Assert.True(view.Up(Key.Enter, physical: PhysicalKey.NumPadEnter).Handled);
        Assert.Equal(new[] { "\u001b[57414u", "\u001b[57414;1:2u", "\u001b[57414;1:3u" }, sent);
    }

    [AvaloniaFact]
    public void Text_and_recovery_observers_keep_the_existing_input_order_contract()
    {
        var (view, _, sent) = Create(10);
        var observations = new List<string>();
        view.EnterObserving += () => observations.Add($"before:{sent.Count}");
        view.EnterObserved += () => observations.Add($"after:{sent.Count}");
        int back = 0;
        view.BackspaceObserved += () => back++;
        view.Down(Key.Enter);
        view.Down(Key.Enter);
        view.Up(Key.Enter);
        view.Down(Key.Back);
        view.Down(Key.Back);
        view.Up(Key.Back);
        Assert.Equal(new[] { "before:0", "after:1", "before:1", "after:2" }, observations);
        Assert.Equal(2, back);
    }

    [AvaloniaFact]
    public void Capability_downgrade_and_kill_switch_restore_legacy_text()
    {
        var (view, buffer, sent) = Create(15);
        buffer.Modes.KittyKeyboard.SetSupportedFlags(7);
        Assert.False(view.Down(Key.A, symbol: "a").Handled);
        view.Text("a");
        buffer.Modes.KittyKeyboard.SetSupportedFlags(15);
        Assert.True(view.Down(Key.A, symbol: "a").Handled);
        view.ApplySettings(new TerminalSettings { EnableKittyKeyboardProtocol = false });
        Assert.False(view.Up(Key.A).Handled);
        Assert.False(view.Down(Key.A, symbol: "a").Handled);
        view.Text("a");
        Assert.Equal(new[] { "a", "\u001b[97u", "a" }, sent);
    }

    [AvaloniaFact]
    public void Focus_loss_and_mode_reset_discard_all_key_releases()
    {
        var (view, buffer, sent) = Create(10);
        view.Down(Key.A);
        view.LoseFocus();
        Assert.False(view.Up(Key.A).Handled);
        view.Down(Key.A);
        buffer.Modes.KittyKeyboard.Pop(1);
        buffer.Modes.KittyKeyboard.Push(10);
        Assert.False(view.Up(Key.A).Handled);
        Assert.Equal(new[] { "\u001b[97u", "\u001b[97u" }, sent);
    }
}
