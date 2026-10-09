namespace Ntilde.Update;

/// <summary>
/// <c>NTILDE_UPDATE_SOURCE_DIR</c> (Phase 5 Task 24): a verification hook that points the updater at a local Velopack feed
/// (a directory holding <c>releases.&lt;channel&gt;.json</c> and its packages, as <c>vpk pack</c> writes them) instead of this
/// repository's GitHub releases. <c>scripts/mux-update-survival.ps1</c> and <c>.sh</c> use it to apply a real update to a
/// sandboxed install without publishing anything. It works only for an install packed under
/// <see cref="VerificationAppId"/>; every other install - the released one included - ignores it.
/// </summary>
/// <remarks>
/// An allow-list, not a deny-list (ruling R1): a list of ids to refuse would fail open the next time the released app's
/// packId is renamed, as it has been once already (NovaTerminalApp to NtildeApp). The released app is packed as
/// <c>NtildeApp</c>, so for it the variable changes nothing and the feed's origin stays the compiled-in
/// <see cref="VelopackUpdateService.DefaultRepoUrl"/>. It is read from the process environment only, never from
/// settings.json or any other file. Whether it is used or ignored is logged at startup and again when the updater is built,
/// by the one <see cref="Resolve(string?, string?, out string?)"/>, so the two lines always agree. No Velopack type is
/// named here: <c>Program.Main</c> reads it on the startup path and hands in the app id.
/// </remarks>
internal static class UpdateSourceOverride
{
    /// <summary>The environment variable.</summary>
    public const string Variable = "NTILDE_UPDATE_SOURCE_DIR";

    /// <summary>
    /// The only Velopack app id (packId) that honours <see cref="Variable"/>: the update-survival scripts' sandboxed
    /// installs. Compared ordinally. A test pins that both scripts pack under it.
    /// </summary>
    public const string VerificationAppId = "NtildeSurvival";

    /// <summary>
    /// The full path of the local feed directory <paramref name="value"/> names, or null when updates come from GitHub:
    /// the variable unset (null); this install not the verification one (<paramref name="appId"/>, the running install's
    /// Velopack app id, null when not installed); or a value that is blank or names no existing directory.
    /// <paramref name="note"/> is the line to log - which source is used and why - and null only when the variable is unset.
    /// </summary>
    public static string? Resolve(string? value, string? appId, out string? note)
    {
        if (value is null)
        {
            note = null;
            return null;
        }

        if (!string.Equals(appId, VerificationAppId, StringComparison.Ordinal))
        {
            note = $"Update source: {Variable} is set but ignored: this install is not a verification install"
                + $" (app id {(appId is null ? "none" : $"'{appId}'")}, not '{VerificationAppId}'); updates come from GitHub.";
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

    /// <summary><see cref="Resolve(string?, string?, out string?)"/> over this process's environment.</summary>
    public static string? ResolveFromEnvironment(string? appId, out string? note) =>
        Resolve(Environment.GetEnvironmentVariable(Variable), appId, out note);
}
