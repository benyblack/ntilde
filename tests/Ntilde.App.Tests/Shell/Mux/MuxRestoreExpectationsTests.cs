using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>R2: a session file saved before the machine last booted names daemon sessions a reboot ended.</summary>
public sealed class MuxRestoreExpectationsTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Boot = Now - TimeSpan.FromHours(3);

    [Fact]
    public void The_boot_time_is_now_less_the_milliseconds_since_boot() =>
        Assert.Equal(Boot, MuxRestoreExpectations.BootTimeUtc(() => Now, () => (long)TimeSpan.FromHours(3).TotalMilliseconds));

    [Fact]
    public void A_file_saved_before_boot_means_a_reboot_ended_its_sessions() =>
        Assert.True(MuxRestoreExpectations.SessionsEndedByReboot(Boot - TimeSpan.FromMinutes(1), Boot));

    [Fact]
    public void A_file_saved_after_boot_does_not() =>
        Assert.False(MuxRestoreExpectations.SessionsEndedByReboot(Boot + TimeSpan.FromMinutes(1), Boot));

    [Fact]
    public void A_file_saved_at_the_boot_instant_does_not() =>
        Assert.False(MuxRestoreExpectations.SessionsEndedByReboot(Boot, Boot));

    /// <summary>Clock skew: a save time in the future is not before boot, so today's notices stand.</summary>
    [Fact]
    public void A_file_from_the_future_does_not() =>
        Assert.False(MuxRestoreExpectations.SessionsEndedByReboot(Now + TimeSpan.FromDays(1), Boot));

    [Fact]
    public void No_save_time_does_not() =>
        Assert.False(MuxRestoreExpectations.SessionsEndedByReboot(null, Boot));
}
