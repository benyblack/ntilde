using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ntilde.Shell;
using Ntilde.UI.About;

namespace Ntilde.Tests.Core;

/// <summary>
/// The About window reads its version through <see cref="AppVersionInfo"/> and shows exactly what it
/// showed when it read the entry assembly's attribute itself: the informational version as built.
/// </summary>
public sealed class AboutWindowVersionTests
{
    [AvaloniaFact]
    public void The_version_line_is_the_entry_assembly_s_informational_version()
    {
        string expected = "Version " + (
            Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? "unknown");
        var window = new AboutWindow();
        try
        {
            Assert.Equal(expected, window.FindControl<TextBlock>("VersionText")!.Text);
            Assert.Equal("Version " + (AppVersionInfo.InformationalVersion ?? "unknown"), expected);
        }
        finally
        {
            window.Close();
        }
    }
}
