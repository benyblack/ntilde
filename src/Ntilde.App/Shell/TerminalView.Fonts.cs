using Avalonia.Media;
using System.Collections.Concurrent;
using System.Globalization;
using SkiaSharp;
using Ntilde.VT;
using Ntilde.Rendering;

namespace Ntilde.Shell
{
    /// <summary>
    /// Fonts and cell metrics: the primary typeface, the process-wide glyph fallback chain, and measuring a cell.
    /// </summary>
    public partial class TerminalView
    {
        // Keep primary font deterministic and monospace-first for box-drawing stability.
        // The bundled default leads, so this resolves with no system font installed.
        // A locally installed Nerd Font build of the same face is preferred over the
        // bundled copy when present, under both names the two Nerd Font installers
        // register (the Windows-compatible package as "JetBrainsMonoNL NFM", the
        // full-name package as "JetBrainsMonoNL Nerd Font Mono"), because those
        // carry the icon glyphs in the primary face rather than through fallback.
        private static readonly string FontFamilyList = $"JetBrainsMonoNL NFM, JetBrainsMonoNL Nerd Font Mono, {BundledFontCatalog.DefaultTerminalFontFamily}, {BundledFontCatalog.CascadiaFontFamily}, Cascadia Mono, JetBrains Mono, DejaVu Sans Mono, Consolas, MesloLGS NF, MesloLGM Nerd Font, Fira Code, Monospace";
        private Typeface _typeface = new Typeface(FontFamilyList, FontStyle.Normal, FontWeight.Normal);
        private double _fontSize = 14;


        private GlyphTypeface? _glyphTypeface;
        private SharedSKTypeface? _skTypeface;
        private SharedSKFont? _skFont;
        private static readonly bool GlyphDiagnosticsEnabled = IsEnvFlagEnabled("NTILDE_DIAG_GLYPH");
        private static readonly int[] BoxDrawingProbeCodePoints = { 0x2502, 0x2500, 0x250C, 0x2510, 0x2514, 0x2518, 0x253C };
        private static readonly string[] PreferredMonospaceFonts = { "JetBrainsMonoNL NFM", "JetBrainsMonoNL Nerd Font Mono", BundledFontCatalog.DefaultTerminalFontFamily, BundledFontCatalog.CascadiaFontFamily, "Cascadia Mono", "JetBrains Mono", "DejaVu Sans Mono", "Consolas", "Cascadia Code" };

        private static readonly string[] FallbackChainNames = {
            "Segoe UI Symbol", "Symbola",                              // Symbols
            "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", // Emojis
            // Bundled symbols-only Nerd Font first: it is the only entry guaranteed
            // to be present, and it is what makes icon glyphs work under a primary
            // face that has none (every plain monospace font). It carries no ASCII,
            // so it can only ever satisfy a glyph the primary face already missed.
            BundledFontCatalog.SymbolsFontFamily,
            "JetBrainsMonoNL NFM", "JetBrainsMonoNL Nerd Font Mono", // locally installed Nerd Font builds
            BundledFontCatalog.DefaultTerminalFontFamily, BundledFontCatalog.CascadiaFontFamily, "Cascadia Mono", "JetBrains Mono", "DejaVu Sans Mono", "Consolas", // Monospace-first
            "Cascadia Code", "Fira Code", "MesloLGS NF",                        // Alternate symbol sources
            "Courier New", "Monospace"                                 // Last Resort
        };

        private static readonly List<SKTypeface> FallbackChain = new();
        private static bool _fallbackChainInitialized = false;

        private static void EnsureFallbackChain()
        {
            if (_fallbackChainInitialized) return;
            lock (FallbackChain)
            {
                if (_fallbackChainInitialized) return;
                foreach (var name in FallbackChainNames)
                {
                    var tf = TryCreateTypeface(name);
                    if (tf != null && tf.FamilyName == name)
                    {
                        FallbackChain.Add(tf);
                    }
                    else
                    {
                        tf?.Dispose();
                    }
                }
                _fallbackChainInitialized = true;
            }
        }

        /// <summary>
        /// The process-wide glyph fallback chain the live renderer draws with,
        /// for <see cref="TerminalSnapshotRenderer"/>. SKTypeface instances are
        /// immutable and already shared with every draw operation, so handing the
        /// same ones to an off-thread capture adds no new sharing — unlike the
        /// SKFont, which the capture allocates for itself.
        /// </summary>
        internal static SKTypeface[] GetSnapshotFallbackChain()
        {
            EnsureFallbackChain();
            lock (FallbackChain)
            {
                return FallbackChain.ToArray();
            }
        }

        private readonly ConcurrentDictionary<string, SKTypeface?> _fallbackCache = new();

        public double FontSize
        {
            get => _fontSize;
            set
            {
                _fontSize = value;
                ClearSkiaResources();
                InvalidateVisual();
            }
        }

        public Typeface Typeface
        {
            get => _typeface;
            set
            {
                _typeface = value;
                ClearSkiaResources();
                InvalidateVisual();
            }
        }

