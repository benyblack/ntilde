using System.Text.Json;

namespace Ntilde.VT.Tests;

// Issue #829: xterm modifyOtherKeys negotiation (XTMODKEYS CSI > 4 ; Pv m), its reset forms,
// the XTQMODKEYS query (CSI ? 4 m), per-screen-buffer levels, RIS/DECSTR/prompt resets and the
// complete-state transfer the mux attach path uses.
public class ModifyOtherKeysProtocolTests
{
    private static (TerminalBuffer Buffer, AnsiParser Parser, List<string> Responses) CreateTerminal()
    {
        var buffer = new TerminalBuffer(cols: 40, rows: 8);
        var parser = new AnsiParser(buffer);
        var responses = new List<string>();
        parser.OnResponse = responses.Add;
        return (buffer, parser, responses);
    }

    private static string Query(AnsiParser parser, List<string> responses)
    {
        responses.Clear();
        parser.Process("\x1b[?4m");
        return Assert.Single(responses);
    }

    [Fact]
    public void Default_IsLevelZero_AndQueryReportsIt()
    {
        var (buffer, parser, responses) = CreateTerminal();

        Assert.Equal(0, buffer.Modes.ModifyOtherKeys.Level);
        Assert.Equal("\x1b[>4;0m", Query(parser, responses));
    }

    [Theory]
    [InlineData("\x1b[>4;1m", 1)]
    [InlineData("\x1b[>4;2m", 2)]
    [InlineData("\x1b[>4;2m\x1b[>4;0m", 0)]
    [InlineData("\x1b[>4;2;7m", 2)] // xterm reads only Pp ; Pv
    [InlineData("\x9b>4;1m", 1)]    // C1 CSI
    public void Set_StoresTheLevel_AndQueryReportsIt(string sequence, int expected)
    {
        var (buffer, parser, responses) = CreateTerminal();

        parser.Process(sequence);

        Assert.Equal(expected, buffer.Modes.ModifyOtherKeys.Level);
        Assert.Equal($"\x1b[>4;{expected}m", Query(parser, responses));
    }

    [Theory]
    [InlineData("\x1b[>4m")]   // tmux Dseks, Emacs xterm.el exit
    [InlineData("\x1b[>4;m")]  // Vim's t_TE: empty Pv resets to the initial value
    [InlineData("\x1b[>m")]    // reset every resource
    [InlineData("\x1b[>4n")]   // XTMODKEYS disable
    public void ResetAndDisableForms_ReturnToLevelZero(string reset)
    {
        var (buffer, parser, responses) = CreateTerminal();
        parser.Process("\x1b[>4;2m");

        parser.Process(reset);

        Assert.Equal(0, buffer.Modes.ModifyOtherKeys.Level);
        Assert.Equal("\x1b[>4;0m", Query(parser, responses));
        Assert.Empty(responses.Skip(1));
    }

    [Theory]
    [InlineData("\x1b[>4;3m")]    // level 3 is unsupported
    [InlineData("\x1b[>4;99m")]
    [InlineData("\x1b[>1;0m")]    // other resources are not tracked
    [InlineData("\x1b[>0;1m")]
    [InlineData("\x1b[>2m")]
    [InlineData("\x1b[>4:0m")]    // colon sub-parameters are not implemented
    [InlineData("\x1b[>4;0:1m")]
    [InlineData("\x1b[>4;0$m")]   // intermediates make it a different sequence
    [InlineData("\x1b[>1n")]
    [InlineData("\x1b[>4;0n")]
    [InlineData("\x1b[=4;0m")]
    [InlineData("\x1b[<4;0m")]
    public void UnsupportedForms_LeaveTheLevelUnchanged(string sequence)
    {
        var (buffer, parser, responses) = CreateTerminal();
        parser.Process("\x1b[>4;2m");

        parser.Process(sequence);

        Assert.Equal(2, buffer.Modes.ModifyOtherKeys.Level);
        Assert.Empty(responses);
    }

    [Theory]
    [InlineData("\x1b[?m")]
    [InlineData("\x1b[?1m")]
    [InlineData("\x1b[?0;2m")]
    [InlineData("\x1b[?4:1m")]
    [InlineData("\x1b[?4$m")]
    public void QueriesForUntrackedResources_StaySilent(string query)
    {
        var (_, parser, responses) = CreateTerminal();

        parser.Process(query);

        Assert.Empty(responses);
    }

