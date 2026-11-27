using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Moq;
using Ntilde.Pty;
using Ntilde.Shell;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Input;

public class TerminalViewPromptClickTests
{
    [AvaloniaTheory]
    [InlineData(1, "\x1b[<0;5;5M")]
    [InlineData(2, "\x1b[<0;5;1M")]
    public void SingleClickSendsOneSgrPressOnRelease(int mode, string expected)
    {
        using var fixture = new PointerFixture(mode);
        fixture.Press(4, 4);
        Assert.Empty(fixture.Sent);
        fixture.Release(4, 4);
        Assert.Equal(expected, Assert.Single(fixture.Sent));
        Assert.False(fixture.View.HasSelection());
    }

    [AvaloniaFact]
    public void DragSelectsWithoutSubmittingAPromptClick()
    {
        using var fixture = new PointerFixture(2);
        fixture.Press(4, 0);
        fixture.Move(4, 3);
        fixture.Release(4, 3);
        Assert.Empty(fixture.Sent);
        Assert.True(fixture.View.HasSelection());
    }

    [AvaloniaFact]
    public void DragReturningToTheStartDoesNotSubmitAClick()
    {
        using var fixture = new PointerFixture(2);
        fixture.Press(4, 0);
        fixture.Move(4, 3);
        fixture.Move(4, 0);
        fixture.Release(4, 0);
        Assert.Empty(fixture.Sent);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void MouseButtonChordDoesNotSubmitAPromptClick(bool releaseLeftLast)
    {
        using var fixture = new PointerFixture(2);
        fixture.Press(4, 4);
        fixture.Move(4, 4, RawInputModifiers.LeftMouseButton | RawInputModifiers.RightMouseButton,
            PointerUpdateKind.RightButtonPressed);
        fixture.Move(4, 4, releaseLeftLast ? RawInputModifiers.LeftMouseButton : RawInputModifiers.RightMouseButton,
            releaseLeftLast ? PointerUpdateKind.RightButtonReleased : PointerUpdateKind.LeftButtonReleased);
        fixture.Release(4, 4, kind: releaseLeftLast ? PointerUpdateKind.LeftButtonReleased : PointerUpdateKind.RightButtonReleased);
        Assert.Empty(fixture.Sent);
    }

    [AvaloniaFact]
    public void ActualReleaseButtonMustBeLeft()
    {
        using var fixture = new PointerFixture(2);
        fixture.Press(4, 4);
        fixture.Release(4, 4, kind: PointerUpdateKind.RightButtonReleased);
        Assert.Empty(fixture.Sent);
    }

    [AvaloniaFact]
    public void ClickCannotCrossIntoTheNextPrompt()
    {
        using var fixture = new PointerFixture(2);
        fixture.Press(4, 4);
        fixture.Parser.Process("\x1b]133;C\a\x1b[5;1H\x1b]133;A;click_events=2\aPS1> input");
        fixture.Release(4, 4);
        Assert.Empty(fixture.Sent);
    }

    [AvaloniaFact]
    public void ApplicationMouseReportingTakesPrecedence()
    {
        using var fixture = new PointerFixture(2);
        fixture.Parser.Process("\x1b[?1000h\x1b[?1006h");
        fixture.Press(4, 4);
        fixture.Release(4, 4);
        Assert.Equal(new[] { "\x1b[<0;5;5M", "\x1b[<0;5;5m" }, fixture.Sent);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ModifiedClicksAndSessionsWithoutOptInAreSilent(bool enablePromptClicks)
    {
        using var fixture = new PointerFixture(2, enablePromptClicks);
        var modifiers = enablePromptClicks ? KeyModifiers.Shift : KeyModifiers.None;
        fixture.Press(4, 4, modifiers);
        fixture.Release(4, 4, modifiers);
        Assert.Empty(fixture.Sent);
    }

    private sealed class PointerFixture : IDisposable
    {
        private readonly Window _window;
        private readonly Pointer _pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);

        public PointerFixture(int mode, bool enablePromptClicks = true)
        {
            var buffer = new TerminalBuffer(80, 24);
            Parser = new AnsiParser(buffer);
            var session = new Mock<ITerminalSession>();
            session.Setup(value => value.SendInput(It.IsAny<string>())).Callback<string>(Sent.Add);
            View = new TerminalView();
            View.SetBuffer(buffer);
            View.SetSession(session.Object, enablePromptClicks);
            _window = new Window { Content = View, Width = 640, Height = 384 };
            _window.Show();
            View.SetMetricsForTest(8, 16);
            Parser.Process($"\x1b[3;1H\x1b]133;A;click_events={mode}\aPS1> \x1b[5;1H\x1b]133;A;k=s\aPS2> input");
        }

        public TerminalView View { get; }
        public AnsiParser Parser { get; }
        public List<string> Sent { get; } = new();

        private Point At(int row, int column) => View.TranslatePoint(new Point((column + 0.5) * 8, (row + 0.5) * 16), _window)!.Value;

        public void Press(int row, int column, KeyModifiers modifiers = KeyModifiers.None)
            => View.RaiseEvent(new PointerPressedEventArgs(View, _pointer, _window, At(row, column), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), modifiers));

        public void Move(int row, int column, RawInputModifiers flags = RawInputModifiers.LeftMouseButton,
            PointerUpdateKind kind = PointerUpdateKind.Other)
            => View.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, View, _pointer, _window, At(row, column), 0,
                new PointerPointProperties(flags, kind), KeyModifiers.None));

        public void Release(int row, int column, KeyModifiers modifiers = KeyModifiers.None,
            PointerUpdateKind kind = PointerUpdateKind.LeftButtonReleased)
            => View.RaiseEvent(new PointerReleasedEventArgs(View, _pointer, _window, At(row, column), 0,
                new PointerPointProperties(RawInputModifiers.None, kind), modifiers, MouseButton.Left));

        public void Dispose()
        {
            _pointer.Dispose();
            _window.Close();
        }
    }
}
