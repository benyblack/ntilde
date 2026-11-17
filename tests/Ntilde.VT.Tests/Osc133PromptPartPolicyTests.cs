namespace Ntilde.VT.Tests;

/// <summary>OSC 133 P is not OSC 633's property setter or an A/B/C/D lifecycle edge.</summary>
public class Osc133PromptPartPolicyTests
{
    public static TheoryData<string, string> IgnoredForms => new()
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

    [Theory]
    [MemberData(nameof(IgnoredForms))]
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
