using System.Linq;

namespace Ntilde.VT.Tests;

// OSC 3008 hierarchical context signalling, UAPI.15 v1.0. Expectations are written from the specification's
// semantics, ABNF and security section, plus the recorded policies in docs/qa/terminfo/osc3008.md, not from the
// implementation. The data is untrusted metadata: nothing here may run, reply, notify, retitle or move a shell mark.
public class Osc3008ContextTests
{
    private const string Esc = "\x1b";
    private const string St = "\x1b\\";

    private static (AnsiParser Parser, TerminalBuffer Buffer, List<string> Responses) Create()
    {
        var buffer = new TerminalBuffer(cols: 20, rows: 4);
        var parser = new AnsiParser(buffer);
        var responses = new List<string>();
        parser.OnResponse = responses.Add;
        return (parser, buffer, responses);
    }

    private static string Start(string id, string fields = "") => $"{Esc}]3008;start={id}{fields}{St}";

    private static string End(string id, string fields = "") => $"{Esc}]3008;end={id}{fields}{St}";

    private static string[] Ids(AnsiParser parser) => parser.ContextStack.Select(c => c.Id).ToArray();

    [Fact]
    public void The_specifications_container_example_opens_one_context_with_every_field()
    {
        var (parser, _, responses) = Create();

        parser.Process($"{Esc}]3008;start=bed86fab93af4328bbed0a1224af6d40;type=container;user=lennart;hostname=zeta;" +
            "machineid=3deb5353d3ba43d08201c136a47ead7b;bootid=d4a3d0fdf2e24fdea6d971ce73f4fbf2;pid=1062862;pidfdid=1063162;" +
            $"comm=systemd-nspawn;container=foobar{St}");

        ContextRecord context = Assert.Single(parser.ContextStack);
        Assert.Equal("bed86fab93af4328bbed0a1224af6d40", context.Id);
        Assert.Equal("container", context.Type);
        Assert.Equal("lennart", context.User);
        Assert.Equal("zeta", context.Hostname);
        Assert.Equal("3deb5353d3ba43d08201c136a47ead7b", context.MachineId);
        Assert.Equal("d4a3d0fdf2e24fdea6d971ce73f4fbf2", context.BootId);
        Assert.Equal("1062862", context.Pid);
        Assert.Equal("1063162", context.PidFdId);
        Assert.Equal("systemd-nspawn", context.Comm);
        Assert.Equal("foobar", context.Container);
        Assert.Null(context.Cwd);
        Assert.Empty(responses);

        parser.Process(End("bed86fab93af4328bbed0a1224af6d40"));
        Assert.Empty(parser.ContextStack);
        Assert.Equal("bed86fab93af4328bbed0a1224af6d40", Assert.Single(parser.EndedContexts).Id);
    }

    [Fact]
    public void Bel_and_st_both_terminate_and_fields_may_appear_in_any_order()
    {
        var (parser, _, _) = Create();

        parser.Process($"{Esc}]3008;start=a;pid=7;type=shell;cwd=/tmp\x07");
        parser.Process(Start("b", ";comm=bash;type=command;cmdline=ls -l"));

        Assert.Equal(new[] { "a", "b" }, Ids(parser));
        Assert.Equal("shell", parser.ContextStack[0].Type);
        Assert.Equal("/tmp", parser.ContextStack[0].Cwd);
        Assert.Equal("command", parser.ContextStack[1].Type);
        Assert.Equal("ls -l", parser.ContextStack[1].CmdLine);
    }

    [Fact]
    public void A_start_is_an_id_only_context_when_it_carries_no_metadata()
    {
        var (parser, _, _) = Create();

        parser.Process(Start("only-id"));

        ContextRecord context = Assert.Single(parser.ContextStack);
        Assert.Equal("only-id", context.Id);
        Assert.Null(context.Type);
        Assert.Null(context.User);
    }

    [Fact]
    public void New_contexts_nest_and_the_last_start_is_active()
    {
        var (parser, _, _) = Create();

        parser.Process(Start("session", ";type=session") + Start("shell", ";type=shell") + Start("cmd", ";type=command"));
        Assert.Equal(new[] { "session", "shell", "cmd" }, Ids(parser));

        parser.Process(End("cmd", ";exit=success;status=0"));
        Assert.Equal(new[] { "session", "shell" }, Ids(parser));
    }

