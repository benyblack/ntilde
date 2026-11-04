namespace Ntilde.VT.Tests;

public sealed class KittyAspectSnapshotTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(23, true)]
    public void Render_projection_retains_protocol_fit_without_changing_cells_clipping_or_handle(int id, bool fit)
    {
        var buffer = new TerminalBuffer(20, 10);
        var handle = new object();
        var image = new TerminalImage(handle, 3, 2, 4, 5)
        { KittyImageId = id < 0 ? null : (uint)id, ClipTopRows = 1, ClipBottomRows = 1, ClipRightColumns = 1 };
        buffer.AddImage(image);
        using var snapshot = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = 10, ViewportCols = 20, Isolated = true }, out _);
        Assert.Equal(1, snapshot.Images.Length);
        RenderImageSnapshot projected = snapshot.Images.Array[0];
        Assert.Equal(fit, projected.PreserveAspectRatio);
        Assert.Equal((3, 2, 4, 5), (projected.CellX, projected.CellY, projected.CellWidth, projected.CellHeight));
        Assert.Equal((1, 1, 1), (projected.ClipTopRows, projected.ClipBottomRows, projected.ClipRightColumns));
        Assert.Same(handle, projected.ImageHandle);
        buffer.Lock.EnterWriteLock();
        try { image.KittyImageId = fit ? null : 23; }
        finally { buffer.Lock.ExitWriteLock(); }
        Assert.Equal(fit, snapshot.Images.Array[0].PreserveAspectRatio);
    }
}
