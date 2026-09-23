using System;
using Ntilde.Pty;

namespace Ntilde.Platform.Input
{
    public static class TerminalInputSender
    {
        private const string BracketedPasteStart = "\x1b[200~";
        private const string BracketedPasteEnd = "\x1b[201~";

        // ESC, and the 8-bit C1 CSI that stands for "ESC [" in a single character.
        private const string Escape = "\u001b";
        private const string C1ControlSequenceIntroducer = "\u009b";

        public static string PreparePaste(string content, bool bracketedPasteModeEnabled)
        {
            if (string.IsNullOrEmpty(content))
            {
                return string.Empty;
            }

            if (!bracketedPasteModeEnabled)
            {
                return content.Replace("\r\n", "\r");
            }

            // Pasted text must not be able to end the paste block early: everything after an
            // early terminator reaches the shell as typed input, so a copied snippet could run
            // commands. Removing the terminator's spelling does not work, because removing it
            // can assemble a new one ("ESC[20" + "ESC[201~" + "1~" becomes "ESC[201~"), and
            // "\u009B201~" is another spelling of it. Instead remove the characters every
            // spelling has to start with. Deleting a single character can never create another
            // occurrence of it, so one pass leaves no ESC or C1 CSI and therefore no terminator,
            // however it is nested or spelled. Pasted escape sequences are lost with them; they
            // have no business inside a paste block anyway.
            //
            // Sanitize before normalizing line endings, so that "CR ESC LF", which is a CRLF once
            // the ESC is gone, still ends up as a single CR.
            string safeContent = content
                .Replace(Escape, string.Empty)
                .Replace(C1ControlSequenceIntroducer, string.Empty)
                .Replace("\r\n", "\r");
            return BracketedPasteStart + safeContent + BracketedPasteEnd;
        }

        public static void SendBracketedPaste(ITerminalSession session, string content)
        {
            if (session == null || string.IsNullOrEmpty(content))
            {
                return;
            }

            session.SendInput(PreparePaste(content, bracketedPasteModeEnabled: true));
        }
    }
}
