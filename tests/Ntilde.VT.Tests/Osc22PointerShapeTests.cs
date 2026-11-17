namespace Ntilde.VT.Tests;

public class Osc22PointerShapeTests
{
    [Theory]
    [InlineData("pointer", "pointer")]
    [InlineData("text", "text")]
    [InlineData("crosshair", "crosshair")]
    [InlineData("default", "default")]
    [InlineData("=text", "text")]
    [InlineData("", null)]
    [InlineData("=", null)]
    public void BasicSetter_EmitsOnlyTheBoundedRequest_WithoutMovingTheCursor(string payload, string? expected)
    {
        foreach (string terminator in new[] { "\x07", "\x1b\\" })
        {
            var buffer = new TerminalBuffer(20, 8);
            var parser = new AnsiParser(buffer);
            var requests = new List<(string?, bool)>();
            var replies = new List<string>();
            parser.OnPointerShapeRequested = (shape, alternate) => requests.Add((shape, alternate));
            parser.OnResponse = replies.Add;
            foreach (char c in $"\x1b]22;{payload}{terminator}") parser.Process(c.ToString());
            Assert.Equal(new[] { (expected, false) }, requests);
            Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
            Assert.Empty(replies);
        }
    }

    [Theory]
    [InlineData("Pointer")]
    [InlineData(" pointer")]
    [InlineData("pointer,text")]
    [InlineData("hand2")]
    [InlineData("wait")]
    [InlineData(">pointer")]
    [InlineData("<")]
    [InlineData("?__current__")]
    [InlineData("?pointer")]
    [InlineData("pointer;ignored")]
    public void UnsupportedForms_DoNotChangeOrAdvertisePointerState(string payload)
    {
        var parser = new AnsiParser(new TerminalBuffer(20, 8));
        parser.OnPointerShapeRequested = (_, _) => Assert.Fail("unsupported request forwarded");
        parser.OnResponse = _ => Assert.Fail("unsupported query answered");
        parser.Process($"\x1b]22;{payload}\x07");
    }

    [Fact]
    public void ScreenIdentityIsCapturedAtTheRequest_AndResetsAreExplicit()
    {
        var parser = new AnsiParser(new TerminalBuffer(20, 8));
        var requests = new List<(string?, bool)>();
        int resets = 0;
        parser.OnPointerShapeRequested = (shape, alternate) => requests.Add((shape, alternate));
        parser.OnPointerShapeReset = () => resets++;
        parser.Process("\x1b]22;text\x07\x1b[?1049h\x1b]22;crosshair\x07\x1b[?1049l\x1b]22;pointer\x07");
        Assert.Equal(new (string?, bool)[] { ("text", false), ("crosshair", true), ("pointer", false) }, requests);
        parser.Process("\x1b[!p\u001bc");
        Assert.Equal(2, resets);
    }

    [Fact]
    public void OversizedNameIsIgnored_AndTheNextValidRequestStillWorks()
    {
        var parser = new AnsiParser(new TerminalBuffer(20, 8));
        var requests = new List<string?>();
        parser.OnPointerShapeRequested = (shape, _) => requests.Add(shape);
        parser.Process("\x1b]22;" + new string('a', 33) + "\x07\x1b]22;text\x07");
        Assert.Equal(new[] { "text" }, requests);
    }
}
