using System.Buffers.Binary;

namespace Ntilde.VT.Tests;

/// <summary>
/// Image bytes whose header declares a chosen size. Kitty and iTerm2 images are placed from their
/// header (<see cref="ImageHeaderProbe"/>), so a test that needs an image of a given size sends one
/// of these; the body is irrelevant to placement and is left out.
/// </summary>
internal static class TestImages
{
    /// <summary>A PNG signature and IHDR chunk declaring <paramref name="width"/> x <paramref name="height"/>.</summary>
    public static byte[] PngHeader(int width, int height)
    {
        var png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), (uint)height);
        png[24] = 8; // bit depth
        png[25] = 6; // RGBA
        return png;
    }

    /// <summary><see cref="PngHeader"/> as base64, for Kitty and iTerm2 payloads.</summary>
    public static string PngBase64(int width, int height) => Convert.ToBase64String(PngHeader(width, height));
}
