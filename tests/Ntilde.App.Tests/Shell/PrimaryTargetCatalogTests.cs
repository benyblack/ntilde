using Ntilde.Shell.Clipboard;

namespace Ntilde.Tests.Shell;

public class PrimaryTargetCatalogTests
{
    [Fact]
    public void Many_control_targets_do_not_hide_standard_text_or_trigger_the_old_raw_atom_cap()
    {
        int lookups = 0;
        var catalog = new PrimaryTargetCatalog(7000, 7001, _ => { lookups++; return "CONTROL"; }, _ => 0);
        uint[] targets = Enumerable.Range(100, 1000).Select(i => (uint)i).Append(7000u).ToArray();
        var formats = catalog.Select(targets, []);
        Assert.Equal(7000u, formats["text/plain"]);
        Assert.InRange(lookups, 1, 512);
    }

    [Fact]
    public void MIME_cap_applies_after_target_filtering_and_explicit_reads_find_late_targets_without_name_scans()
    {
        var catalog = new PrimaryTargetCatalog(7000, 7001, atom => "application/x-" + atom, _ => 9999);
        uint[] targets = Enumerable.Range(100, 300).Select(i => (uint)i).Append(9999u).ToArray();
        Assert.Equal(128, catalog.Select(targets, []).Count);
        var requested = catalog.Select(targets, ["application/late"]);
        Assert.Equal(9999u, Assert.Single(requested).Value);
    }

    [Fact]
    public void Exact_plain_MIME_takes_precedence_over_UTF8_and_legacy_STRING()
    {
        var catalog = new PrimaryTargetCatalog(100, 101, _ => "CONTROL", _ => 0);
        Assert.Equal(101u, catalog.Select([31, 100, 101], ["text/plain"])["text/plain"]);
        Assert.Equal(100u, catalog.Select([31, 100], ["text/plain"])["text/plain"]);
    }
}
