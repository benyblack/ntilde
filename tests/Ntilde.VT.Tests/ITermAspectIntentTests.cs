using System.Globalization;
using System.Text;

namespace Ntilde.VT.Tests;

public sealed class ITermAspectIntentTests
{
    private const string Prefix = "\x1b]1337;File=inline=1;width=4;height=2";
    private static string Image(string option) => Prefix + option + ":" + TestImages.PngBase64(6, 2) + "\x1b\\";
    private static AnsiParser Parser(TerminalBuffer buffer) => new(buffer, forceConPtyFiltering: false)
    { ImageDecoder = new FixedSizeImageDecoder(6, 2) };

    [Theory]
    [InlineData("", true)]
    [InlineData(";preserveAspectRatio=1", true)]
    [InlineData(";preserveAspectRatio=0", false)]
    [InlineData(";preserveAspectRatio=2", true)]
    [InlineData(";PRESERVEASPECTRATIO=0", false)]
    public void Parser_retains_each_images_requested_fit_in_the_detached_render_projection(string option, bool preserve)
    {
        var buffer = new TerminalBuffer(20, 10); var parser = Parser(buffer);
        parser.Process("\x1b[4;4H" + Image(option));
        TerminalImage image = Assert.Single(buffer.Images);
        Assert.Equal(preserve, image.PreserveAspectRatio); Assert.Null(image.KittyImageId);
        Assert.Equal((3, 3, 4, 2), (image.CellX, image.CellY, image.CellWidth, image.CellHeight));
        using var snapshot = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = 10, ViewportCols = 20, Isolated = true }, out _);
        Assert.Equal(preserve, snapshot.Images.Array[0].PreserveAspectRatio);
        buffer.Lock.EnterWriteLock();
        try { image.PreserveAspectRatio = !preserve; }
        finally { buffer.Lock.ExitWriteLock(); }
        Assert.Equal(preserve, snapshot.Images.Array[0].PreserveAspectRatio);
    }

    [Fact]
    public void Explicit_stretch_does_not_become_a_terminal_mode_or_affect_the_next_image()
    {
        var buffer = new TerminalBuffer(20, 20); var parser = Parser(buffer);
        foreach (string option in new[] { ";preserveAspectRatio=0", ";preserveAspectRatio=1", "" })
            parser.Process("\x1b[4;4H" + Image(option));
        Assert.Equal(new[] { false, true, true }, buffer.Images.Select(image => image.PreserveAspectRatio));
        Assert.All(buffer.Images, image => Assert.Equal((4, 2), (image.CellWidth, image.CellHeight)));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(";preserveAspectRatio=1", true)]
    [InlineData(";preserveAspectRatio=0", false)]
    public void Existing_pending_OSC_state_resumes_with_the_same_argument_semantics_without_a_new_wire_field(string option, bool preserve)
    {
        var source = new TerminalBuffer(20, 10); var parser = Parser(source);
        parser.Process("\x1b[4;4H" + Prefix + option + ':');
        var snapshot = source.ExportState(0); snapshot.Parser = parser.ExportState();
        byte[] bytes = TerminalStateSerializer.ToBytes(snapshot);
        var restored = new TerminalBuffer(20, 10); var resumed = Parser(restored);
        TerminalStateTransfer.Restore(restored, resumed, TerminalStateSerializer.FromBytes(bytes));
        resumed.Process(TestImages.PngBase64(6, 2) + "\x1b\\");
        TerminalImage image = Assert.Single(restored.Images);
        Assert.Equal(preserve, image.PreserveAspectRatio);
        Assert.Equal((3, 3, 4, 2), (image.CellX, image.CellY, image.CellWidth, image.CellHeight));
    }

    [Fact]
    public void Legacy_constructors_and_restored_SIXEL_keep_their_existing_policy_and_snapshot_schema()
    {
        Assert.False(new TerminalImage(new object(), 0, 0, 1, 1).PreserveAspectRatio);
        var snapshot = new TerminalBuffer(20, 10).ExportState(0);
        snapshot.Sixels = [new TerminalSixelImageState { Source = "q~~~", X = 3, Y = 3, Width = 4, Height = 2 }];
        byte[] bytes = TerminalStateSerializer.ToBytes(snapshot);
        Assert.DoesNotContain("aspect", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
        var restored = new TerminalBuffer(20, 10);
        TerminalStateTransfer.Restore(restored, new AnsiParser(restored), TerminalStateSerializer.FromBytes(bytes));
        TerminalImage image = Assert.Single(restored.Images);
        Assert.True(image.IsSixelImage); Assert.False(image.PreserveAspectRatio);
        using var render = restored.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = 10, ViewportCols = 20, Isolated = true }, out _);
        Assert.False(render.Images.Array[0].PreserveAspectRatio);
    }

    [Fact]
    public void Completed_opaque_images_remain_outside_state_transfer_instead_of_adding_layout_or_handle_fields()
    {
        var source = new TerminalBuffer(20, 10); Parser(source).Process(Image(""));
        Assert.True(Assert.Single(source.Images).PreserveAspectRatio);
        var snapshot = source.ExportState(0);
        Assert.Null(snapshot.Sixels);
        byte[] bytes = TerminalStateSerializer.ToBytes(snapshot);
        Assert.DoesNotContain("aspect", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
        var restored = new TerminalBuffer(20, 10);
        TerminalStateTransfer.Restore(restored, new AnsiParser(restored), TerminalStateSerializer.FromBytes(bytes));
        Assert.Empty(restored.Images);
    }

    [Fact]
    public void Uppercase_aspect_key_is_invariant_under_Turkish_case_mapping()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var buffer = new TerminalBuffer(20, 10); Parser(buffer).Process(Image(";PRESERVEASPECTRATIO=0"));
            Assert.False(Assert.Single(buffer.Images).PreserveAspectRatio);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
