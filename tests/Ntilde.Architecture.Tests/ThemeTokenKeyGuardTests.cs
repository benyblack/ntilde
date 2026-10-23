using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Ntilde.Architecture.Tests.ThemeLiterals;
using Ntilde.Shell;
using Ntilde.Shell.Theming;

namespace Ntilde.Architecture.Tests;

/// <summary>
/// A DynamicResource to a key nobody defines renders nothing: no exception, just an invisible
/// border or text. These tests make every Nt* key reference resolvable by construction.
/// </summary>
/// <remarks>
/// Reads the XAML as XML and the markup-extension grammar, not raw text (memory:
/// guards-parse-dont-match-spellings). Single quotes, spacing, and the ResourceKey= and element
/// forms all reach the same parser.
/// </remarks>
public partial class ThemeTokenKeyGuardTests
{
    private const string Remedy =
        "Theme colors come only from ThemeKeys (src/Ntilde.App/Shell/Theming/ThemeKeys.cs), referenced as " +
        "{DynamicResource NtX} in XAML and ThemeKeys.X in C#. See docs/ThemeSystem.md, \"UI color tokens\". Offenders: ";

    // The tokens, the UI-scale transform, and whatever App.axaml itself declares (NtMono): those
    // resolve from Application.Resources in every window.
    private static readonly HashSet<string> AppKeys = BuildAppKeys();

    [Fact]
    public void Every_Nt_resource_reference_in_xaml_names_a_theme_token_or_a_local_resource()
    {
        var offenders = new List<string>();
        foreach (var file in ThemeGuardFiles.ShippedXaml())
        {
            var document = XDocument.Parse(File.ReadAllText(ThemeGuardFiles.Absolute(file)), LoadOptions.SetLineInfo);
            var local = LocalKeys(document);
            foreach (var (key, line) in ReferencedTokenKeys(document))
            {
                if (!AppKeys.Contains(key) && !local.Contains(key))
                {
                    offenders.Add($"{file}:{line} {key}");
                }
            }
        }

        Assert.True(offenders.Count == 0, Remedy + string.Join(", ", offenders));
    }

    [Fact]
    public void No_xaml_file_shadows_a_theme_token()
    {
        var offenders = new List<string>();
        foreach (var file in ThemeGuardFiles.ShippedXaml())
        {
            var document = XDocument.Parse(File.ReadAllText(ThemeGuardFiles.Absolute(file)));
            offenders.AddRange(LocalKeys(document).Where(ThemeKeys.All.Contains).Select(key => $"{file} redeclares {key}"));
        }

        Assert.True(offenders.Count == 0,
            "A local x:Key with a token's name silently shadows the app-wide token for that subtree. " + Remedy + string.Join(", ", offenders));
    }

    [Fact]
    public void No_Nt_key_is_spelled_as_a_string_outside_the_theming_layer()
    {
        var offenders = new List<string>();
        foreach (var file in ThemeGuardFiles.AppSources().Where(f => !ThemeLiteralPolicy.IsTokenLayer(f)))
        {
            var cancellation = TestContext.Current.CancellationToken;
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(ThemeGuardFiles.Absolute(file)), cancellationToken: cancellation).GetRoot(cancellation);
            foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
            {
                if (!literal.IsKind(SyntaxKind.StringLiteralExpression)) continue;

                var text = literal.Token.ValueText;
                bool isUiScaleDefinition = string.Equals(text, UiScale.TransformResourceKey, StringComparison.Ordinal) &&
                                           file.EndsWith("/Shell/UiScale.cs", StringComparison.Ordinal);
                if (TokenKeyPattern().IsMatch(text) && !isUiScaleDefinition)
                {
                    int line = literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    offenders.Add($"{file}:{line} \"{text}\"");
                }
            }
        }

        Assert.True(offenders.Count == 0, Remedy + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("<Border Background=\"{DynamicResource NtNope}\"/>")]
    [InlineData("<Border Background='{StaticResource NtNope}'/>")]
    [InlineData("<Border Background=\"{ DynamicResource   NtNope }\"/>")]
    [InlineData("<Border Background=\"{DynamicResource ResourceKey=NtNope}\"/>")]
    [InlineData("<Border><Border.Background><DynamicResource ResourceKey=\"NtNope\"/></Border.Background></Border>")]
    [InlineData("<Style Selector=\"Border\"><Setter Property=\"Background\" Value=\"{DynamicResource NtNope}\"/></Style>")]
    public void Every_reference_form_is_seen(string fragment)
        => Assert.Contains(ReferencedTokenKeys(Wrap(fragment)), reference => reference.Key == "NtNope");

    [Fact]
    public void Non_token_resources_are_ignored()
        => Assert.Empty(ReferencedTokenKeys(Wrap("<Button Theme=\"{StaticResource NtildeDrawnCaptionButton}\" Background=\"{DynamicResource SystemRegionBrush}\"/>")));

    internal static IEnumerable<(string Key, int Line)> ReferencedTokenKeys(XDocument document)
    {
        foreach (var element in document.Descendants())
        {
            bool isResourceElement = element.Name.LocalName is "DynamicResource" or "StaticResource";
            foreach (var attribute in element.Attributes())
            {
                int line = ((IXmlLineInfo)attribute).HasLineInfo() ? ((IXmlLineInfo)attribute).LineNumber : 0;
                if (isResourceElement && attribute.Name.LocalName == "ResourceKey" && TokenKeyPattern().IsMatch(attribute.Value.Trim()))
                {
                    yield return (attribute.Value.Trim(), line);
                }

                foreach (Match match in MarkupReference().Matches(attribute.Value))
                {
                    var key = match.Groups[1].Value;
                    if (TokenKeyPattern().IsMatch(key))
                    {
                        yield return (key, line);
                    }
                }
            }
        }
    }

    private static HashSet<string> BuildAppKeys()
    {
        var keys = new HashSet<string>(ThemeKeys.All, StringComparer.Ordinal) { UiScale.TransformResourceKey };
        keys.UnionWith(LocalKeys(XDocument.Parse(File.ReadAllText(ThemeGuardFiles.Absolute(AppXaml)))));
        return keys;
    }

    private const string AppXaml = "src/Ntilde.App/App.axaml";

    private static HashSet<string> LocalKeys(XDocument document)
        => document.Descendants().Attributes()
            .Where(attribute => attribute.Name.LocalName == "Key")
            .Select(attribute => attribute.Value.Trim())
            .ToHashSet(StringComparer.Ordinal);

    private static XDocument Wrap(string fragment)
        => XDocument.Parse(
            "<Root xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" + fragment + "</Root>",
            LoadOptions.SetLineInfo);

    [GeneratedRegex(@"\{\s*(?:DynamicResource|StaticResource)\s+(?:ResourceKey\s*=\s*)?([A-Za-z0-9_.]+)")]
    private static partial Regex MarkupReference();

    [GeneratedRegex(@"^Nt[A-Z][A-Za-z0-9]*$")]
    private static partial Regex TokenKeyPattern();
}
