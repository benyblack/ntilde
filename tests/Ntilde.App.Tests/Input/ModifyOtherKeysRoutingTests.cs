using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Moq;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Input;

// Issue #829: routed Avalonia key events against levels negotiated through the real parser -
// precedence against kitty and the legacy paths, the TerminalView carve-outs, Command Assist's
// interceptor, and one-shot text suppression for text-capable chords.
public sealed class ModifyOtherKeysRoutingTests
{
    private sealed class KeyView : TerminalView
    {
        public KeyEventArgs Down(Key key, KeyModifiers modifiers, PhysicalKey physical = PhysicalKey.None, string? symbol = null)
        {
            var e = new KeyEventArgs { Key = key, KeyModifiers = modifiers, PhysicalKey = physical, KeySymbol = symbol, RoutedEvent = KeyDownEvent };
            OnKeyDown(e);
            return e;
        }

        public KeyEventArgs Up(Key key, KeyModifiers modifiers, PhysicalKey physical = PhysicalKey.None)
        {
            var e = new KeyEventArgs { Key = key, KeyModifiers = modifiers, PhysicalKey = physical, RoutedEvent = KeyUpEvent };
            OnKeyUp(e);
            return e;
        }

        public void Text(string text) => OnTextInput(new TextInputEventArgs { Text = text, RoutedEvent = TextInputEvent });
    }

    private static (KeyView View, TerminalBuffer Buffer, AnsiParser Parser, Mock<ITerminalSession> Session, List<string> Sent) Create(string negotiation)
    {
        var sent = new List<string>();
        var session = new Mock<ITerminalSession>();
        session.SetupGet(s => s.IsProcessRunning).Returns(true);
        session.Setup(s => s.SendInput(It.IsAny<string>())).Callback<string>(sent.Add);
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        parser.Process(negotiation);
        var view = new KeyView();
        view.SetBuffer(buffer);
        view.SetSession(session.Object);
        return (view, buffer, parser, session, sent);
    }

    [AvaloniaTheory]
    [InlineData(Key.A, KeyModifiers.Control, "\x01")]
    [InlineData(Key.C, KeyModifiers.Control, "\x03")]
    [InlineData(Key.OemOpenBrackets, KeyModifiers.Control, "\x1b")]
    [InlineData(Key.D2, KeyModifiers.Control, "\0")]
    [InlineData(Key.Enter, KeyModifiers.Shift, "\r")]
    [InlineData(Key.Tab, KeyModifiers.Shift, "\x1b[Z")]
    [InlineData(Key.Back, KeyModifiers.Shift, "\x7f")]
    [InlineData(Key.A, KeyModifiers.Alt, "\u001ba")]
    [InlineData(Key.Left, KeyModifiers.Control, "\x1b[1;5D")]
    public void LevelZero_KeepsLegacyBytes(Key key, KeyModifiers modifiers, string expected)
    {
        var (view, _, _, _, sent) = Create("\x1b[>4;2m\x1b[>4m");
        Assert.True(view.Down(key, modifiers).Handled);
        Assert.Equal(new[] { expected }, sent);
    }

    [AvaloniaTheory]
    [InlineData(1, Key.A, KeyModifiers.Control, "\x01")]
    [InlineData(1, Key.D1, KeyModifiers.Control, "\x1b[27;5;49~")]
    [InlineData(1, Key.Enter, KeyModifiers.Shift, "\x1b[27;2;13~")]
    [InlineData(1, Key.Tab, KeyModifiers.Control, "\x1b[27;5;9~")]
    [InlineData(1, Key.A, KeyModifiers.Alt, "\u001ba")]
    [InlineData(1, Key.Back, KeyModifiers.Alt, "\x1b\x7f")]
    [InlineData(1, Key.Escape, KeyModifiers.Shift, "\x1b")]
    [InlineData(2, Key.A, KeyModifiers.Control, "\x1b[27;5;97~")]
    [InlineData(2, Key.A, KeyModifiers.Control | KeyModifiers.Shift, "\x1b[27;6;65~")]
    [InlineData(2, Key.A, KeyModifiers.Alt, "\x1b[27;3;97~")]
    [InlineData(2, Key.I, KeyModifiers.Control, "\x1b[27;5;105~")]
    [InlineData(2, Key.OemOpenBrackets, KeyModifiers.Control, "\x1b[27;5;91~")]
    [InlineData(2, Key.Enter, KeyModifiers.Control, "\x1b[27;5;13~")]
    [InlineData(2, Key.Back, KeyModifiers.Alt, "\x1b[27;3;127~")]
    [InlineData(2, Key.Escape, KeyModifiers.Alt, "\x1b[27;3;27~")]
    [InlineData(2, Key.Tab, KeyModifiers.Shift, "\x1b[Z")]
    [InlineData(2, Key.Back, KeyModifiers.Control, "\x7f")]
    [InlineData(2, Key.Left, KeyModifiers.Control, "\x1b[1;5D")]
    [InlineData(2, Key.F5, KeyModifiers.Shift, "\x1b[15;2~")]
    public void NegotiatedLevel_EncodesClaimedChords_AndLeavesTheRestLegacy(int level, Key key, KeyModifiers modifiers, string expected)
    {
        var (view, _, _, _, sent) = Create($"\x1b[>4;{level}m");
        Assert.True(view.Down(key, modifiers).Handled);
        Assert.Equal(new[] { expected }, sent);
    }

