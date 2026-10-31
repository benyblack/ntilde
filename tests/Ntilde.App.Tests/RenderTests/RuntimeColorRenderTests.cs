using Avalonia.Headless.XUnit;
using Xunit;

namespace Ntilde.Tests.RenderTests;

[Trait("Lane", "PlatformBoot")]
[Collection("GoldenPng")]
public sealed class RuntimeColorRenderTests
{
    [AvaloniaTheory]
    [InlineData("\u001b[?25l\u001b[48;5;200m \u001b[0m", "4;200;#123456")]
    [InlineData("\u001b[?25lText", "10;#123456;#789abc")]
    [InlineData("\u001b[?25lText", "10;#123456")]
    [InlineData("", "12;#123456")]
    public void Live_row_cache_draws_runtime_colors_and_pop_restores_identical_pixels(string content, string change)
    {
        using var harness = new DrawPathHarness(10, 3);
        harness.Parser.Process(content);
        harness.DrawFrame();
        byte[] before = harness.Pixels();
        harness.Parser.Process("\u001b]30001\a\u001b]" + change + "\a");
        harness.DrawFrame();
        byte[] after = harness.Pixels();
        Assert.NotEqual(before, after);
        harness.DrawFrame();
        Assert.Equal(after, harness.Pixels());
        harness.Parser.Process("\u001b]30101\a");
        harness.DrawFrame();
        Assert.Equal(before, harness.Pixels());
    }
}
