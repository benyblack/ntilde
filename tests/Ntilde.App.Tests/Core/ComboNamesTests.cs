using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Ntilde.Settings;

namespace Ntilde.Tests.Core;

public sealed class ComboNamesTests
{
    [AvaloniaFact]
    public void Fill_ReplacesTheItems_WithOnePerName_InTheGivenOrder()
    {
        var combo = new ComboBox();
        combo.Items.Add(new ComboBoxItem { Content = "Stale Mono" });

        ComboNames.Fill(combo, ["Zeta Mono", "Alpha Mono"]);

        Assert.Equal(["Zeta Mono", "Alpha Mono"], combo.Items.OfType<ComboBoxItem>().Select(item => (string?)item.Content));
    }
}
