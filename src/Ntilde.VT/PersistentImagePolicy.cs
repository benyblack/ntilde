namespace Ntilde.VT;

/// <summary>
/// Which image transports besides SIXEL a persistent (mux) session carries (#838). Fixed when the session
/// is spawned and negotiated like <see cref="SixelRasterPolicy"/>: a peer that does not support a session's
/// policy is refused, never silently downgraded. The daemon and every client configure their parsers with
/// <see cref="Apply"/>, so all of them make the same decision for every image from the same bytes.
/// </summary>
public static class PersistentImagePolicy
{
    /// <summary>SIXEL only: Kitty graphics and iTerm2 inline images are refused, as before #838.</summary>
    public const int Legacy = 0;

    /// <summary>
    /// Kitty graphics and iTerm2 inline images, placed from their image headers (<see cref="ImageHeaderProbe"/>)
    /// and retained as sent so snapshots carry them. Native Kitty APC is trusted, as
    /// <see cref="AnsiParser.AllowNativeKittyGraphics"/> is by default: a per-window setting cannot apply,
    /// since every side of a session must decide alike.
    /// </summary>
    public const int RetainedImagesVersion = 1;

    /// <summary>
    /// <see cref="RetainedImagesVersion"/> plus iTerm2's multipart form (<c>MultipartFile</c>, <c>FilePart</c>,
    /// <c>FileEnd</c>), which <c>imgcat</c> sends by default. A version of its own because a version-1 peer
    /// ignores that form: under one policy, one side would place the image and the other would not.
    /// </summary>
    public const int MultipartImagesVersion = 2;

    /// <summary>The highest policy this build supports.</summary>
    public const int EnabledVersion = MultipartImagesVersion;

    /// <summary>Whether <paramref name="version"/> carries Kitty graphics and iTerm2 inline images.</summary>
    public static bool AllowsImages(int version) => version >= RetainedImagesVersion;

    /// <summary>Whether <paramref name="version"/> assembles iTerm2's multipart inline images.</summary>
    public static bool AllowsITerm2Multipart(int version) => version >= MultipartImagesVersion;

    /// <summary>Configures a persistent-session parser (daemon or client) for <paramref name="version"/>.</summary>
    public static void Apply(AnsiParser parser, int version)
    {
        ArgumentNullException.ThrowIfNull(parser);
        bool images = AllowsImages(version);
        parser.AllowNonSixelImages = images;
        parser.RetainImageSources = images;
        parser.AllowNativeKittyGraphics = images;
        parser.AllowITerm2Multipart = AllowsITerm2Multipart(version);
    }
}
