using SkiaSharp;

namespace Ntilde.Rendering
{
    /// <summary>
    /// Labels which Skia backend a frame was drawn with, for render metrics and the HUD.
    /// Avalonia picks the backend (ANGLE/OpenGL on Windows, OpenGL on X11, Metal on macOS,
    /// with a software fallback when GPU init fails); Ntilde never chooses one, so the only
    /// reliable answer is the GRContext on the canvas lease it hands us.
    /// </summary>
    public static class RenderBackend
    {
        /// <summary>Drawn outside Avalonia's compositor (capture, export, tests).</summary>
        public const string Offscreen = "Offscreen";

        /// <summary>Avalonia gave us a canvas with no GPU context: CPU rasterisation.</summary>
        public const string Software = "Software";

        public static string Describe(GRContext? context) => Describe(context?.Backend);

        public static string Describe(GRBackend? backend) => backend is { } b ? $"GPU/{b}" : Software;
    }
}
