using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Moq;
using Ntilde.Platform;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Input
{
    /// <summary>
    /// Legacy (non-kitty) encoding of Ctrl+punctuation and Ctrl+digit as C0 control bytes, as
    /// xterm (Xlib's XLookupString control mapping), Windows Terminal and VTE send them.
    /// Before this, only Ctrl+A..Z had a legacy encoding: Ctrl+\, Ctrl+], Ctrl+4, Ctrl+6 and the
    /// rest were swallowed, because Avalonia drops the control character Windows puts in WM_CHAR.
    /// </summary>
    public class LegacyControlKeyEncodingTests
    {
        public static TheoryData<Key, KeyModifiers, string> C0Table => new()
        {
            // NUL: Ctrl+@ (Ctrl+Shift+2), Ctrl+Space, Ctrl+2
            { Key.D2, KeyModifiers.Control | KeyModifiers.Shift, "\x00" },
            { Key.Space, KeyModifiers.Control, "\x00" },
            { Key.D2, KeyModifiers.Control, "\x00" },
            // ESC: Ctrl+[, Ctrl+3
            { Key.OemOpenBrackets, KeyModifiers.Control, "\x1b" },
            { Key.D3, KeyModifiers.Control, "\x1b" },
            // FS: Ctrl+\, Ctrl+4 (both backslash VKs, as GetUnshiftedCodepoint maps them)
            { Key.OemPipe, KeyModifiers.Control, "\x1c" },
            { Key.OemBackslash, KeyModifiers.Control, "\x1c" },
            { Key.D4, KeyModifiers.Control, "\x1c" },
            // GS: Ctrl+], Ctrl+5
            { Key.OemCloseBrackets, KeyModifiers.Control, "\x1d" },
            { Key.D5, KeyModifiers.Control, "\x1d" },
            // RS: Ctrl+^ (Ctrl+Shift+6), Ctrl+6
            { Key.D6, KeyModifiers.Control | KeyModifiers.Shift, "\x1e" },
            { Key.D6, KeyModifiers.Control, "\x1e" },
            // US: Ctrl+_ (Ctrl+Shift+-), Ctrl+7, Ctrl+/
            { Key.OemMinus, KeyModifiers.Control | KeyModifiers.Shift, "\x1f" },
            { Key.D7, KeyModifiers.Control, "\x1f" },
            { Key.OemQuestion, KeyModifiers.Control, "\x1f" },
            // DEL: Ctrl+8
            { Key.D8, KeyModifiers.Control, "\x7f" },
        };

        [Theory]
        [MemberData(nameof(C0Table))]
        public void EncodeLegacyControlKey_MapsTheXtermC0Table(Key key, KeyModifiers modifiers, string expected)
        {
            Assert.Equal(expected, TerminalInputModeEncoder.EncodeLegacyControlKey(key, modifiers));
        }

        [Theory]
        [InlineData(Key.A, "\x01")]
        [InlineData(Key.C, "\x03")]
        [InlineData(Key.Z, "\x1a")]
        public void EncodeLegacyControlKey_LettersKeepTheirExistingBytes(Key key, string expected)
        {
            Assert.Equal(expected, TerminalInputModeEncoder.EncodeLegacyControlKey(key, KeyModifiers.Control));
        }

        [Theory]
        // AltGr arrives as Ctrl+Alt on Windows and composes real text (AltGr+8 -> '[' and
        // AltGr+9 -> ']' on German, AltGr+ß -> '\'); it must fall through to TextInput.
        [InlineData(Key.D8, KeyModifiers.Control | KeyModifiers.Alt)]
        [InlineData(Key.D9, KeyModifiers.Control | KeyModifiers.Alt)]
        [InlineData(Key.OemPipe, KeyModifiers.Control | KeyModifiers.Alt)]
        [InlineData(Key.OemCloseBrackets, KeyModifiers.Control | KeyModifiers.Alt)]
        [InlineData(Key.D2, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift)]
        [InlineData(Key.A, KeyModifiers.Control | KeyModifiers.Alt)]
        // Meta/Super chords are not control characters.
        [InlineData(Key.OemPipe, KeyModifiers.Control | KeyModifiers.Meta)]
        // No Ctrl, no control character.
        [InlineData(Key.OemPipe, KeyModifiers.None)]
        [InlineData(Key.D4, KeyModifiers.Shift)]
        [InlineData(Key.Space, KeyModifiers.None)]
        // Shift only reaches C0 where the shifted glyph is the C0 name (@ ^ _); Ctrl+Shift+letters
        // stay app shortcuts, and Ctrl+Shift+3 ('#') has no control character.
        [InlineData(Key.D3, KeyModifiers.Control | KeyModifiers.Shift)]
        [InlineData(Key.OemPipe, KeyModifiers.Control | KeyModifiers.Shift)]
        [InlineData(Key.A, KeyModifiers.Control | KeyModifiers.Shift)]
        // Keys outside the table.
        [InlineData(Key.D1, KeyModifiers.Control)]
        [InlineData(Key.D9, KeyModifiers.Control)]
        [InlineData(Key.D0, KeyModifiers.Control)]
        [InlineData(Key.OemMinus, KeyModifiers.Control)]
        [InlineData(Key.OemPlus, KeyModifiers.Control)]
        [InlineData(Key.OemComma, KeyModifiers.Control)]
        [InlineData(Key.NumPad2, KeyModifiers.Control)]
        public void EncodeLegacyControlKey_ReturnsNullOutsideTheTable(Key key, KeyModifiers modifiers)
        {
            Assert.Null(TerminalInputModeEncoder.EncodeLegacyControlKey(key, modifiers));
        }

        private static (TerminalView View, Mock<ITerminalSession> Session) CreateView(TerminalBuffer? buffer = null)
        {
            var session = new Mock<ITerminalSession>();
            session.SetupGet(x => x.IsProcessRunning).Returns(true);
            var view = new TerminalView();
            view.SetBuffer(buffer ?? new TerminalBuffer(80, 24));
            view.SetSession(session.Object);
            view.ApplySettings(new TerminalSettings());
            return (view, session);
        }

        [AvaloniaTheory]
        [MemberData(nameof(C0Table))]
        public void HandleKeyDownCore_LegacyMode_SendsTheC0Byte(Key key, KeyModifiers modifiers, string expected)
        {
            var (view, session) = CreateView();

            Assert.True(view.HandleKeyDownCore(key, modifiers));

            session.Verify(x => x.SendInput(expected), Times.Once);
        }

        [AvaloniaFact]
        public void HandleKeyDownCore_AltGrChord_IsLeftForTextInput()
        {
            var (view, session) = CreateView();

            Assert.False(view.HandleKeyDownCore(Key.OemCloseBrackets, KeyModifiers.Control | KeyModifiers.Alt));
            Assert.False(view.HandleKeyDownCore(Key.D8, KeyModifiers.Control | KeyModifiers.Alt));

            session.Verify(x => x.SendInput(It.IsAny<string>()), Times.Never);
        }

        [AvaloniaFact]
        public void HandleKeyDownCore_KittyDisambiguate_StillWinsOverC0()
        {
            var buffer = new TerminalBuffer(80, 24);
            buffer.Modes.KittyKeyboard.Push(KittyKeyboardState.FlagDisambiguateEscapeCodes);
            var (view, session) = CreateView(buffer);

            Assert.True(view.HandleKeyDownCore(Key.OemPipe, KeyModifiers.Control));
            Assert.True(view.HandleKeyDownCore(Key.D6, KeyModifiers.Control | KeyModifiers.Shift));
            Assert.True(view.HandleKeyDownCore(Key.Space, KeyModifiers.Control));

            session.Verify(x => x.SendInput("\x1b[92;5u"), Times.Once);
            session.Verify(x => x.SendInput("\x1b[54;6u"), Times.Once);
            session.Verify(x => x.SendInput("\x1b[32;5u"), Times.Once);
            session.Verify(x => x.SendInput("\x1c"), Times.Never);
            session.Verify(x => x.SendInput("\x1e"), Times.Never);
            session.Verify(x => x.SendInput("\x00"), Times.Never);
        }
    }
}
