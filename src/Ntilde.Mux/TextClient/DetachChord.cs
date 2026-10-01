using System.Text;

namespace Ntilde.Mux.TextClient;

/// <summary>
/// Ctrl+\ then d detaches, and so does Ctrl+\ then Ctrl+D: users keep Ctrl held, and GNU screen
/// takes both <c>C-a d</c> and <c>C-a C-d</c>. Ctrl+\ Ctrl+\ sends one literal Ctrl+\; Ctrl+\ then
/// anything else sends both, so nothing typed is lost (spec §6.4). The state survives across reads.
/// </summary>
/// <remarks>
/// <para>
/// The price of the Ctrl+D form: a literal Ctrl+\ followed by Ctrl+D cannot be sent through the
/// client (Ctrl+\ Ctrl+\ then Ctrl+D still can).
/// </para>
/// <para>
/// It sees characters, not keystrokes: pasted text that contains <c>\x1c</c> followed by <c>d</c>
/// or <c>\x04</c> detaches too. Telling a paste apart (bracketed-paste markers) is deliberately not
/// attempted.
/// </para>
/// </remarks>
public sealed class DetachChord
{
    public const char Prefix = '\u001c';

    /// <summary>Ctrl+D: the d with Ctrl still held.</summary>
    private const char CtrlD = '\u0004';

    private bool _afterPrefix;

    /// <summary>Appends what should reach the session; true when the chord completed (input after it is discarded).</summary>
    public bool Feed(ReadOnlySpan<char> input, StringBuilder passThrough)
    {
        ArgumentNullException.ThrowIfNull(passThrough);
        foreach (char c in input)
        {
            if (_afterPrefix)
            {
                _afterPrefix = false;
                if (c is 'd' or 'D' or CtrlD) return true;
                passThrough.Append(Prefix);
                if (c != Prefix) passThrough.Append(c);
                continue;
            }

            if (c == Prefix)
            {
                _afterPrefix = true;
                continue;
            }

            passThrough.Append(c);
        }

        return false;
    }
}
