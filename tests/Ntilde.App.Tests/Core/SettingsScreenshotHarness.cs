using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.Controls;
using Ntilde.Settings;
using Ntilde.Shell;
using SkiaSharp;

namespace Ntilde.Tests.Core;

/// <summary>
/// Writes headless PNGs of the Settings window and the connection manager for the settings
/// redesign's before/after comparisons and PR screenshots. Does nothing unless
/// NTILDE_SETTINGS_SHOTS names an output directory, so CI never runs it.
/// </summary>
/// <remarks>
/// With NTILDE_SETTINGS_SHOTS_BASELINE also set to an earlier run's directory, every image is
/// compared pixel by pixel with the file of the same name there, and the test fails naming each
/// image that differs or that the baseline has for the same theme but the run did not produce. That is how a "no visible change" PR proves it.
/// </remarks>
public sealed class SettingsScreenshotHarness
{
    private const string OutputVariable = "NTILDE_SETTINGS_SHOTS";
    private const string BaselineVariable = "NTILDE_SETTINGS_SHOTS_BASELINE";

    [AvaloniaTheory]
    [InlineData("Default", "dark")]
    [InlineData("GitHub Light", "light")]
    public void Capture(string themeName, string slug)
    {
        string? output = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(output), $"Set {OutputVariable} to a directory to capture Settings screenshots.");
        Directory.CreateDirectory(output!);

        using var root = SettingsTestRoot.WithSettings($$"""{"ThemeName":"{{themeName}}"}""");
        root.CopyShippedTheme("GitHubLight.json");

