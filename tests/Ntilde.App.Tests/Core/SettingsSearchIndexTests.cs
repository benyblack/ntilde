using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ntilde.Settings;

namespace Ntilde.Tests.Core;

public sealed class SettingsSearchIndexTests
{
    private static SettingsSearchEntry Entry(string title, string? description = null, string? keywords = null, string? group = null, SettingsPage page = SettingsPage.Appearance)
        => new(page, SettingsPageRegistry.Get(page).Title, group, title.ToLowerInvariant().Replace(' ', '-'), title, description, keywords);

    [Fact]
    public void Search_IsCaseInsensitive_AndTrimsTheQuery()
    {
        var index = new SettingsSearchIndex([Entry("Window opacity")]);

        Assert.Single(index.Search("  OPACITY "));
    }

    [Fact]
    public void Search_PutsTitleMatchesFirst_ThenKeepsIndexOrder()
    {
        var index = new SettingsSearchIndex(
        [
            Entry("Blur effect", keywords: "transparency opacity"),
            Entry("Window opacity"),
            Entry("Image opacity"),
        ]);

        string[] titles = index.Search("opacity").Select(r => r.Entry.Title).ToArray();

        Assert.Equal(new[] { "Window opacity", "Image opacity", "Blur effect" }, titles);
    }

    [Fact]
    public void Search_ReportsWhereTheTitleMatched()
    {
        var index = new SettingsSearchIndex([Entry("Window opacity", description: "opacity")]);

        SettingsSearchResult result = Assert.Single(index.Search("opac"));

        Assert.Equal((7, 4), (result.TitleMatchStart, result.TitleMatchLength));
    }

    [Fact]
    public void Search_MatchesDescriptionKeywordsGroupAndPageTitle()
    {
        var index = new SettingsSearchIndex(
        [
            Entry("A", description: "uses the zebra"),
            Entry("B", keywords: "giraffe"),
            Entry("C", group: "OKAPI"),
            Entry("D", page: SettingsPage.Ssh),
        ]);

        Assert.Equal("A", Assert.Single(index.Search("zebra")).Entry.Title);
        Assert.Equal("B", Assert.Single(index.Search("giraffe")).Entry.Title);
        Assert.Equal("C", Assert.Single(index.Search("okapi")).Entry.Title);
        Assert.Contains(index.Search("ssh"), r => r.Entry.Title == "D");
    }

    [Fact]
    public void Search_TreatsPunctuationLiterally_AndIgnoresAnEmptyQuery()
    {
        var index = new SettingsSearchIndex([Entry("Progress reporting (OSC 9;4)")]);

        Assert.Single(index.Search("9;4"));
        Assert.Single(index.Search("(OSC"));
        Assert.Empty(index.Search(""));
        Assert.Empty(index.Search("   "));
        Assert.Empty(index.Search(null));
    }

    /// <summary>Spec: a row added to a page is searchable with no extra registration.</summary>
    [AvaloniaFact]
    public void Build_IndexesEveryTitledRow_WithItsGroup_AndOneEntryPerPage()
    {
        var newRow = new SettingRow { Title = "Brand new row", RowId = "brand-new" };
        var view = new UserControl
        {
            Content = new StackPanel
            {
                Children =
                {
                    new SettingGroup { Header = "WINDOW", Items = { new SettingRow { Title = "Window opacity", RowId = "window-opacity" } } },
                    newRow,
                    new SettingRow { Title = "" },
                },
            },
        };

        var index = SettingsSearchIndex.Build([(SettingsPageRegistry.Get(SettingsPage.Appearance), (Control)view)]);

        string[] titles = index.Entries.Select(e => e.Title).ToArray();
        Assert.Equal(new[] { "Appearance", "Window opacity", "Brand new row" }, titles);
        Assert.Equal("WINDOW", index.Entries[1].Group);
        Assert.Null(index.Entries[0].RowId);
        Assert.Equal("brand-new", index.Entries[2].RowId);
    }
}