        private static bool IsEnvFlagEnabled(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SupportsBoxDrawing(SKTypeface typeface)
        {
            foreach (int cp in BoxDrawingProbeCodePoints)
            {
                if (!typeface.ContainsGlyph(cp)) return false;
            }
            return true;
        }

        /// <summary>
        /// Resolves <paramref name="family"/> to a Skia typeface, bundled fonts first.
        /// </summary>
        /// <param name="requireExactFamily">
        /// When true, a typeface whose family name is not the one asked for is rejected
        /// instead of returned. <see cref="SKTypeface.FromFamilyName"/> substitutes a
        /// default face for a family that is not installed rather than failing, so
        /// "resolved" is not the same as "present" - and a substituted face is usually
        /// proportional, which is the worst possible outcome for a terminal grid. Off by
        /// default so callers that probe a candidate list (and check box-drawing coverage
        /// themselves) keep their existing behaviour.
        /// </param>
        private static SKTypeface? TryCreateTypeface(string family, bool requireExactFamily = false)
        {
            var bundled = BundledFontCatalog.TryCreateSkTypeface(family);
            if (bundled != null)
            {
                return bundled;
            }

            var tf = SKTypeface.FromFamilyName(family);
            if (tf == null) return null;
            if (string.IsNullOrWhiteSpace(tf.FamilyName))
            {
                tf.Dispose();
                return null;
            }
            if (requireExactFamily && !string.Equals(tf.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            {
                tf.Dispose();
                return null;
            }
            return tf;
        }

        /// <summary>
        /// Resolves the primary monospace typeface for <paramref name="configuredFamily"/>.
        /// Internal rather than private so <see cref="TerminalSnapshotRenderer"/> can
        /// resolve fonts exactly the way the live control does
        /// (<see cref="SnapshotFontResolution.LiveParity"/>).
        /// </summary>
        internal static SKTypeface ResolveMonospacePrimaryTypeface(string configuredFamily, out bool usedFallback)
        {
            usedFallback = false;

            // Exact match required: without it, a configured font that is not
            // installed comes back as whatever Skia substitutes - frequently a
            // proportional face - and is used as though it were the real thing,
            // silently. Falling through to the preferred list below is better: those
            // candidates are probed for box-drawing coverage, and the caller gets
            // usedFallback set so the mismatch is logged.
            var configured = TryCreateTypeface(configuredFamily, requireExactFamily: true);
            if (configured != null)
            {
                return configured;
            }
            foreach (string family in PreferredMonospaceFonts)
            {
                var candidate = TryCreateTypeface(family);
                if (candidate == null) continue;
                if (!SupportsBoxDrawing(candidate))
                {
                    candidate.Dispose();
                    continue;
                }

                usedFallback = true;
                return candidate;
            }

            usedFallback = true;
            return SKTypeface.FromFamilyName("Consolas") ?? SKTypeface.FromFamilyName("Monospace") ?? SKTypeface.Default;
        }

        public void MeasureCharSize()
        {
            _rowCache.RequestClear();
            double scaling = _cachedRenderScaling;

            // Try to get SKTypeface first as it's our source of truth
            ClearSkiaResources();
            bool skiaSuccess = false;

            try
            {
                string configuredFamily = _typeface.FontFamily.Name;
                SKTypeface primaryTypeface = ResolveMonospacePrimaryTypeface(configuredFamily, out bool usedFallback);
                if (usedFallback && !string.Equals(primaryTypeface.FamilyName, configuredFamily, StringComparison.OrdinalIgnoreCase))
                {
                    TerminalLogger.Warning($"configured font '{configuredFamily}' unavailable; using '{primaryTypeface.FamilyName}'.");
                    if (GlyphDiagnosticsEnabled)
                    {
                        TerminalLogger.Debug($"[GlyphDiag] configured='{configuredFamily}' fallbackPrimary='{primaryTypeface.FamilyName}'");
                    }
                }

                _skTypeface = new SharedSKTypeface(primaryTypeface);
                if (_skTypeface?.Typeface != null)
                {
                    _skFont = new SharedSKFont(new SKFont(_skTypeface.Typeface, (float)_fontSize));
                    if (_skFont.Font != null)
                    {
                        _skFont.Font.Edging = SKFontEdging.Antialias;
                        _skFont.Font.Hinting = SKFontHinting.Normal;

                        var m = _skFont.Font.Metrics;

                        // Authority: Skia metrics
                        float ascent = -m.Ascent;
                        float descent = m.Descent;
                        float leading = m.Leading;
                        float height = ascent + descent + leading;

                        // CELL WIDTH: Authority is 'M' or '0' width in Skia
                        float width = _skFont.Font.MeasureText("M");

                        // PIXEL SNAP: Ensure width/height are exact physical pixel multiples
                        _metrics.CellWidth = (float)(Math.Ceiling(width * scaling) / scaling);
                        _metrics.CellHeight = (float)(Math.Ceiling(height * scaling) / scaling);

                        // Vertical centering logic for baseline
                        float gap = _metrics.CellHeight - (ascent + descent + leading);
                        _metrics.Baseline = (float)(Math.Round((ascent + gap / 2.0f) * scaling) / scaling);

                        _metrics.Ascent = ascent;
                        _metrics.Descent = descent;
                        _metrics.Leading = leading;

                        _glyphTypeface = _typeface.GlyphTypeface;
                        skiaSuccess = true;
                    }
                }
            }
            catch { }

            if (!skiaSuccess)
            {
                // FALLBACK TO AVALONIA (Should be rare)
                // Measured only, never drawn, so the brush is immaterial.
                var testText = new FormattedText("M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, _fontSize * scaling, Brushes.Transparent);
                _metrics.CellWidth = (float)(Math.Ceiling(testText.Width) / scaling);
                _metrics.CellHeight = (float)(Math.Ceiling(testText.Height) / scaling);
                _metrics.Baseline = (float)(Math.Round(testText.Baseline) / scaling);
                _metrics.Ascent = (float)testText.Baseline;
                _metrics.Descent = (float)(testText.Height - testText.Baseline);
                _metrics.Leading = 0;
            }

            MetricsChanged?.Invoke(_metrics.CellWidth, _metrics.CellHeight);
            _glyphTypeface = _typeface.GlyphTypeface;
        }
    }
}