        var written = new List<string>();
        CaptureSettings(output!, slug, written);
        CaptureConnectionManager(output!, slug, themeName, written);
        CompareWithBaseline(written, slug);
    }

    /// <summary>
    /// The Tabs &amp; title bar page with all 8 bar slots pinned (the four defaults plus Command
    /// Palette, Find, Split Vertical and Split Horizontal), so the full-bar state - the disabled
    /// "Bar" segment, the mini bar at capacity - is on record at the default size and at the minimum.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Default", "dark")]
    [InlineData("GitHub Light", "light")]
    public void CaptureFullTitleBar(string themeName, string slug)
    {
        string? output = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(output), $"Set {OutputVariable} to a directory to capture Settings screenshots.");
        Directory.CreateDirectory(output!);

        using var root = SettingsTestRoot.WithSettings(
            $$"""
            {"ThemeName":"{{themeName}}",
             "TitleBarItems":{"command_palette":"Pinned","find":"Pinned","split_vertical":"Pinned","split_horizontal":"Pinned"},
             "TitleBarOrder":["new_tab","open_tab_list","connections","settings","command_palette","find","split_vertical","split_horizontal"]}
            """);
        root.CopyShippedTheme("GitHubLight.json");

        foreach ((int width, int height, string name) in new[] { (880, 620, "TitleBarFull"), (780, 520, "Min-TitleBarFull") })
        {
            var window = new Ntilde.SettingsWindow { Width = width, Height = height };
            try
            {
                window.Show();
                window.NavigateTo(new SettingsTarget(SettingsPage.TabsAndTitleBar));
                CaptureScrolled(window, output!, $"settings-{slug}-{name}");
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static void CaptureSettings(string output, string slug, List<string> written)
    {
        var window = new Ntilde.SettingsWindow();
        try
        {
            window.Show();
            foreach (SettingsPageDescriptor descriptor in SettingsPageRegistry.Pages)
            {
                window.NavigateTo(new SettingsTarget(descriptor.Page));
                written.AddRange(CaptureScrolled(window, output, $"settings-{slug}-{descriptor.Page}"));
            }

            window.NavigateTo(new SettingsTarget(SettingsPage.Appearance));
            window.GetPage<Ntilde.Settings.Pages.AppearancePage>().FindControl<Button>("BtnEditTheme")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            written.AddRange(CaptureScrolled(window, output, $"settings-{slug}-ThemeEditor"));
        }
        finally
        {
            window.Close();
        }

        CaptureMinimumSize(output, slug, written);
    }

    /// <summary>
    /// The window at its MinWidth/MinHeight (780x520): the sidebar and the theme editor must still
    /// be reachable and fully visible there.
    /// </summary>
    private static void CaptureMinimumSize(string output, string slug, List<string> written)
    {
        var window = new Ntilde.SettingsWindow { Width = 780, Height = 520 };
        try
        {
            window.Show();
            window.NavigateTo(new SettingsTarget(SettingsPage.General));
            Dispatcher.UIThread.RunJobs();
            written.Add(Save(window, output, $"settings-{slug}-Min-General.png"));

            window.NavigateTo(new SettingsTarget(SettingsPage.Appearance));
            window.GetPage<Ntilde.Settings.Pages.AppearancePage>().FindControl<Button>("BtnEditTheme")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            written.Add(Save(window, output, $"settings-{slug}-Min-ThemeEditor.png"));
        }
        finally
        {
            window.Close();
        }
    }

    private static void CaptureConnectionManager(string output, string slug, string themeName, List<string> written)
    {
        var manager = new ConnectionManager();
        manager.ApplyTheme(new ThemeManager().GetTheme(themeName));
        var host = new Window { Width = 1080, Height = 720, Content = manager };
        try
        {
            host.Show();
            written.Add(Save(host, output, $"connections-{slug}.png"));
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>
    /// Captures the page in viewport-sized steps down its main scroller, so content below the
    /// fold is compared too.
    /// </summary>
    private static List<string> CaptureScrolled(Window window, string output, string name)
    {
        Dispatcher.UIThread.RunJobs();
        ScrollViewer? scroller = window.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .Where(sv => sv.IsEffectivelyVisible && sv.Extent.Height > sv.Viewport.Height)
            .OrderByDescending(sv => sv.Viewport.Width * sv.Viewport.Height)
            .FirstOrDefault();

        if (scroller is null)
        {
            return [Save(window, output, $"{name}.png")];
        }

        var paths = new List<string>();
        int part = 0;
        for (double y = 0; ; y += scroller.Viewport.Height)
        {
            scroller.Offset = new Vector(0, Math.Min(y, scroller.Extent.Height - scroller.Viewport.Height));
            paths.Add(Save(window, output, $"{name}-{part++}.png"));
            if (y + scroller.Viewport.Height >= scroller.Extent.Height)
            {
                break;
            }
        }

        scroller.Offset = default;
        return paths;
    }

    internal static string Save(TopLevel topLevel, string output, string fileName)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = topLevel.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string path = Path.Combine(output, fileName);
        frame!.Save(path);
        return path;
    }

    internal static void CompareWithBaseline(IReadOnlyList<string> written, string slug)
    {
        string? baseline = Environment.GetEnvironmentVariable(BaselineVariable);
        if (string.IsNullOrWhiteSpace(baseline))
        {
            return;
        }

        var differences = new List<string>();

        // A page that vanished or got shorter leaves baseline images this run never wrote;
        // comparing only what was written would let that pass. Scope to this case's theme.
        var produced = new HashSet<string>(written.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
        var expected = Directory.EnumerateFiles(baseline, $"settings-{slug}-*.png")
            .Concat(Directory.EnumerateFiles(baseline, $"connections-{slug}.png"))
            .Where(f => !Path.GetFileName(f).Contains("TitleBarFull", StringComparison.Ordinal)); // written by CaptureFullTitleBar, not by Capture
        foreach (string missing in expected.Select(Path.GetFileName).Order(StringComparer.Ordinal))
        {
            if (!produced.Contains(missing!))
            {
                differences.Add($"{missing}: in the baseline but not produced by this run");
            }
        }

        foreach (string path in written)
        {
            string name = Path.GetFileName(path);
            string before = Path.Combine(baseline, name);
            if (!File.Exists(before))
            {
                differences.Add($"{name}: no baseline image");
                continue;
            }

            using SKBitmap a = SKBitmap.Decode(before);
            using SKBitmap b = SKBitmap.Decode(path);
            if (a.Width != b.Width || a.Height != b.Height)
            {
                differences.Add($"{name}: size {a.Width}x{a.Height} -> {b.Width}x{b.Height}");
                continue;
            }

            int changed = 0;
            for (int y = 0; y < a.Height; y++)
            {
                for (int x = 0; x < a.Width; x++)
                {
                    if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    {
                        changed++;
                    }
                }
            }

            if (changed > 0)
            {
                differences.Add($"{name}: {changed} pixels differ");
            }
        }

        Assert.True(differences.Count == 0, "Screenshots differ from the baseline:\n" + string.Join("\n", differences));
    }
}
