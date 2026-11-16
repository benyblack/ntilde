using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>Kitty's color-control protocol (OSC 21) over the runtime colors the parser already keeps.</summary>
public sealed class KittyColorTests
{
    private const string Esc = "\u001b";
    private static string Osc(string value) => Esc + "]" + value + Esc + "\\";

    private static (AnsiParser Parser, List<string> Replies) Create()
    {
        var parser = new AnsiParser(new TerminalBuffer(10, 3)) { DefaultForeground = TermColor.White, DefaultBackground = TermColor.Black };
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        return (parser, replies);
    }

    [Fact]
    public void Foreground_query_reports_the_host_default_with_a_standard_terminator()
    {
        var (parser, replies) = Create();
        parser.Process(Osc("21;foreground=?"));
        Assert.Equal(new[] { Osc("21;foreground=rgb:ff/ff/ff") }, replies);
    }

    [Fact]
    public void Bel_terminated_request_gets_a_standard_terminator_reply()
    {
        var (parser, replies) = Create();
        parser.Process(Esc + "]21;background=?\u0007");
        Assert.Equal(new[] { Osc("21;background=rgb:00/00/00") }, replies);
    }

    [Theory]
    [InlineData("#123456", "rgb:12/34/56")]
    [InlineData("#abc", "rgb:aa/bb/cc")]
    [InlineData("rgb:f/0/0", "rgb:ff/00/00")]
    [InlineData("rgb:1234/5678/9abc", "rgb:12/56/9a")]
    [InlineData("rgbi:1/0/0.5", "rgb:ff/00/7f")]
    public void Set_then_query_in_one_sequence_reports_the_new_color(string value, string expected)
    {
        var (parser, replies) = Create();
        parser.Process(Osc("21;foreground=" + value + ";foreground=?"));
        Assert.Equal(new[] { Osc("21;foreground=" + expected) }, replies);
    }

    [Fact]
    public void Fields_are_answered_in_request_order_and_unknown_keys_are_base64_without_padding()
    {
        var (reference, known) = Create();
        reference.Process(Osc("21;7=?;cursor=?"));
        string[] defaults = known[0][(Esc.Length + "]21;".Length)..^2].Split(';');
        var (parser, replies) = Create();
        parser.Process(Osc("21;foreground=?;nonsense=?;7=?;a=?;ab=?;cursor=?;Foreground=?"));
        Assert.Equal(new[]
        {
            Osc($"21;foreground=rgb:ff/ff/ff;unknown=bm9uc2Vuc2U;{defaults[0]};unknown=YQ;unknown=YWI;{defaults[1]};unknown=Rm9yZWdyb3VuZA")
        }, replies);
    }

    [Theory]
    [InlineData("256")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1000")]
    [InlineData("0x1")]
    [InlineData("selection_bg")]
    [InlineData("cursor_text")]
    [InlineData("visual_bell")]
    public void Keys_outside_the_modeled_set_are_unknown(string key)
    {
        var (parser, replies) = Create();
        parser.Process(Osc("21;" + key + "=?"));
        string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(key)).TrimEnd('=');
        Assert.Equal(new[] { Osc("21;unknown=" + encoded) }, replies);
    }

    [Fact]
    public void Palette_keys_share_state_with_osc_4()
    {
        var (parser, replies) = Create();
        parser.Process(Osc("21;5=#abcdef;5=?") + Osc("4;5;?") + Osc("4;9;#010203") + Osc("21;9=?"));
        Assert.Equal(new[] { Osc("21;5=rgb:ab/cd/ef"), Osc("4;5;rgb:abab/cdcd/efef"), Osc("21;9=rgb:01/02/03") }, replies);
    }

