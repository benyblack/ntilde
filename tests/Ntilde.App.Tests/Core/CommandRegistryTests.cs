using Avalonia.Headless.XUnit;
using Ntilde.Shell;

namespace Ntilde.Tests.Core;

/// <summary>
/// #112: <see cref="CommandRegistry.GetCommands"/> and an empty <see cref="CommandRegistry.Search"/> handed out the
/// registry's backing list. The registry is process-wide, so these are [AvaloniaFact]: they run on the headless
/// dispatcher with the window tests that Clear and re-register it, never beside them.
/// </summary>
public sealed class CommandRegistryTests
{
    [AvaloniaFact]
    public void GetCommands_is_a_snapshot_that_a_later_registration_does_not_change()
    {
        CommandRegistry.Clear();
        CommandRegistry.Register("First", "Test", () => { }, id: "registry_test_first");

        IReadOnlyList<TerminalCommand> before = CommandRegistry.GetCommands();
        CommandRegistry.Register("Second", "Test", () => { }, id: "registry_test_second");

        Assert.Single(before);
        Assert.Equal(2, CommandRegistry.GetCommands().Count);
        Assert.IsNotType<List<TerminalCommand>>(before);
        CommandRegistry.Clear();
    }

    [AvaloniaFact]
    public void Changing_an_empty_query_result_does_not_change_the_registry()
    {
        CommandRegistry.Clear();
        CommandRegistry.Register("First", "Test", () => { }, id: "registry_test_first");

        List<TerminalCommand> all = CommandRegistry.Search("");
        all.Clear();

        Assert.Single(CommandRegistry.GetCommands());
        CommandRegistry.Clear();
    }
}