    [AvaloniaFact]
    public void KittyFlags_WinOverModifyOtherKeys()
    {
        var (view, buffer, parser, _, sent) = Create("\x1b[>4;2m\x1b[>1u");
        Assert.True(view.Down(Key.A, KeyModifiers.Control).Handled);
        Assert.True(view.Down(Key.Enter, KeyModifiers.Shift).Handled);
        Assert.Equal(new[] { "\x1b[97;5u", "\x1b[13;2u" }, sent);

        // Once kitty is popped the level that was negotiated all along takes over again.
        parser.Process("\x1b[<u");
        Assert.Equal(2, buffer.Modes.ModifyOtherKeys.Level);
        Assert.True(view.Down(Key.A, KeyModifiers.Control).Handled);
        Assert.Equal("\x1b[27;5;97~", sent[^1]);
    }

    [AvaloniaFact]
    public void TheActiveScreensLevel_DrivesEncoding()
    {
        var (view, _, parser, _, sent) = Create("\x1b[?1049h\x1b[>4;2m");
        view.Down(Key.A, KeyModifiers.Control);
        parser.Process("\x1b[?1049l");
        view.Down(Key.A, KeyModifiers.Control);
        Assert.Equal(new[] { "\x1b[27;5;97~", "\x01" }, sent);
    }

    [AvaloniaFact]
    public void CommandAssistInterceptor_StillTakesItsKeysFirst()
    {
        var (view, _, _, _, sent) = Create("\x1b[>4;2m");
        view.KeyDownInterceptor = (key, modifiers) => key == Key.Enter && modifiers == KeyModifiers.Shift;
        Assert.True(view.Down(Key.Enter, KeyModifiers.Shift).Handled);
        Assert.Empty(sent);
        Assert.True(view.Down(Key.Enter, KeyModifiers.Control).Handled);
        Assert.Equal(new[] { "\x1b[27;5;13~" }, sent);
    }

    [AvaloniaFact]
    public void EnterOnAnExitedSession_StillBubblesForReconnect()
    {
        var (view, _, _, session, sent) = Create("\x1b[>4;2m");
        session.SetupGet(s => s.IsProcessRunning).Returns(false);
        Assert.False(view.Down(Key.Enter, KeyModifiers.Shift).Handled);
        Assert.Empty(sent);
    }

    [AvaloniaFact]
    public void ShiftSpace_IsEncodedOnce_AndItsTextCallbackIsSuppressed()
    {
        var (view, _, _, _, sent) = Create("\x1b[>4;2m");
        Assert.True(view.Down(Key.Space, KeyModifiers.Shift, PhysicalKey.Space, " ").Handled);
        view.Text(" ");
        view.Up(Key.Space, KeyModifiers.Shift, PhysicalKey.Space);
        // The suppression is one-shot and ends with the key; later text is typed normally.
        view.Text("x");
        Assert.Equal(new[] { "\x1b[27;2;32~", "x" }, sent);
    }

    [AvaloniaFact]
    public void ShiftedLetters_StayOnTextInput()
    {
        var (view, _, _, _, sent) = Create("\x1b[>4;2m");
        Assert.False(view.Down(Key.A, KeyModifiers.Shift, PhysicalKey.A, "A").Handled);
        view.Text("A");
        Assert.Equal(new[] { "A" }, sent);
    }

    [AvaloniaFact]
    public void AltGr_StaysOnTextInput()
    {
        var (view, _, _, _, sent) = Create("\x1b[>4;2m");
        Assert.False(view.Down(Key.Q, KeyModifiers.Control | KeyModifiers.Alt, PhysicalKey.Q, "@").Handled);
        view.Text("@");
        Assert.Equal(new[] { "@" }, sent);
    }
}
