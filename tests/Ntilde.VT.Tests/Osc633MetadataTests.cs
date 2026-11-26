using Ntilde.VT;

namespace Ntilde.VT.Tests;

public class Osc633MetadataTests
{
    [Theory]
    [InlineData(@"echo\x20hi\x3B\\dir\X0Aé", "echo hi;\\dir\né")]
    [InlineData(@"echo \x20hi", "echo  hi")]
    [InlineData(@"\\x3b", @"\x3b")]
    [InlineData("ls -la;opaque-nonce", "ls -la")]
    public void E_DecodesOnlyForTheCurrent633Submission(string record, string expected)
    {
        var parser = Make();
        List<(string?, string?)> seen = [];
        parser.OnCommandAcceptedWithMetadata = (accepted, reported) => seen.Add((accepted, reported));

        parser.Process(Mark("A") + Mark("B") + Mark("E;" + record));
        Assert.Empty(seen);

        parser.Process(Mark("C;ignored") + Mark("D;0") + Mark("A") + Mark("B") + Mark("C"));
        Assert.Equal([(null, expected), (null, null)], seen);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"hi\x")]
    [InlineData(@"hi\xGG")]
    [InlineData(@"hi\g")]
    [InlineData(@"hi\x00")]
    [InlineData(@"hi\x1b")]
    [InlineData(@"hi\x80")]
    [InlineData("hi\tthere")]
    [InlineData("hi;")]
    [InlineData("hi;not a token")]
    [InlineData("hi;token;extra")]
    public void InvalidE_DiscardsThePendingReport(string record)
    {
        var parser = Make();
        List<string?> seen = [];
        parser.OnCommandAcceptedWithMetadata = (_, reported) => seen.Add(reported);

        parser.Process(Mark("B") + Mark("E;previous") + Mark("E;" + record) + Mark("C"));

        Assert.Null(Assert.Single(seen));
    }

    [Theory]
    [InlineData("\x1b]633;A\a")]
    [InlineData("\x1b]633;B\a")]
    [InlineData("\x1b]633;D\a")]
    [InlineData("\x1b]133;A\a")]
    [InlineData("\x1b]133;B\a")]
    [InlineData("\x1b]133;D;3\a")]
    [InlineData("\x1b" + "c")]
    [InlineData("\x1b[!p")]
    public void CycleBoundary_AbandonsUnsubmittedMetadata(string boundary)
    {
        var parser = Make();
        List<string?> seen = [];
        parser.OnCommandAcceptedWithMetadata = (_, reported) => seen.Add(reported);

        parser.Process(Mark("B") + Mark("E;discard") + boundary + Mark("C"));

        Assert.Null(Assert.Single(seen));
    }

    [Fact]
    public void E_CannotEnterAnExisting133SubmissionOrReopenAConsumedCycle()
    {
        var parser = Make();
        List<(string?, string?)> seen = [];
        parser.OnCommandAcceptedWithMetadata = (accepted, reported) => seen.Add((accepted, reported));

        parser.Process(Mark("E;before-B") + Mark("B") + Mark("E;before-133-C")
            + "\x1b]133;C;ZWNobyB2YWxpZA==\a" + Mark("E;after-C") + Mark("C")
            + "\x1b]133;B\a" + Mark("E;after-133-B") + Mark("C"));

        Assert.Equal([("echo valid", null), (null, null), (null, null)], seen);
    }

    [Fact]
    public void E_IsEphemeralAcrossStateTransfer()
    {
        var source = Make();
        source.Process(Mark("B") + Mark("E;source"));
        var receiving = Make();
        receiving.Process(Mark("B") + Mark("E;recipient"));
        List<string?> seen = [];
        receiving.OnCommandAcceptedWithMetadata = (_, reported) => seen.Add(reported);

        receiving.ImportState(source.ExportState());
        receiving.Process(Mark("E;after-import") + Mark("C"));

        Assert.Null(Assert.Single(seen));
    }

    [Fact]
    public void SplitRecords_ApplyAtTheTerminatorAndOnlyCwdUpdatesTheReportedPath()
    {
        var parser = Make();
        List<string?> commands = [];
        List<string> directories = [];
        parser.OnCommandAcceptedWithMetadata = (_, reported) => commands.Add(reported);
        parser.OnWorkingDirectoryChanged = directories.Add;

        parser.Process(Mark("B") + "\x1b]633;E;one\\x");
        parser.Process("3btwo\x1b");
        parser.Process("\\");
        Assert.Empty(commands);
        parser.Process(Mark("C"));
        Assert.Equal(["one;two"], commands);

        parser.Process("\x1b]7;file:///tmp/control\a\x1b]633;P;Cwd=/tmp/one\\");
        parser.Process("x3b=two\x1b");
        Assert.Equal(["/tmp/control"], directories);
        parser.Process("\\" + Mark(@"P;Cwd=C:\\second\x20x" + ";opaque")
            + Mark("P;Other=/tmp/irrelevant") + Mark("P;Cwd") + Mark("P;Cwd=")
            + Mark(@"P;Cwd=/tmp/\x0a") + Mark("P;Cwd=/tmp;token;extra")
            + "\x1b]133;P;Cwd=/tmp/ignored\a");

        Assert.Equal(["/tmp/control", "/tmp/one;=two", @"C:\second x"], directories);
    }

    [Fact]
    public void MetadataAndNonceLengthsAreBounded()
    {
        var parser = Make();
        List<string?> commands = [];
        List<string> directories = [];
        parser.OnCommandAcceptedWithMetadata = (_, reported) => commands.Add(reported);
        parser.OnWorkingDirectoryChanged = directories.Add;
        string atLimit = new('a', AnsiParser.MaxVsCodeMetadataChars);

        parser.Process(Mark("B") + Mark("E;" + atLimit) + Mark("C")
            + Mark("B") + Mark("E;" + atLimit + "b") + Mark("C")
            + Mark("B") + Mark("E;hello;" + new string('n', 129)) + Mark("C")
            + Mark("P;Cwd=/tmp/short") + Mark("P;Cwd=/" + atLimit));

        Assert.Equal([atLimit, null, null], commands);
        Assert.Equal(["/tmp/short"], directories);
    }

    private static AnsiParser Make() => new(new TerminalBuffer(40, 8));

    private static string Mark(string body) => $"\x1b]633;{body}\a";
}
