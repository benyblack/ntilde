using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Moq;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Input;

public sealed class X10MouseRoutingTests
{
    [AvaloniaTheory]
    [InlineData((int)TerminalMouseButton.Left, false, 32)]
    [InlineData((int)TerminalMouseButton.Middle, false, 33)]
    [InlineData((int)TerminalMouseButton.Right, false, 34)]
    [InlineData((int)TerminalMouseButton.Left, true, 0)]
    [InlineData((int)TerminalMouseButton.Middle, true, 1)]
    [InlineData((int)TerminalMouseButton.Right, true, 2)]
    public void Parser_enabled_X10_presses_use_production_coordinates_and_ignore_modifier_bits(int buttonValue, bool sgr, byte code)
    {
        var button = (TerminalMouseButton)buttonValue;
        var bytes = new List<byte[]>(); var text = new List<string>();
        var session = new Mock<ITerminalSession>();
        session.Setup(x => x.SendInput(It.IsAny<string>())).Callback<string>(text.Add);
        var raw = session.As<ITerminalByteInput>(); raw.SetupGet(x => x.SupportsByteInput).Returns(true);
        raw.Setup(x => x.TrySendInputBytes(It.IsAny<ReadOnlyMemory<byte>>()))
            .Callback<ReadOnlyMemory<byte>>(value => bytes.Add(value.ToArray())).Returns(true);
        var buffer = new TerminalBuffer(300, 250);
        new AnsiParser(buffer).Process(sgr ? "\u001b[?9;1006h" : "\u001b[?9h");
        var view = new TerminalView(); view.SetBuffer(buffer); view.SetSession(session.Object); view.SetMetricsForTest(8, 16);
        view.HandleMousePressAt(new Point(95.5 * 8, 222.5 * 16), button, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift);
        if (sgr) { Assert.Equal($"\u001b[<{code};96;223M", Assert.Single(text)); Assert.Empty(bytes); }
        else { Assert.Equal(new byte[] { 27, 91, 77, code, 128, 255 }, Assert.Single(bytes)); Assert.Empty(text); }
        view.HandleMouseMoveAt(new Point(96.5 * 8, 222.5 * 16), button, KeyModifiers.Control);
        Assert.Equal(1, bytes.Count + text.Count);
    }

    [Theory]
    [InlineData((int)TerminalMouseEventKind.Release, (int)TerminalMouseButton.Left)]
    [InlineData((int)TerminalMouseEventKind.Release, (int)TerminalMouseButton.Right)]
    [InlineData((int)TerminalMouseEventKind.Move, (int)TerminalMouseButton.Left)]
    [InlineData((int)TerminalMouseEventKind.Move, (int)TerminalMouseButton.None)]
    [InlineData((int)TerminalMouseEventKind.Wheel, (int)TerminalMouseButton.WheelUp)]
    [InlineData((int)TerminalMouseEventKind.Wheel, (int)TerminalMouseButton.WheelDown)]
    [InlineData((int)TerminalMouseEventKind.Press, (int)TerminalMouseButton.None)]
    [InlineData((int)TerminalMouseEventKind.Press, (int)TerminalMouseButton.WheelUp)]
    public void X10_ignores_nonpresses_and_buttons_outside_the_three_button_contract(int kindValue, int buttonValue)
    {
        var kind = (TerminalMouseEventKind)kindValue; var button = (TerminalMouseButton)buttonValue;
        var buffer = new TerminalBuffer(80, 24); new AnsiParser(buffer).Process("\u001b[?9h");
        Assert.Null(TerminalInputModeEncoder.EncodeMouseEvent(buffer.Modes,
            new TerminalMouseEvent(kind, button, 10, 5, KeyModifiers.Control)));
    }
}
