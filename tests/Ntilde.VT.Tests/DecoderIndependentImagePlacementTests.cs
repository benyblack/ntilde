using System.IO.Compression;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// Kitty and iTerm2 placement does not depend on the decoder (#838). A persistent session's daemon
/// parses with no decoder while each window parses the same bytes with one, so for every input both
/// must reserve the same cells, keep the same images and send the same replies. Each case runs the
/// same traffic through a parser without a decoder and through parsers whose decoders fail, throw,
/// misreport the size or hand back an already used handle, and compares everything observable.
/// </summary>
public sealed class DecoderIndependentImagePlacementTests
{
    private static string Kitty(string controls, string payload = "") => $"\x1b_G{controls};{payload}\x1b\\";
    private static string ITerm(string arguments, string payload) => $"\x1b]1337;File=inline=1{arguments}:{payload}\x07";

    public static TheoryData<string, string> Traffic => new()
    {
        { "kitty png, default size", Kitty("a=T,i=1,f=100", TestImages.PngBase64(50, 70)) + "after" },
        { "kitty png, columns given", Kitty("a=T,i=2,f=100,c=4", TestImages.PngBase64(50, 70)) },
        { "kitty png, pixel width", Kitty("a=T,i=3,f=100,w=120", TestImages.PngBase64(50, 70)) },
        { "kitty transmit, place, delete", Kitty("a=t,i=4,f=100", TestImages.PngBase64(30, 30)) + Kitty("a=p,i=4,r=3") + "x" + Kitty("a=p,i=4,p=2,C=1") + Kitty("a=d,d=i,i=4") },
        { "kitty numbered image", Kitty("a=T,I=9,f=100", TestImages.PngBase64(16, 16)) + Kitty("a=p,I=9") },
        { "kitty raw rgba", Kitty("a=T,i=5,f=32,s=2,v=2", Convert.ToBase64String(new byte[16])) },
        { "kitty raw rgb wrong length", Kitty("a=T,i=6,f=24,s=2,v=2", Convert.ToBase64String(new byte[11])) },
        { "kitty compressed png", Kitty("a=T,i=7,f=100,o=z", Convert.ToBase64String(Deflate(TestImages.PngHeader(40, 20)))) },
        { "kitty compressed raw", Kitty("a=T,i=8,f=24,s=3,v=1,o=z", Convert.ToBase64String(Deflate(new byte[9]))) },
        { "kitty corrupt body, valid header", Kitty("a=T,i=10,f=100", Convert.ToBase64String([.. TestImages.PngHeader(20, 10), 1, 2, 3])) },
        { "kitty unknown format", Kitty("a=T,i=11,f=100", Convert.ToBase64String("BM not a png"u8.ToArray())) },
        { "kitty too large", Kitty("a=T,i=12,f=100", TestImages.PngBase64(2001, 10)) },
        { "kitty chunked", "\x1b_Ga=T,i=13,f=100,m=1;" + TestImages.PngBase64(24, 24)[..12] + "\x1b\\" + Kitty("m=0", TestImages.PngBase64(24, 24)[12..]) },
        { "kitty query", Kitty("a=q,i=14,s=1,v=1,f=24", "AAAA") },
        { "kitty quiet", Kitty("a=T,i=15,q=1,f=100", TestImages.PngBase64(10, 10)) + Kitty("a=T,i=16,q=2,f=100", "AAAA") },
        { "iterm2 default size", "before" + ITerm("", TestImages.PngBase64(30, 40)) + "after" },
        { "iterm2 width", ITerm(";width=5", TestImages.PngBase64(30, 40)) },
        { "iterm2 height in pixels", ITerm(";height=50px;preserveAspectRatio=0", TestImages.PngBase64(30, 40)) },
        { "iterm2 corrupt body", ITerm("", Convert.ToBase64String([.. TestImages.PngHeader(30, 40), 9, 9])) },
        { "iterm2 unknown format", ITerm("", Convert.ToBase64String("GIF8 nope"u8.ToArray())) },
        { "iterm2 too large", ITerm("", TestImages.PngBase64(10, 3000)) },
        { "mixed", "a\r\n" + Kitty("a=T,i=20,f=100", TestImages.PngBase64(64, 32)) + ITerm(";width=3", TestImages.PngBase64(8, 8)) + "b\r\n" + Kitty("a=p,i=20,c=2,r=2") },
    };

    [Theory]
    [MemberData(nameof(Traffic))]
    public void Every_decoder_places_exactly_what_a_parser_without_one_places(string name, string input)
    {
        Outcome expected = Run(input, decoder: null);
        foreach ((string decoderName, IImageDecoder decoder) in Decoders())
            Assert.True(expected.Equals(Run(input, decoder)), $"{name}: the parser with a {decoderName} decoder diverged from the one without a decoder.");
    }

    [Fact]
    public void Without_a_decoder_the_image_is_kept_undecoded_and_acknowledged()
    {
        var (buffer, parser, replies) = Create(decoder: null);
        parser.Process(Kitty("a=T,i=1,f=100", TestImages.PngBase64(50, 70)) + ITerm("", TestImages.PngBase64(30, 40)));
        Assert.Equal(["\x1b_Gi=1;OK\x1b\\"], replies);
        Assert.All(buffer.Images, image => Assert.IsType<UndecodedImage>(image.ImageHandle));
        Assert.Equal(2, buffer.Images.Count);
        var undecoded = (UndecodedImage)buffer.Images[0].ImageHandle;
        Assert.Equal((50, 70), (undecoded.PixelWidth, undecoded.PixelHeight));
    }