    [Fact]
    public void Dynamic_keys_share_state_with_osc_10_11_12_17_19()
    {
        var (parser, replies) = Create();
        parser.Process(Osc("10;#102030") + Osc("11;#405060") + Osc("12;#708090") + Osc("17;#a0b0c0") + Osc("19;#d0e0f0"));
        parser.Process(Osc("21;foreground=?;background=?;cursor=?;selection_background=?;selection_foreground=?"));
        Assert.Equal(new[]
        {
            Osc("21;foreground=rgb:10/20/30;background=rgb:40/50/60;cursor=rgb:70/80/90;selection_background=rgb:a0/b0/c0;selection_foreground=rgb:d0/e0/f0")
        }, replies);
        parser.Process(Osc("21;selection_background=#000001;cursor=#000002"));
        parser.Process(Osc("17;?") + Osc("12;?"));
        Assert.Equal(new[] { Osc("17;rgb:0000/0000/0101"), Osc("12;rgb:0000/0000/0202") }, replies.Skip(1));
    }

    [Fact]
    public void Unset_selection_colors_have_no_defined_value_and_report_an_empty_one()
    {
        var (parser, replies) = Create();
        parser.Process(Osc("21;selection_background=?;selection_foreground=?"));
        Assert.Equal(new[] { Osc("21;selection_background=;selection_foreground=") }, replies);
    }

    [Fact]
    public void A_bare_key_resets_the_override_to_the_host_default()
    {
        var (parser, replies) = Create();
        parser.Process(Osc("21;foreground=#112233;5=#445566;foreground=?;5=?"));
        parser.Process(Osc("21;foreground;5;foreground=?"));
        Assert.Equal(new[] { Osc("21;foreground=rgb:11/22/33;5=rgb:44/55/66"), Osc("21;foreground=rgb:ff/ff/ff") }, replies);
        parser.Process(Osc("4;5;?"));
        Assert.NotEqual(Osc("4;5;rgb:4545/5555/6666"), replies[^1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("green")]
    [InlineData("#123456@0.5")]
    [InlineData("rgb:1/2")]
    [InlineData("#gg0000")]
    [InlineData("rgbi:NaN/0/0")]
    public void Values_the_parser_does_not_model_leave_the_previous_color(string value)
    {
        var (parser, replies) = Create();
        parser.Process(Osc("21;foreground=#102030;foreground=" + value + ";foreground=?"));
        Assert.Equal(new[] { Osc("21;foreground=rgb:10/20/30") }, replies);
    }

    [Fact]
    public void A_set_only_sequence_is_silent()
    {
        var (parser, replies) = Create();
        parser.Process(Osc("21;foreground=#102030;background;7=#000000"));
        Assert.Empty(replies);
    }

    [Fact]
    public void An_oversized_sequence_is_dropped_without_a_reply_or_a_change()
    {
        var (parser, replies) = Create();
        parser.Process(Osc("21;foreground=#102030"));
        parser.Process(Osc("21;foreground=#405060;" + new string('x', 5000) + "=?"));
        parser.Process(Osc("21;foreground=?"));
        Assert.Equal(new[] { Osc("21;foreground=rgb:10/20/30") }, replies);
    }

    [Fact]
    public void Only_the_first_256_fields_are_processed()
    {
        var (parser, replies) = Create();
        parser.Process(Osc("21;" + string.Join(';', Enumerable.Repeat("1=?", 300))));
        Assert.Single(replies);
        Assert.Equal(256, replies[0].Split("1=rgb:").Length - 1);
    }

    [Fact]
    public void Empty_and_bare_osc_21_are_silent()
    {
        var (parser, replies) = Create();
        parser.Process(Osc("21") + Osc("21;") + Osc("21;;;"));
        Assert.Empty(replies);
    }

    [Fact]
    public void Colors_set_through_osc_21_are_saved_and_restored_by_the_kitty_stack()
    {
        var (parser, replies) = Create();
        parser.Process(Osc("21;foreground=#111111") + Osc("30001") + Osc("21;foreground=#222222") + Osc("30101") + Osc("21;foreground=?"));
        Assert.Equal(new[] { Osc("21;foreground=rgb:11/11/11") }, replies);
    }
}
