using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxCommandMatchTests
{
    [Theory]
    [InlineData("pwsh", "pwsh")]
    [InlineData("pwsh.exe", "pwsh")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", "pwsh.exe")]
    [InlineData("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\"", "pwsh")]
    [InlineData("/bin/zsh", "zsh")]
    [InlineData("", "bash")]          // unknown daemon command: do not refuse a reattach over it
    [InlineData(null, "bash")]
    public void Same_executable(string? daemon, string request) => Assert.True(MuxCommandMatch.SameExecutable(daemon, request));

    [Theory]
    [InlineData("pwsh", "cmd.exe")]
    [InlineData("/bin/zsh", "/bin/bash")]
    public void Different_executable(string daemon, string request) => Assert.False(MuxCommandMatch.SameExecutable(daemon, request));

    [Fact]
    public void Case_is_ignored_only_on_Windows() =>
        Assert.Equal(OperatingSystem.IsWindows(), MuxCommandMatch.SameExecutable("PWSH.EXE", "pwsh"));
}
