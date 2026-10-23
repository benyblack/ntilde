using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Media;

namespace Ntilde.Architecture.Tests.ThemeLiterals;

/// <summary>
/// Finds hardcoded colors in XAML by parsing it: every attribute value, markup-extension argument
/// and leaf element text that Avalonia's own Color.TryParse accepts (hex, named, rgb()/hsl()).
/// </summary>
/// <remarks>
/// Fail-closed. The only exemptions are attributes that cannot hold a color. A missing exemption
/// costs a false failure; an over-broad one would cost a missed literal. "Transparent" is exempt
/// because it is not a color choice.
/// </remarks>
internal static partial class XamlColorLiteralScanner
{
    private static readonly HashSet<string> NonColorAttributes = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "Title", "ToolTip.Tip", "PlaceholderText", "Watermark",
        "Name", "Key", "Class", "Classes", "Uid", "Selector", "Property", "FontFamily", "Data",
        "Tag", "AutomationProperties.Name", "AutomationProperties.HelpText",
    };

    public static IReadOnlyList<ColorLiteralFinding> Scan(string relativePath, string xaml)
    {
        var findings = new List<ColorLiteralFinding>();
        var document = XDocument.Parse(xaml, LoadOptions.SetLineInfo);

        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration || NonColorAttributes.Contains(attribute.Name.LocalName))
                {
                    continue;
                }

                foreach (var token in ColorTokens(attribute.Value))
                {
                    findings.Add(new ColorLiteralFinding(relativePath, LineOf(attribute), "", $"{element.Name.LocalName}.{attribute.Name.LocalName}={token}"));
                }
            }

            if (!element.HasElements)
            {
                foreach (var token in ColorTokens(element.Value))
                {
                    findings.Add(new ColorLiteralFinding(relativePath, LineOf(element), "", $"{element.Name.LocalName}={token}"));
                }
            }
        }

        return findings;
    }

    public static bool IsColorLiteral(string candidate)
        => candidate.Length > 0 &&
           !string.Equals(candidate, "Transparent", StringComparison.OrdinalIgnoreCase) &&
           Color.TryParse(candidate, out _);

    private static IEnumerable<string> ColorTokens(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            yield break;
        }

        if (trimmed.StartsWith('{'))
        {
            // A markup extension: any argument can carry a literal (FallbackValue=#FF0000).
            foreach (var token in MarkupSeparators().Split(trimmed))
            {
                if (IsColorLiteral(token))
                {
                    yield return token;
                }
            }

            yield break;
        }

        if (IsColorLiteral(trimmed))
        {
            yield return trimmed;
        }
    }

    private static int LineOf(XObject node)
        => node is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

    [GeneratedRegex(@"[\s,={}]+")]
    private static partial Regex MarkupSeparators();
}
