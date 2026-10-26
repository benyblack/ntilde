using Ntilde.Architecture.Tests.ThemeLiterals;

namespace Ntilde.Architecture.Tests;

/// <summary>
/// Every spelling of a hardcoded color the scanners must catch, and the look-alikes they must not.
/// These are the scanners' own contract; ThemeLiteralGuardTests applies them to the repo.
/// </summary>
public class ColorLiteralScannerTests
{
    private const string XamlOpen = "<Root xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">";

    [Theory]
    [InlineData("<Border Background=\"#252526\"/>", 1)]
    [InlineData("<Border Background='#252526'/>", 1)]
    [InlineData("<TextBlock Foreground=\"White\"/>", 1)]
    [InlineData("<Border Background=\"rgb(1,2,3)\"/>", 1)]
    [InlineData("<Style><Setter Property=\"Background\" Value=\"#30FFFFFF\"/></Style>", 1)]
    [InlineData("<SolidColorBrush x:Key=\"TextControlBackground\">#1a1a1a</SolidColorBrush>", 1)]
    [InlineData("<GradientStop Color=\"#FF0000\"/>", 1)]
    [InlineData("<Border Background=\"#002a2f38\"/>", 1)]
    [InlineData("<TextBlock Foreground=\"{Binding Tint, FallbackValue=#FF0000}\"/>", 1)]
    [InlineData("<Border Background=\"Transparent\"/>", 0)]
    [InlineData("<Border Background=\"{DynamicResource NtPanel}\"/>", 0)]
    [InlineData("<Border ToolTip.Tip=\"Black\"/>", 0)]
    [InlineData("<TextBlock Text=\"Red\"/>", 0)]
    [InlineData("<Style Selector=\"Button#SettingsBtn:pointerover\"/>", 0)]
    [InlineData("<Border Width=\"1080\" Opacity=\"0.6\" Margin=\"0,50,20,0\"/>", 0)]
    public void Xaml_rules(string fragment, int expected)
        => Assert.Equal(expected, XamlColorLiteralScanner.Scan("src/Probe.axaml", XamlOpen + fragment + "</Root>").Count);

    [Theory]
    [InlineData("var b = Avalonia.Media.Brushes.White;", 1)]
    [InlineData("var c = Avalonia.Media.Colors.Red;", 1)]
    [InlineData("var c = Avalonia.Media.Color.Parse(\"#112233\");", 1)]
    [InlineData("var c = Avalonia.Media.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);", 1)]
    [InlineData("Avalonia.Media.Color c = new(255, 1, 2, 3);", 1)]
    [InlineData("var s = new SkiaSharp.SKColor(51, 153, 255, 100);", 1)]
    [InlineData("SkiaSharp.SKColor s = 0xFF336699;", 1)]
    [InlineData("var s = (SkiaSharp.SKColor)0xFF336699;", 1)]
    [InlineData("var t = Ntilde.VT.TermColor.FromRgb(1, 2, 3);", 1)]
    [InlineData("var t = Ntilde.VT.TermColor.Black;", 1)]
    [InlineData("string[] palette = { \"#e06c75\", \"#98c379\" };", 2)]
    [InlineData("var b = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Red);", 1)]
    [InlineData("var b = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.FromRgb(0xF3, 0xF6, 0xFA));", 1)]
    [InlineData("var b = Avalonia.Media.Brushes.Transparent;", 0)]
    [InlineData("var s = SkiaSharp.SKColors.Empty;", 0)]
    [InlineData("byte r = 1; var c = Avalonia.Media.Color.FromRgb(r, 2, 3);", 0)]
    [InlineData("var k = \"#region\";", 0)]
    [InlineData("var n = 0xFF336699;", 0)]
    public void Csharp_rules(string statement, int expected)
    {
        var source = "class Probe { void M() { " + statement + " } }";
        Assert.Equal(expected, CSharpColorLiteralScanner.Scan([("src/Ntilde.App/Probe.cs", source)], _ => true).Count);
    }

    [Fact]
    public void Using_static_does_not_hide_a_named_color()
    {
        const string source = "using static Avalonia.Media.Colors; class Probe { object M() => White; }";
        Assert.Single(CSharpColorLiteralScanner.Scan([("src/Ntilde.App/Probe.cs", source)], _ => true));
    }

    [Fact]
    public void Findings_name_their_enclosing_member()
    {
        const string source = "class Probe { private static readonly string[] GroupSwatches = { \"#e06c75\" }; }";
        var finding = Assert.Single(CSharpColorLiteralScanner.Scan([("src/Ntilde.App/Probe.cs", source)], _ => true));
        Assert.Equal("GroupSwatches", finding.Member);
        Assert.Equal(1, finding.Line);
    }

    [Fact]
    public void Unparseable_source_fails_closed()
        => Assert.Throws<InvalidOperationException>(() =>
            CSharpColorLiteralScanner.Scan([("src/Ntilde.App/Broken.cs", "class {")], _ => true));

    [Fact]
    public void Unreported_files_still_bind_but_yield_nothing()
    {
        const string source = "class Probe { object M() => Avalonia.Media.Brushes.White; }";
        Assert.Empty(CSharpColorLiteralScanner.Scan([("src/Ntilde.App/Shell/Theming/Probe.cs", source)], _ => false));
    }
}
