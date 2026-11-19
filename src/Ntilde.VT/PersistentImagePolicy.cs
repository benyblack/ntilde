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

    /// <summary>
    /// <see cref="MultipartImagesVersion"/> plus Kitty animation frames (#1017). A version of its own because a version-2 peer refuses animation in a
    /// persistent session (<c>a=f</c> and <c>a=a</c> answer <c>ENOTSUP</c>): under one policy, one side would play an animation and the other would not,
    /// and a snapshot carrying frames could not be restored by a peer that predates them.
    /// </summary>
    public const int AnimationImagesVersion = 3;

    /// <summary>
    /// <see cref="AnimationImagesVersion"/> plus Kitty frame composition (<c>a=c</c>, #1027). A version of its own because a version-3 peer answers <c>ENOTSUP</c> to it:
    /// under one policy, one side would compose a frame and the other would not, and the two would then hold different frames. A composed frame is an ordinary frame
    /// (its result replaces the destination frame), so snapshots carry nothing new.
    /// </summary>
    public const int CompositionImagesVersion = 4;

    /// <summary>
    /// <see cref="CompositionImagesVersion"/> plus Kitty Unicode placeholders (<c>U=1</c> virtual placements, #989). A version of its own because a version-4 peer
    /// answers <c>ENOTSUP</c> to <c>U=1</c>: under one policy, one side would hold the grid a placeholder cell is fitted into and the other would draw nothing, and a
    /// snapshot carrying virtual placements could not be restored by a peer that predates them.
    /// </summary>
    public const int PlaceholderImagesVersion = 5;

    /// <summary>The highest policy this build supports.</summary>
    public const int EnabledVersion = PlaceholderImagesVersion;

    /// <summary>Whether <paramref name="version"/> carries Kitty graphics and iTerm2 inline images.</summary>
    public static bool AllowsImages(int version) => version >= RetainedImagesVersion;

    /// <summary>Whether <paramref name="version"/> assembles iTerm2's multipart inline images.</summary>
    public static bool AllowsITerm2Multipart(int version) => version >= MultipartImagesVersion;

    /// <summary>Whether <paramref name="version"/> retains Kitty animation frames in snapshots.</summary>
    public static bool AllowsAnimation(int version) => version >= AnimationImagesVersion;

    /// <summary>Whether <paramref name="version"/> composes Kitty animation frames (<c>a=c</c>).</summary>
    public static bool AllowsComposition(int version) => version >= CompositionImagesVersion;

    /// <summary>Whether <paramref name="version"/> carries Kitty Unicode placeholders (<c>U=1</c> virtual placements).</summary>
    public static bool AllowsPlaceholders(int version) => version >= PlaceholderImagesVersion;

    /// <summary>Configures a persistent-session parser (daemon or client) for <paramref name="version"/>.</summary>
    public static void Apply(AnsiParser parser, int version)
    {
        ArgumentNullException.ThrowIfNull(parser);
        bool images = AllowsImages(version);
        parser.AllowNonSixelImages = images;
        parser.RetainImageSources = images;
        parser.AllowNativeKittyGraphics = images;
        parser.AllowITerm2Multipart = AllowsITerm2Multipart(version);
        parser.RetainedAnimation = AllowsAnimation(version);
        parser.RetainedComposition = AllowsComposition(version);
        parser.RetainedPlaceholders = AllowsPlaceholders(version);
    }
}
