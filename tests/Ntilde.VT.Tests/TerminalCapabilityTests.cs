using System.Text;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// The shared capability table (#844) and the XTGETTCAP replies built from it. Every string value
/// is expanded the way curses would and run through the parser, so an entry cannot claim behaviour
/// Ntilde does not have.
/// </summary>
public sealed class TerminalCapabilityTests
{
    private const string Failure = "\x1bP0+r\x1b\\";

    [Fact]
    public void Table_entries_are_unique_well_formed_and_cite_existing_tests()
    {
        string root = RepositoryRoot();
        Assert.Equal(TerminalCapabilities.All.Count, TerminalCapabilities.All.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (TerminalCapability capability in TerminalCapabilities.All)
        {
            Assert.Matches("^[A-Za-z][A-Za-z0-9]*$", capability.Name);
            switch (capability.Kind)
            {
                case TerminalCapabilityKind.Flag: Assert.Equal("", capability.Value); break;
                case TerminalCapabilityKind.Number: Assert.Matches("^[1-9][0-9]*$", capability.Value); break;
                default: Assert.NotEqual("", capability.Value); break;
            }
            Assert.All(capability.Value, ch => Assert.InRange((int)ch, 0, 0x7E));
            Assert.Contains("tests/Ntilde.VT.Tests/TerminalCapabilityTests.cs", capability.Evidence);
            Assert.All(capability.Evidence, path => Assert.True(File.Exists(Path.Combine(root, path)), $"{capability.Name}: {path}"));
            Assert.True(TerminalCapabilities.TryGet(capability.Name, out TerminalCapability? found));
            Assert.Same(capability, found);
        }
        Assert.False(TerminalCapabilities.TryGet("co", out _)); // names are case-sensitive
        Assert.False(TerminalCapabilities.TryGet("TN", out _)); // the name depends on the session's TERM
    }

    [Theory]
    [InlineData("setrgbf", new object[] { 1, 2, 3 }, "0;38;2;1;2;3")]
    [InlineData("setrgbb", new object[] { 250, 128, 0 }, "0;48;2;250;128;0")]
    [InlineData("Smulx", new object[] { 3 }, "0;4:3")]
    [InlineData("Smulx", new object[] { 5 }, "0;4:5")]
    [InlineData("Setulc", new object[] { 0x0A141E }, "0;58;2;10;20;30")]
    [InlineData("Setulc", new object[] { 0xFF0080 }, "0;58;2;255;0;128")]
    public void Color_and_underline_values_set_the_pen_they_describe(string name, object[] parameters, string sgr)
    {
        Assert.Equal("\x1bP1$r" + sgr + "m\x1b\\", Reply(Expand(name, parameters) + "\x1bP$qm\x1b\\"));
    }

    [Fact]
    public void Color_count_matches_the_palette_the_parser_accepts()
    {
        Assert.True(TerminalCapabilities.TryGet("colors", out TerminalCapability? colors));
        int top = int.Parse(colors.Value, System.Globalization.CultureInfo.InvariantCulture) - 1;
        Assert.Equal($"\x1bP1$r0;38;5;{top}m\x1b\\", Reply($"\x1b[38;5;{top}m\x1bP$qm\x1b\\"));
        Assert.Equal("\x1bP1$r0m\x1b\\", Reply($"\x1b[38;5;{top + 1}m\x1bP$qm\x1b\\"));
    }

    [Fact]
    public void Smulx_zero_ends_the_underline()
        => Assert.Equal("\x1bP1$r0m\x1b\\", Reply(Expand("Smulx", 3) + Expand("Smulx", 0) + "\x1bP$qm\x1b\\"));

    [Fact]
    public void Sync_begins_with_one_and_ends_with_two()
    {
        Assert.Equal("\x1b[?2026h", Expand("Sync", 1));
        Assert.Equal("\x1b[?2026l", Expand("Sync", 2));
        Assert.Equal("\x1b[?2026;1$y", Reply(Expand("Sync", 1) + "\x1b[?2026$p"));
        Assert.Equal("\x1b[?2026;2$y", Reply(Expand("Sync", 1) + Expand("Sync", 2) + "\x1b[?2026$p"));
    }

    [Theory]
    [InlineData("Enmg", "\x1b[?69h")]
    [InlineData("Dsmg", "\x1b[?69l")]
    [InlineData("Clmg", "\x1b[s")]
    [InlineData("Cmg", "\x1b[%i%p1%d;%p2%ds")]
    public void Margin_capabilities_are_in_the_table_and_answer_XTGETTCAP(string name, string value)
    {
        Assert.True(TerminalCapabilities.TryGet(name, out TerminalCapability? capability));
        Assert.Equal(TerminalCapabilityKind.Text, capability.Kind);
        Assert.Equal(value, capability.Value);
        Assert.Null(capability.TerminfoOmission);
        Assert.Equal($"\x1bP1+r{Hex(name)}={Hex(value)}\x1b\\", Reply($"\x1bP+q{Hex(name)}\x1b\\"));
    }

    [Fact]
    public void Margin_capabilities_set_and_clear_the_left_and_right_margins()
    {
        Assert.Equal("\x1b[5;14s", Expand("Cmg", 4, 13));
        var buffer = new TerminalBuffer(20, 6);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process(Expand("Enmg") + Expand("Cmg", 4, 13));
        Assert.Equal((4, 13), (buffer.LeftMargin, buffer.RightMargin));
        parser.Process(Expand("Clmg"));
        Assert.Equal((0, 19), (buffer.LeftMargin, buffer.RightMargin));
        parser.Process(Expand("Cmg", 2, 7));
        Assert.Equal((2, 7), (buffer.LeftMargin, buffer.RightMargin));
        parser.Process(Expand("Dsmg"));
        Assert.Equal((0, 19), (buffer.LeftMargin, buffer.RightMargin));
    }

    [Fact]
    public void Ss_sets_and_Se_restores_the_cursor_style()
    {
        Assert.Equal("\x1bP1$r6 q\x1b\\", Reply(Expand("Ss", 6) + "\x1bP$q q\x1b\\"));
        Assert.Equal("\x1bP1$r0 q\x1b\\", Reply(Expand("Ss", 6) + Expand("Se") + "\x1bP$q q\x1b\\"));
    }

    [Fact]
    public void Ms_writes_the_clipboard()
    {
        var writes = new List<(string Target, string Text)>();
        var parser = new AnsiParser(new TerminalBuffer(12, 4))
        {
            OnClipboardWrite = (target, data) => writes.Add((target, Encoding.UTF8.GetString(data))),
        };
        parser.Process(Expand("Ms", "c", Convert.ToBase64String("copied"u8.ToArray())));
        Assert.Equal(("c", "copied"), Assert.Single(writes));
    }

    [Fact]
    public void Status_line_strings_set_the_title()
    {
        var titles = new List<string>();
        var parser = new AnsiParser(new TerminalBuffer(12, 4)) { OnTitleChanged = titles.Add };
        parser.Process(Expand("tsl") + "status" + Expand("fsl"));
        Assert.Equal("status", Assert.Single(titles));
    }

    [Fact]
    public void Every_advertised_capability_reports_its_exact_value()
    {
        foreach (TerminalCapability capability in TerminalCapabilities.All)
        {
            string hexName = Hex(capability.Name);
            string expected = capability.Kind == TerminalCapabilityKind.Flag
                ? $"\x1bP1+r{hexName}\x1b\\"
                : $"\x1bP1+r{hexName}={Hex(capability.Value)}\x1b\\";
            Assert.Equal(expected, Reply($"\x1bP+q{hexName}\x1b\\", clipboardWrites: true));
        }
    }

    [Theory]
    [InlineData("436f", "\x1bP1+r436f=323536\x1b\\")] // Co=256
    [InlineData("436F", "\x1bP1+r436F=323536\x1b\\")] // the name is echoed as spelled
    [InlineData("5463", "\x1bP1+r5463\x1b\\")] // Tc, a boolean
    [InlineData("536d756c78", "\x1bP1+r536d756c78=1B5B343A25703125646D\x1b\\")] // Smulx
    [InlineData("5365", "\x1bP1+r5365=1B5B302071\x1b\\")] // Se
    [InlineData("66736c", "\x1bP1+r66736c=07\x1b\\")] // fsl
    [InlineData("6873", "\x1bP1+r6873\x1b\\")] // hs, a boolean
    [InlineData("544e", Failure)] // TN
    [InlineData("636f", Failure)] // co: names are case-sensitive
    [InlineData("6b63757531", Failure)] // kcuu1, inherited from xterm-256color
    public void Single_names_get_exact_replies(string names, string expected)
    {
        Assert.Equal(expected, Reply($"\x1bP+q{names}\x1b\\"));
    }

    [Fact]
    public void A_list_gets_one_reply_per_name_in_order()
    {
        Assert.Equal(new[] { "\x1bP1+r436f=323536\x1b\\", Failure, "\x1bP1+r5463\x1b\\", "\x1bP1+r436f=323536\x1b\\" },
            Replies("\x1bP+q436f;544e;5463;436f\x1b\\"));
        string sixteen = string.Join(';', Enumerable.Repeat("5463", AnsiParser.XtgettcapMaxNames));
        Assert.Equal(AnsiParser.XtgettcapMaxNames, Replies($"\x1bP+q{sixteen}\x1b\\").Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(";")]
    [InlineData("436f;")]
    [InlineData(";436f")]
    [InlineData("436f;;5463")]
    [InlineData("5")]
    [InlineData("436")]
    [InlineData("zz")]
    [InlineData("436g")]
    [InlineData(" 436f")]
    [InlineData("436f;5")]
    [InlineData("1b5b")] // decodes to a control character
    [InlineData("4320")] // decodes to a space
    [InlineData("43c3")] // decodes outside ASCII
    public void An_invalid_list_gets_one_failure_and_nothing_else(string names)
    {
        Assert.Equal(new[] { Failure }, Replies($"\x1bP+q{names}\x1b\\"));
    }

    [Fact]
    public void Oversized_names_and_lists_are_refused_whole()
    {
        string longest = Hex(new string('A', AnsiParser.XtgettcapMaxNameChars));
        Assert.Equal(new[] { Failure, "\x1bP1+r5463\x1b\\" }, Replies($"\x1bP+q{longest};5463\x1b\\"));
        Assert.Equal(new[] { Failure }, Replies($"\x1bP+q{longest}41;5463\x1b\\"));
        string tooMany = string.Join(';', Enumerable.Repeat("5463", AnsiParser.XtgettcapMaxNames + 1));
        Assert.Equal(new[] { Failure }, Replies($"\x1bP+q{tooMany}\x1b\\"));
    }

    [Theory]
    [InlineData("\x1bP", "\x1b\\")]
    [InlineData("\x1bP", "\u009c")]
    [InlineData("\u0090", "\x1b\\")]
    [InlineData("\u0090", "\u009c")]
    public void Standard_framings_reply_under_the_write_lock_without_mutating_state(string start, string end)
    {
        var buffer = new TerminalBuffer(12, 6);
        var replies = new List<string>();
        var parser = new AnsiParser(buffer)
        {
            OnResponse = reply =>
            {
                Assert.True(buffer.Lock.IsWriteLockHeld);
                replies.Add(reply);
            },
        };
        parser.Process("\x1b[1;4:3;31mtext\x1b[5 q\x1b[2;4H");
        byte[] before = TerminalStateSerializer.ToBytes(buffer.ExportState(0));
        parser.Process(start + "+q436f;5463" + end);
        Assert.Equal(new[] { "\x1bP1+r436f=323536\x1b\\", "\x1bP1+r5463\x1b\\" }, replies);
        Assert.Equal(before, TerminalStateSerializer.ToBytes(buffer.ExportState(0)));
    }

    [Theory]
    [InlineData("\x1bP+q436f\a")]
    [InlineData("\u0090+q436f\a")]
    public void Bel_recovery_stays_negative(string request)
    {
        Assert.Equal(new[] { Failure }, Replies(request));
    }

    [Fact]
    public void A_truncated_valid_prefix_stays_negative_and_an_exact_cap_length_request_succeeds()
    {
        const string Payload = "+q436f";
        var replies = new List<string>();
        var parser = new AnsiParser(new TerminalBuffer(12, 6)) { MaxStringSequenceChars = Payload.Length, OnResponse = replies.Add };
        parser.Process("\x1bP" + Payload + "3b5463\x1b\\");
        parser.Process("\x1bP" + Payload + "\x1b\\");
        Assert.Equal(new[] { Failure, "\x1bP1+r436f=323536\x1b\\" }, replies);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Ms_is_advertised_only_while_the_host_accepts_clipboard_writes(bool? available, bool advertised)
    {
        string? reply = null;
        var parser = new AnsiParser(new TerminalBuffer(12, 4)) { OnResponse = r => reply = r };
        if (available is bool value) parser.ClipboardWriteAvailable = () => value;
        parser.Process("\x1bP+q4d73\x1b\\");
        Assert.Equal(advertised ? "\x1bP1+r4d73=" + Hex("\x1b]52;%p1%s;%p2%s\a") + "\x1b\\" : Failure, reply);
    }

    [Fact]
    public void Ms_follows_a_live_setting_change()
    {
        bool allowed = true;
        var replies = new List<string>();
        var parser = new AnsiParser(new TerminalBuffer(12, 4)) { OnResponse = replies.Add, ClipboardWriteAvailable = () => allowed };
        parser.Process("\x1bP+q4d73\x1b\\");
        allowed = false;
        parser.Process("\x1bP+q4d73\x1b\\");
        Assert.StartsWith("\x1bP1+r4d73=", replies[0]);
        Assert.Equal(Failure, replies[1]);
    }

    [Fact]
    [Trait("Category", "Replay")]
    public void Every_split_and_complete_JSON_state_restore_preserve_the_exact_replies()
    {
        const string Request = "\x1bP+q436f;544e;5463\x1b\\";
        string[] expected = ["\x1bP1+r436f=323536\x1b\\", Failure, "\x1bP1+r5463\x1b\\"];
        for (int cut = 0; cut <= Request.Length; cut++)
        {
            var buffer = new TerminalBuffer(12, 6);
            var replies = new List<string>();
            var source = new AnsiParser(buffer) { OnResponse = replies.Add };
            source.Process(Request[..cut]);
            TerminalStateSnapshot snapshot = buffer.ExportState(0);
            snapshot.Parser = source.ExportState();
            var targetBuffer = new TerminalBuffer(12, 6);
            var resumed = new List<string>();
            var target = new AnsiParser(targetBuffer) { OnResponse = resumed.Add };
            TerminalStateTransfer.Restore(targetBuffer, target,
                TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot)));
            int alreadyReplied = replies.Count;
            source.Process(Request[cut..]);
            target.Process(Request[cut..]);
            Assert.Equal(expected, replies);
            Assert.Equal(replies.Skip(alreadyReplied), resumed);
        }
    }

    private static string Reply(string input, bool clipboardWrites = false) => Assert.Single(Replies(input, clipboardWrites));

    private static List<string> Replies(string input, bool clipboardWrites = false)
    {
        var replies = new List<string>();
        var parser = new AnsiParser(new TerminalBuffer(12, 4)) { OnResponse = replies.Add };
        if (clipboardWrites) parser.ClipboardWriteAvailable = () => true;
        parser.Process(input);
        return replies;
    }

    private static string Hex(string text)
        => string.Concat(text.Select(ch => ((int)ch).ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    private static string Expand(string name, params object[] parameters)
    {
        Assert.True(TerminalCapabilities.TryGet(name, out TerminalCapability? capability));
        Assert.Equal(TerminalCapabilityKind.Text, capability.Kind);
        return TerminfoExpander.Expand(capability.Value, parameters);
    }

    internal static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ntilde.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("Ntilde.sln not found above the test output directory.");
    }
}

/// <summary>
/// The subset of curses' <c>tparm</c> the capability table uses: <c>%p1-%p9</c>, <c>%d</c>,
/// <c>%s</c>, <c>%i</c>, <c>%{n}</c>, the arithmetic operators, <c>%?…%t…%e…%;</c> and <c>%%</c>. Anything
/// else fails the test, so a new entry cannot rely on an operation this expander ignores.
/// </summary>
internal static class TerminfoExpander
{
    public static string Expand(string format, params object[] parameters)
    {
        var output = new StringBuilder();
        parameters = (object[])parameters.Clone(); // %i edits them in place
        var stack = new Stack<object>();
        for (int i = 0; i < format.Length; i++)
        {
            char ch = format[i];
            if (ch != '%')
            {
                output.Append(ch);
                continue;
            }
            char op = format[++i];
            switch (op)
            {
                case '%': output.Append('%'); break;
                case 'i': // one-based: increment the first two parameters
                    for (int p = 0; p < Math.Min(2, parameters.Length); p++) parameters[p] = (int)parameters[p] + 1;
                    break;
                case 'p': stack.Push(parameters[format[++i] - '1']); break;
                case 'd': output.Append(((int)stack.Pop()).ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
                case 's': output.Append((string)stack.Pop()); break;
                case '{':
                    int close = format.IndexOf('}', i);
                    stack.Push(int.Parse(format[(i + 1)..close], System.Globalization.CultureInfo.InvariantCulture));
                    i = close;
                    break;
                case '+' or '-' or '*' or '/' or 'm' or '&' or '|' or '^':
                    int b = (int)stack.Pop(), a = (int)stack.Pop();
                    stack.Push(op switch
                    {
                        '+' => a + b,
                        '-' => a - b,
                        '*' => a * b,
                        '/' => a / b,
                        'm' => a % b,
                        '&' => a & b,
                        '|' => a | b,
                        _ => a ^ b,
                    });
                    break;
                case '?': break;
                case 't':
                    if ((int)stack.Pop() == 0) i = SkipBranch(format, i, stopAtElse: true);
                    break;
                case 'e': i = SkipBranch(format, i, stopAtElse: false); break;
                case ';': break;
                default: throw new NotSupportedException($"terminfo operation %{op}");
            }
        }
        Assert.Empty(stack);
        return output.ToString();
    }

    /// <summary>Returns the index of the matching %e (when allowed) or %; after position i.</summary>
    private static int SkipBranch(string format, int i, bool stopAtElse)
    {
        int depth = 0;
        for (i++; i < format.Length - 1; i++)
        {
            if (format[i] != '%') continue;
            char op = format[++i];
            if (op == '?') depth++;
            else if (op == ';' && depth-- == 0) return i;
            else if (op == 'e' && depth == 0 && stopAtElse) return i;
        }
        throw new FormatException("unterminated terminfo conditional");
    }
}
