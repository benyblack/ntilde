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
