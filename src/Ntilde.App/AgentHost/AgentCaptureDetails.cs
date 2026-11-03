using System;
using System.Linq;
using System.Runtime.InteropServices;
using Ntilde.AgentHost.Contracts;
using Ntilde.Shell;

namespace Ntilde.AgentHost;

internal static class AgentCaptureDetails
{
    internal static CaptureRenderDetails From(SnapshotRenderContext context)
    {
        var frame = context.Frame;
        var grid = frame.PixelGrid;
        var theme = frame.Theme;
        var cursorText = frame.DerivedColors.CursorText;
        return new CaptureRenderDetails
        {
            Platform = PlatformName(),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            Font = new CaptureFontDetails
            {
                ConfiguredFamily = context.ConfiguredFontFamily,
                PrimaryFamily = context.PrimaryFontFamily,
                PrimarySha256 = context.PrimaryFontSha256,
                CollectionIndex = context.PrimaryFontCollectionIndex,
                Weight = context.PrimaryFontWeight,
                Width = context.PrimaryFontWidth,
                Slant = context.PrimaryFontSlant,
                FallbackFamilies = context.FallbackFontFamilies,
                SizeDip = context.FontSize,
            },
            Grid = new CaptureGridDetails
            {
                RenderWidthPx = context.RenderWidthPx,
                RenderHeightPx = context.RenderHeightPx,
                CellWidthDip = context.Metrics.CellWidth,
                CellHeightDip = context.Metrics.CellHeight,
                BaselineDip = context.Metrics.Baseline,
                OriginXPx = grid.OriginXPx,
                OriginYPx = grid.OriginYPx,
                CellWidthPx = grid.CellWidthPx,
                CellHeightPx = grid.CellHeightPx,
                BaselineOffsetPx = grid.BaselineOffsetPx,
                UnderlineOffsetPx = grid.UnderlineOffsetPx,
                StrikeOffsetPx = grid.StrikeOffsetPx,
            },
            Theme = new CaptureThemeDetails
            {
                ForegroundArgb = Argb(theme.Foreground.A, theme.Foreground.R, theme.Foreground.G, theme.Foreground.B),
                BackgroundArgb = Argb(theme.Background.A, theme.Background.R, theme.Background.G, theme.Background.B),
                CursorArgb = Argb(theme.CursorColor.A, theme.CursorColor.R, theme.CursorColor.G, theme.CursorColor.B),
                CursorTextArgb = Argb(cursorText.A, cursorText.R, cursorText.G, cursorText.B),
                AnsiPaletteArgb = theme.AnsiPalette.Select(color => Argb(color.A, color.R, color.G, color.B)).ToArray(),
                ForegroundOverride = theme.HasForegroundOverride,
                BackgroundOverride = theme.HasBackgroundOverride,
            },
            Presentation = new CapturePresentationDetails
            {
                TimeMs = context.PresentationTimeMs,
                HalfPeriodMs = (int)TextBlinkAnimation.HalfPeriod.TotalMilliseconds,
                TextBlinkVisible = context.TextBlinkVisible,
                CursorVisible = frame.CursorVisible,
                CursorBlinkEnabled = frame.CursorBlinkEnabled,
                CursorStyle = (int)frame.CursorStyle,
                CursorRow = frame.CursorRow,
                CursorCol = frame.CursorCol,
            },
            EnableLigatures = context.EnableLigatures,
            EnableComplexShaping = context.EnableComplexShaping,
            MinimumContrast = context.MinimumContrast,
        };
    }

    private static string PlatformName()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsLinux()) return "linux";
        return "unknown";
    }

    private static uint Argb(byte alpha, byte red, byte green, byte blue)
        => (uint)alpha << 24 | (uint)red << 16 | (uint)green << 8 | blue;
}
