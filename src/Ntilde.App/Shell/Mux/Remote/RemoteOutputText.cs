using System.Globalization;
using System.Text;

namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// Text a remote host printed, made fit for the install dialog's messages (Phase 4 spec §9) and for toasts:
/// cut short and stripped of control characters, since nothing bounds what a host or its rc files print.
/// </summary>
internal static class RemoteOutputText
{
    /// <summary>The most characters <see cref="Quote"/> keeps.</summary>
    public const int MaxLength = 200;

    /// <summary>The last <paramref name="count"/> non-blank lines of <paramref name="text"/>, joined with " / " and <see cref="Quote"/>d.</summary>
    public static string LastLines(string? text, int count = 3)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string[] lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        return Quote(string.Join(" / ", lines.Skip(Math.Max(0, lines.Length - count))));
    }

    /// <summary>
    /// True for a character that must not reach a message or a command line: a control character, or a Unicode
    /// format (a bidi override or isolate spoofs what the user reads; U+200E/F, U+202A-E, U+2066-9, U+FEFF), line
    /// or paragraph separator one. Judged on a <see cref="Rune"/>, so one above U+FFFF counts too.
    /// </summary>
    public static bool IsHidden(Rune rune) =>
        Rune.IsControl(rune)
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    /// <summary>
    /// <paramref name="text"/> without <see cref="IsHidden"/> characters and unpaired surrogates (which enumerate as U+FFFD).
    /// </summary>
    public static string Clean(string? text)
    {
        var clean = new StringBuilder();
        foreach (Rune rune in (text ?? string.Empty).EnumerateRunes())
        {
            if (rune == Rune.ReplacementChar || IsHidden(rune)) continue;
            clean.Append(rune.ToString());
        }

        return clean.ToString();
    }

    /// <summary>The first <paramref name="max"/> characters of a <see cref="Clean"/> string, never cutting a surrogate pair in two.</summary>
    public static string Cut(string clean, int max)
    {
        if (clean.Length <= max) return clean;
        int cut = char.IsHighSurrogate(clean[max - 1]) ? max - 1 : max;
        return clean[..cut];
    }

    /// <summary>Host-supplied text, <see cref="Clean"/>ed and cut to <see cref="MaxLength"/> characters (then an ellipsis).</summary>
    public static string Quote(string? text)
    {
        string clean = Clean(text);
        return clean.Length <= MaxLength ? clean : Cut(clean, MaxLength) + "…";
    }
}
