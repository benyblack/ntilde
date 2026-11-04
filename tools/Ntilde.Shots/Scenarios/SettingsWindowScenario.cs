using Ntilde.Settings;

namespace Ntilde.Shots.Scenarios;

/// <summary>
/// Shared open/assert-page/close scaffolding for every scenario that opens
/// <see cref="SettingsWindow"/> on a specific page. Only the page, the window size and what to do
/// once the page is confirmed open differ between callers, so those are the only things left to
/// them.
/// </summary>
internal static class SettingsWindowScenario
{
    /// <summary>
    /// Opens <see cref="SettingsWindow"/> on <paramref name="page"/>, asserts the window actually
    /// landed there - failing loudly rather than silently capturing whichever page the sidebar
    /// defaulted to - then runs <paramref name="body"/> and always closes the window afterwards,
    /// success or failure.
    /// </summary>
    public static Task RunAsync(
        ShotContext context,
        SettingsPage page,
        double width,
        double height,
        Action<ShotContext, SettingsWindow> body)
    {
        var settingsWindow = new SettingsWindow(new SettingsTarget(page))
        {
            Width = width,
            Height = height
        };

        // Show/Pump run inside the try, not before it: Pump runs Dispatcher.UIThread.RunJobs(),
        // which executes work tied to the window just shown, so a throw there would otherwise
        // leak a second live Window past this method with nothing left to close it.
        try
        {
            settingsWindow.Show();
            context.Driver.Pump(5);

            if (settingsWindow.CurrentPage != page)
            {
                throw new InvalidOperationException(
                    $"Expected SettingsWindow to open on {page}, but it shows {settingsWindow.CurrentPage}. " +
                    "SettingsTarget navigation changed - update the scenario rather than capturing the wrong page.");
            }

            body(context, settingsWindow);
        }
        finally
        {
            settingsWindow.Close();
            context.Driver.Pump(3);
        }

        return Task.CompletedTask;
    }
}
