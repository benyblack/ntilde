using System;
using System.Linq;
using Ntilde.AgentHost;

namespace Ntilde.AppTests.AgentHost;

public class AgentActivityJournalTests
{
    [Fact]
    public void Record_appends_newest_first_and_raises_event()
    {
        var clock = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
        var journal = new AgentActivityJournal(() => clock);
        int raised = 0;
        journal.EntryAdded += _ => raised++;

        var pane = Guid.NewGuid();
        journal.Record("sendInput", pane, "Profile", "ok");
        journal.Record("sendInput", pane, "Profile", "actDisabled");

        Assert.Equal(2, raised);
        var entries = journal.Snapshot();
        Assert.Equal("actDisabled", entries[0].Outcome); // newest first
        Assert.Equal("ok", entries[1].Outcome);
        Assert.Equal(pane, entries[0].PaneId);
        Assert.Equal(clock, entries[0].TimestampUtc);
    }

    [Fact]
    public void Ring_is_bounded_to_capacity()
    {
        var journal = new AgentActivityJournal();
        for (int i = 0; i < AgentActivityJournal.Capacity + 50; i++)
        {
            journal.Record("sendInput", null, i.ToString(), "ok");
        }

        Assert.Equal(AgentActivityJournal.Capacity, journal.Count);
        var entries = journal.Snapshot();
        Assert.Equal(AgentActivityJournal.Capacity, entries.Count);
        // Newest entry is the last recorded; oldest survivor is entry #50.
        Assert.Equal((AgentActivityJournal.Capacity + 49).ToString(), entries[0].Target);
        Assert.Equal("50", entries[^1].Target);
    }

    // ── Reads fold (Phase 5 ruling R5, fix round 1) ─────────────────────────

    private const string Target = "windowless · this computer";

    [Fact]
    public void Identical_reads_since_the_last_act_fold_into_one_entry_that_becomes_the_newest()
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var journal = new AgentActivityJournal(() => now);
        var raised = new System.Collections.Generic.List<AgentActivityEntry>();
        journal.EntryAdded += raised.Add;
        var session = Guid.NewGuid();

        journal.RecordRead("getSessionStatus", session, Target, "ok", windowless: true);
        journal.RecordRead("readScreen", session, Target, "ok", windowless: true);
        now = now.AddSeconds(5);
        journal.RecordRead("getSessionStatus", session, Target, "ok", windowless: true);

        var entries = journal.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.Equal(("getSessionStatus", 2, now), (entries[0].Method, entries[0].Count, entries[0].TimestampUtc));
        Assert.Equal(("readScreen", 1), (entries[1].Method, entries[1].Count));
        Assert.True(entries[0].Windowless);
        Assert.Equal(session, entries[0].PaneId);
        Assert.Equal([1, 1, 2], raised.Select(e => e.Count)); // the fold is announced, with the entry as it now is
    }

    [Fact]
    public void Reads_never_fold_across_an_act_and_acts_never_fold()
    {
        var journal = new AgentActivityJournal();
        var session = Guid.NewGuid();

        journal.RecordRead("readScreen", session, Target, "ok", windowless: true);
        journal.Record("sendInput", session, Target, "ok", windowless: true);
        journal.RecordRead("readScreen", session, Target, "ok", windowless: true);
        journal.RecordRead("readScreen", session, Target, "ok", windowless: true);
        journal.Record("sendInput", session, Target, "ok", windowless: true);
        journal.Record("sendInput", session, Target, "ok", windowless: true);

        Assert.Equal(
            [("sendInput", 1), ("sendInput", 1), ("readScreen", 2), ("sendInput", 1), ("readScreen", 1)],
            journal.Snapshot().Select(e => (e.Method, e.Count)));
    }

    [Fact]
    public void Reads_that_differ_in_method_id_target_outcome_or_kind_do_not_fold()
    {
        var journal = new AgentActivityJournal();
        var session = Guid.NewGuid();

        journal.RecordRead("readScreen", session, Target, "ok", windowless: true);
        journal.RecordRead("readScrollback", session, Target, "ok", windowless: true);
        journal.RecordRead("readScreen", Guid.NewGuid(), Target, "ok", windowless: true);
        journal.RecordRead("readScreen", session, "windowless · nova@build-box", "ok", windowless: true);
        journal.RecordRead("readScreen", session, Target, "unsupported", windowless: true);
        journal.RecordRead("readScreen", session, Target, "ok", windowless: false);

        Assert.Equal(6, journal.Count);
        Assert.All(journal.Snapshot(), e => Assert.Equal(1, e.Count));
    }

    [Fact]
    public void A_polling_agent_does_not_push_an_act_out_of_the_ring()
    {
        var journal = new AgentActivityJournal();
        var session = Guid.NewGuid();
        journal.Record("sendInput", session, Target, "ok", windowless: true);

        for (int i = 0; i < 300; i++)
        {
            journal.RecordRead(i % 2 == 0 ? "getSessionStatus" : "readScreen", session, Target, "ok", windowless: true);
        }

        Assert.Equal(
            [("readScreen", 150), ("getSessionStatus", 150), ("sendInput", 1)],
            journal.Snapshot().Select(e => (e.Method, e.Count)));
    }
}
