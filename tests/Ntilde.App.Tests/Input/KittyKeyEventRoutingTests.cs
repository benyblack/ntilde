using System;
using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Moq;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Input;

public sealed class KittyKeyEventRoutingTests
{
    private sealed class KeyView : TerminalView
    {
        public KeyEventArgs Down(Key key, KeyModifiers modifiers, PhysicalKey physical = PhysicalKey.None, bool handled = false)
        {
            var e = new KeyEventArgs { Key = key, KeyModifiers = modifiers, PhysicalKey = physical, RoutedEvent = KeyDownEvent, Handled = handled };
            OnKeyDown(e);
            return e;
        }

        public KeyEventArgs Up(Key key, KeyModifiers modifiers, PhysicalKey physical = PhysicalKey.None, bool handled = false)
        {
            var e = new KeyEventArgs { Key = key, KeyModifiers = modifiers, PhysicalKey = physical, RoutedEvent = KeyUpEvent, Handled = handled };
            OnKeyUp(e);
            return e;
        }

        public void Text(string text) => OnTextInput(new TextInputEventArgs { Text = text, RoutedEvent = TextInputEvent });
        public void LoseFocus() => OnLostFocus(new FocusChangedEventArgs(LostFocusEvent));
    }

    private static (KeyView View, TerminalBuffer Buffer, Mock<ITerminalSession> Session, List<string> Sent) Create(int flags = 3)
    {
        var sent = new List<string>();
        var session = new Mock<ITerminalSession>();
        session.SetupGet(s => s.IsProcessRunning).Returns(true);
        session.Setup(s => s.SendInput(It.IsAny<string>())).Callback<string>(sent.Add);
        var buffer = new TerminalBuffer(80, 24);
        buffer.Modes.KittyKeyboard.Push(flags);
        var view = new KeyView();
        view.SetBuffer(buffer);
        view.SetSession(session.Object);
        return (view, buffer, session, sent);
    }

    [AvaloniaFact]
    public void RoutedEvents_ProducePressRepeatRelease_AndIgnoreDuplicateRelease()
    {
        var (view, _, _, sent) = Create();
        Assert.True(view.Down(Key.C, KeyModifiers.Control, PhysicalKey.C).Handled);
        Assert.True(view.Down(Key.C, KeyModifiers.Control, PhysicalKey.C).Handled);
        Assert.True(view.Up(Key.C, KeyModifiers.Control, PhysicalKey.C).Handled);
        Assert.False(view.Up(Key.C, KeyModifiers.Control, PhysicalKey.C).Handled);
        Assert.Equal(new[] { "\x1b[99;5u", "\x1b[99;5:2u", "\x1b[99;5:3u" }, sent);
    }

    [AvaloniaTheory]
    [InlineData(Key.A, KeyModifiers.None, "a")]
    [InlineData(Key.A, KeyModifiers.Shift, "A")]
    [InlineData(Key.Q, KeyModifiers.Control | KeyModifiers.Alt, "@")]
    public void TextAndAltGr_DoNotAcquireReleaseReports(Key key, KeyModifiers modifiers, string text)
    {
        var (view, _, _, sent) = Create();
        Assert.False(view.Down(key, modifiers).Handled);
        view.Text(text);
        Assert.False(view.Down(key, modifiers).Handled);
        view.Text(text);
        Assert.False(view.Up(key, modifiers).Handled);
        Assert.Equal(new[] { text, text }, sent);
    }

    [AvaloniaFact]
    public void EventReportingOff_RemainsByteIdenticalAndHasNoReleases()
    {
        var (view, _, _, sent) = Create(1);
        view.Down(Key.C, KeyModifiers.Control);
        view.Down(Key.C, KeyModifiers.Control);
        Assert.False(view.Up(Key.C, KeyModifiers.Control).Handled);
        Assert.Equal(new[] { "\x1b[99;5u", "\x1b[99;5u" }, sent);
    }

    [AvaloniaTheory]
    [InlineData(Key.Enter, 13)]
    [InlineData(Key.Tab, 9)]
    [InlineData(Key.Back, 127)]
    public void ModifiedRecoveryKeys_ReportRepeatsButNoRelease(Key key, int code)
    {
        var (view, _, _, sent) = Create();
        view.Down(key, KeyModifiers.Control);
        view.Down(key, KeyModifiers.Control);
        Assert.False(view.Up(key, KeyModifiers.Control).Handled);
        Assert.Equal(new[] { $"\x1b[{code};5u", $"\x1b[{code};5:2u" }, sent);
    }

