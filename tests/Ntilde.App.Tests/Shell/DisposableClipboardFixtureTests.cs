using Ntilde.AgentHost.Contracts;
using Ntilde.Shell.Clipboard;
using System.Text;
using System.Security.Cryptography;

namespace Ntilde.AppTests.Shell;

public sealed class DisposableClipboardFixtureTests
{
    private static readonly object SourceParser = new();
    private const string Run = "0123456789abcdef0123456789abcdef";
    private const string Nonce = "terminfo-osc52-01234567-89ab-cdef-0123-456789abcdef";
    private static ClipboardFixtureParams Op(DisposableClipboardFixture fixture, string op, string? value = null)
        => new() { PaneId = Guid.NewGuid(), RunId = Run, FixtureId = fixture.Id, Operation = op, Value = value };

    [Fact]
    public void Exact_armed_write_is_observed_then_private_seed_is_restored_on_close()
    {
        var fixture = new DisposableClipboardFixture(Run, SourceParser, () => true);
        Assert.NotNull(fixture.Apply(Op(fixture, "armWrite", Nonce)));
        Assert.True(fixture.AcceptOsc52(SourceParser, "c", Encoding.ASCII.GetBytes(Nonce)));
        var read = fixture.Apply(Op(fixture, "read"));
        Assert.Equal(Nonce, read?.Value);
        Assert.Equal(1, read?.AcceptedWrites);
        var closed = fixture.Apply(Op(fixture, "close"));
        Assert.Equal("closed", closed?.State);
        Assert.True(closed?.Restored);
        Assert.True(closed?.Complete);
        Assert.Null(closed?.Value);
        Assert.Equal(closed, fixture.Apply(Op(fixture, "close")));
        Assert.False(fixture.AcceptOsc52(SourceParser, "c", Encoding.ASCII.GetBytes(Nonce)));
        Assert.Null(fixture.Apply(Op(fixture, "read")));
    }

    [Fact]
    public void Independent_seed_can_only_read_back_a_declared_known_nonce()
    {
        var fixture = new DisposableClipboardFixture(Run, SourceParser, () => true);
        Assert.NotNull(fixture.Apply(Op(fixture, "seed", Nonce)));
        Assert.Equal(Nonce, fixture.Apply(Op(fixture, "read"))?.Value);
        Assert.Null(fixture.Apply(Op(fixture, "seed", "personal value")));
        Assert.Equal(Nonce, fixture.Apply(Op(fixture, "read"))?.Value);
    }

    [Theory]
    [InlineData("p", Nonce)]
    [InlineData("cp", Nonce)]
    [InlineData("c", "personal value")]
    [InlineData("c", "terminfo-osc52-ffffffff-ffff-ffff-ffff-ffffffffffff")]
    public void Wrong_selection_or_unarmed_payload_aborts_without_retaining_bytes(string target, string value)
    {
        var fixture = new DisposableClipboardFixture(Run, SourceParser, () => true);
        fixture.Apply(Op(fixture, "armWrite", Nonce));
        Assert.False(fixture.AcceptOsc52(SourceParser, target, Encoding.UTF8.GetBytes(value)));
        var aborted = fixture.Apply(Op(fixture, "close"));
        Assert.Equal("aborted", aborted?.State);
        Assert.True(aborted?.Restored);
        Assert.False(aborted?.Complete);
        Assert.Null(aborted?.Value);
        Assert.Equal(aborted, fixture.Apply(Op(fixture, "close")));
    }

    [Fact]
    public void No_arm_and_repeated_arm_refuse_and_cannot_become_successful_close()
    {
        var first = new DisposableClipboardFixture(Run, SourceParser, () => true);
        Assert.False(first.AcceptOsc52(SourceParser, "c", Encoding.ASCII.GetBytes(Nonce)));
        Assert.False(first.Apply(Op(first, "close"))?.Complete);
        var second = new DisposableClipboardFixture(Run, SourceParser, () => true);
        second.Apply(Op(second, "armWrite", Nonce));
        Assert.Null(second.Apply(Op(second, "armWrite", Nonce)));
        Assert.False(second.Apply(Op(second, "close"))?.Complete);
    }

    [Fact]
    public void Identity_loss_and_interruption_restore_but_never_complete_even_after_repeated_close()
    {
        bool current = true;
        var fixture = new DisposableClipboardFixture(Run, SourceParser, () => current);
        fixture.Apply(Op(fixture, "seed", Nonce));
        current = false;
        Assert.Null(fixture.Apply(Op(fixture, "read")));
        current = true;
        fixture.Interrupt();
        var result = fixture.Apply(Op(fixture, "close"));
        Assert.Equal("aborted", result?.State);
        Assert.True(result?.Restored);
        Assert.False(result?.Complete);
        Assert.Equal(result, fixture.Apply(Op(fixture, "close")));
    }

