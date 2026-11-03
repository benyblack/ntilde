using System.Text.Json.Serialization;

namespace Ntilde.AgentHost.Contracts;

/// <summary>Opt-in provenance for one offscreen draw. This is not live desktop or timing evidence.</summary>
public sealed record CaptureRenderDetails
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    [JsonPropertyName("architecture")]
    public required string Architecture { get; init; }

    [JsonPropertyName("font")]
    public required CaptureFontDetails Font { get; init; }

    [JsonPropertyName("grid")]
    public required CaptureGridDetails Grid { get; init; }

    [JsonPropertyName("theme")]
    public required CaptureThemeDetails Theme { get; init; }

    [JsonPropertyName("presentation")]
    public required CapturePresentationDetails Presentation { get; init; }

    [JsonPropertyName("syntheticSelection"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CaptureSelection? SyntheticSelection { get; init; }

    [JsonPropertyName("enableLigatures")]
    public bool EnableLigatures { get; init; }

    [JsonPropertyName("enableComplexShaping")]
    public bool EnableComplexShaping { get; init; }

    [JsonPropertyName("minimumContrast")]
    public double MinimumContrast { get; init; }
}

public sealed record CaptureFontDetails
{
    [JsonPropertyName("configuredFamily")]
    public required string ConfiguredFamily { get; init; }

    [JsonPropertyName("primaryFamily")]
    public required string PrimaryFamily { get; init; }

    /// <summary>SHA-256 of the resolved face's complete stream; absent if unavailable or over 64 MiB.</summary>
    [JsonPropertyName("primarySha256")]
    public string? PrimarySha256 { get; init; }

    [JsonPropertyName("collectionIndex")]
    public int CollectionIndex { get; init; }

    [JsonPropertyName("weight")]
    public int Weight { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("slant")]
    public required string Slant { get; init; }

    /// <summary>Resolved fallback family names; their bytes and per-glyph use are not attested.</summary>
    [JsonPropertyName("fallbackFamilies")]
    public required string[] FallbackFamilies { get; init; }

    [JsonPropertyName("sizeDip")]
    public float SizeDip { get; init; }
}

/// <summary>Pixel coordinates describe the original draw, before optional maxWidth resampling.</summary>
public sealed record CaptureGridDetails
{
    [JsonPropertyName("renderWidthPx")]
    public int RenderWidthPx { get; init; }
    [JsonPropertyName("renderHeightPx")]
    public int RenderHeightPx { get; init; }
    [JsonPropertyName("cellWidthDip")]
    public float CellWidthDip { get; init; }
    [JsonPropertyName("cellHeightDip")]
    public float CellHeightDip { get; init; }
    [JsonPropertyName("baselineDip")]
    public float BaselineDip { get; init; }
    [JsonPropertyName("originXPx")]
    public int OriginXPx { get; init; }
    [JsonPropertyName("originYPx")]
    public int OriginYPx { get; init; }
    [JsonPropertyName("cellWidthPx")]
    public int CellWidthPx { get; init; }
    [JsonPropertyName("cellHeightPx")]
    public int CellHeightPx { get; init; }
    [JsonPropertyName("baselineOffsetPx")]
    public int BaselineOffsetPx { get; init; }
    [JsonPropertyName("underlineOffsetPx")]
    public int UnderlineOffsetPx { get; init; }
    [JsonPropertyName("strikeOffsetPx")]
    public int StrikeOffsetPx { get; init; }
}

public sealed record CaptureThemeDetails
{
    [JsonPropertyName("foregroundArgb")]
    public uint ForegroundArgb { get; init; }
    [JsonPropertyName("backgroundArgb")]
    public uint BackgroundArgb { get; init; }
    [JsonPropertyName("cursorArgb")]
    public uint CursorArgb { get; init; }
    [JsonPropertyName("cursorTextArgb")]
    public uint CursorTextArgb { get; init; }
    [JsonPropertyName("ansiPaletteArgb")]
    public required uint[] AnsiPaletteArgb { get; init; }
    /// <summary>Effective theme selection values, before App derivation; null may mean no concrete OSC query reply.</summary>
    [JsonPropertyName("selectionBackgroundArgb"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public uint? SelectionBackgroundArgb { get; init; }
    [JsonPropertyName("selectionForegroundArgb"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public uint? SelectionForegroundArgb { get; init; }
    /// <summary>Actual selection paint color; may be a translucent host-derived tint.</summary>
    [JsonPropertyName("effectiveSelectionBackgroundArgb")]
    public uint EffectiveSelectionBackgroundArgb { get; init; }
    /// <summary>Null means selected cells keep their own foreground, rather than a concrete override.</summary>
    [JsonPropertyName("effectiveSelectionForegroundArgb"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public uint? EffectiveSelectionForegroundArgb { get; init; }
    [JsonPropertyName("cursorOverride")]
    public bool CursorOverride { get; init; }
    [JsonPropertyName("selectionBackgroundOverride")]
    public bool SelectionBackgroundOverride { get; init; }
    [JsonPropertyName("selectionForegroundOverride")]
    public bool SelectionForegroundOverride { get; init; }
    /// <summary>Sorted indices with stream-owned overrides, even when an override equals its host color.</summary>
    [JsonPropertyName("paletteOverrideIndices")]
    public int[] PaletteOverrideIndices { get; init; } = [];
    [JsonPropertyName("foregroundOverride")]
    public bool ForegroundOverride { get; init; }
    [JsonPropertyName("backgroundOverride")]
    public bool BackgroundOverride { get; init; }
}

public sealed record CapturePresentationDetails
{
    /// <summary>Absent means fixed visible phases, preserving the original deterministic capture.</summary>
    [JsonPropertyName("timeMs")]
    public long? TimeMs { get; init; }
    [JsonPropertyName("halfPeriodMs")]
    public int HalfPeriodMs { get; init; }
    [JsonPropertyName("textBlinkVisible")]
    public bool TextBlinkVisible { get; init; }
    [JsonPropertyName("cursorVisible")]
    public bool CursorVisible { get; init; }
    [JsonPropertyName("cursorBlinkEnabled")]
    public bool CursorBlinkEnabled { get; init; }
    /// <summary>Effective shape: 0 block, 1 beam, 2 underline; includes the presenting host preference.</summary>
    [JsonPropertyName("cursorStyle")]
    public int CursorStyle { get; init; }
    [JsonPropertyName("cursorRow")]
    public int CursorRow { get; init; }
    [JsonPropertyName("cursorCol")]
    public int CursorCol { get; init; }
}
