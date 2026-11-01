using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Moq;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Input;

public sealed class KittyAlternateKeyRoutingTests
{
    private sealed class KeyView : TerminalView
    {
        public KeyEventArgs Down(Key key, KeyModifiers modifiers, PhysicalKey physical, string? symbol)
        {
            var e = new KeyEventArgs { Key = key, KeyModifiers = modifiers, PhysicalKey = physical, KeySymbol = symbol, RoutedEvent = KeyDownEvent };
            OnKeyDown(e);
            return e;
        }
        public KeyEventArgs Up(Key key, KeyModifiers modifiers, PhysicalKey physical)
        {
            var e = new KeyEventArgs { Key = key, KeyModifiers = modifiers, PhysicalKey = physical, RoutedEvent = KeyUpEvent };
            OnKeyUp(e);
            return e;
        }
        public void Text(string text) => OnTextInput(new TextInputEventArgs { Text = text, RoutedEvent = TextInputEvent });
    }

    private static (KeyView View, TerminalBuffer Buffer, List<string> Sent) Create(int flags = 7)
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
    [InlineData(Key.A, KeyModifiers.Control | KeyModifiers.Shift, PhysicalKey.A, "A", "\x1b[97:65;6u")]
    [InlineData(Key.OemPlus, KeyModifiers.Control | KeyModifiers.Shift, PhysicalKey.Equal, "+", "\x1b[61:43;6u")]
    [InlineData(Key.A, KeyModifiers.Control, PhysicalKey.Q, "a", "\x1b[97::113;5u")]
    [InlineData(Key.Y, KeyModifiers.Control, PhysicalKey.Z, "y", "\x1b[121::122;5u")]
    [InlineData(Key.A, KeyModifiers.Control, PhysicalKey.A, "𐐨", "\x1b[66600::97;5u")]
    [InlineData(Key.C, KeyModifiers.Control, PhysicalKey.C, "с", "\x1b[1089::99;5u")]
    [InlineData(Key.C, KeyModifiers.Control | KeyModifiers.Shift, PhysicalKey.C, "С", "\x1b[1089:1057:99;6u")]
    [InlineData(Key.OemOpenBrackets, KeyModifiers.Control, PhysicalKey.BracketLeft, "å", "\x1b[229::91;5u")]
    public void Encoded_keys_include_available_layout_and_physical_alternates(Key key, KeyModifiers modifiers, PhysicalKey physical, string symbol, string expected)
    {
        var (view, _, sent) = Create();
        Assert.True(view.Down(key, modifiers, physical, symbol).Handled);
        Assert.Equal(expected, Assert.Single(sent));
    }

    [AvaloniaTheory]
    [InlineData(Key.S, KeyModifiers.Alt, PhysicalKey.S, "ß", "\x1b[115;3u")]
    [InlineData(Key.A, KeyModifiers.Alt | KeyModifiers.Shift, PhysicalKey.A, "Å", "\x1b[97;4u")]
    public void Alt_produced_symbols_are_not_mistaken_for_the_primary_key(Key key, KeyModifiers modifiers, PhysicalKey physical, string symbol, string expected)
    {
        var (view, _, sent) = Create();
        view.Down(key, modifiers, physical, symbol);
        Assert.Equal(expected, Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Legacy_Alt_press_preserves_alternate_identity_for_negotiated_repeat_and_release()
    {
        var (view, _, sent) = Create(6);
        view.Down(Key.A, KeyModifiers.Alt, PhysicalKey.Q, "a");
        view.Down(Key.Y, KeyModifiers.Alt, PhysicalKey.Q, "y");
        Assert.True(view.Up(Key.Y, KeyModifiers.Alt, PhysicalKey.Q).Handled);
        Assert.Equal(new[] { "\u001ba", "\x1b[97::113;3:2u", "\x1b[97::113;3:3u" }, sent);
    }

    [AvaloniaFact]
    public void Repeat_and_release_retain_the_pressed_identity_across_layout_changes()
    {
        var (view, _, sent) = Create();
        view.Down(Key.A, KeyModifiers.Control | KeyModifiers.Shift, PhysicalKey.Q, "A");
        view.Down(Key.Z, KeyModifiers.Control | KeyModifiers.Shift, PhysicalKey.Q, "Z");
        Assert.True(view.Up(Key.Z, KeyModifiers.None, PhysicalKey.Q).Handled);
        Assert.Equal(new[] { "\x1b[97:65:113;6u", "\x1b[97:65:113;6:2u", "\x1b[97::113;1:3u" }, sent);
    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("ab")]
    [InlineData("\u001b")]
    [InlineData("\ud800")]
    [InlineData("e\u0301")]
    [InlineData("\u0301")]
    [InlineData("\u0903")]
    [InlineData("\u20dd")]
    [InlineData("\u200d")]
    public void Missing_or_unusable_symbols_keep_the_known_primary_and_physical_key(string? symbol)
    {
        var (view, _, sent) = Create();
        view.Down(Key.A, KeyModifiers.Control, PhysicalKey.Q, symbol);
        Assert.Equal("\x1b[97::113;5u", Assert.Single(sent));
    }

    [AvaloniaTheory]
    [InlineData(Key.A, KeyModifiers.None, "a")]
    [InlineData(Key.A, KeyModifiers.Shift, "A")]
    [InlineData(Key.Q, KeyModifiers.Control | KeyModifiers.Alt, "@")]
    [InlineData(Key.None, KeyModifiers.None, "かな")]
    public void Text_composition_and_AltGr_remain_on_the_text_path(Key key, KeyModifiers modifiers, string text)
    {
        var (view, _, sent) = Create();
        Assert.False(view.Down(key, modifiers, PhysicalKey.Q, text).Handled);
        view.Text(text);
        Assert.False(view.Up(key, modifiers, PhysicalKey.Q).Handled);
        Assert.Equal(text, Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Flag_four_alone_does_not_enable_key_encoding()
    {
        var (view, _, sent) = Create(4);
        view.Down(Key.A, KeyModifiers.Control, PhysicalKey.Q, "a");
        Assert.False(view.Up(Key.A, KeyModifiers.Control, PhysicalKey.Q).Handled);
        Assert.Equal("\x01", Assert.Single(sent));
    }

    [AvaloniaTheory]
    [InlineData(Key.Enter, KeyModifiers.Control, "\r")]
    [InlineData(Key.Tab, KeyModifiers.Control, "\t")]
    [InlineData(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift, "\x1b[Z")]
    [InlineData(Key.Back, KeyModifiers.Control, "\x7f")]
    public void Flag_four_does_not_change_legacy_recovery_keys(Key key, KeyModifiers modifiers, string expected)
    {
        var (view, _, sent) = Create(4);
        Assert.True(view.Down(key, modifiers, PhysicalKey.None, null).Handled);
        Assert.Equal(expected, Assert.Single(sent));
    }

    [AvaloniaFact]
    public void Without_flag_four_layout_metadata_does_not_change_legacy_bytes()
    {
        var (view, _, sent) = Create(3);
        view.Down(Key.C, KeyModifiers.Control | KeyModifiers.Shift, PhysicalKey.C, "С");
        view.Up(Key.C, KeyModifiers.Control | KeyModifiers.Shift, PhysicalKey.C);
        Assert.Equal(new[] { "\x1b[99;6u", "\x1b[99;6:3u" }, sent);
    }
}