    [Fact]
    public void Starting_an_open_id_updates_it_flushes_old_metadata_and_ends_its_subcontexts()
    {
        var (parser, _, _) = Create();
        parser.Process(Start("a", ";type=shell;user=old;cwd=/old") + Start("b", ";type=command") + Start("c"));

        parser.Process(Start("a", ";type=app;comm=new"));

        ContextRecord context = Assert.Single(parser.ContextStack);
        Assert.Equal("a", context.Id);
        Assert.Equal("app", context.Type);
        Assert.Equal("new", context.Comm);
        Assert.Null(context.User);
        Assert.Null(context.Cwd);
    }

    [Fact]
    public void Ending_a_context_also_ends_its_subcontexts_and_an_unknown_end_is_ignored()
    {
        var (parser, _, _) = Create();
        parser.Process(Start("a") + Start("b") + Start("c"));

        parser.Process(End("zzz"));
        Assert.Equal(new[] { "a", "b", "c" }, Ids(parser));
        Assert.Empty(parser.EndedContexts);

        parser.Process(End("b"));
        Assert.Equal(new[] { "a" }, Ids(parser));
        Assert.Equal("b", Assert.Single(parser.EndedContexts).Id);

        parser.Process(End("b"));
        Assert.Single(parser.EndedContexts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tab\there")]
    [InlineData("bad\\id")]
    [InlineData("naïve")]
    [InlineData("del\u007fete")]
    public void An_invalid_context_id_ignores_the_whole_sequence(string id)
    {
        var (parser, _, _) = Create();

        parser.Process(Start(id, ";type=shell"));
        parser.Process(End(id));

        Assert.Empty(parser.ContextStack);
        Assert.Empty(parser.EndedContexts);
    }

    [Fact]
    public void A_sixty_four_character_id_may_contain_spaces_and_escaped_separators()
    {
        var (parser, _, _) = Create();
        string escaped = "x " + new string('y', 59) + @"\x3b\x5c" + "z";
        string decoded = "x " + new string('y', 59) + ";\\" + "z";
        Assert.Equal(64, decoded.Length);

        parser.Process(Start(escaped, ";user=a"));
        Assert.Equal(decoded, Assert.Single(parser.ContextStack).Id);

        // One character more, counted after unescaping, is refused.
        parser.Process(Start("x " + new string('y', 60) + @"\x3b\x5c" + "z"));
        Assert.Single(parser.ContextStack);

        parser.Process(End(escaped, ";status=1"));
        Assert.Empty(parser.ContextStack);
        Assert.Equal(decoded, Assert.Single(parser.EndedContexts).Id);
    }

    [Fact]
    public void A_sixty_five_character_id_is_refused_and_never_truncated_into_another_context()
    {
        var (parser, _, _) = Create();
        string sixtyFour = new('k', 64);

        parser.Process(Start(sixtyFour));
        parser.Process(Start(sixtyFour + "k"));
        parser.Process(End(sixtyFour + "k"));

        Assert.Equal(new[] { sixtyFour }, Ids(parser));
    }

    [Fact]
    public void Unknown_and_invalid_fields_are_ignored_but_the_valid_remainder_is_used()
    {
        var (parser, _, _) = Create();

        parser.Process(Start("a", ";nonsense=1;pid=not-a-number;type=shell;user=;hostname=ok;pidfdid=99999999999999999999999;bootid=zz;machineid=short;=novalue;noequals"));

        ContextRecord context = Assert.Single(parser.ContextStack);
        Assert.Equal("shell", context.Type);
        Assert.Equal("ok", context.Hostname);
        Assert.Null(context.Pid);
        Assert.Null(context.User);
        Assert.Null(context.PidFdId);
        Assert.Null(context.BootId);
        Assert.Null(context.MachineId);
    }

    [Fact]
    public void An_unknown_type_value_is_ignored_and_every_defined_type_is_accepted()
    {
        var (parser, _, _) = Create();
        string[] types = { "boot", "container", "vm", "elevate", "chpriv", "subcontext", "remote", "shell", "command", "app", "service", "session" };

        parser.Process(Start("bad", ";type=hacker"));
        Assert.Null(parser.ContextStack[0].Type);
        parser.Process(End("bad"));

        for (int i = 0; i < types.Length; i++) parser.Process(Start($"t{i}", $";type={types[i]}"));
        Assert.Equal(types, parser.ContextStack.Select(c => c.Type).ToArray());

        parser.Process(Start("upper", ";type=SHELL"));
        Assert.Null(parser.ContextStack.Single(c => c.Id == "upper").Type);
    }

    [Fact]
    public void Text_fields_are_capped_at_255_characters_and_discarded_when_longer()
    {
        var (parser, _, _) = Create();

        parser.Process(Start("a", $";cwd={new string('d', 255)};comm={new string('c', 256)};cmdline={new string('m', 255)}"));

        ContextRecord context = Assert.Single(parser.ContextStack);
        Assert.Equal(255, context.Cwd!.Length);
        Assert.Null(context.Comm);
        Assert.Equal(255, context.CmdLine!.Length);

        parser.Process(Start("b", $";cmdline={new string('m', 256)}"));
        Assert.Null(parser.ContextStack.Single(c => c.Id == "b").CmdLine);
    }

    [Fact]
    public void Cmdline_may_be_empty_but_other_text_fields_may_not()
    {
        var (parser, _, _) = Create();

        parser.Process(Start("a", ";cmdline=;cwd=;comm="));

        ContextRecord context = Assert.Single(parser.ContextStack);
        Assert.Equal(string.Empty, context.CmdLine);
        Assert.Null(context.Cwd);
        Assert.Null(context.Comm);
    }

    [Fact]
    public void Semicolons_and_backslashes_in_values_are_escaped_and_a_bare_backslash_is_invalid()
    {
        var (parser, _, _) = Create();

        parser.Process(Start("a", @";cwd=/a\x3bb\x5cc;cmdline=echo \x3B done;comm=bad\q;user=lennart"));

        ContextRecord context = Assert.Single(parser.ContextStack);
        Assert.Equal(@"/a;b\c", context.Cwd);
        Assert.Equal("echo ; done", context.CmdLine);
        Assert.Null(context.Comm);
        Assert.Equal("lennart", context.User);
    }

    [Fact]
    public void Non_ascii_utf8_text_is_kept_but_control_characters_invalidate_the_field()
    {
        var (parser, _, _) = Create();

        parser.Process(Start("a", ";user=zoë;cwd=/tmp/日本語;comm=ba\u0001d;hostname=h\u0085x"));

        ContextRecord context = Assert.Single(parser.ContextStack);
        Assert.Equal("zoë", context.User);
        Assert.Equal("/tmp/日本語", context.Cwd);
        Assert.Null(context.Comm);
        Assert.Null(context.Hostname);
    }

    [Fact]
    public void A_repeated_field_keeps_its_last_valid_value()
    {
        var (parser, _, _) = Create();

        parser.Process(Start("a", ";user=first;user=;user=last;pid=1;pid=oops"));

        ContextRecord context = Assert.Single(parser.ContextStack);
        Assert.Equal("last", context.User);
        Assert.Equal("1", context.Pid);
    }

    [Fact]
    public void Numeric_and_id128_fields_follow_the_abnf_bounds()
    {
        var (parser, _, _) = Create();
        string id36 = "3deb5353-d3ba-43d0-8201-c136a47ead7b";

        parser.Process(Start("ok", $";pid=18446744073709551615;pidfdid=0;machineid={id36};bootid=ABCDEFabcdef01234567890123456789"));
        parser.Process(Start("bad", ";pid=18446744073709551616;pidfdid=-1;machineid=" + new string('a', 37) + ";bootid=" + new string('a', 31)));

        ContextRecord ok = parser.ContextStack[0];
        Assert.Equal("18446744073709551615", ok.Pid);
        Assert.Equal("0", ok.PidFdId);
        Assert.Equal(id36, ok.MachineId);
        Assert.Equal("ABCDEFabcdef01234567890123456789", ok.BootId);
        ContextRecord bad = parser.ContextStack[1];
        Assert.Null(bad.Pid);
        Assert.Null(bad.PidFdId);
        Assert.Null(bad.MachineId);
        Assert.Null(bad.BootId);
    }

    [Fact]
    public void End_metadata_records_exit_status_and_signal_and_ignores_start_only_fields()
    {
        var (parser, _, _) = Create();
        parser.Process(Start("a", ";type=command") + Start("b", ";type=command") + Start("c", ";type=command"));

        parser.Process(End("c", ";exit=crash;status=139;signal=SIGSEGV;user=ignored;pid=9"));
        parser.Process(End("b", ";exit=exploded;status=256;signal=segv"));
        parser.Process(End("a", ";exit=success;status=0;signal=SIGRTMIN+3"));

        EndedContextRecord[] ended = parser.EndedContexts.ToArray();
        Assert.Equal(("c", "command", "crash", "139", "SIGSEGV"), (ended[0].Id, ended[0].Type, ended[0].Exit, ended[0].Status, ended[0].Signal));
        Assert.Equal(("b", "command", null, null, null), (ended[1].Id, ended[1].Type, ended[1].Exit, ended[1].Status, ended[1].Signal));
        Assert.Equal(("a", "success", "0", "SIGRTMIN+3"), (ended[2].Id, ended[2].Exit, ended[2].Status, ended[2].Signal));
    }

    [Fact]
    public void The_ended_ring_is_bounded_and_keeps_the_most_recent_in_order()
    {
        var (parser, _, _) = Create();

        for (int i = 0; i < 11; i++)
        {
            parser.Process(Start($"c{i}") + End($"c{i}", $";status={i}"));
        }

        Assert.Equal(Enumerable.Range(3, 8).Select(i => $"c{i}").ToArray(), parser.EndedContexts.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void A_full_stack_drops_new_contexts_and_keeps_the_earlier_ones_but_still_updates_open_ids()
    {
        var (parser, _, _) = Create();

        for (int i = 0; i < 20; i++) parser.Process(Start($"c{i}", $";pid={i}"));

        Assert.Equal(Enumerable.Range(0, 16).Select(i => $"c{i}").ToArray(), Ids(parser));

        parser.Process(Start("c15", ";pid=999"));
        Assert.Equal("999", parser.ContextStack[^1].Pid);
        Assert.Equal(16, parser.ContextStack.Count);

        parser.Process(End("c0"));
        Assert.Empty(parser.ContextStack);
        parser.Process(Start("again"));
        Assert.Equal(new[] { "again" }, Ids(parser));
    }

    [Fact]
    public void Oversized_sequences_and_extra_fields_are_bounded()
    {
        var (parser, _, _) = Create();

        parser.Process(Start("huge", ";cwd=/" + new string('x', 20000)));
        Assert.Empty(parser.ContextStack);

        string manyFields = string.Concat(Enumerable.Repeat(";nop=1", 40)) + ";user=beyond";
        parser.Process(Start("many", ";user=within" + manyFields));
        Assert.Equal("within", Assert.Single(parser.ContextStack).User);
    }

    [Fact]
    public void Resets_and_screen_changes_do_not_touch_the_stack_but_a_hangup_does()
    {
        var (parser, _, _) = Create();
        parser.Process(Start("a", ";type=session") + Start("b", ";type=shell") + End("b", ";status=0") + Start("c"));

        parser.Process($"{Esc}c");
        parser.Process($"{Esc}[!p");
        parser.Process($"{Esc}[?1049h{Esc}[?1049l");

        Assert.Equal(new[] { "a", "c" }, Ids(parser));
        Assert.Equal("b", Assert.Single(parser.EndedContexts).Id);

        parser.ResetContexts();
        Assert.Empty(parser.ContextStack);
        Assert.Empty(parser.EndedContexts);
    }

    [Fact]
    public void Foreign_or_malformed_sequences_have_no_effect_and_are_not_confused_with_osc_133_or_633()
    {
        var (parser, buffer, responses) = Create();
        string titleBefore = parser.ExportState().WindowTitle ?? "";

        // The terminfo.dev probe payload and other non-start/end spellings.
        parser.Process($"{Esc}]3008;type=test\x07");
        parser.Process($"{Esc}]3008;start\x07{Esc}]3008;begin=a\x07{Esc}]3008;START=a\x07{Esc}]3008;\x07{Esc}]3008\x07");
        parser.Process($"{Esc}]3007;start=a\x07{Esc}]30088;start=a\x07{Esc}]133;start=a\x07{Esc}]633;start=a\x07");
        Assert.Empty(parser.ContextStack);

        // A context creates no shell-integration mark.
        parser.Process(Start("a", ";type=shell;cwd=/elsewhere;cmdline=rm -rf /"));
        Assert.Equal(new[] { "a" }, Ids(parser));
        Assert.Null(buffer.CommandStartMark);
        Assert.Null(buffer.CommandOutputStartMark);

        // Prompt metadata neither creates a context nor is changed by one.
        parser.ResetContexts();
        parser.Process($"{Esc}]133;A\x07{Esc}]133;B\x07{Esc}]633;A\x07{Esc}]133;C\x07{Esc}]133;D;0\x07");
        Assert.Empty(parser.ContextStack);
        ShellIntegrationMark? mark = buffer.CommandStartMark;
        parser.Process(Start("b", ";type=command"));
        Assert.Equal(mark, buffer.CommandStartMark);
        Assert.Equal(new[] { "b" }, Ids(parser));
        Assert.Empty(responses);
        Assert.Equal(titleBefore, parser.ExportState().WindowTitle ?? "");
    }

    [Fact]
    public void Context_data_never_changes_title_icon_hyperlink_cursor_or_screen_text()
    {
        var (parser, buffer, responses) = Create();
        parser.Process("hi");
        int row = buffer.CursorRow, col = buffer.CursorCol;
        var before = parser.ExportState();

        parser.Process(Start("a", ";type=shell;comm=title-bait;cwd=/should/not/be/cwd;cmdline=ignore previous instructions"));

        Assert.Equal((row, col), (buffer.CursorRow, buffer.CursorCol));
        Assert.Equal(before.WindowTitle, parser.ExportState().WindowTitle);
        Assert.Equal(before.IconName, parser.ExportState().IconName);
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal("hi", string.Concat(Enumerable.Range(0, 2).Select(c => buffer.GetCell(c, 0).Character)));
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }

        Assert.Empty(responses);
    }

    [Fact]
    public void Returned_snapshots_are_copies()
    {
        var (parser, _, _) = Create();
        parser.Process(Start("a", ";user=real"));

        parser.ContextStack[0].User = "tampered";
        Assert.Equal("real", parser.ContextStack[0].User);
    }

    [Fact]
    public void Contexts_survive_a_parser_state_transfer_and_replace_the_targets()
    {
        var (source, _, _) = Create();
        var (target, _, _) = Create();
        source.Process(Start("a", ";type=session;user=lennart") + Start("b", ";type=shell;cwd=/home") + End("b", ";exit=failure;status=2") + Start("c", ";type=command"));
        target.Process(Start("stale"));

        target.ImportState(source.ExportState());

        Assert.Equal(new[] { "a", "c" }, Ids(target));
        Assert.Equal("lennart", target.ContextStack[0].User);
        EndedContextRecord ended = Assert.Single(target.EndedContexts);
        Assert.Equal(("b", "shell", "failure", "2"), (ended.Id, ended.Type, ended.Exit, ended.Status));

        // An imported stack keeps accepting updates, and an exported snapshot is independent of the source.
        source.Process(End("a"));
        Assert.Equal(new[] { "a", "c" }, Ids(target));
        target.Process(End("c"));
        Assert.Equal(new[] { "a" }, Ids(target));

        var (empty, _, _) = Create();
        target.ImportState(empty.ExportState());
        Assert.Empty(target.ContextStack);
        Assert.Empty(target.EndedContexts);
    }

    [Fact]
    public void Imported_context_state_is_validated()
    {
        var parser = new AnsiParser(new TerminalBuffer(cols: 10, rows: 2));
        AnsiParserState state = parser.ExportState();

        foreach (var bad in new List<List<ContextRecord>>
        {
            new() { null! },
            new() { new ContextRecord { Id = "" } },
            new() { new ContextRecord { Id = new string('x', 65) } },
            new() { new ContextRecord { Id = "a\tb" } },
            new() { new ContextRecord { Id = "a" }, new ContextRecord { Id = "a" } },
            new() { new ContextRecord { Id = "a", Type = "hacker" } },
            new() { new ContextRecord { Id = "a", Cwd = new string('x', 256) } },
            new() { new ContextRecord { Id = "a", Comm = "line\nbreak" } },
            new() { new ContextRecord { Id = "a", Pid = "12x" } },
            new() { new ContextRecord { Id = "a", MachineId = "short" } },
            Enumerable.Range(0, 17).Select(i => new ContextRecord { Id = $"c{i}" }).ToList(),
        })
        {
            state.ContextStack = bad;
            Assert.ThrowsAny<Exception>(() => parser.ImportState(state));
        }

        state.ContextStack = null;
        foreach (var bad in new List<List<EndedContextRecord>>
        {
            new() { null! },
            new() { new EndedContextRecord { Id = "" } },
            new() { new EndedContextRecord { Id = "a", Exit = "exploded" } },
            new() { new EndedContextRecord { Id = "a", Status = "256" } },
            new() { new EndedContextRecord { Id = "a", Signal = "segv" } },
            Enumerable.Range(0, 9).Select(i => new EndedContextRecord { Id = $"e{i}" }).ToList(),
        })
        {
            state.EndedContexts = bad;
            Assert.ThrowsAny<Exception>(() => parser.ImportState(state));
        }

        // A refused import leaves the parser untouched.
        parser.Process(Start("kept"));
        state.EndedContexts = new() { new EndedContextRecord { Id = "" } };
        Assert.ThrowsAny<Exception>(() => parser.ImportState(state));
        Assert.Equal(new[] { "kept" }, Ids(parser));
    }
}
