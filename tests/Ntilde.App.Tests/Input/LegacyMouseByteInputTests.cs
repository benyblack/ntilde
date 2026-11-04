using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Moq;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Input;

public sealed class LegacyMouseByteInputTests
{
    private static (TerminalView View, TerminalBuffer Buffer, Mock<ITerminalSession> Session, List<string> Text, List<byte[]> Bytes)
        Create(bool? supportsBytes, bool accepts = true)
    {
        var text = new List<string>(); var bytes = new List<byte[]>();
        var session = new Mock<ITerminalSession>();
        session.Setup(x => x.SendInput(It.IsAny<string>())).Callback<string>(text.Add);
        if (supportsBytes.HasValue)
        {
            var raw = session.As<ITerminalByteInput>();
            raw.SetupGet(x => x.SupportsByteInput).Returns(supportsBytes.Value);
            raw.Setup(x => x.TrySendInputBytes(It.IsAny<ReadOnlyMemory<byte>>()))
                .Callback<ReadOnlyMemory<byte>>(value => bytes.Add(value.ToArray())).Returns(accepts);
        }
        var buffer = new TerminalBuffer(300, 250);
        buffer.Modes.MouseModeX10 = true;
        var view = new TerminalView(); view.SetBuffer(buffer); view.SetSession(session.Object); view.SetMetricsForTest(8, 16);
        return (view, buffer, session, text, bytes);
    }
    private static Point At(int column, int row) => new((column - 0.5) * 8, (row - 0.5) * 16);

    [AvaloniaTheory]
    [InlineData(95, 95, 127, 127)]
    [InlineData(96, 223, 128, 255)]
    [InlineData(223, 223, 255, 255)]
    [InlineData(300, 250, 255, 255)]
    public void Production_coordinate_conversion_delivers_six_exact_legacy_octets(int column, int row, byte x, byte y)
    {
        var (view, _, _, text, bytes) = Create(true);
        view.HandleMousePressAt(At(column, row), TerminalMouseButton.Left, KeyModifiers.None);
        Assert.Equal(new byte[] { 27, 91, 77, 32, x, y }, Assert.Single(bytes));
        Assert.Empty(text);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(null)]
    public void A_text_only_peer_can_carry_ASCII_but_cannot_expand_high_coordinate_bytes(bool? supports)
    {
        var (view, _, _, text, bytes) = Create(supports);
        view.HandleMousePressAt(At(95, 95), TerminalMouseButton.Left, KeyModifiers.None);
        Assert.Equal("\u001b[M \u007f\u007f", Assert.Single(text));
        view.HandleMousePressAt(At(96, 223), TerminalMouseButton.Left, KeyModifiers.None);
        Assert.Single(text); Assert.Empty(bytes);
    }

    [AvaloniaFact]
    public void SGR_stays_on_the_existing_ASCII_text_path_without_legacy_clamping()
    {
        var (view, buffer, _, text, bytes) = Create(true);
        buffer.Modes.MouseModeSGR = true;
        view.HandleMousePressAt(At(300, 250), TerminalMouseButton.Left, KeyModifiers.Control);
        Assert.Equal("\u001b[<16;300;250M", Assert.Single(text)); Assert.Empty(bytes);
    }

    [AvaloniaFact]
    public void Refused_binary_input_cannot_fall_back_to_a_different_text_write()
    {
        var (view, _, _, text, bytes) = Create(true, accepts: false);
        view.HandleMousePressAt(At(1, 1), TerminalMouseButton.Left, KeyModifiers.None);
        Assert.Single(bytes); Assert.Empty(text);
    }

    [AvaloniaFact]
    public void Refused_motion_is_retried_at_the_same_cell_then_coalesced_after_acceptance()
    {
        var (view, buffer, session, text, _) = Create(true);
        buffer.Modes.MouseModeButtonEvent = true;
        int attempts = 0;
        session.As<ITerminalByteInput>().Setup(x => x.TrySendInputBytes(It.IsAny<ReadOnlyMemory<byte>>()))
            .Returns(() => ++attempts > 1);
        view.HandleMouseMoveCore(TerminalMouseButton.Left, 96, 223, KeyModifiers.None);
        view.HandleMouseMoveCore(TerminalMouseButton.Left, 96, 223, KeyModifiers.None);
        view.HandleMouseMoveCore(TerminalMouseButton.Left, 96, 223, KeyModifiers.None);
        Assert.Equal(2, attempts); Assert.Empty(text);
    }
}
