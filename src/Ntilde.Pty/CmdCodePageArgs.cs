using System;

namespace Ntilde.Pty;

/// <summary>
/// Adds <c>chcp 65001</c> to a <c>cmd.exe</c> argument line without breaking the user's own
/// <c>/k</c> or <c>/c</c> command (#348).
/// </summary>
/// <remarks>
/// The merge used to be <c>"/k chcp 65001 " + args</c>. cmd treats everything after its first
/// <c>/c</c>, <c>/k</c> or <c>/r</c> as one command string, so a profile's own
/// <c>/k mycmd</c> became an argument to chcp ("Parameter format not correct"), and a
/// <c>/c</c> profile that should run and exit stayed open.
///
/// cmd finds that switch anywhere on the line, glued to other switches or to the command
/// (<c>/d/c</c>, <c>/cdir</c>, <c>foo /c dir</c> all work), so this scans for it the same way
/// rather than matching a leading token.
///
/// The command string after the switch goes through cmd's quote rules (<c>cmd /?</c>): a string
/// that opens with a quote may have its first and last quote stripped, depending among other
/// things on whether the quoted text names an executable, and cmd also probes space-separated
/// path prefixes on its own. Those rules can't be replayed faithfully from here, so the
/// codepage switch only goes where it provably leaves the user's command as cmd would read it:
/// <list type="bullet">
/// <item>in front, <c>chcp 65001 &amp; command</c>, when the command doesn't open with a quote.
/// Neither rule changes a string that doesn't open with one, before or after the prefix;</item>
/// <item>behind, <c>command &amp; chcp 65001</c>, for a quote-opened <c>/k</c> command. The
/// suffix holds no quote, so which quotes cmd strips is unchanged, and the interactive session
/// that follows still gets UTF-8;</item>
/// <item>nowhere for a quote-opened <c>/c</c> or <c>/r</c> command: a suffix would replace the
/// command's exit code with chcp's, so it runs as written.</item>
/// </list>
/// <c>&amp;</c> rather than <c>&amp;&amp;</c> so a failed chcp can't stop the user's command,
/// and because it binds loosest, a user's <c>a || b</c> stays one unit.
/// </remarks>
internal static class CmdCodePageArgs
{
    private const string CodePageCommand = "chcp 65001";

    public static string Merge(string? args)
    {
        string line = args?.Trim() ?? "";
        int switchAt = FindCommandSwitch(line);
        if (switchAt < 0)
        {
            // Only switches (or nothing): the codepage command becomes the session's /k
            // command. Switches stay in front of it, where cmd reads them as switches.
            return line.Length == 0 ? "/k " + CodePageCommand : line + " /k " + CodePageCommand;
        }

        string head = line.Substring(0, switchAt + 2);
        string command = line.Substring(switchAt + 2).TrimStart();
        if (command.Length == 0)
        {
            return head + " " + CodePageCommand;
        }

        if (command[0] != '"')
        {
            return head + " " + CodePageCommand + " & " + command;
        }

        bool keepsSessionOpen = char.ToLowerInvariant(line[switchAt + 1]) == 'k';
        if (keepsSessionOpen && CountQuotes(command) % 2 == 0)
        {
            return head + " " + command + " & " + CodePageCommand;
        }

        return line;
    }

    /// The index of the '/' of cmd's first /c, /k or /r switch, or -1.
    private static int FindCommandSwitch(string line)
    {
        for (int i = 0; i < line.Length - 1; i++)
        {
            if (line[i] != '/')
            {
                continue;
            }

            char c = char.ToLowerInvariant(line[i + 1]);
            if (c is 'c' or 'k' or 'r')
            {
                return i;
            }
        }

        return -1;
    }

    private static int CountQuotes(string s)
    {
        int count = 0;
        foreach (char c in s)
        {
            if (c == '"')
            {
                count++;
            }
        }

        return count;
    }
}
