namespace Ntilde.Shell.Mux;

/// <summary>Parses <see cref="TerminalSettings.SessionPersistence"/> (spec §7).</summary>
internal static class SessionPersistenceMode
{
    public const string Off = "Off";
    public const string KeepOnClose = "KeepOnClose";

    /// <summary>Trimmed, ordinal-ignore-case; anything else (a typo included) is off.</summary>
    public static bool IsKeepOnClose(string? value) =>
        value is not null && string.Equals(value.Trim(), KeepOnClose, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the settings file at <paramref name="settingsPath"/> turns persistence off, as loading it would decide:
    /// the <c>SessionPersistence</c> property (case-sensitive, the last one if repeated) when present - a null, a typo or
    /// "Off" is off - and <see cref="TerminalSettings.DefaultSessionPersistence"/> (on) when absent. For the startup update
    /// gate (Phase 5 Task 22), before anything else is up: one file read and a <see cref="System.Text.Json.JsonDocument"/>
    /// over it - no reflection (NativeAOT), none of <see cref="TerminalSettings.Load"/>'s repairs or migrations. A missing
    /// file is the defaults; a file that cannot be read or parsed, or holds a value of another kind, is not evidence of
    /// "off" either. Never throws.
    /// </summary>
    public static bool IsOffInSettingsFile(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return false;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(settingsPath));
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !document.RootElement.TryGetProperty(nameof(TerminalSettings.SessionPersistence), out System.Text.Json.JsonElement value))
            {
                return false;
            }

            return value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Null => true,
                System.Text.Json.JsonValueKind.String => !IsKeepOnClose(value.GetString()),
                _ => false,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
