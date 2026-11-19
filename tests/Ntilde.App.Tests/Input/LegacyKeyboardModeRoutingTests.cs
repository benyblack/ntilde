using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Moq;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Input;

public sealed class LegacyKeyboardModeRoutingTests
{
    private static (TerminalView View, AnsiParser Parser, List<string> Sent) CreateView()
    {
        var buffer = new TerminalBuffer(80, 24);
        var sent = new List<string>();
        var session = new Mock<ITerminalSession>();
        session.SetupGet(x => x.IsProcessRunning).Returns(true);
        session.Setup(x => x.SendInput(It.IsAny<string>())).Callback<string>(sent.Add);
        var view = new TerminalView();
        view.SetBuffer(buffer);
        view.SetSession(session.Object);
        return (view, new AnsiParser(buffer), sent);
    }

    [AvaloniaFact]
    public void Return_sends_crlf_only_while_new_line_mode_is_set()
    {
        var (view, parser, sent) = CreateView();

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

    [AvaloniaTheory]
    [InlineData(KeyModifiers.None, "\x7f", "\b", 4)]
    [InlineData(KeyModifiers.Alt, "\x1b\x7f", "\x1b\b", 0)]
    public void Backspace_sends_bs_only_while_backarrow_key_mode_is_set(
        KeyModifiers modifiers, string resetBytes, string setBytes, int expectedObservations)
    {
        var (view, parser, sent) = CreateView();
        int observed = 0;
        view.BackspaceObserved += () => observed++;

        Assert.True(view.HandleKeyDownCore(Key.Back, modifiers));
        parser.Process("\u001b[?67h");
        Assert.True(view.HandleKeyDownCore(Key.Back, modifiers));
        parser.Process("\u001b[?67l");
        Assert.True(view.HandleKeyDownCore(Key.Back, modifiers));
        parser.Process("\u001b[?67h\u001bc");
        Assert.True(view.HandleKeyDownCore(Key.Back, modifiers));

        Assert.Equal(new[] { resetBytes, setBytes, resetBytes, resetBytes }, sent);
        Assert.Equal(expectedObservations, observed);
    }

    [Theory]
    [InlineData("\u001b[?67h", "\b")]
    [InlineData("\u001b[?67h\u001b[?67l", "\x7f")]
    public void Broadcast_backspace_uses_the_receiving_panes_backarrow_mode(string modeSequence, string expected)
    {
        var buffer = new TerminalBuffer(80, 24);
        new AnsiParser(buffer).Process(modeSequence);

        Assert.True(MainWindow.TryMapBroadcastKey(Key.Back, KeyModifiers.None, PhysicalKey.None, null,
            buffer, kittyFlags: 0, focusedConsumesText: false, out string? sequence));
        Assert.Equal(expected, sequence);
    }
}
