using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.Input;

public class TerminalViewPointerShapeTests
{
    [AvaloniaFact]
    public void RequestAndActualCursorPropertyAreIndependentObservations()
    {
        var view = new TerminalView();
        var buffer = new TerminalBuffer(40, 12);
        view.SetBuffer(buffer);
        Assert.Null(view.RequestedPointerShape);
        Assert.Equal("Arrow", view.AppliedPointerCursor);
        view.RequestPointerShape("text", false);
        Assert.Equal("text", view.RequestedPointerShape);
        Assert.Equal("Ibeam", view.AppliedPointerCursor);
        view.SetBuffer(buffer); // idempotent reattachment does not withdraw a program request
        Assert.Equal("text", view.RequestedPointerShape);
        using var external = new Cursor(StandardCursorType.Wait);
        view.Cursor = external;
        Assert.Equal("text", view.RequestedPointerShape);
        Assert.Equal("Wait", view.AppliedPointerCursor);
        view.ResetPointerShapes();
        Assert.Null(view.RequestedPointerShape);
        Assert.Same(Cursor.Default, view.Cursor);
        view.Retire();
    }

    [AvaloniaFact]
    public void SeparateScreensRestoreTheirRequests_AndResetClearsBoth()
    {
        var buffer = new TerminalBuffer(40, 12);
        var view = new TerminalView();
        view.SetBuffer(buffer);
        view.RequestPointerShape("text", false);
        view.RequestPointerShape("crosshair", true);
        Assert.Equal("Ibeam", view.AppliedPointerCursor);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[?1049h"); Dispatcher.UIThread.RunJobs();
        Assert.Equal("Cross", view.AppliedPointerCursor);
        parser.Process("\x1b[?1049l"); Dispatcher.UIThread.RunJobs();
        Assert.Equal("Ibeam", view.AppliedPointerCursor);
        view.ResetPointerShapes();
        parser.Process("\x1b[?1049h"); Dispatcher.UIThread.RunJobs();
        Assert.Null(view.RequestedPointerShape);
        Assert.Equal("Arrow", view.AppliedPointerCursor);
        view.Retire();
        view.RequestPointerShape("pointer", true);
        Assert.Null(view.RequestedPointerShape);
        Assert.Same(Cursor.Default, view.Cursor);
    }

    [AvaloniaFact]
    public void LinkHoverTakesPriority_AndLeavingItRestoresTheProgramRequest()
    {
        var buffer = new TerminalBuffer(40, 12);
        new AnsiParser(buffer).Process("\x1b]8;;https://example.invalid/\x07LINK\x1b]8;;\x07");
        var view = new TerminalView();
        view.SetBuffer(buffer); view.SetMetricsForTest(8, 16);
        view.RequestPointerShape("crosshair", false);
        view.UpdateHoveredLink(new Point(4, 8));
        Assert.True(view.PointerShapeOverriddenByLink);
        Assert.Equal("crosshair", view.RequestedPointerShape);
        Assert.Equal("Hand", view.AppliedPointerCursor);
        view.UpdateHoveredLink(new Point(120, 80));
        Assert.False(view.PointerShapeOverriddenByLink);
        Assert.Equal("Cross", view.AppliedPointerCursor);
        view.SetBuffer(new TerminalBuffer(40, 12));
        Assert.Null(view.RequestedPointerShape);
        Assert.Equal("Arrow", view.AppliedPointerCursor);
        view.Retire();
    }
}
