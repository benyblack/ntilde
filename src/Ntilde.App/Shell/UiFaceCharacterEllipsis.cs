using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Ntilde.Shell
{
    /// <summary>
    /// Trailing character ellipsis whose "…" is shaped in the UI face rather than the block's
    /// own first face. Avalonia builds the ellipsis symbol from the first family in the list
    /// with no glyph fallback, so under <see cref="BundledFontCatalog.ShellTextUiFontFamily"/>
    /// (symbols font first) the stock <c>TextTrimming.CharacterEllipsis</c> drew it as notdef.
    /// Everything else about the symbol - size, weight, style, brush - follows the trimmed text.
    /// </summary>
    internal sealed class UiFaceCharacterEllipsis : TextTrimming
    {
        internal static UiFaceCharacterEllipsis Instance { get; } = new();

        private const string Ellipsis = "\u2026";

        private UiFaceCharacterEllipsis()
        {
        }

        public override TextCollapsingProperties CreateCollapsingProperties(TextCollapsingCreateInfo createInfo)
        {
            TextRunProperties text = createInfo.TextRunProperties;
            var typeface = new Typeface(
                BundledFontCatalog.UiFontFamily, text.Typeface.Style, text.Typeface.Weight, text.Typeface.Stretch);
            var symbol = new GenericTextRunProperties(
                typeface,
                fontRenderingEmSize: text.FontRenderingEmSize,
                textDecorations: text.TextDecorations,
                foregroundBrush: text.ForegroundBrush,
                backgroundBrush: text.BackgroundBrush,
                baselineAlignment: text.BaselineAlignment,
                cultureInfo: text.CultureInfo);
            return new TextTrailingCharacterEllipsis(Ellipsis, createInfo.Width, symbol, createInfo.FlowDirection);
        }
    }
}
