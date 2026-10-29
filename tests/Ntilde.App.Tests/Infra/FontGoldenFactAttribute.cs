using SkiaSharp;
using System;
using Xunit;

namespace Ntilde.Tests.Infra
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit3003:Classes which extend FactAttribute should provide a public constructor for source information",
        Justification = "Its required fonts are a params list, which has to come last, so it cannot also take the caller's file " +
                        "and line; the goldens still run, xUnit only loses their source link.")]
    public sealed class FontGoldenFactAttribute : FactAttribute
    {
        public FontGoldenFactAttribute(params string[] requiredFonts)
        {
            if (!IsEnabled())
            {
                Skip = "ENABLE_FONT_GOLDENS is not set to 1. Skipping optional OS/font goldens.";
                return;
            }

            if (requiredFonts == null || requiredFonts.Length == 0)
            {
                return;
            }

            foreach (string family in requiredFonts)
            {
                using var matched = SKFontManager.Default.MatchFamily(family);
                if (matched != null)
                {
                    return;
                }
            }

            Skip = $"Missing required fonts for optional font golden: [{string.Join(", ", requiredFonts)}].";
        }

        private static bool IsEnabled()
        {
            string? raw = Environment.GetEnvironmentVariable("ENABLE_FONT_GOLDENS");
            return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