    [Fact]
    public void Close_with_pending_write_and_wrong_run_cannot_attest_completion()
    {
        var fixture = new DisposableClipboardFixture(Run, SourceParser, () => true);
        Assert.Null(fixture.Apply(Op(fixture, "close") with { RunId = new string('f', 32) }));
        fixture.Apply(Op(fixture, "armWrite", Nonce));
        Assert.False(fixture.Apply(Op(fixture, "close"))?.Complete);
    }

    [Fact]
    public void Identity_loss_during_read_or_after_restore_withholds_observation_and_success()
    {
        int readChecks = 0;
        var reading = new DisposableClipboardFixture(Run, SourceParser, () => ++readChecks == 1);
        Assert.Null(reading.Apply(Op(reading, "read")));
        Assert.Equal("aborted", reading.Observe().State);
        int closeChecks = 0;
        var closing = new DisposableClipboardFixture(Run, SourceParser, () => ++closeChecks == 1);
        var closed = closing.Apply(Op(closing, "close"));
        Assert.Equal("aborted", closed?.State);
        Assert.True(closed?.Restored);
        Assert.False(closed?.Complete);
    }

    [Fact]
    public void Per_probe_restore_cancels_armed_write_and_reports_seed_hash_without_completing_fixture()
    {
        var fixture = new DisposableClipboardFixture(Run, SourceParser, () => true);
        fixture.Apply(Op(fixture, "seed", Nonce));
        fixture.Apply(Op(fixture, "armWrite", Nonce));
        var restored = fixture.Apply(Op(fixture, "restore"));
        Assert.NotNull(restored);
        Assert.Equal("active", restored.State);
        Assert.True(restored.Restored);
        Assert.False(restored.Complete);
        Assert.Null(restored.Value);
        string seed = "terminfo-private-seed-" + Run;
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed))), restored.ValueSha256);
        Assert.Equal(seed, fixture.Apply(Op(fixture, "read"))?.Value);
        Assert.Equal(restored, fixture.Apply(Op(fixture, "restore")));
        // Pending arm was cancelled, so final close can complete without an outstanding write.
        Assert.True(fixture.Apply(Op(fixture, "close"))?.Complete);
        Assert.Null(fixture.Apply(Op(fixture, "restore")));
    }

    [Fact]
    public void Restored_storage_can_be_reused_but_later_seed_and_write_clear_restore_fact()
    {
        var fixture = new DisposableClipboardFixture(Run, SourceParser, () => true);
        Assert.True(fixture.Apply(Op(fixture, "restore"))?.Restored);
        Assert.False(fixture.Apply(Op(fixture, "seed", Nonce))?.Restored);
        fixture.Apply(Op(fixture, "restore"));
        fixture.Apply(Op(fixture, "armWrite", Nonce));
        Assert.True(fixture.AcceptOsc52(SourceParser, "c", Encoding.ASCII.GetBytes(Nonce)));
        Assert.False(fixture.Observe().Restored);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Nonce))), fixture.Observe().ValueSha256);
        Assert.True(fixture.Apply(Op(fixture, "restore"))?.Restored);
        Assert.True(fixture.Apply(Op(fixture, "close"))?.Complete);
    }

    [Fact]
    public void Restore_refuses_wrong_identity_and_cannot_revive_an_aborted_fixture()
    {
        bool current = true;
        var fixture = new DisposableClipboardFixture(Run, SourceParser, () => current);
        fixture.Apply(Op(fixture, "armWrite", Nonce));
        current = false;
        Assert.Null(fixture.Apply(Op(fixture, "restore")));
        current = true;
        Assert.Null(fixture.Apply(Op(fixture, "restore")));
        Assert.False(fixture.Apply(Op(fixture, "close"))?.Complete);
        int checks = 0;
        var racing = new DisposableClipboardFixture(Run, SourceParser, () => ++checks == 1);
        Assert.Null(racing.Apply(Op(racing, "restore")));
        Assert.Equal("aborted", racing.Observe().State);
        Assert.False(racing.Observe().Complete);
    }

    [Fact]
    public void A_write_arriving_after_restore_cannot_use_the_cancelled_arm()
    {
        var fixture = new DisposableClipboardFixture(Run, SourceParser, () => true);
        fixture.Apply(Op(fixture, "armWrite", Nonce));
        fixture.Apply(Op(fixture, "restore"));
        Assert.False(fixture.AcceptOsc52(SourceParser, "c", Encoding.ASCII.GetBytes(Nonce)));
        Assert.Equal("aborted", fixture.Observe().State);
        Assert.Equal(0, fixture.Observe().AcceptedWrites);
    }

    [Theory]
    [InlineData("terminfo-osc52-01234567-89AB-cdef-0123-456789abcdef")]
    [InlineData("terminfo-osc52-0123456789abcdef0123456789abcdef")]
    [InlineData("prefix-01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("terminfo-osc52-01234567-89ab-cdef-0123-456789abcdef\n")]
    public void Noncanonical_or_arbitrary_values_are_not_known_values(string value)
        => Assert.False(ClipboardFixtureValidation.IsNonce(value));
}
