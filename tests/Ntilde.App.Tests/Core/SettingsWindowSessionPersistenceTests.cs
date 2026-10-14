using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Ntilde.Controls;

namespace Ntilde.Tests.Core;

/// <summary>
/// Task 18, and Task 29's fix: the session-persistence row says what each value does, so it reads right whichever one is
/// chosen, and names the profile checkbox as the connection editor labels it.
/// </summary>
public sealed class SettingsWindowSessionPersistenceTests : IClassFixture<TestAppDataRoot>
{
    public SettingsWindowSessionPersistenceTests(TestAppDataRoot appData) => _ = appData;

    [AvaloniaFact]
    public void The_row_description_says_what_each_value_does()
    {
        var settings = new SettingsWindow();
        ComboBox list = settings.FindControl<ComboBox>("SessionPersistenceList")!;
        var row = (Grid)list.Parent!;

        string desc = row.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("RowDesc")).Text!;

        Assert.Equal(
            "With Keep running, your shells run in a background process (the multiplexer), so closing the window or a crash "
            + "does not end them, and Ntilde reattaches them when it starts. With Off, a shell ends when its window closes. "
            + "SSH tabs keep running on the host only when their connection also turns on "
            + "'Keep remote sessions running (ntilde-mux)'. Applies to new tabs.",
            desc);
        Assert.Equal(
            ["Off", "KeepOnClose"],
            list.Items.Cast<ComboBoxItem>().Select(i => (string)i.Tag!).ToArray());
        Assert.Equal(
            ["Off", "Keep running"],
            list.Items.Cast<ComboBoxItem>().Select(i => (string)i.Content!).ToArray());
    }
}
