namespace Ntilde.Tests.Core;

public sealed class SettingsWindowShortcutTextTests
{
    private const string Written = "ask for it with Ctrl+Space or Ctrl+R.";

    [Fact]
    public void SubstituteChords_ReplacesEachChordWithItsOwnBinding()
    {
        string text = SettingsWindow.SubstituteChords(Written, ("Ctrl+Space", "Ctrl+Space"), ("Ctrl+R", "Cmd+R"));

        Assert.Equal("ask for it with Ctrl+Space or Cmd+R.", text);
    }

    [Fact]
    public void SubstituteChords_DoesNotRewriteASubstitutedBinding()
    {
        // Toggle rebound to Ctrl+R, history on Cmd+R: a sequential replace turned the toggle's
        // freshly inserted Ctrl+R into Cmd+R as well.
        string text = SettingsWindow.SubstituteChords(Written, ("Ctrl+Space", "Ctrl+R"), ("Ctrl+R", "Cmd+R"));

        Assert.Equal("ask for it with Ctrl+R or Cmd+R.", text);
    }
}
