namespace Ntilde.VT;

/// <summary>
/// The handle of a Kitty or iTerm2 image that was placed from its header
/// (<see cref="ImageHeaderProbe"/>) but has no pixels: the parser has no decoder (the
/// persistent-session daemon, the text client) or the decoder could not decode the body. Its
/// cells are reserved exactly as for a decoded image, and a renderer draws nothing in them.
/// </summary>
public sealed class UndecodedImage(int pixelWidth, int pixelHeight)
{
    /// <summary>The width the header declared, in image pixels.</summary>
    public int PixelWidth { get; } = pixelWidth;

    /// <summary>The height the header declared, in image pixels.</summary>
    public int PixelHeight { get; } = pixelHeight;
}
