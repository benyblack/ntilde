namespace Ntilde.Update;

/// <summary>
/// <c>NTILDE_UPDATE_SOURCE_DIR</c> (Phase 5 Task 24): a verification hook that points the updater at a local Velopack feed
/// (a directory holding <c>releases.&lt;channel&gt;.json</c> and its packages, as <c>vpk pack</c> writes them) instead of this
/// repository's GitHub releases. <c>scripts/mux-update-survival.ps1</c> uses it to apply a real update to a sandboxed
/// install without publishing anything. A value that names no directory is ignored, and updates come from GitHub.
/// </summary>
/// <remarks>
/// It is read from the process environment only, never from settings.json or any other file, so it does not make the
/// feed's origin a setting (see <see cref="VelopackUpdateService.DefaultRepoUrl"/>). Whoever can set a user's environment
/// can already replace the binaries in that user's own install folder, so it grants nothing new. It is logged at startup
/// and again when the updater is built, so a debug log always says where updates come from. No Velopack type is named
/// here: <c>Program.Main</c> reads it on the startup path.
/// </remarks>
internal static class UpdateSourceOverride
{
    /// <summary>The environment variable.</summary>
    public const string Variable = "NTILDE_UPDATE_SOURCE_DIR";

    /// <summary>
    /// The full path of the local feed directory <paramref name="value"/> names, or null when updates come from GitHub:
    /// the variable unset (null), blank, or not naming an existing directory. <paramref name="note"/> is the line to log -
    /// which source is used and why - and null only when the variable is unset.
    /// </summary>
    public static string? Resolve(string? value, out string? note)
    {
        if (value is null)
        {
            note = null;
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            note = $"Update source: {Variable} is set but blank, so it is ignored; updates come from GitHub.";
            return null;
        }

        string? full = null;
        try
        {
            full = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a usable path: reported below as naming no directory.
        }

        if (full is null || !Directory.Exists(full))
        {
            note = $"Update source: {Variable} is '{value}', which is not a directory, so it is ignored; updates come from GitHub.";
            return null;
        }

        note = $"Update source: the local directory {full} ({Variable}, a verification hook), not GitHub.";
        return full;
    }

    /// <summary><see cref="Resolve(string?, out string?)"/> over this process's environment.</summary>
    public static string? Resolve(out string? note) => Resolve(Environment.GetEnvironmentVariable(Variable), out note);
}
