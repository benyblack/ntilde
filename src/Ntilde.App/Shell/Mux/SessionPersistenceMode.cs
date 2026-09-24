namespace Ntilde.Shell.Mux;

/// <summary>Parses <see cref="TerminalSettings.SessionPersistence"/> (spec §7).</summary>
internal static class SessionPersistenceMode
{
    public const string Off = "Off";
    public const string KeepOnClose = "KeepOnClose";

    /// <summary>Trimmed, ordinal-ignore-case; anything else (a typo included) is off.</summary>
    public static bool IsKeepOnClose(string? value) =>
        value is not null && string.Equals(value.Trim(), KeepOnClose, StringComparison.OrdinalIgnoreCase);
}