    [Fact]
    public void A_valid_header_with_a_corrupt_body_holds_its_cells_and_is_acknowledged()
    {
        // The visible change of #838: before, the decoder's failure skipped this image with EINVAL.
        var (buffer, parser, replies) = Create(new FailingDecoder());
        parser.Process(Kitty("a=T,i=1,f=100", Convert.ToBase64String([.. TestImages.PngHeader(20, 40), 1, 2, 3])));
        Assert.Equal(["\x1b_Gi=1;OK\x1b\\"], replies);
        TerminalImage image = Assert.Single(buffer.Images);
        Assert.IsType<UndecodedImage>(image.ImageHandle);
        Assert.Equal((2, 2), (image.CellWidth, image.CellHeight)); // 20 x 40 pixels in 10 x 20 cells
    }

    [Fact]
    public void A_format_the_header_reader_cannot_size_is_refused_even_if_the_decoder_could_decode_it()
    {
        // The other visible change: placement no longer depends on what this decoder happens to accept.
        var (buffer, parser, replies) = Create(new FixedSizeImageDecoder(10, 10));
        parser.Process(Kitty("a=T,i=1,f=100", Convert.ToBase64String("BM\0\0\0\0"u8.ToArray()))
            + ITerm("", Convert.ToBase64String("BM\0\0\0\0"u8.ToArray())));
        Assert.Equal(["\x1b_Gi=1;EINVAL:Image decode failed\x1b\\"], replies);
        Assert.Empty(buffer.Images);
    }

    [Fact]
    public void The_decoder_still_supplies_the_pixels_when_it_can()
    {
        var decoder = new FixedSizeImageDecoder(7, 3);
        var (buffer, parser, _) = Create(decoder);
        parser.Process(Kitty("a=T,i=1,f=100", TestImages.PngBase64(50, 70)));
        TerminalImage image = Assert.Single(buffer.Images);
        Assert.IsNotType<UndecodedImage>(image.ImageHandle);
        Assert.Equal((5, 4), (image.CellWidth, image.CellHeight)); // from the 50 x 70 header, not the decoder's 7 x 3
    }

    [Fact]
    public void Undecoded_images_are_released_without_waiting_for_a_renderer()
    {
        // A parser with no renderer (the daemon, headless replay) never drains retired handles, so an
        // undecoded image must not be queued there on replacement, deletion, eviction or clearing.
        var (buffer, parser, _) = Create(decoder: null);
        string png = TestImages.PngBase64(1, 1);
        for (int i = 0; i < 300; i++)
            parser.Process(Kitty($"a=T,i={i % 3 + 1},f=100,q=2,C=1", png) + Kitty($"a=t,i={i + 10},f=100,q=2", png) + ITerm("", png));
        parser.Process(Kitty("a=d,d=A") + "\x1b[2J");
        buffer.ClearImages();
        var retired = new List<object>();
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Empty(retired);
    }

    private static IEnumerable<(string, IImageDecoder)> Decoders() =>
    [
        ("working", new FixedSizeImageDecoder(50, 70)),
        ("misreporting", new FixedSizeImageDecoder(3, 999)),
        ("failing", new FailingDecoder()),
        ("throwing", new ThrowingDecoder()),
        ("handle-reusing", new ReusedHandleDecoder()),
    ];

    private sealed record Outcome(string Replies, byte[] State, string Images)
    {
        public bool Equals(Outcome? other) => other is not null && Replies == other.Replies && Images == other.Images && State.AsSpan().SequenceEqual(other.State);
        public override int GetHashCode() => HashCode.Combine(Replies, Images);
    }

    private static Outcome Run(string input, IImageDecoder? decoder)
    {
        var (buffer, parser, replies) = Create(decoder);
        parser.Process("\x1b[3;5H" + input + "\r\nend");
        string images = string.Join(";", buffer.Images.Select(i => $"{i.CellX},{i.CellY},{i.CellWidth}x{i.CellHeight},{i.KittyImageId},{i.KittyPlacementId},{i.PreserveAspectRatio},{i.IsAltScreenImage}"));
        return new Outcome(string.Join("|", replies), TerminalStateSerializer.ToBytes(buffer.ExportState(0)), images);
    }

    private static (TerminalBuffer Buffer, AnsiParser Parser, List<string> Replies) Create(IImageDecoder? decoder)
    {
        var buffer = new TerminalBuffer(40, 20);
        var replies = new List<string>();
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = decoder, OnResponse = replies.Add };
        return (buffer, parser, replies);
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal)) zlib.Write(data);
        return output.ToArray();
    }

    private sealed class FailingDecoder : IImageDecoder
    {
        public object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight) { pixelWidth = pixelHeight = 0; return null; }
        public object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight) { pixelWidth = pixelHeight = 0; return null; }
        public object? DecodeRawImage(byte[] data, int bytesPerPixel, int width, int height, out int pixelWidth, out int pixelHeight) { pixelWidth = pixelHeight = 0; return null; }
    }

    private sealed class ThrowingDecoder : IImageDecoder
    {
        public object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight) => throw new InvalidOperationException("decoder exploded");
        public object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight) => throw new InvalidOperationException("decoder exploded");
        public object? DecodeRawImage(byte[] data, int bytesPerPixel, int width, int height, out int pixelWidth, out int pixelHeight) => throw new InvalidOperationException("decoder exploded");
    }

    /// <summary>Hands back the same handle every time, so later images find it already owned or retired.</summary>
    private sealed class ReusedHandleDecoder : IImageDecoder
    {
        private readonly object _handle = new();
        public object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight) { pixelWidth = pixelHeight = 1; return _handle; }
        public object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight) { pixelWidth = pixelHeight = 1; return _handle; }
        public object? DecodeRawImage(byte[] data, int bytesPerPixel, int width, int height, out int pixelWidth, out int pixelHeight) { pixelWidth = pixelHeight = 1; return _handle; }
    }
}
