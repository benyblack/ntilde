using System.Diagnostics;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Input;

/// <summary>
/// Hover and Ctrl+click on links in a real <see cref="TerminalView"/>, with the view's
/// <see cref="TerminalView.LinkOpener"/> swapped for one over a recording environment so nothing is
/// probed or launched. The attack these pin: program output shows text that looks like a web link
/// but whose OSC 8 target is a local program or a remote SMB share.
/// </summary>
/// <remarks>
/// <see cref="TerminalView.UpdateHoveredLink"/> and <see cref="TerminalView.TryActivateLinkAt"/> are
/// the calls OnPointerMoved and OnPointerPressed make, position in, so the pixel-to-cell conversion
/// and the OSC 8 / detected-URL lookup under test are the shipping ones.
/// </remarks>
public sealed class TerminalViewLinkActivationTests
{
    private const float CellWidth = 8f;
    private const float CellHeight = 16f;

    // Ctrl on Windows and Linux, Cmd on macOS; carrying both keeps the tests platform-neutral.
    private const KeyModifiers LinkModifier = KeyModifiers.Control | KeyModifiers.Meta;

    private sealed class RecordingEnvironment : ILinkLaunchEnvironment
    {
        public bool IsWindows => true;

        public bool IsMacOS => false;

        public IReadOnlyCollection<string> LocalHostNames { get; } = new[] { "DEVBOX", "devbox" };

        public HashSet<string> Files { get; } = new();

        public List<string> Probed { get; } = new();

        public List<ProcessStartInfo> Started { get; } = new();

        public bool FileExists(string path)
        {
            Probed.Add(path);
            return Files.Contains(path);
        }

        public bool DirectoryExists(string path)
        {
            Probed.Add(path);
            return false;
        }

        // Windows routes never look anything up on PATH.
        public string? FindOnPath(string executableName) => null;

        public void Start(ProcessStartInfo startInfo) => Started.Add(startInfo);
    }

    private static Point CellCentre(int row, int column) =>
        new((column * CellWidth) + (CellWidth / 2), (row * CellHeight) + (CellHeight / 2));

    private static (TerminalView View, RecordingEnvironment Env) CreateView(string output)
    {
        var buffer = new TerminalBuffer(80, 24);
        new AnsiParser(buffer).Process(output);

        var env = new RecordingEnvironment();
        var view = new TerminalView { LinkOpener = new TerminalLinkOpener(env) };
        view.SetBuffer(buffer);
        view.SetMetricsForTest(CellWidth, CellHeight);
        return (view, env);
    }

    private static string Osc8(string target, string text) => $"\u001b]8;;{target}\u0007{text}\u001b]8;;\u0007";

    [AvaloniaTheory]
    [InlineData("file://attacker/share/x.lnk")]
    [InlineData("file:///%5C%5Cattacker%5Cshare%5Cx.lnk")]
    public void Osc8_link_disguised_as_web_link_but_targeting_a_UNC_share_is_inert(string target)
    {
        var (view, env) = CreateView(Osc8(target, "https://github.com/org/repo"));

        view.UpdateHoveredLink(CellCentre(0, 3));
        Assert.Null(view.HoveredLinkUriForTest);

        Assert.False(view.TryActivateLinkAt(CellCentre(0, 3), LinkModifier));
        Assert.Empty(env.Started);
        Assert.Empty(env.Probed);
    }

    [AvaloniaFact]
    public void Osc8_link_to_a_local_program_reveals_it_instead_of_running_it()
    {
        var (view, env) = CreateView(Osc8("file:///C:/Users/me/Downloads/evil.exe", "https://github.com/org/repo"));
        env.Files.Add(@"C:\Users\me\Downloads\evil.exe");

        view.UpdateHoveredLink(CellCentre(0, 3));
        Assert.Equal("file:///C:/Users/me/Downloads/evil.exe", view.HoveredLinkUriForTest);

        Assert.True(view.TryActivateLinkAt(CellCentre(0, 3), LinkModifier));

        ProcessStartInfo started = Assert.Single(env.Started);
        Assert.False(started.UseShellExecute);
        Assert.Equal(@"/select,""C:\Users\me\Downloads\evil.exe""", started.Arguments);
    }

    [AvaloniaFact]
    public void Osc8_web_link_still_opens_through_the_OS_URL_handler()
    {
        var (view, env) = CreateView(Osc8("https://example.com/docs", "docs"));

        view.UpdateHoveredLink(CellCentre(0, 1));
        Assert.Equal("https://example.com/docs", view.HoveredLinkUriForTest);

        Assert.True(view.TryActivateLinkAt(CellCentre(0, 1), LinkModifier));

        ProcessStartInfo started = Assert.Single(env.Started);
        Assert.True(started.UseShellExecute);
        Assert.Equal("https://example.com/docs", started.FileName);
    }

    /// <summary>The URL detector matches any <c>scheme://</c> in plain text, file ones included.</summary>
    [AvaloniaFact]
    public void Detected_plain_text_UNC_file_url_is_inert()
    {
        var (view, env) = CreateView("see file://attacker/share/x.lnk now");

        view.UpdateHoveredLink(CellCentre(0, 10));
        Assert.Null(view.HoveredLinkUriForTest);

        Assert.False(view.TryActivateLinkAt(CellCentre(0, 10), LinkModifier));
        Assert.Empty(env.Started);
        Assert.Empty(env.Probed);
    }

    [AvaloniaFact]
    public void Detected_plain_text_web_url_still_underlines_and_opens()
    {
        var (view, env) = CreateView("see https://example.com now");

        view.UpdateHoveredLink(CellCentre(0, 10));
        Assert.Equal("https://example.com", view.HoveredLinkUriForTest);

        Assert.True(view.TryActivateLinkAt(CellCentre(0, 10), LinkModifier));
        Assert.True(Assert.Single(env.Started).UseShellExecute);
    }

    /// <summary>
    /// The <c>ls --hyperlink</c> shape, <c>file://$HOSTNAME/path</c>, naming this machine: clickable and
    /// revealed. A different hostname (a remote session's) stays inert.
    /// </summary>
    [AvaloniaFact]
    public void Osc8_link_under_this_machines_hostname_is_revealed_and_another_host_is_inert()
    {
        var (view, env) = CreateView(
            Osc8("file://DevBox/C:/Users/me/notes.txt", "notes.txt") + "\r\n" +
            Osc8("file://buildhost/C:/Users/me/notes.txt", "notes.txt"));
        env.Files.Add(@"C:\Users\me\notes.txt");

        view.UpdateHoveredLink(CellCentre(0, 2));
        Assert.Equal("file://DevBox/C:/Users/me/notes.txt", view.HoveredLinkUriForTest);
        Assert.True(view.TryActivateLinkAt(CellCentre(0, 2), LinkModifier));
        Assert.Equal(@"/select,""C:\Users\me\notes.txt""", Assert.Single(env.Started).Arguments);

        view.UpdateHoveredLink(CellCentre(1, 2));
        Assert.Null(view.HoveredLinkUriForTest);
        Assert.False(view.TryActivateLinkAt(CellCentre(1, 2), LinkModifier));
        Assert.Single(env.Started);
    }

    [AvaloniaFact]
    public void Click_without_the_link_modifier_opens_nothing()
    {
        var (view, env) = CreateView(Osc8("https://example.com", "docs"));

        Assert.False(view.TryActivateLinkAt(CellCentre(0, 1), KeyModifiers.None));
        Assert.Empty(env.Started);
    }
}
