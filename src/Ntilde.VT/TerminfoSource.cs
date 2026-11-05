using System.Globalization;
using System.Text;

namespace Ntilde.VT;

/// <summary>
/// Generates the opt-in <c>xterm-ntilde</c> terminfo source from <see cref="TerminalCapabilities"/>
/// (#844). The entry is <c>xterm-256color</c> plus every table capability whose
/// <see cref="TerminalCapability.TerminfoOmission"/> is null, so it cannot claim more than
/// XTGETTCAP does. The checked-in copy at <see cref="RepositoryPath"/> is pinned to this output by
/// <c>TerminfoSourceTests</c>, which also compiles it with <c>tic -x</c> and reads it back with
/// <c>infocmp -x</c>.
/// </summary>
public static class TerminfoSource
{
    /// <summary>The terminal name a session sets in <c>TERM</c> to use the entry.</summary>
    public const string EntryName = "xterm-ntilde";

    /// <summary>The entry everything not in the table is inherited from.</summary>
    public const string BaseEntry = "xterm-256color";

    /// <summary>Where the generated source is checked in, relative to the repository root.</summary>
    public const string RepositoryPath = "packaging/terminfo/xterm-ntilde.terminfo";

    /// <summary>The table entries the terminfo entry writes, in table order.</summary>
    public static IEnumerable<TerminalCapability> Capabilities
        => TerminalCapabilities.All.Where(capability => capability.TerminfoOmission is null);

    /// <summary>The complete source text, with LF line endings, as <c>tic -x</c> reads it.</summary>
    public static string Generate()
    {
        var source = new StringBuilder();
        source.Append("# ").Append(EntryName).Append(": opt-in terminfo entry for Ntilde.\n");
        source.Append("#\n");
        source.Append("# Generated from src/Ntilde.VT/TerminalCapabilities.cs, the table XTGETTCAP answers from.\n");
        source.Append("# Do not edit by hand: change the table and regenerate (see packaging/terminfo/README.md).\n");
        source.Append("# Install: tic -x -o ~/.terminfo ").Append(Path.GetFileName(RepositoryPath)).Append('\n');
        source.Append(EntryName).Append("|Ntilde terminal emulator,\n");
        foreach (TerminalCapability capability in Capabilities)
        {
            source.Append('\t').Append(capability.Name);
            switch (capability.Kind)
            {
                case TerminalCapabilityKind.Number:
                    source.Append('#').Append(int.Parse(capability.Value, NumberStyles.None, CultureInfo.InvariantCulture));
                    break;
                case TerminalCapabilityKind.Text:
                    source.Append('=').Append(Escape(capability.Value));
                    break;
            }
            source.Append(",\n");
        }
        source.Append("\tuse=").Append(BaseEntry).Append(",\n");
        return source.ToString();
    }

    /// <summary>
    /// Writes a string value in terminfo source syntax: ESC as <c>\E</c>, other C0 controls and DEL
    /// in caret form, and the characters terminfo treats specially (<c>, : ^ \</c>) escaped.
    /// </summary>
    public static string Escape(string value)
    {
        var escaped = new StringBuilder(value.Length + 8);
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '\u001b': escaped.Append("\\E"); break;
                case '\u007f': escaped.Append("^?"); break;
                case ',' or ':' or '^' or '\\': escaped.Append('\\').Append(ch); break;
                case < ' ': escaped.Append('^').Append((char)(ch + '@')); break;
                case > '~': throw new ArgumentException($"Terminfo values here are ASCII; got U+{(int)ch:X4}.", nameof(value));
                default: escaped.Append(ch); break;
            }
        }
        return escaped.ToString();
    }
}
