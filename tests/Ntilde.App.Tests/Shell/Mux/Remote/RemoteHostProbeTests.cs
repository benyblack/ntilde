using Ntilde.Platform.Ssh.Exec;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// <see cref="RemoteHostProbe"/> (Phase 4 spec §9 step 1): what <c>uname -sm</c>, the libc line and
/// <c>HOME=</c> say about a host, from the probe's real output on each kind of host. A host the
/// binary cannot run on is refused with the exact reason the install dialog shows.
/// </summary>
public sealed class RemoteHostProbeTests
{
    private static SshExecResult Probe(string stdout, int? exitCode = 0, string stderr = "") => new(exitCode, stdout, stderr);

    [Theory]
    // Ubuntu 22.04 x64: the glibc floor itself.
    [InlineData("Linux x86_64\nldd (Ubuntu GLIBC 2.35-0ubuntu3.8) 2.35\nHOME=/home/nova\n", "linux-x64", "/home/nova")]
    // Debian 12 arm64.
    [InlineData("Linux aarch64\nldd (Debian GLIBC 2.36-9+deb12u7) 2.36\nHOME=/home/admin\n", "linux-arm64", "/home/admin")]
    // Fedora 40, root.
    [InlineData("Linux x86_64\nldd (GNU libc) 2.39\nHOME=/root\n", "linux-x64", "/root")]
    // A host whose ldd printed nothing, answered by getconf GNU_LIBC_VERSION.
    [InlineData("Linux aarch64\nglibc 2.36\nHOME=/home/pi\n", "linux-arm64", "/home/pi")]
    // An arm64 kernel that names itself arm64.
    [InlineData("Linux arm64\nldd (GNU libc) 2.38\nHOME=/home/nova\n", "linux-arm64", "/home/nova")]
    // macOS arm64: no ldd, no glibc, no check.
    [InlineData("Darwin arm64\nsh: ldd: command not found\nHOME=/Users/nova\n", "osx-arm64", "/Users/nova")]
    // An rc file that prints before the command runs, and CRLF line ends.
    [InlineData("Welcome to box!\r\nLinux x86_64\r\nldd (Ubuntu GLIBC 2.39-0ubuntu8.3) 2.39\r\nHOME=/home/nova\r\n", "linux-x64", "/home/nova")]
    // A home with a space in it is still a home.
    [InlineData("Linux x86_64\nldd (GNU libc) 2.40\nHOME=/home/my user\n", "linux-x64", "/home/my user")]
    public void Supported_hosts_give_their_rid_and_home(string stdout, string rid, string home)
    {
        RemoteHostProbeOutcome outcome = RemoteHostProbe.Parse(Probe(stdout));

        Assert.Equal(new RemoteHostFacts(rid, home), outcome);
    }

    [Theory]
    // Alpine (musl), x64 and arm64.
    [InlineData("Linux x86_64\nmusl libc (x86_64)\nHOME=/root\n", "musl libc is not supported")]
    [InlineData("Linux aarch64\nmusl libc (aarch64)\nHOME=/home/alpine\n", "musl libc is not supported")]
    // CentOS 7 and Ubuntu 20.04: glibc older than the floor.
    [InlineData("Linux x86_64\nldd (GNU libc) 2.17\nHOME=/home/centos\n", "glibc 2.17 is older than 2.35")]
    [InlineData("Linux x86_64\nldd (Ubuntu GLIBC 2.31-0ubuntu9.16) 2.31\nHOME=/home/nova\n", "glibc 2.31 is older than 2.35")]
    // macOS on Intel.
    [InlineData("Darwin x86_64\nsh: ldd: command not found\nHOME=/Users/nova\n", "Intel Macs are not supported")]
    // FreeBSD: its ldd has no --version.
    [InlineData("FreeBSD amd64\nldd: illegal option -- -\nHOME=/home/nova\n", "FreeBSD is not supported")]
    [InlineData("OpenBSD amd64\nldd: unknown option -- -\nHOME=/home/nova\n", "OpenBSD is not supported")]
    // A 32-bit Raspberry Pi.
    [InlineData("Linux armv7l\nldd (Debian GLIBC 2.36-9+rpt2+deb12u4) 2.36\nHOME=/home/pi\n", "Linux on armv7l is not supported")]
    // ldd missing: its error is the line head kept.
    [InlineData("Linux x86_64\nsh: 1: ldd: not found\nHOME=/root\n", "Could not tell the C library version from: sh: 1: ldd: not found")]
    // Neither ldd nor getconf printed anything: head printed no line at all.
    [InlineData("Linux x86_64\nHOME=/root\n", "Could not tell the C library version: the host printed none")]
    // HOME unset, or not absolute.
    [InlineData("Linux x86_64\nldd (GNU libc) 2.39\nHOME=\n", "The remote HOME is not set")]
    [InlineData("Linux x86_64\nldd (GNU libc) 2.39\nHOME=home/nova\n", "The remote HOME is not an absolute path: home/nova")]
    public void Unsupported_hosts_are_refused_with_the_reason(string stdout, string reason)
    {
        RemoteHostProbeOutcome outcome = RemoteHostProbe.Parse(Probe(stdout));

        Assert.Equal(new RemoteHostRefusal(reason), outcome);
    }

    [Fact]
    public void A_failed_probe_is_refused_with_ssh_s_reason()
    {
        RemoteHostProbeOutcome outcome = RemoteHostProbe.Parse(
            Probe("", exitCode: 255, stderr: "ssh: connect to host box port 22: Connection refused\r\n"));

        Assert.Equal(new RemoteHostRefusal("Probing the host failed (exit 255): ssh: connect to host box port 22: Connection refused"), outcome);
    }

    [Fact]
    public void A_lost_transport_is_refused_with_the_native_message()
    {
        RemoteHostProbeOutcome outcome = RemoteHostProbe.Parse(Probe("", exitCode: null, stderr: "native ssh: authentication failed\n"));

        Assert.Equal(new RemoteHostRefusal("Probing the host failed (no exit status): native ssh: authentication failed"), outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Linux x86_64\n")]
    [InlineData("HOME=/home/nova\n")]
    [InlineData("whatever\nHOME=/home/nova\n")]
    [InlineData("Linux x86 64 bits\nldd (GNU libc) 2.39\nHOME=/home/nova\n")]
    public void Output_that_is_not_the_probe_s_is_refused(string stdout)
    {
        var refusal = Assert.IsType<RemoteHostRefusal>(RemoteHostProbe.Parse(Probe(stdout)));

        Assert.StartsWith("The host probe's output was not recognized", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_minimum_glibc_is_ubuntu_22_04_s()
    {
        Assert.Equal(new Version(2, 35), RemoteHostProbe.MinimumGlibc);
    }

    [Fact]
    public void The_probe_is_one_single_quoted_sh_script()
    {
        Assert.Equal(
            "sh -c 'uname -sm; (ldd --version 2>&1 || getconf GNU_LIBC_VERSION 2>&1) | head -n 1; printf \"HOME=%s\\n\" \"$HOME\"'",
            RemoteHostProbe.Command);
        RemoteMuxInstallCommandsTests.AssertOneSingleQuotedShScript(RemoteHostProbe.Command);
    }
}
