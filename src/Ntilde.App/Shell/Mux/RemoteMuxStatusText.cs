namespace Ntilde.Shell.Mux;

/// <summary>
/// Editor status line for a profile's remote ntilde-mux install (spec §9). Pure so the wording is
/// unit-testable without a view model.
/// </summary>
public static class RemoteMuxStatusText
{
    public static string Describe(string? installedVersion, string? appVersion)
    {
        // A "+sha" build-metadata suffix never affects version identity.
        string installed = AppVersionInfo.WithoutBuildMetadata(installedVersion);
        if (installed.Length == 0)
        {
            return "ntilde-mux not installed";
        }

        string app = AppVersionInfo.WithoutBuildMetadata(appVersion);
        return app.Length == 0 || string.Equals(installed, app, StringComparison.Ordinal)
            ? $"ntilde-mux {installed} installed"
            : $"ntilde-mux {installed} installed \u2014 this app is {app}";
    }
}
