namespace Ntilde.Shell.Mux;

/// <summary>
/// Editor status line for a profile's remote ntilde-mux install (spec §9). Pure so the wording is
/// unit-testable without a view model.
/// </summary>
public static class RemoteMuxStatusText
{
    public static string Describe(string? installedVersion, string? appVersion)
    {
        string installed = StripBuildMetadata(installedVersion);
        if (installed.Length == 0)
        {
            return "ntilde-mux not installed";
        }

        string app = StripBuildMetadata(appVersion);
        return string.Equals(installed, app, StringComparison.Ordinal)
            ? $"ntilde-mux {installed} installed"
            : $"ntilde-mux {installed} installed — this app is {app}";
    }

    /// <summary>Drops a SemVer "+sha" build-metadata suffix, which never affects version identity.</summary>
    public static string StripBuildMetadata(string? version)
    {
        string v = version?.Trim() ?? string.Empty;
        int plus = v.IndexOf('+');
        return plus >= 0 ? v.Substring(0, plus) : v;
    }
}
