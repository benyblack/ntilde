using Ntilde.AgentHost;
using Ntilde.CommandAssist.Domain;

namespace Ntilde.AppTests.AgentHost;

/// <summary>
/// The monitor's real dependencies. Screens must go through the screen-oriented filter, not the
/// command-history one: the two differ in what they catch, and only the composition root chooses.
/// </summary>
public class ObservedActivityMonitorCompositionTests
{
    [Fact]
    public void Default_screen_filter_is_the_screen_oriented_layer_over_the_history_filter()
    {
        ISecretsFilter filter = ObservedActivityMonitorComposition.CreateScreenSecretsFilter();

        Assert.IsType<ScreenSecretsFilter>(filter);
        // One pattern from each layer, through the composed instance.
        Assert.Equal("--password [REDACTED] DB_PASSWORD=[REDACTED]", filter.Redact("--password x DB_PASSWORD=y").RedactedText);
    }
}
