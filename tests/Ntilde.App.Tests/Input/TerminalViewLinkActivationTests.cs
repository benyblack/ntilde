using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Ntilde.Shell;
using Ntilde.VT;
using Ntilde.VT.Links;

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

    /// <summary>
    /// #95 gap 3: an OSC 8 link underlined only the cell under the pointer, while a URL found by the detector
    /// underlined its whole span. The whole anchor underlines now, from any cell of it.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(11)]
    public void Hovering_any_cell_of_an_Osc8_link_underlines_the_whole_link(int hoveredCol)
    {
        var (view, _) = CreateView("see " + Osc8("https://example.com/docs", "the docs") + " now");

        view.UpdateHoveredLink(CellCentre(0, hoveredCol));

        Assert.Equal("https://example.com/docs", view.HoveredLinkUriForTest);
        Assert.Equal([new HyperlinkRun(0, 4, 11)], view.HoveredLinkRunsForTest);
    }

    [AvaloniaFact]
    public void Adjacent_Osc8_links_to_the_same_target_underline_separately()
    {
        // Equal URIs are not one link: without an id, each OSC 8 run is its own anchor.
        var (view, _) = CreateView(Osc8("https://example.com", "one") + Osc8("https://example.com", "two"));

        view.UpdateHoveredLink(CellCentre(0, 1));
        Assert.Equal([new HyperlinkRun(0, 0, 2)], view.HoveredLinkRunsForTest);

        view.UpdateHoveredLink(CellCentre(0, 4));
        Assert.Equal([new HyperlinkRun(0, 3, 5)], view.HoveredLinkRunsForTest);
    }

    /// <summary>
    /// #95 gap 3, the rest of it: the parts an <c>id</c> joins are one link, so hovering either underlines both.
    /// The overlay used to hold one run, and only the part under the pointer underlined.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(4)]
    public void An_Osc8_id_joins_runs_and_every_run_underlines(int hoveredCol)
    {
        var (view, _) = CreateView(
            "\u001b]8;id=a;https://example.com\u0007ab\u001b]8;;\u0007--\u001b]8;id=a;https://example.com\u0007cd\u001b]8;;\u0007");

        view.UpdateHoveredLink(CellCentre(0, hoveredCol));

        Assert.Equal([new HyperlinkRun(0, 0, 1), new HyperlinkRun(0, 4, 5)], view.HoveredLinkRunsForTest);
    }

    /// <summary>A link too long for the rest of its row wraps; it underlines on both rows, from either.</summary>
    [AvaloniaTheory]
    [InlineData(0, 77)]
    [InlineData(1, 2)]
    public void A_wrapped_Osc8_link_underlines_on_every_row_it_covers(int hoveredRow, int hoveredCol)
    {
        var (view, _) = CreateView(new string(' ', 75) + Osc8("https://example.com/docs", "the-docs!!"));

        view.UpdateHoveredLink(CellCentre(hoveredRow, hoveredCol));

        Assert.Equal([new HyperlinkRun(0, 75, 79), new HyperlinkRun(1, 0, 4)], view.HoveredLinkRunsForTest);
    }

    /// <summary>
    /// Every pointer move asks for the hovered link, so moving within one anchor on an unchanged screen must reuse its runs
    /// rather than rescan every visible row under the buffer's lock each time (Codex review of #632). Another anchor, or a
    /// screen that moved, is looked up again.
    /// </summary>
    [AvaloniaFact]
    public void Moving_within_one_Osc8_link_does_not_rescan_the_screen()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        parser.Process(Osc8("https://example.com/one", "first link") + " " + Osc8("https://example.com/two", "second"));
        var view = new TerminalView { LinkOpener = new TerminalLinkOpener(new RecordingEnvironment()) };
        view.SetBuffer(buffer);
        view.SetMetricsForTest(CellWidth, CellHeight);

        view.UpdateHoveredLink(CellCentre(0, 1));
        view.UpdateHoveredLink(CellCentre(0, 5));
        view.UpdateHoveredLink(CellCentre(0, 9));
        Assert.Equal(1, view.Osc8ScansForTest);

        view.UpdateHoveredLink(CellCentre(0, 12));
        Assert.Equal("https://example.com/two", view.HoveredLinkUriForTest);
        Assert.Equal(2, view.Osc8ScansForTest);

        // Output that scrolls the screen moves the link under the pointer: what is there now is looked up again.
        parser.Process("\r\n" + string.Concat(Enumerable.Repeat("filler\r\n", 30)));
        parser.Process(Osc8("https://example.com/one", "first link"));
        view.UpdateHoveredLink(CellCentre(23, 1));
        Assert.Equal(3, view.Osc8ScansForTest);
    }

    /// <summary>
    /// Codex's case on #632: output that overwrites part of an <c>id</c>-joined link leaves the rest of it the same anchor on
    /// the same screen. The runs found before the write must not be reused for it: the overwritten cells no longer underline.
    /// </summary>
    [AvaloniaFact]
    public void Output_over_part_of_a_hovered_link_is_seen_on_the_next_move()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        parser.Process("\u001b]8;id=a;https://example.com\u0007abcdef\u001b]8;;\u0007");
        var view = new TerminalView { LinkOpener = new TerminalLinkOpener(new RecordingEnvironment()) };
        view.SetBuffer(buffer);
        view.SetMetricsForTest(CellWidth, CellHeight);

        view.UpdateHoveredLink(CellCentre(0, 1));
        Assert.Equal([new HyperlinkRun(0, 0, 5)], view.HoveredLinkRunsForTest);

        parser.Process("\u001b[1;3HXXXX"); // columns 2-5 rewritten as plain text
        view.UpdateHoveredLink(CellCentre(0, 0));

        Assert.Equal([new HyperlinkRun(0, 0, 1)], view.HoveredLinkRunsForTest);
    }

    /// <summary>What Render draws for the wrapped link: a 1-pixel bar under each run, on its own row.</summary>
    [AvaloniaFact]
    public void A_wrapped_Osc8_link_is_drawn_underlined_on_both_rows()
    {
        var (view, _) = CreateView(new string(' ', 75) + Osc8("https://example.com/docs", "the-docs!!"));

        view.UpdateHoveredLink(CellCentre(1, 2));

        Assert.Equal(
            [new Rect(75 * CellWidth, CellHeight - 1, 5 * CellWidth, 1), new Rect(0, (2 * CellHeight) - 1, 5 * CellWidth, 1)],
            view.HoveredLinkUnderlines(displayStart: 0, rows: 24));
        // A run on a row scrolled out of view is not drawn.
        Assert.Equal([new Rect(0, CellHeight - 1, 5 * CellWidth, 1)], view.HoveredLinkUnderlines(displayStart: 1, rows: 24));
    }

    /// <summary>
    /// #95 gap 5: a hovered link says where it leads before it is followed - the attack these tests pin is text that looks
    /// like one link with a target that is another - and how to follow it. Leaving it takes the tip away.
    /// </summary>
    [AvaloniaFact]
    public void A_hovered_link_shows_its_target_in_a_tooltip_until_the_pointer_leaves_it()
    {
        var (view, _) = CreateView(Osc8("https://evil.example/login", "https://github.com/org/repo") + " plain text");

        view.UpdateHoveredLink(CellCentre(0, 3));
        string? tip = Avalonia.Controls.ToolTip.GetTip(view) as string;
        Assert.NotNull(tip);
        Assert.StartsWith("https://evil.example/login", tip, StringComparison.Ordinal);
        Assert.Contains("click to open", tip, StringComparison.Ordinal);

        view.UpdateHoveredLink(CellCentre(0, 32)); // plain text: no link
        Assert.Null(Avalonia.Controls.ToolTip.GetTip(view));
    }

    /// <summary>
    /// The tip shows the target as it is, so it cannot be disguised in turn: a bidi override would reorder what is shown
    /// (a "moc.elgoog" spoof), so it is shown as a visible escape; control characters are dropped; and a target too long to
    /// read keeps its start - the scheme and host - and its end.
    /// </summary>
    [Fact]
    public void The_link_tooltip_shows_the_target_as_it_is()
    {
        Assert.Equal("https://example.com/a\nCtrl+click to open", TerminalView.LinkTargetTip("https://example.com/a", macOS: false, "open"));
        Assert.Equal("https://example.com/a\nCmd+click to open", TerminalView.LinkTargetTip("https://example.com/a", macOS: true, "open"));

        string spoof = TerminalView.LinkTargetTip("https://example.com/\u202Egpj.exe", macOS: false, "open");
        Assert.DoesNotContain("\u202E", spoof, StringComparison.Ordinal);
        Assert.StartsWith("https://example.com/\\u202Egpj.exe\n", spoof, StringComparison.Ordinal);

        Assert.StartsWith("https://example.com/ab\n", TerminalView.LinkTargetTip("https://example.com/a\u0007b", macOS: false, "open"), StringComparison.Ordinal);

        string longTarget = "https://example.com/" + new string('x', 1000) + "/end";
        string target = TerminalView.LinkTargetTip(longTarget, macOS: false, "open").Split('\n')[0];
        Assert.True(target.Length <= TerminalView.LinkTargetTipMaxLength, $"{target.Length} chars");
        Assert.StartsWith("https://example.com/xxx", target, StringComparison.Ordinal);
        Assert.EndsWith("xxx/end", target, StringComparison.Ordinal);
        Assert.Contains("\u2026", target, StringComparison.Ordinal);
    }

    /// <summary>
    /// CodeRabbit on #634: a long user-info part could push the real host out of the shown target, so a tip reading
    /// https://trusted.example.com... would lead to attacker.example. The host it reaches is named on a line of its own then,
    /// and for an international host, in its ASCII form, so a look-alike letter cannot pass for another. A plain link says
    /// nothing more than its target.
    /// </summary>
    [Fact]
    public void The_link_tooltip_names_the_host_when_the_target_could_hide_it()
    {
        string spoofed = TerminalView.LinkTargetTip("https://trusted.example.com" + new string('a', 400) + "@attacker.example/login", macOS: false, "open");
        Assert.StartsWith("https://trusted.example.com", spoofed, StringComparison.Ordinal);
        Assert.Contains("\nHost: attacker.example\n", spoofed, StringComparison.Ordinal);

        string lookAlike = TerminalView.LinkTargetTip("https://ex\u0430mple.com/", macOS: false, "open"); // a Cyrillic a
        Assert.Contains("\nHost: xn--", lookAlike, StringComparison.Ordinal);

        Assert.DoesNotContain("Host:", TerminalView.LinkTargetTip("https://example.com/a", macOS: false, "open"), StringComparison.Ordinal);
        Assert.DoesNotContain("Host:", TerminalView.LinkTargetTip("file:///C:/Users/me/report.txt", macOS: false, "open"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Codex review of #634: a line or paragraph separator is not a control character, and a tooltip renders it as a line
    /// break - so a target could forge a "Host:" line of its own. It is shown as an escape, as an invisible tag character
    /// past U+FFFF is: the tip is the target and the action, nothing more.
    /// </summary>
    [Fact]
    public void The_link_tooltip_cannot_be_given_lines_of_its_own()
    {
        string forged = TerminalView.LinkTargetTip(
            "https://example.com/a\u2028Host: trusted.example\u2029x\U000E0041", macOS: false, "open");

        Assert.Equal(2, forged.Split('\n').Length);
        Assert.DoesNotContain("\u2028", forged, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2029", forged, StringComparison.Ordinal);
        Assert.StartsWith("https://example.com/a\\u2028Host: trusted.example\\u2029x\\U000E0041\n", forged, StringComparison.Ordinal);
    }

    /// <summary>
    /// Codex review of #634: output that rewrites the link under a pointer that has not moved leaves the tip showing the old
    /// target, and the click used to resolve the cell afresh - opening the new one. A click opens only what the tip
    /// previewed: this one opens nothing and brings the tip up to date, and the next opens what it now shows.
    /// </summary>
    [AvaloniaFact]
    public void A_click_never_opens_a_link_other_than_the_one_its_tooltip_showed()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        parser.Process(Osc8("https://trusted.example/docs", "docs"));
        var env = new RecordingEnvironment();
        var view = new TerminalView { LinkOpener = new TerminalLinkOpener(env) };
        view.SetBuffer(buffer);
        view.SetMetricsForTest(CellWidth, CellHeight);
        view.UpdateHoveredLink(CellCentre(0, 1));

        parser.Process("\u001b[1;1H" + Osc8("https://evil.example/x", "docs")); // rewritten under the resting pointer

        Assert.False(view.TryActivateLinkAt(CellCentre(0, 1), LinkModifier));
        Assert.Empty(env.Started);
        Assert.StartsWith("https://evil.example/x\n", Avalonia.Controls.ToolTip.GetTip(view) as string, StringComparison.Ordinal);

        Assert.True(view.TryActivateLinkAt(CellCentre(0, 1), LinkModifier));
        Assert.Equal("https://evil.example/x", Assert.Single(env.Started).FileName);
    }

    /// <summary>
    /// Codex review of #634: the same for a detected URL rewritten on its own row. The row's detection was kept until the
    /// pointer changed rows, so the tip kept the old URL and every click was refused as not matching it. The rewrite is
    /// seen: the first click brings the tip up to date, the next opens what it shows.
    /// </summary>
    [AvaloniaFact]
    public void A_detected_url_rewritten_on_its_row_is_previewed_afresh_before_it_opens()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        parser.Process("see https://trusted.example/a now");
        var env = new RecordingEnvironment();
        var view = new TerminalView { LinkOpener = new TerminalLinkOpener(env) };
        view.SetBuffer(buffer);
        view.SetMetricsForTest(CellWidth, CellHeight);
        view.UpdateHoveredLink(CellCentre(0, 10));
        Assert.Equal("https://trusted.example/a", view.HoveredLinkUriForTest);

        parser.Process("\u001b[1;1Hsee https://evil.example/b now"); // the same row, rewritten under the resting pointer

        Assert.False(view.TryActivateLinkAt(CellCentre(0, 10), LinkModifier));
        Assert.Equal("https://evil.example/b", view.HoveredLinkUriForTest);
        Assert.True(view.TryActivateLinkAt(CellCentre(0, 10), LinkModifier));
        Assert.Equal("https://evil.example/b", Assert.Single(env.Started).FileName);
    }

    /// <summary>
    /// CodeRabbit on #634: a local path is shown in the file manager, never opened, and the tip says so. It does not check
    /// that anything is there: a hover never touches the file system, as underlining does not.
    /// </summary>
    [AvaloniaFact]
    public void A_local_file_link_says_a_click_shows_it_in_the_file_manager()
    {
        var (view, _) = CreateView(Osc8("file:///C:/Users/me/report.txt", "report"));

        view.UpdateHoveredLink(CellCentre(0, 1));

        Assert.EndsWith("+click to show it in the file manager", Avalonia.Controls.ToolTip.GetTip(view) as string, StringComparison.Ordinal);
    }

    /// <summary>
    /// CodeRabbit on #634: while a program has mouse reporting on, pointer motion is its own - sent to the program, never to
    /// the hover - so a link hovered before that kept its underline and its tooltip. Motion clears them now.
    /// </summary>
    [AvaloniaFact]
    public void Mouse_reporting_takes_the_hover_and_its_tooltip_away()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        parser.Process(Osc8("https://example.com/docs", "docs"));
        var view = new TerminalView { LinkOpener = new TerminalLinkOpener(new RecordingEnvironment()) };
        view.SetBuffer(buffer);
        var window = new Avalonia.Controls.Window { Width = 800, Height = 500, Content = view };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        view.SetMetricsForTest(CellWidth, CellHeight);
        try
        {
            window.MouseMove(CellCentre(0, 1));
            Assert.NotNull(Avalonia.Controls.ToolTip.GetTip(view));

            parser.Process("\u001b[?1003h"); // any-event tracking: motion is the program's now
            window.MouseMove(CellCentre(0, 2));

            Assert.Null(Avalonia.Controls.ToolTip.GetTip(view));
            Assert.Empty(view.HoveredLinkRunsForTest);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_detected_url_still_underlines_its_own_span()
    {
        var (view, _) = CreateView("see https://example.com now");

        view.UpdateHoveredLink(CellCentre(0, 10));

        Assert.Equal([new HyperlinkRun(0, 4, 22)], view.HoveredLinkRunsForTest);
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
