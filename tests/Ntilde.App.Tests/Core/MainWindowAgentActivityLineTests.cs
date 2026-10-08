using Ntilde.AgentHost;

namespace Ntilde.Tests.Core;

/// <summary>
/// One line of the Agent Activity dialog (Phase 5 Task 13, fix round 1): a windowless session's id is called a
/// session's, never a pane's, and a folded run of reads says how many it stands for.
/// </summary>
public class MainWindowAgentActivityLineTests
{
    private static readonly Guid Id = Guid.Parse("0a1b2c3d-0000-0000-0000-000000000001");

    private static AgentActivityEntry Entry(bool windowless, int count = 1, string method = "readScreen") => new()
    {
        TimestampUtc = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero),
        Method = method,
        PaneId = Id,
        Target = windowless ? "windowless · nova@build-box" : "Profile",
        Outcome = "ok",
        Count = count,
        Windowless = windowless,
    };

    [Fact]
    public void A_windowless_entry_names_its_session_not_a_pane()
    {
        string line = MainWindow.DescribeAgentActivity(Entry(windowless: true));

        Assert.EndsWith($"[ok]  windowless · nova@build-box · session {Id}", line, StringComparison.Ordinal);
        Assert.DoesNotContain("pane", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pane_entry_still_names_its_pane()
    {
        string line = MainWindow.DescribeAgentActivity(Entry(windowless: false, method: "sendInput"));

        Assert.EndsWith($"sendInput  [ok]  Profile · pane {Id}", line, StringComparison.Ordinal);
        Assert.DoesNotContain("×", line, StringComparison.Ordinal); // a single entry carries no count
    }

    [Fact]
    public void A_folded_run_of_reads_says_how_many_it_stands_for()
    {
        string line = MainWindow.DescribeAgentActivity(Entry(windowless: true, count: 150, method: "getSessionStatus"));

        Assert.Contains("getSessionStatus ×150  [ok]", line, StringComparison.Ordinal);
    }
}
