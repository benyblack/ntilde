using Ntilde.Mux.Daemon;

namespace Ntilde.Mux.Tests.Daemon;

public sealed class LoginSessionTests
{
    /// <summary>
    /// Only a Mach IPC failure means the bootstrap port is dead: what a shell of a daemon that outlived its login got
    /// (0x10000003, MACH_SEND_INVALID_DEST). A live bootstrap server's answers - success, or BOOTSTRAP_UNKNOWN_SERVICE for a
    /// name a future macOS dropped - and other kernel errors are not.
    /// </summary>
    [Theory]
    [InlineData(0, false)]          // KERN_SUCCESS
    [InlineData(1102, false)]       // BOOTSTRAP_UNKNOWN_SERVICE
    [InlineData(5, false)]          // KERN_FAILURE
    [InlineData(0x10000003, true)]  // MACH_SEND_INVALID_DEST
    [InlineData(0x10000004, true)]  // MACH_SEND_TIMED_OUT
    [InlineData(0x10004003, true)]  // MACH_RCV_TIMED_OUT
    public void Only_a_mach_ipc_failure_counts_as_ended(int kernReturn, bool ended) =>
        Assert.Equal(ended, LoginSession.IsIpcFailure(kernReturn));

    /// <summary>The test runner belongs to a live login (or is not on macOS, where the answer is always false).</summary>
    [Fact]
    public void This_process_has_not_outlived_its_login() => Assert.False(LoginSession.HasEnded());
}
