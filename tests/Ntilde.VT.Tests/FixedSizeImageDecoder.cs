using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// An image decoder that ignores the bytes and reports every image, of every kind, as decoded at one
/// fixed pixel size: for tests about where an image is placed and how many cells it covers, not about
/// decoding.
/// </summary>
internal sealed class FixedSizeImageDecoder(int decodedWidth, int decodedHeight) : IImageDecoder
{
    public object? DecodeImageBytes(byte[] imageData, out int pixelWidth, out int pixelHeight) =>
        Decoded(out pixelWidth, out pixelHeight);

    public object? DecodeSixel(string sixelData, out int pixelWidth, out int pixelHeight) =>
        Decoded(out pixelWidth, out pixelHeight);

    public object? DecodeRawImage(byte[] data, int bytesPerPixel, int width, int height, out int pixelWidth, out int pixelHeight) =>
        Decoded(out pixelWidth, out pixelHeight);

    private object Decoded(out int pixelWidth, out int pixelHeight)
    {
        pixelWidth = decodedWidth;
        pixelHeight = decodedHeight;
        return new object();
    }
}
