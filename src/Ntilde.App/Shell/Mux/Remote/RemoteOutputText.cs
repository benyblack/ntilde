namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// Text a remote host printed, made fit for the install dialog's messages (Phase 4 spec §9): cut short
/// and stripped of control characters, since nothing bounds what a host or its rc files print.
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

    /// <summary>Host-supplied text, cut to <see cref="MaxLength"/> characters, with control characters dropped.</summary>
    public static string Quote(string? text)
    {
        string clean = new((text ?? string.Empty).Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= MaxLength ? clean : string.Concat(clean.AsSpan(0, MaxLength), "\u2026");
    }
}
