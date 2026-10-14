using System.Xml.Linq;

namespace Ntilde.Architecture.Tests;

/// <summary>
/// Every .axaml under src/Ntilde.App/Settings must reference resources with DynamicResource.
/// </summary>
/// <remarks>
/// A Settings page loads its XAML in its own constructor, before it joins the window, so a
/// StaticResource there cannot see the window's palette. Depending on where it sits, it either
/// throws at load or quietly resolves to nothing. NtStyles has the same problem one level up.
/// This parses each attribute's markup extensions (nested ones included) rather than searching for
/// a spelling, so a prefixed or nested StaticResource is caught too.
/// </remarks>
public class SettingsXamlResourceTests
{
    [Fact]
    public void SettingsXaml_ReferencesResourcesDynamically()
    {
        string settingsDirectory = Path.Combine(RepoRoot(), "src", "Ntilde.App", "Settings");
        var violations = new List<string>();

        foreach (string file in Directory.EnumerateFiles(settingsDirectory, "*.axaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(RepoRoot(), file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            XDocument document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants())
            {
                if (element.Name.LocalName is "StaticResource" or "StaticResourceExtension")
                {
                    violations.Add($"{relative}:{((System.Xml.IXmlLineInfo)element).LineNumber}: <{element.Name.LocalName}> element");
                }

                foreach (XAttribute attribute in element.Attributes())
                {
                    if (MarkupExtensionNames(attribute.Value).Any(name => name is "StaticResource" or "StaticResourceExtension"))
                    {
                        violations.Add($"{relative}:{((System.Xml.IXmlLineInfo)attribute).LineNumber}: {attribute.Name.LocalName}=\"{attribute.Value}\"");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Settings XAML must use {DynamicResource …}; a page loads before it joins the window:\n" + string.Join("\n", violations));
    }

    [Theory]
    [InlineData("{StaticResource NtFg}", "StaticResource")]
    [InlineData("{x:StaticResource NtFg}", "StaticResource")]
    [InlineData("{DynamicResource NtFg}", "DynamicResource")]
    [InlineData("{Binding Path, Converter={StaticResource Conv}}", "Binding,StaticResource")]
    [InlineData("{}{StaticResource is literal}", "")]
    [InlineData("plain text", "")]
    public void MarkupExtensionNames_FindsEveryExtension_IncludingNestedOnes(string value, string expected)
    {
        Assert.Equal(expected, string.Join(",", MarkupExtensionNames(value)));
    }

    // Hoisted: a constant array argument trips CA1861 under TreatWarningsAsErrors.
    private static readonly char[] NameEnd = [' ', '}', ',', '\t', '\r', '\n'];

    /// <summary>
    /// The markup extension names an attribute value invokes, outermost first. A value starting with
    /// "{}" is an escaped literal and invokes none.
    /// </summary>
    internal static IEnumerable<string> MarkupExtensionNames(string value)
    {
        string trimmed = value.TrimStart();
        if (!trimmed.StartsWith('{') || trimmed.StartsWith("{}", StringComparison.Ordinal))
        {
            yield break;
        }

        for (int i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] != '{')
            {
                continue;
            }

            int end = trimmed.IndexOfAny(NameEnd, i + 1);
            string name = end < 0 ? trimmed[(i + 1)..] : trimmed[(i + 1)..end];
            int colon = name.IndexOf(':');
            yield return colon >= 0 ? name[(colon + 1)..] : name;
        }
    }

    // Same walk-up as CompiledBindingTests.RepoRoot().
    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ntilde.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }
}
