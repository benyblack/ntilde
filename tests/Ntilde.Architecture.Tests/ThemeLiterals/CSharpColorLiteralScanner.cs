using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Ntilde.Architecture.Tests.ThemeLiterals;

/// <summary>
/// Finds hardcoded colors in the App's C# by RESOLVED SYMBOL, not spelling, using Roslyn's
/// semantic model over the App's own sources. It catches target-typed new(...), using static,
/// aliases and implicit conversions. The rules:
/// <list type="number">
/// <item>any static member of Colors / Brushes / SKColors, and static color-typed members of the
/// color types (TermColor.Black), except Transparent/Empty;</item>
/// <item>constructing a color type (Color, SKColor, TermColor, SolidColorBrush,
/// ImmutableSolidColorBrush, HslColor, HsvColor, SKColorF) from all-constant arguments;</item>
/// <item>a static factory on a color type returning a color type, called with all-constant
/// arguments (Parse, FromRgb, FromArgb, …);</item>
/// <item>a constant cast or implicitly converted to a color type (SKColor s = 0xFF336699);</item>
/// <item>a "#…" string literal Avalonia parses as a color (palettes kept as strings).</item>
/// </list>
/// Computed colors (non-constant arguments) are not findings: derivation is the token layer's job,
/// and constant spelling is what a token replaces.
/// </summary>
internal static class CSharpColorLiteralScanner
{
    private static readonly string[] ColorTypeNames =
    [
        "Avalonia.Media.Color", "Avalonia.Media.HslColor", "Avalonia.Media.HsvColor",
        "Avalonia.Media.SolidColorBrush", "Avalonia.Media.Immutable.ImmutableSolidColorBrush",
        "SkiaSharp.SKColor", "SkiaSharp.SKColorF", "Ntilde.VT.TermColor",
    ];

    private static readonly string[] PaletteTypeNames = ["Avalonia.Media.Colors", "Avalonia.Media.Brushes", "SkiaSharp.SKColors"];

    // Types the scan is blind without; resolving none of them would silently pass everything.
    private static readonly string[] RequiredTypeNames =
        ["Avalonia.Media.Color", "Avalonia.Media.Colors", "Avalonia.Media.Brushes", "SkiaSharp.SKColor", "Ntilde.VT.TermColor"];

    private static readonly HashSet<string> NoColorMembers = new(StringComparer.Ordinal) { "Transparent", "Empty" };

    // The App builds with ImplicitUsings (Directory.Build.props); mirror Microsoft.NET.Sdk's set.
    private const string ImplicitUsings =
        "global using global::System; global using global::System.Collections.Generic; global using global::System.IO; " +
        "global using global::System.Linq; global using global::System.Net.Http; global using global::System.Threading; " +
        "global using global::System.Threading.Tasks;";

    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(LoadReferences);

