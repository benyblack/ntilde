namespace Ntilde.Shell.Mux;

/// <summary>
/// Editor status line for a profile's remote ntilde-mux install (spec §9). Pure so the wording is
/// unit-testable without a view model.
/// </summary>
public static class RemoteMuxStatusText
{
    /// <summary>
    /// What a persistent remote tab does not have (Phase 5 spec R8), under "Keep remote sessions running" in the connection
    /// editor (written out in its XAML, which a layout test holds to this) and in the install dialog.
    /// </summary>
    public const string PersistentTabLimits =
        "Persistent tabs do not run this connection's port forwards. With the OpenSSH backend they also have no Remote Files sidebar (transfers from the command palette work).";

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
