using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>
/// R2: a session file saved before the boundary no live daemon session can predate - the boot, or on Windows the
/// later of the boot and this logon - names sessions that ended with the machine or the logon.
/// </summary>
public sealed class MuxRestoreExpectationsTests
{
    private static readonly DateTime Boundary = new(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_file_saved_before_the_boundary_means_its_sessions_ended() =>
        Assert.True(MuxRestoreExpectations.SessionsEndedByReboot(Boundary - TimeSpan.FromMinutes(1), Boundary));

    [Fact]
    public void A_file_saved_after_the_boundary_does_not() =>
        Assert.False(MuxRestoreExpectations.SessionsEndedByReboot(Boundary + TimeSpan.FromMinutes(1), Boundary));

    [Fact]
    public void A_file_saved_at_the_boundary_does_not() =>
        Assert.False(MuxRestoreExpectations.SessionsEndedByReboot(Boundary, Boundary));

    /// <summary>Clock skew: a save time in the future is not before the boundary, so today's notices stand.</summary>
    [Fact]
    public void A_file_from_the_future_does_not() =>
        Assert.False(MuxRestoreExpectations.SessionsEndedByReboot(DateTime.UtcNow + TimeSpan.FromDays(1), Boundary));

    [Fact]
    public void No_save_time_does_not() =>
        Assert.False(MuxRestoreExpectations.SessionsEndedByReboot(null, Boundary));

    /// <summary>The OS would not say when the boot or logon was: never quiet, whatever the save time.</summary>
    [Fact]
    public void No_boundary_does_not() =>
        Assert.False(MuxRestoreExpectations.SessionsEndedByReboot(new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc), null));
}
