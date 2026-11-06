using Ntilde.Mux.Daemon;

namespace Ntilde.Mux.Tests.Daemon;

public sealed class LoginSessionTests
{
    /// <summary>
    /// Only MACH_SEND_INVALID_DEST proves the bootstrap port dead: what a shell of a daemon that outlived its login got. A
    /// live bootstrap server's answers - success, or BOOTSTRAP_UNKNOWN_SERVICE for a name a future macOS dropped - other
    /// kernel errors, and transient IPC failures (timeouts, no buffer) prove nothing.
    /// </summary>
    [Theory]
    [InlineData(0, false)]          // KERN_SUCCESS
    [InlineData(1102, false)]       // BOOTSTRAP_UNKNOWN_SERVICE
    [InlineData(5, false)]          // KERN_FAILURE
    [InlineData(0x10000003, true)]  // MACH_SEND_INVALID_DEST
    [InlineData(0x10000004, false)] // MACH_SEND_TIMED_OUT
    [InlineData(0x1000000d, false)] // MACH_SEND_NO_BUFFER
    [InlineData(0x10004003, false)] // MACH_RCV_TIMED_OUT
    public void Only_a_dead_destination_counts_as_ended(int kernReturn, bool ended) =>
        Assert.Equal(ended, LoginSession.IsDeadDestination(kernReturn));

    /// <summary>The test runner belongs to a live login (or is not on macOS, where the answer is always false).</summary>
    [Fact]
    public void This_process_has_not_outlived_its_login() => Assert.False(LoginSession.HasEnded());
}
