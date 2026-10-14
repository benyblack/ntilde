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

    [Fact]
    public void Search_MatchesEveryWord_InAnyFieldAndAnyOrder()
    {
        var index = new SettingsSearchIndex(
        [
            Entry("Font size", group: "FONT"),
            Entry("Programming ligatures", keywords: "font arrows"),
            Entry("Window opacity"),
        ]);

        Assert.Equal("Font size", Assert.Single(index.Search("size font")).Entry.Title);
        Assert.Equal("Programming ligatures", Assert.Single(index.Search("font  ligatures")).Entry.Title);
        Assert.Empty(index.Search("font opacity")); // each word is somewhere, but never both in one entry
    }

    /// <summary>
    /// A multi-word query ranks the entries with every word in the title first, then the rest, each
    /// in index order; the highlight is the whole query where the title holds it as typed, else the
    /// first query word the title holds.
    /// </summary>
    [Fact]
    public void Search_MultiWord_RanksTitlesHoldingEveryWordFirst_AndHighlightsWhatTheTitleHolds()
    {
        var index = new SettingsSearchIndex(
        [
            Entry("Blur effect", group: "WINDOW"),
            Entry("Acrylic", keywords: "blur", group: "WINDOW"),
            Entry("Window blur radius"),
            Entry("Blur behind window"),
            Entry("Window opacity"),
        ]);

        List<SettingsSearchResult> results = index.Search("window blur").ToList();

        Assert.Equal(new[] { "Window blur radius", "Blur behind window", "Blur effect", "Acrylic" }, results.Select(r => r.Entry.Title));
        Assert.Equal(
            new[] { (0, 11), (12, 6), (0, 4), (-1, 0) },
            results.Select(r => (r.TitleMatchStart, r.TitleMatchLength)));
    }

    /// <summary>The text behind "More" is searched, and a result says when a word was found only there.</summary>
    [Fact]
    public void Search_MatchesDetails_AndSaysWhenAWordIsOnlyThere()
    {
        var index = new SettingsSearchIndex(
        [
            Entry("Remember commands", description: "Record what you run.") with { Details = "A password typed at a hidden prompt is never captured." },
            Entry("Password prompt"),
        ]);

        Assert.Equal(
            new[] { ("Password prompt", false), ("Remember commands", true) },
            index.Search("password").Select(r => (r.Entry.Title, r.MatchedOnlyInDetails)));
        Assert.True(Assert.Single(index.Search("record password")).MatchedOnlyInDetails);
        Assert.False(Assert.Single(index.Search("commands")).MatchedOnlyInDetails);
    }

    /// <summary>Spec: a row added to a page is searchable with no extra registration.</summary>
    [AvaloniaFact]
    public void Build_IndexesEveryTitledRow_WithItsGroup_AndOneEntryPerPage()
    {
        var newRow = new SettingRow
        {
            Title = "Brand new row",
            RowId = "brand-new",
            Details = "More about it.",
        };
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
        Assert.Equal("More about it.", index.Entries[2].Details);
    }

    [AvaloniaFact]
    public void Build_GivesAPageEntryItsPageKeywords()
    {
        SettingsPageDescriptor backup = SettingsPageRegistry.Get(SettingsPage.Backup);

        var index = SettingsSearchIndex.Build([(backup, (Control)new UserControl())]);

        SettingsSearchEntry page = Assert.Single(index.Entries);
        Assert.Equal(backup.Keywords, page.Keywords);
        Assert.Contains("snapshot", page.Keywords);
    }
}
