using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Ntilde.Controls;

namespace Ntilde.Tests.Core;

/// <summary>
/// Task 18: the session-persistence row reads right for a reader who has persistence on by default.
/// </summary>
public sealed class SettingsWindowSessionPersistenceTests : IClassFixture<TestAppDataRoot>
{
    public SettingsWindowSessionPersistenceTests(TestAppDataRoot appData) => _ = appData;

    [AvaloniaFact]
    public void The_row_description_explains_the_multiplexer_and_how_to_turn_it_off()
    {
        var settings = new SettingsWindow();
        ComboBox list = settings.FindControl<ComboBox>("SessionPersistenceList")!;
        var row = (Grid)list.Parent!;

        string desc = row.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("RowDesc")).Text!;

        Assert.Contains("multiplexer", desc);
        Assert.Contains("Turn this off to end shells when their window closes.", desc);
        Assert.Contains("'Keep remote sessions running'", desc);
        Assert.EndsWith("Applies to new tabs.", desc);
        Assert.Equal(
            ["Off", "KeepOnClose"],
            list.Items.Cast<ComboBoxItem>().Select(i => (string)i.Tag!).ToArray());
        Assert.Equal(
            ["Off", "Keep running"],
            list.Items.Cast<ComboBoxItem>().Select(i => (string)i.Content!).ToArray());
    }
}