    [Fact]
    public void Query_ListingSeveralResources_AnswersOnlyResourceFour()
    {
        var (_, parser, responses) = CreateTerminal();
        parser.Process("\x1b[>4;1m");

        parser.Process("\x1b[?1;4;2m");

        Assert.Equal("\x1b[>4;1m", Assert.Single(responses));
    }

    [Fact]
    public void XtModKeysAndQuery_AreNotSgr()
    {
        var (buffer, parser, _) = CreateTerminal();

        parser.Process("\x1b[>4;1m\x1b[?4m\x1b[>4mA");

        Assert.False(buffer.IsUnderline);
        Assert.False(buffer.IsBold);
        buffer.Lock.EnterReadLock();
        try { Assert.Equal('A', buffer.GetCell(0, 0).Character); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void Levels_ArePerScreenBuffer()
    {
        var (buffer, parser, responses) = CreateTerminal();
        parser.Process("\x1b[>4;1m");

        parser.Process("\x1b[?1049h");
        Assert.Equal(0, buffer.Modes.ModifyOtherKeys.Level);
        Assert.Equal("\x1b[>4;0m", Query(parser, responses));

        parser.Process("\x1b[>4;2m");
        Assert.Equal("\x1b[>4;2m", Query(parser, responses));

        parser.Process("\x1b[?1049l");
        Assert.Equal(1, buffer.Modes.ModifyOtherKeys.Level);
        Assert.Equal((1, 2), (buffer.Modes.ModifyOtherKeys.MainLevel, buffer.Modes.ModifyOtherKeys.AltLevel));

        // Like the kitty stacks, the alternate screen's value is retained across visits.
        parser.Process("\x1b[?47h");
        Assert.Equal(2, buffer.Modes.ModifyOtherKeys.Level);
    }

    [Fact]
    public void Ris_ResetsBothScreens_AndReturnsToMain()
    {
        var (buffer, parser, responses) = CreateTerminal();
        parser.Process("\x1b[>4;1m\x1b[?1049h\x1b[>4;2m");

        parser.Process("\u001bc");

        Assert.False(buffer.IsAltScreenActive);
        Assert.Equal(0, buffer.Modes.ModifyOtherKeys.Level);
        Assert.False(buffer.Modes.ModifyOtherKeys.IsAltScreenActive);
        Assert.Equal((0, 0), (buffer.Modes.ModifyOtherKeys.MainLevel, buffer.Modes.ModifyOtherKeys.AltLevel));
        Assert.Equal("\x1b[>4;0m", Query(parser, responses));
    }

    [Fact]
    public void Decstr_ResetsBothScreens_AndKeepsTheActiveScreen()
    {
        // xterm's soft reset restores the key-modifier resources (unlike the kitty stacks).
        var (buffer, parser, responses) = CreateTerminal();
        parser.Process("\x1b[>4;1m\x1b[?1049h\x1b[>4;2m\x1b[>1u");

        parser.Process("\x1b[!p");

        Assert.True(buffer.IsAltScreenActive);
        Assert.True(buffer.Modes.ModifyOtherKeys.IsAltScreenActive);
        Assert.Equal((0, 0), (buffer.Modes.ModifyOtherKeys.MainLevel, buffer.Modes.ModifyOtherKeys.AltLevel));
        Assert.Equal("\x1b[>4;0m", Query(parser, responses));
        Assert.Equal(1, buffer.Modes.KittyKeyboard.Flags);
    }

    [Fact]
    public void ShellPrompt_ResetsTheLevelLikeOtherTransientInputModes()
    {
        var (buffer, parser, _) = CreateTerminal();
        parser.Process("\x1b[>4;2m");

        parser.Process("\x1b]133;A\x07");

        Assert.Equal(0, buffer.Modes.ModifyOtherKeys.Level);
    }

    [Fact]
    public void ModeStateClone_CopiesBothLevelsAndTheActiveScreen()
    {
        var (buffer, parser, _) = CreateTerminal();
        parser.Process("\x1b[>4;1m\x1b[?1049h\x1b[>4;2m");

        ModeState clone = buffer.Modes.Clone();
        parser.Process("\x1b[>4m\x1b[?1049l\x1b[>4m");

        Assert.Equal(2, clone.ModifyOtherKeys.Level);
        Assert.Equal((1, 2), (clone.ModifyOtherKeys.MainLevel, clone.ModifyOtherKeys.AltLevel));
        Assert.True(clone.ModifyOtherKeys.IsAltScreenActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StateTransfer_RoundTripsBothLevelsAndTheLiveOne(bool altActive)
    {
        var (source, parser, _) = CreateTerminal();
        parser.Process("\x1b[>4;1m\x1b[?1049h\x1b[>4;2m");
        if (!altActive) parser.Process("\x1b[?1049l");

        string json = JsonSerializer.Serialize(source.ExportState(maxScrollbackRows: 100));
        Assert.Contains("\"mok_main\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"mok_alt\":2", json, StringComparison.Ordinal);

        var target = new TerminalBuffer(cols: 40, rows: 8);
        target.ImportState(JsonSerializer.Deserialize<TerminalStateSnapshot>(json)!);

        Assert.Equal((1, 2), (target.Modes.ModifyOtherKeys.MainLevel, target.Modes.ModifyOtherKeys.AltLevel));
        Assert.Equal(altActive, target.Modes.ModifyOtherKeys.IsAltScreenActive);
        Assert.Equal(altActive ? 2 : 1, target.Modes.ModifyOtherKeys.Level);

        // The imported selection keeps following later screen switches.
        new AnsiParser(target).Process(altActive ? "\x1b[?1049l" : "\x1b[?1049h");
        Assert.Equal(altActive ? 1 : 2, target.Modes.ModifyOtherKeys.Level);
    }

    [Fact]
    public void StateTransfer_FromALegacyPeerWithoutTheFields_ImportsLevelZero()
    {
        var (source, parser, _) = CreateTerminal();
        TerminalStateSnapshot snapshot = source.ExportState(maxScrollbackRows: 100);
        snapshot.Modes.ModifyOtherKeysMain = null;
        snapshot.Modes.ModifyOtherKeysAlt = null;
        string json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("mok_", json, StringComparison.Ordinal);

        var target = new TerminalBuffer(cols: 40, rows: 8);
        new AnsiParser(target).Process("\x1b[>4;2m\x1b[?1049h\x1b[>4;2m");
        target.ImportState(JsonSerializer.Deserialize<TerminalStateSnapshot>(json)!);

        Assert.Equal((0, 0), (target.Modes.ModifyOtherKeys.MainLevel, target.Modes.ModifyOtherKeys.AltLevel));
        Assert.Equal(0, target.Modes.ModifyOtherKeys.Level);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void StateTransfer_ClampsAnUnsupportedLevelToZero(int corrupted)
    {
        var (source, parser, _) = CreateTerminal();
        parser.Process("\x1b[>4;1m");
        TerminalStateSnapshot snapshot = source.ExportState(maxScrollbackRows: 100);
        snapshot.Modes.ModifyOtherKeysMain = corrupted;
        snapshot.Modes.ModifyOtherKeysAlt = 2;

        var target = new TerminalBuffer(cols: 40, rows: 8);
        target.ImportState(snapshot);

        Assert.Equal((0, 2), (target.Modes.ModifyOtherKeys.MainLevel, target.Modes.ModifyOtherKeys.AltLevel));
    }

    [Fact]
    public void HostWithoutModifyOtherKeys_StillTracksTheLevel_ButQueryReportsZero()
    {
        // Same gate as KittyKeyboardEnabled: the mux daemon clears it while a text client that
        // forwards legacy bytes is attached.
        var (buffer, parser, responses) = CreateTerminal();
        parser.ModifyOtherKeysEnabled = false;

        parser.Process("\x1b[>4;2m");

        Assert.Equal(2, buffer.Modes.ModifyOtherKeys.Level);
        Assert.Equal("\x1b[>4;0m", Query(parser, responses));
        parser.ModifyOtherKeysEnabled = true;
        Assert.Equal("\x1b[>4;2m", Query(parser, responses));
    }
}
