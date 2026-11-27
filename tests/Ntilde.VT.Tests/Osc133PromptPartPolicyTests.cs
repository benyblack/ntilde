namespace Ntilde.VT.Tests;

/// <summary>OSC 133 P is not OSC 633's property setter or an A/B/C/D lifecycle edge.</summary>
public class Osc133PromptPartPolicyTests
{
    public static TheoryData<string, string> Forms => new()
    {
        { "P;Cwd=/tmp", "\x07" },
        { "P;Cwd=/tmp", "\x1b\\" },
        { "P;k=r", "\x07" },
        { "P;k=r", "\x1b\\" },
        { "P;k=i", "\x07" },
        { "P;k=c", "\x07" },
        { "P;k=s", "\x07" },
        { "P", "\x07" },
    };

    [Fact]
    public void RedrawReplacesBoundariesAndAnOverwriteRestoresTheFullCommand()
    {
        var buffer = new TerminalBuffer(60, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]133;A\a$ \x1b]133;B\acmd\x1b[12G\x1b]133;P;k=r\aold");
        parser.Process("\x1b[12G\x1b[K\x1b[8G\x1b]133;P;k=r\aRP\x1b[6G");
        Assert.Equal(new PromptPart("r", 0, 7), Assert.Single(buffer.ReadPromptParts()));
        var anchor = buffer.CommandStartMark!.Value;
        Assert.True(GridQueryReader.TryReadCommandLine(buffer, anchor, out var line));
        Assert.Equal("cmd", line.Text);
        Assert.True(line.RightPromptTrimmed);