    [AvaloniaFact]
    public void Release_UsesCurrentModifiersAndThePressedPhysicalKeyIdentity()
    {
        var (view, _, _, sent) = Create();
        view.Down(Key.C, KeyModifiers.Control, PhysicalKey.C);
        // The layout/modifier state may have changed before the physical key comes up.
        Assert.True(view.Up(Key.A, KeyModifiers.None, PhysicalKey.C).Handled);
        Assert.Equal(new[] { "\x1b[99;5u", "\x1b[99;1:3u" }, sent);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void AppOrAssistConsumedPress_DoesNotLeakARelease(bool appShortcut)
    {
        var (view, _, _, sent) = Create();
        if (!appShortcut) view.KeyDownInterceptor = (_, _) => true;
        Assert.True(view.Down(Key.Up, KeyModifiers.None, handled: appShortcut).Handled);
        Assert.False(view.Up(Key.Up, KeyModifiers.None).Handled);
        Assert.Empty(sent);
    }

    [AvaloniaFact]
    public void ClipboardCopy_DoesNotLeakARelease()
    {
        var (view, _, _, sent) = Create();
        view.UseMacOSClipboardChords = true;
        Assert.True(view.Down(Key.C, KeyModifiers.Meta).Handled);
        Assert.False(view.Up(Key.C, KeyModifiers.Meta).Handled);
        Assert.Empty(sent);
    }

    [AvaloniaTheory]
    [InlineData("focus")]
    [InlineData("session")]
    [InlineData("buffer")]
    [InlineData("settings")]
    [InlineData("stack")]
    [InlineData("screen")]
    public void InputContextChanges_DiscardOldReleasesAndResetRepeatDetection(string change)
    {
        var (view, buffer, session, sent) = Create();
        view.Down(Key.C, KeyModifiers.Control);
        switch (change)
        {
            case "focus": view.LoseFocus(); break;
            case "session": view.SetSession(null); view.SetSession(session.Object); break;
            case "buffer": view.SetBuffer(new TerminalBuffer(80, 24)); view.SetBuffer(buffer); break;
            case "settings":
                view.ApplySettings(new TerminalSettings { EnableKittyKeyboardProtocol = false });
                view.ApplySettings(new TerminalSettings { EnableKittyKeyboardProtocol = true });
                break;
            case "stack": buffer.Modes.KittyKeyboard.Pop(1); buffer.Modes.KittyKeyboard.Push(3); break;
            case "screen": buffer.Modes.KittyKeyboard.SetActiveScreen(true); buffer.Modes.KittyKeyboard.SetActiveScreen(false); break;
        }
        Assert.False(view.Up(Key.C, KeyModifiers.Control).Handled);
        view.Down(Key.C, KeyModifiers.Control);
        Assert.Equal(new[] { "\x1b[99;5u", "\x1b[99;5u" }, sent);
    }

    [AvaloniaFact]
    public void NoOpSetAndPop_PreserveAnExistingReportedKey()
    {
        var (view, buffer, _, sent) = Create();
        view.Down(Key.C, KeyModifiers.Control);
        buffer.Modes.KittyKeyboard.Set(3, 1);
        buffer.Modes.KittyKeyboard.Pop(0);
        Assert.True(view.Up(Key.C, KeyModifiers.Control).Handled);
        Assert.Equal(new[] { "\x1b[99;5u", "\x1b[99;5:3u" }, sent);
    }

    [AvaloniaFact]
    public void EventFlagAlone_PreservesLegacyControlPressAndReportsItsRepeatAndRelease()
    {
        var (view, _, _, sent) = Create(2);
        view.Down(Key.C, KeyModifiers.Control);
        view.Down(Key.C, KeyModifiers.Control);
        view.Up(Key.C, KeyModifiers.Control);
        Assert.Equal(new[] { "\x03", "\x1b[99;5:2u", "\x1b[99;5:3u" }, sent);
    }

    [AvaloniaFact]
    public void UnpairedOrConsumedKeyUp_SendsNothingAndClearsTracking()
    {
        var (view, _, _, sent) = Create();
        Assert.False(view.Up(Key.Up, KeyModifiers.None).Handled);
        view.Down(Key.C, KeyModifiers.Control);
        view.Up(Key.C, KeyModifiers.Control, handled: true);
        view.Down(Key.C, KeyModifiers.Control);
        Assert.Equal(new[] { "\x1b[99;5u", "\x1b[99;5u" }, sent);
    }
}
