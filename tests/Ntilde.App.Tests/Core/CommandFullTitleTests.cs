using Ntilde.Shell;
using Xunit;

namespace Ntilde.Tests.Core;

public sealed class CommandFullTitleTests
{
    [Theory]
    [InlineData("Theme: Dracula", "Theme", "Theme: Dracula")]
    [InlineData("Tab: Next (MRU)", "General", "Tab: Next (MRU)")]
    [InlineData("Cursor: Block", "View", "Cursor: Block")]
    [InlineData("New Tab", "General", "General: New Tab")]
    [InlineData("Split Vertical", "View", "View: Split Vertical")]
    public void FullTitle_PrefixesTheCategoryOnlyWhenTheTitleHasNoPrefix(string title, string category, string expected)
    {
        Assert.Equal(expected, new TerminalCommand { Title = title, Category = category }.FullTitle);
    }
}
