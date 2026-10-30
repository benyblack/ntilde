using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Rendering;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Tests.RenderTests;

/// <summary>
/// #644: a closed pane's glyph atlas and row pictures are disposed once nothing can draw with them, rather than
/// left to the finalizers of its Skia objects.
/// </summary>
/// <remarks>
/// The view holds them until its pane is disposed, and each draw operation holds a lease until the compositor
/// disposes it, which can be after the pane is gone. Whichever lets go last disposes them.
/// </remarks>
public class TerminalRenderCachesTests
{
    private static TerminalRenderCaches NewCaches() => new(new GlyphCache(), new RowImageCache());

    [Fact]
    public void TheViewLettingGo_WithNoDrawOperationHoldingThem_DisposesThem()
    {
        TerminalRenderCaches caches = NewCaches();

        caches.Release();

        Assert.True(caches.IsDisposed);
        Assert.True(caches.Glyphs.IsDisposed);
    }

    [Fact]
    public void ADrawOperationsLease_KeepsThemUntilItIsDisposed()
    {
        // The render thread can still be drawing the view's last frame after its pane closed.
        TerminalRenderCaches caches = NewCaches();
        IDisposable lease = Assert.IsAssignableFrom<IDisposable>(caches.TryLease());

        caches.Release();
        Assert.False(caches.Glyphs.IsDisposed);

        lease.Dispose();
        Assert.True(caches.Glyphs.IsDisposed);
    }

    [Fact]
    public void LeasesDisposedFirst_LeaveTheDisposingToTheView()
    {
        TerminalRenderCaches caches = NewCaches();
        caches.TryLease()!.Dispose();
        caches.TryLease()!.Dispose();
        Assert.False(caches.IsDisposed);

        caches.Release();

        Assert.True(caches.IsDisposed);
    }

    [Fact]
    public void ALeaseDisposedTwice_CountsOnce()
    {
        TerminalRenderCaches caches = NewCaches();
        IDisposable first = caches.TryLease()!;
        IDisposable second = caches.TryLease()!;
        caches.Release();

        first.Dispose();
        first.Dispose();
        Assert.False(caches.IsDisposed); // the second operation still draws with them

        second.Dispose();
        Assert.True(caches.IsDisposed);
    }

    [Fact]
    public void TheViewLettingGoTwice_CountsOnce()
    {
        TerminalRenderCaches caches = NewCaches();
        IDisposable lease = caches.TryLease()!;

        caches.Release();
        caches.Release();
        Assert.False(caches.IsDisposed);

        lease.Dispose();
        Assert.True(caches.IsDisposed);
    }

    [Fact]
    public void NoLease_OnceTheViewLetGo()
    {
        // A retired view can still be drawn before it leaves the tree; it draws without the caches.
        TerminalRenderCaches caches = NewCaches();
        IDisposable lease = caches.TryLease()!;
        caches.Release();

        Assert.Null(caches.TryLease());

        lease.Dispose();
        Assert.True(caches.IsDisposed);
    }

    [Fact]
    public void ALease_IsTheWayToTheCachesForDrawing()
    {
        TerminalRenderCaches caches = NewCaches();

        using TerminalRenderCaches.Lease lease = caches.TryLease()!;

        Assert.Same(caches.Glyphs, lease.Glyphs);
        Assert.Same(caches.Rows, lease.Rows);
    }

    [Fact]
    public void ClearingADisposedGlyphCache_DoesNothing()
    {
        // A font change can still reach a view whose caches are gone; clearing reset the disposed atlas.
        var glyphs = new GlyphCache();
        glyphs.Dispose();

        glyphs.Clear();
        glyphs.Dispose();

        Assert.True(glyphs.IsDisposed);
    }

    [AvaloniaFact]
    public void RetiringAViewThatNeverDrew_DisposesItsCaches()
    {
        var view = new TerminalView();
        view.SetBuffer(new TerminalBuffer(80, 24));

        view.Retire();

        Assert.True(view.RenderCachesForTest.IsDisposed);
    }

    [AvaloniaFact]
    public void ARetiredView_KeepsItsCachesForTheFrameStillShown_AndDrawsOnWithoutThem()
    {
        var view = new TerminalView();
        view.SetBuffer(new TerminalBuffer(80, 24));
        var window = new Window { Content = view, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();

            view.Retire();
            Assert.False(view.RenderCachesForTest.IsDisposed); // the compositor still holds the last frame's operation

            // A disposed pane stays in the tree until it is removed, and is drawn again: without the caches, which
            // go with the frame that held them.
            view.InvalidateVisual();
            window.CaptureRenderedFrame();
            Assert.True(view.RenderCachesForTest.IsDisposed);

            view.InvalidateVisual();
            window.CaptureRenderedFrame();
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void DisposingAShownPane_DisposesItsViewsCachesOnceTheViewIsRemoved()
    {
        var pane = new Ntilde.Controls.TerminalPane();
        var window = new Window { Content = pane, Width = 800, Height = 400 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            TerminalView view = Assert.IsType<TerminalView>(pane.FindControl<TerminalView>("TermView"));

            pane.Dispose();
            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();

            Assert.True(view.RenderCachesForTest.IsDisposed);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