        parser.Process("xyz!");
        Assert.Empty(buffer.ReadPromptParts());
        Assert.True(GridQueryReader.TryReadCommandLine(buffer, anchor, out line));
        Assert.Equal("cmdxyz!", line.Text);
        Assert.False(line.RightPromptTrimmed);
    }

    [Theory]
    [InlineData("\x1b[2@", 10, "cmd")]
    [InlineData("\x1b[2P", 6, "cmd")]
    [InlineData("\x1b[4hXY\x1b[4l", 10, "cmdXY")]
    [InlineData("\x1b[2'}", 10, "cmd")]
    [InlineData("\x1b[2'~", 6, "cmd")]
    public void ColumnEditsMoveTheRightBoundaryWithItsText(string edit, int column, string command)
    {
        var buffer = new TerminalBuffer(24, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]133;A\a\x1b]133;P\a$ \x1b]133;B\acmd\x1b[9G\x1b]133;P;k=r\aRHS\x1b[6G");
        var anchor = buffer.CommandStartMark!.Value;
        parser.Process(edit);
        Assert.Equal([new PromptPart("i", 0, 0), new PromptPart("r", 0, column)], buffer.ReadPromptParts());
        buffer.Lock.EnterReadLock();
        try { Assert.Equal('R', buffer.GetCell(column, 0).Character); }
        finally { buffer.Lock.ExitReadLock(); }
        Assert.Equal(anchor, buffer.CommandStartMark);
        Assert.True(GridQueryReader.TryReadCommandLine(buffer, anchor, out var line));
        Assert.True(line.RightPromptTrimmed);
        Assert.Equal(command, line.Text);
    }

    [Theory]
    [InlineData("\x1b[5G\x1b[3@", 7)]
    [InlineData("\x1b[5G\x1b[2P", -1)]
    [InlineData("\x1b[5G\x1b[4@", -1)]
    public void ColumnEditsRespectMarginsAndRetireLostStarts(string edit, int survivingColumn)
    {
        var buffer = new TerminalBuffer(24, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]133;A\a\x1b]133;P\a$\x1b[5G\x1b]133;P;k=r\aR\x1b[12G\x1b]133;P;k=c\aC");
        parser.Process("\x1b[?69h\x1b[4;8s" + edit);
        var parts = buffer.ReadPromptParts();
        Assert.Contains(new PromptPart("i", 0, 0), parts);
        Assert.Contains(new PromptPart("c", 0, 11), parts);
        if (survivingColumn >= 0) Assert.Contains(new PromptPart("r", 0, survivingColumn), parts);
        else Assert.DoesNotContain(parts, part => part.Kind == "r");
    }

    [Theory]
    [InlineData("\x1b]133;C\a")]
    [InlineData("\x1b]133;D;0\a")]
    [InlineData("\x1b]133;A\a")]
    [InlineData("\x1b]633;A\a")]
    [InlineData("\x1b[2J")]
    [InlineData("\x1b[!p")]
    [InlineData("\x1b[?1049h\x1b[?1049l")]
    [InlineData("\x1b" + "c")]
    public void RetiresAllPartsAtCycleOrScreenBoundaries(string end)
    {
        var buffer = new TerminalBuffer(40, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]133;P;k=r\a");
        Assert.Empty(buffer.ReadPromptParts());
        parser.Process("\x1b]133;A\a\x1b]133;P\a$ \x1b]133;B\a\x1b[2;5H\x1b]133;P;k=r\aRP");
        Assert.Equal([new PromptPart("i", 0, 0), new PromptPart("r", 1, 4)], buffer.ReadPromptParts());
        parser.Process(end);
        Assert.Empty(buffer.ReadPromptParts());
    }

    [Theory]
    [InlineData("\x1b[5Gcdefg", "abcdefg")]
    [InlineData("\x1b[5G\x1b[7Gxyz", "ab  xyz")]
    public void ReturningToAnUnpaintedRightPartOverwritesIt(string input, string expected)
    {
        var buffer = new TerminalBuffer(30, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]133;A\a$ \x1b]133;B\aab\x1b[7G\x1b]133;P;k=r\a");
        Assert.Equal(new PromptPart("r", 0, 6), Assert.Single(buffer.ReadPromptParts()));
        parser.Process(input);
        Assert.Empty(buffer.ReadPromptParts());
        Assert.True(GridQueryReader.TryReadCommandLine(buffer, buffer.CommandStartMark!.Value, out var line));
        Assert.Equal(expected, line.Text);
    }

    [Theory]
    [InlineData("\x1b[S")]
    [InlineData("\x1b[T")]
    public void PartialWidthScrollRetiresOnlyStartsInTheRewrittenBand(string scroll)
    {
        var buffer = new TerminalBuffer(30, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]133;A\a\x1b]133;P\a$ \x1b]133;B\acmd");
        parser.Process("\x1b[3;2H\x1b]133;P;k=c\aC\x1b[3;12H\x1b]133;P;k=r\aR");
        parser.Process("\x1b[2;6r\x1b[?69h\x1b[3;19s" + scroll);
        Assert.Equal([new PromptPart("i", 0, 0), new PromptPart("c", 2, 1)], buffer.ReadPromptParts());
    }

    [Fact]
    public void SelectiveDisplayEraseAndLineSizePreserveSurvivingStarts()
    {
        var buffer = new TerminalBuffer(24, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]133;A\a\x1b]133;P\a$ \x1b[8G\x1b]133;P;k=r\a\x1b[1\"qR\x1b[0\"q");
        parser.Process("\x1b[18G\x1b]133;P;k=s\aS\x1b[6G\x1b[?0J");
        Assert.Equal([new PromptPart("i", 0, 0), new PromptPart("r", 0, 7)], buffer.ReadPromptParts());
        parser.Process("\x1b[18G\x1b]133;P;k=s\aS\x1b#6");
        Assert.Equal([new PromptPart("i", 0, 0), new PromptPart("r", 0, 7)], buffer.ReadPromptParts());
    }

    [Fact]
    public void SelectiveFullDisplayEraseKeepsOnlyProtectedStartsAndOrdinaryClearRetiresThem()
    {
        var buffer = new TerminalBuffer(30, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]133;A\a\x1b]133;P\a$ \x1b]133;B\acmd\x1b[9G\x1b]133;P;k=r\a\x1b[1\"qR\x1b[0\"qX");
        var anchor = buffer.CommandStartMark!.Value;
        parser.Process("\x1b[?2J");
        Assert.Equal(new PromptPart("r", 0, 8), Assert.Single(buffer.ReadPromptParts()));
        Assert.Equal(anchor, buffer.CommandStartMark);
        Assert.True(GridQueryReader.TryReadCommandLine(buffer, anchor, out var line));
        Assert.True(line.RightPromptTrimmed);
        Assert.DoesNotContain("R", line.Text);
        parser.Process("\x1b[2J");
        Assert.Empty(buffer.ReadPromptParts());
    }

    [Fact]
    public void KittySecondaryAndContourContinuationAreIndependentAndResizeAwaitsRedraw()
    {
        var buffer = new TerminalBuffer(40, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]133;A;click_events=2\a$\x1b[2;1H\x1b]133;P;k=s\aP");
        Assert.True(buffer.TryGetPromptClickRow(1, out int row, out _));
        Assert.Equal(2, row);
        parser.Process("\x1b[3;1H\x1b]133;A;k=s\aK");
        Assert.True(buffer.TryGetPromptClickRow(2, out row, out _));
        Assert.Equal(1, row);
        Assert.Equal(new PromptPart("s", 1, 0), Assert.Single(buffer.ReadPromptParts()));
        buffer.Resize(45, 8);
        Assert.Empty(buffer.ReadPromptParts());
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void P_LeavesCwdAndInputStateAlone_WithoutInventingALifecycleEdge(string body, string terminator)
    {
        var buffer = new TerminalBuffer(40, 8);
        var parser = new AnsiParser(buffer);
        var lifecycle = new List<string>();
        var directories = new List<string>();
        parser.OnPromptReady = () => lifecycle.Add("A");
        parser.OnCommandStarted = _ => lifecycle.Add("B");
        parser.OnCommandAccepted = _ => lifecycle.Add("C");
        parser.OnCommandFinished = _ => lifecycle.Add("D");
        parser.OnWorkingDirectoryChanged = directories.Add;
        parser.Process("\x1b]7;file://localhost/ntilde-qa/baseline\x07\x1b]133;A\x07$ \x1b]133;B\x07input");
        Assert.Equal(["A", "B"], lifecycle);
        Assert.Equal(["/ntilde-qa/baseline"], directories);
        Assert.True(buffer.IsAcceptingCommandInput);
        var anchor = buffer.CommandStartMark;
        var cursor = (buffer.CursorRow, buffer.CursorCol);
        lifecycle.Clear();
        directories.Clear();

        // Feed one character at a time to include fragmented OSC and split ST handling.
        foreach (char c in $"\x1b]133;{body}{terminator}") parser.Process(c.ToString());

        var parts = buffer.ReadPromptParts();
        if (body.StartsWith("P;Cwd", StringComparison.Ordinal))
            Assert.Empty(parts);
        else
            Assert.Equal(new PromptPart(body == "P" ? "i" : body[^1..], cursor.CursorRow, cursor.CursorCol), Assert.Single(parts));
        Assert.Empty(lifecycle);
        Assert.Empty(directories);
        Assert.True(buffer.IsAcceptingCommandInput);
        Assert.Equal(anchor, buffer.CommandStartMark);
        Assert.Equal(cursor, (buffer.CursorRow, buffer.CursorCol));
        parser.Process("X"); // marker consumption is only a text-delivery control
        buffer.Lock.EnterReadLock();
        try { Assert.Equal('X', buffer.GetCell(cursor.CursorCol, cursor.CursorRow).Character); }
        finally { buffer.Lock.ExitReadLock(); }

        // Distinct positive controls still work after P; finish/reset the prepared state.
        parser.Process("\x1b]133;C\x07\x1b]133;D;0\x07\x1b]7;file://localhost/ntilde-qa/changed\x07");
        Assert.Equal(["C", "D"], lifecycle);
        Assert.False(buffer.IsAcceptingCommandInput);
        Assert.Equal(["/ntilde-qa/changed"], directories);
        parser.Process("\x1b]7;file://localhost/ntilde-qa/baseline\x07");
        Assert.Equal("/ntilde-qa/baseline", directories[^1]);
    }
}
