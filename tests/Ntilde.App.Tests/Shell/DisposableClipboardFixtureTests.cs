using Ntilde.AgentHost.Contracts;
using Ntilde.Shell.Clipboard;
using System.Text;

namespace Ntilde.AppTests.Shell;

public sealed class DisposableClipboardFixtureTests
{
    private const string Run = "0123456789abcdef0123456789abcdef";
    private const string Nonce = "terminfo-osc52-01234567-89ab-cdef-0123-456789abcdef";
    private static ClipboardFixtureParams Op(DisposableClipboardFixture fixture, string op, string? value = null)
        => new() { PaneId = Guid.NewGuid(), RunId = Run, FixtureId = fixture.Id, Operation = op, Value = value };

    [Fact]
    public void Exact_armed_write_is_observed_then_private_seed_is_restored_on_close()
    {
        var fixture = new DisposableClipboardFixture(Run, () => true);
        Assert.NotNull(fixture.Apply(Op(fixture, "armWrite", Nonce)));
        Assert.True(fixture.AcceptOsc52("c", Encoding.ASCII.GetBytes(Nonce)));
        var read = fixture.Apply(Op(fixture, "read"));
        Assert.Equal(Nonce, read?.Value);
        Assert.Equal(1, read?.AcceptedWrites);
        var closed = fixture.Apply(Op(fixture, "close"));
        Assert.Equal("closed", closed?.State);
        Assert.True(closed?.Restored);
        Assert.True(closed?.Complete);
        Assert.Null(closed?.Value);
        Assert.Equal(closed, fixture.Apply(Op(fixture, "close")));
        Assert.False(fixture.AcceptOsc52("c", Encoding.ASCII.GetBytes(Nonce)));
        Assert.Null(fixture.Apply(Op(fixture, "read")));
    }

    [Fact]
    public void Independent_seed_can_only_read_back_a_declared_known_nonce()
    {
        var fixture = new DisposableClipboardFixture(Run, () => true);
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
        var fixture = new DisposableClipboardFixture(Run, () => true);
        fixture.Apply(Op(fixture, "armWrite", Nonce));
        Assert.False(fixture.AcceptOsc52(target, Encoding.UTF8.GetBytes(value)));
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
        var first = new DisposableClipboardFixture(Run, () => true);
        Assert.False(first.AcceptOsc52("c", Encoding.ASCII.GetBytes(Nonce)));
        Assert.False(first.Apply(Op(first, "close"))?.Complete);
        var second = new DisposableClipboardFixture(Run, () => true);
        second.Apply(Op(second, "armWrite", Nonce));
        Assert.Null(second.Apply(Op(second, "armWrite", Nonce)));
        Assert.False(second.Apply(Op(second, "close"))?.Complete);
    }

    [Fact]
    public void Identity_loss_and_interruption_restore_but_never_complete_even_after_repeated_close()
    {
        bool current = true;
        var fixture = new DisposableClipboardFixture(Run, () => current);
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
        var fixture = new DisposableClipboardFixture(Run, () => true);
        Assert.Null(fixture.Apply(Op(fixture, "close") with { RunId = new string('f', 32) }));
        fixture.Apply(Op(fixture, "armWrite", Nonce));
        Assert.False(fixture.Apply(Op(fixture, "close"))?.Complete);
    }

    [Fact]
    public void Identity_loss_during_read_or_after_restore_withholds_observation_and_success()
    {
        int readChecks = 0;
        var reading = new DisposableClipboardFixture(Run, () => ++readChecks == 1);
        Assert.Null(reading.Apply(Op(reading, "read")));
        Assert.Equal("aborted", reading.Observe().State);
        int closeChecks = 0;
        var closing = new DisposableClipboardFixture(Run, () => ++closeChecks == 1);
        var closed = closing.Apply(Op(closing, "close"));
        Assert.Equal("aborted", closed?.State);
        Assert.True(closed?.Restored);
        Assert.False(closed?.Complete);
    }

    [Theory]
    [InlineData("terminfo-osc52-01234567-89AB-cdef-0123-456789abcdef")]
    [InlineData("terminfo-osc52-0123456789abcdef0123456789abcdef")]
    [InlineData("prefix-01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("terminfo-osc52-01234567-89ab-cdef-0123-456789abcdef\n")]
    public void Noncanonical_or_arbitrary_values_are_not_known_values(string value)
        => Assert.False(ClipboardFixtureValidation.IsNonce(value));
}
