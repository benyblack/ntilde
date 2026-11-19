using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Moq;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Input;

public sealed class LegacyKeyboardModeRoutingTests
{
    [AvaloniaFact]
    public void Return_sends_crlf_only_while_new_line_mode_is_set()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        var sent = new List<string>();
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(true);
        session.Setup(x => x.SendInput(It.IsAny<string>())).Callback<string>(sent.Add);
        var view = new TerminalView();
        view.SetBuffer(buffer);
        view.SetSession(session.Object);

        Assert.True(view.HandleKeyDownCore(Key.Enter, KeyModifiers.None, PhysicalKey.Enter));
        parser.Process("\u001b[20h");
        Assert.True(view.HandleKeyDownCore(Key.Enter, KeyModifiers.None, PhysicalKey.Enter));
        parser.Process("\u001b[20l");
        Assert.True(view.HandleKeyDownCore(Key.Enter, KeyModifiers.None, PhysicalKey.Enter));

        parser.Process("\u001b[20h\u001bc");
        Assert.True(view.HandleKeyDownCore(Key.Enter, KeyModifiers.None, PhysicalKey.Enter));

        Assert.Equal(new[] { "\r", "\r\n", "\r", "\r" }, sent);
    }

    [Theory]
    [InlineData("\u001b[20h", "\r\n")]
    [InlineData("\u001b[20h\u001b[20l", "\r")]
    public void Broadcast_return_uses_the_receiving_panes_new_line_mode(string modeSequence, string expected)
    {
        var buffer = new TerminalBuffer(80, 24);
        new AnsiParser(buffer).Process(modeSequence);

        Assert.True(MainWindow.TryMapBroadcastKey(Key.Enter, KeyModifiers.None, PhysicalKey.Enter, null,
            buffer, kittyFlags: 0, focusedConsumesText: false, out string? sequence));
        Assert.Equal(expected, sequence);
    }
}