    public static IReadOnlyList<ColorLiteralFinding> Scan(IReadOnlyList<(string Path, string Source)> sources, Func<string, bool> shouldReport)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s.Source, parseOptions, s.Path)).ToList();

        var syntaxErrors = trees.SelectMany(t => t.GetDiagnostics())
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        if (syntaxErrors.Count > 0)
        {
            throw new InvalidOperationException(
                "The color-literal guard cannot vouch for code it cannot parse (fail closed). Upgrade " +
                "Microsoft.CodeAnalysis.CSharp in Directory.Packages.props if the syntax is new: " +
                string.Join(Environment.NewLine, syntaxErrors));
        }

        trees.Add(CSharpSyntaxTree.ParseText(ImplicitUsings, parseOptions, "ImplicitUsings.g.cs"));
        var compilation = CSharpCompilation.Create(
            "Ntilde",
            trees,
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));

        var missing = RequiredTypeNames.Where(name => compilation.GetTypeByMetadataName(name) is null).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException("The color-literal guard is blind: unresolved " + string.Join(", ", missing));
        }

        var colorTypes = Resolve(compilation, ColorTypeNames);
        var paletteTypes = Resolve(compilation, PaletteTypeNames);
        var memberNames = paletteTypes.Concat(colorTypes)
            .SelectMany(t => t.GetMembers())
            .Where(m => m.IsStatic)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var findings = new List<ColorLiteralFinding>();
        foreach (var tree in trees)
        {
            if (shouldReport(tree.FilePath))
            {
                findings.AddRange(ScanTree(tree, compilation.GetSemanticModel(tree), colorTypes, paletteTypes, memberNames));
            }
        }

        return findings;
    }

    private static List<ColorLiteralFinding> ScanTree(
        SyntaxTree tree,
        SemanticModel model,
        HashSet<INamedTypeSymbol> colorTypes,
        HashSet<INamedTypeSymbol> paletteTypes,
        HashSet<string> memberNames)
    {
        var findings = new List<ColorLiteralFinding>();
        var flagged = new List<TextSpan>();
        var root = tree.GetRoot();

        bool IsColorType(ITypeSymbol? type) => type is INamedTypeSymbol named && colorTypes.Contains(named.OriginalDefinition);

        void Flag(SyntaxNode node, string rule)
        {
            flagged.Add(node.Span);
            int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            findings.Add(new ColorLiteralFinding(tree.FilePath, line, EnclosingMember(node), $"{rule}: {Normalize(node)}"));
        }

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BaseObjectCreationExpressionSyntax creation
                    when model.GetOperation(creation) is IObjectCreationOperation op &&
                         IsColorType(op.Type) && op.Arguments.Length > 0 &&
                         op.Arguments.All(a => a.Value.ConstantValue.HasValue):
                    Flag(creation, "constant construction");
                    break;

                case InvocationExpressionSyntax invocation
                    when model.GetOperation(invocation) is IInvocationOperation op &&
                         op.TargetMethod.IsStatic && IsColorType(op.TargetMethod.ContainingType) &&
                         IsColorType(op.TargetMethod.ReturnType) && op.Arguments.Length > 0 &&
                         op.Arguments.All(a => a.Value.ConstantValue.HasValue):
                    Flag(invocation, "constant factory");
                    break;

                case IdentifierNameSyntax identifier
                    when memberNames.Contains(identifier.Identifier.ValueText) &&
                         !NoColorMembers.Contains(identifier.Identifier.ValueText) &&
                         model.GetSymbolInfo(identifier).Symbol is { IsStatic: true } symbol &&
                         symbol is IPropertySymbol or IFieldSymbol &&
                         (paletteTypes.Contains(symbol.ContainingType.OriginalDefinition) ||
                          (IsColorType(symbol.ContainingType) && IsColorType(MemberType(symbol)))):
                    Flag(identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier ? access : identifier, "named color");
                    break;

                case CastExpressionSyntax cast
                    when IsColorType(model.GetTypeInfo(cast).Type) && model.GetConstantValue(cast.Expression).HasValue:
                    Flag(cast, "constant conversion");
                    break;
            }
        }

        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (flagged.Any(span => span.Contains(literal.Span)))
            {
                continue;
            }

            if (literal.IsKind(SyntaxKind.StringLiteralExpression) &&
                literal.Token.ValueText.StartsWith('#') &&
                XamlColorLiteralScanner.IsColorLiteral(literal.Token.ValueText))
            {
                Flag(literal, "color string");
            }
            else if (literal.IsKind(SyntaxKind.NumericLiteralExpression))
            {
                var info = model.GetTypeInfo(literal);
                if (IsColorType(info.ConvertedType) && !IsColorType(info.Type))
                {
                    Flag(literal, "constant conversion");
                }
            }
        }

        return findings;
    }

    private static ITypeSymbol? MemberType(ISymbol symbol) => symbol switch
    {
        IPropertySymbol property => property.Type,
        IFieldSymbol field => field.Type,
        _ => null,
    };

    private static string EnclosingMember(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax method: return method.Identifier.ValueText;
                case ConstructorDeclarationSyntax constructor: return constructor.Identifier.ValueText;
                case PropertyDeclarationSyntax property: return property.Identifier.ValueText;
                case FieldDeclarationSyntax field: return field.Declaration.Variables[0].Identifier.ValueText;
                case EventDeclarationSyntax @event: return @event.Identifier.ValueText;
                case IndexerDeclarationSyntax: return "this[]";
            }
        }

        return "";
    }

    private static string Normalize(SyntaxNode node) => string.Join(' ', node.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static HashSet<INamedTypeSymbol> Resolve(Compilation compilation, IEnumerable<string> names)
    {
        var set = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var name in names)
        {
            if (compilation.GetTypeByMetadataName(name) is { } type)
            {
                set.Add(type);
            }
        }

        return set;
    }

    private static List<MetadataReference> LoadReferences()
    {
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var local = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll");

        // "Ntilde" is the App assembly: the compilation IS that assembly, built from source.
        return trusted.Concat(local)
            .Where(path => !string.Equals(Path.GetFileNameWithoutExtension(path), "Ntilde", StringComparison.OrdinalIgnoreCase))
            .GroupBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(IsManagedAssembly)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.HasMetadata && reader.GetMetadataReader().IsAssembly;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
