using System.Text.Json;

namespace Ntilde.VT.Tests;

/// <summary>
/// ?1005 (UTF-8), ?1015 (urxvt) and ?1016 (SGR-Pixels) coordinate encodings share one selection with ?1006 (xterm's extend_coords):
/// setting one replaces the others, a reset only clears the encoding it names, RIS clears, DECSTR keeps.
/// </summary>
public sealed class ExtendedMouseCoordinateModeTests
{
    private static readonly int[] Encodings = { 1005, 1006, 1015, 1016 };

    private static (TerminalBuffer Buffer, AnsiParser Parser, List<string> Replies) Create()
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        return (buffer, parser, replies);
    }

    private static string[] Query(AnsiParser parser, List<string> replies)
    {
        replies.Clear();
        foreach (int mode in Encodings) parser.Process($"\u001b[?{mode}$p");
        return replies.ToArray();
    }

    private static string[] Expect(int? active) =>
        Encodings.Select(mode => $"\u001b[?{mode};{(mode == active ? 1 : 2)}$y").ToArray();

    [Theory]
    [InlineData(1005)]
    [InlineData(1015)]
    [InlineData(1016)]
    public void Mode_has_exact_live_queries_and_fragmented_enable_reset(int mode)
    {
        var (_, parser, replies) = Create();
        parser.Process($"\u001b[?{mode}$p");
        foreach (char value in $"\u001b[?{mode}h\u001b[?{mode}$p\u001b[?{mode}l\u001b[?{mode}$p") parser.Process(value.ToString());
        Assert.Equal(new[] { $"\u001b[?{mode};2$y", $"\u001b[?{mode};1$y", $"\u001b[?{mode};2$y" }, replies);
    }

    public static IEnumerable<object[]> OrderedPairs =>
        from first in Encodings
        from second in Encodings
        where first != second
        select new object[] { first, second };

    [Theory]
    [MemberData(nameof(OrderedPairs))]
    public void Setting_one_encoding_replaces_the_one_that_was_active(int first, int second)
    {
        var (buffer, parser, replies) = Create();
        parser.Process($"\u001b[?{first}h");
        Assert.Equal(Expect(first), Query(parser, replies));
        parser.Process($"\u001b[?{second}h");
        Assert.Equal(Expect(second), Query(parser, replies));
        Assert.Equal(second == 1005, buffer.Modes.MouseModeUtf8);
        Assert.Equal(second == 1006, buffer.Modes.MouseModeSGR);
        Assert.Equal(second == 1015, buffer.Modes.MouseModeUrxvt);
        Assert.Equal(second == 1016, buffer.Modes.MouseModePixel);
    }

    [Fact]
    public void Several_modes_in_one_sequence_end_with_the_last_one_selected()
    {
        var (_, parser, replies) = Create();
        parser.Process("\u001b[?1005;1006;1015;1016h");
        Assert.Equal(Expect(1016), Query(parser, replies));
        parser.Process("\u001b[?1005;1006;1015h");
        Assert.Equal(Expect(1015), Query(parser, replies));
        parser.Process("\u001b[?1015;1005h");
        Assert.Equal(Expect(1005), Query(parser, replies));
    }

    public static IEnumerable<object[]> ActiveAndReset =>
        from active in Encodings
        from reset in Encodings
        select new object[] { active, reset };

    [Theory]
    [MemberData(nameof(ActiveAndReset))]
    public void A_reset_is_only_effective_against_the_matching_encoding(int active, int reset)
    {
        var (_, parser, replies) = Create();
        parser.Process($"\u001b[?{active}h\u001b[?{reset}l");
        Assert.Equal(Expect(active == reset ? null : active), Query(parser, replies));
    }

    [Fact]
    public void Encoding_selection_is_independent_of_the_tracking_selector()
    {
        var (buffer, parser, replies) = Create();
        parser.Process("\u001b[?1002h\u001b[?1005h\u001b[?1015h\u001b[?1006h\u001b[?1006l");
        Assert.True(buffer.Modes.MouseModeButtonEvent);
        Assert.Equal(Expect(null), Query(parser, replies));
        Assert.True(buffer.IsMouseReportingActive());
        parser.Process("\u001b[?1002l\u001b[?1015h");
        Assert.False(buffer.IsMouseReportingActive());
        Assert.True(buffer.Modes.MouseModeUrxvt);
    }

    [Theory]
    [InlineData(1005)]
    [InlineData(1015)]
    [InlineData(1016)]
    public void Soft_reset_keeps_the_encoding_but_full_reset_and_the_prompt_policy_clear_it(int mode)
    {
        var (buffer, parser, replies) = Create();
        parser.Process($"\u001b[?1000h\u001b[?{mode}h\u001b[!p");
        Assert.Equal(Expect(mode), Query(parser, replies));
        parser.Process("\u001bc");
        Assert.Equal(Expect(null), Query(parser, replies));

        parser.Process($"\u001b[?{mode}h");
        buffer.Modes.ResetTransientInputReporting();
        Assert.Equal(Expect(null), Query(parser, replies));

        parser.Process($"\u001b[?{mode}h\u001b]133;A\u0007");
        Assert.Equal(Expect(null), Query(parser, replies));
    }

    [Theory]
    [InlineData(1005)]
    [InlineData(1015)]
    [InlineData(1016)]
    public void Clone_and_complete_snapshot_preserve_the_encoding_and_old_snapshots_default_off(int mode)
    {
        var source = new TerminalBuffer(20, 5);
        new AnsiParser(source).Process($"\u001b[?1000h\u001b[?{mode}h");
        Assert.Equal(mode == 1005, source.Modes.Clone().MouseModeUtf8);
        Assert.Equal(mode == 1015, source.Modes.Clone().MouseModeUrxvt);
        Assert.Equal(mode == 1016, source.Modes.Clone().MouseModePixel);

        string json = JsonSerializer.Serialize(source.ExportState(0));
        string property = mode switch { 1005 => "\"m_utf8\":true", 1015 => "\"m_urxvt\":true", _ => "\"m_pixel\":true" };
        Assert.Contains(property, json, StringComparison.Ordinal);
        var target = new TerminalBuffer(20, 5);
        target.ImportState(JsonSerializer.Deserialize<TerminalStateSnapshot>(json)!);
        Assert.Equal(mode == 1005, target.Modes.MouseModeUtf8);
        Assert.Equal(mode == 1015, target.Modes.MouseModeUrxvt);
        Assert.Equal(mode == 1016, target.Modes.MouseModePixel);
        Assert.False(target.Modes.MouseModeSGR);

        // Payloads from builds that never knew the encoding carry neither property; they import as off.
        target.ImportState(JsonSerializer.Deserialize<TerminalStateSnapshot>(json.Replace(property + ",", "").Replace("," + property, ""))!);
        Assert.False(target.Modes.MouseModeUtf8);
        Assert.False(target.Modes.MouseModeUrxvt);
        Assert.False(target.Modes.MouseModePixel);
    }

    [Fact]
    public void A_snapshot_with_neither_encoding_writes_neither_property_so_old_payloads_stay_byte_identical()
    {
        var source = new TerminalBuffer(20, 5);
        new AnsiParser(source).Process("\u001b[?1000h\u001b[?1006h");
        string json = JsonSerializer.Serialize(source.ExportState(0));
        Assert.DoesNotContain("m_utf8", json, StringComparison.Ordinal);
        Assert.DoesNotContain("m_urxvt", json, StringComparison.Ordinal);
        Assert.DoesNotContain("m_pixel", json, StringComparison.Ordinal);
        Assert.Contains("\"m_sgr\":true", json, StringComparison.Ordinal);
    }
}
