using Ntilde.Platform.Ssh.Exec;

namespace Ntilde.Platform.Tests.Ssh.Exec;

/// <summary>The exec argv (Phase 4 spec §8.2): the plan's arguments wrapped in the exec-only options.</summary>
public sealed class OpenSshExecCommandLineTests
{
    private static readonly string[] Plan = ["-F", @"C:\cfg\ntilde_ssh_config", "ntilde_0123abcd"];

    private static readonly string[] ProxyArgv =
    [
        "-T", "-o", "ClearAllForwardings=yes", "-o", "BatchMode=no",
        "-F", @"C:\cfg\ntilde_ssh_config", "ntilde_0123abcd",
        "--", "ntilde-mux proxy --stdio",
    ];

    [Fact]
    public void Build_wraps_the_plan_in_the_exec_options_and_ends_with_the_command()
    {
        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], Plan, "ntilde-mux proxy --stdio");

        Assert.Equal(ProxyArgv, argv);
    }

    [Fact]
    public void Build_puts_the_diagnostics_arguments_first()
    {
        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build(["-v"], Plan, "true");

        Assert.Equal("-v", argv[0]);
        Assert.Equal("-T", argv[1]);
        Assert.Equal("--", argv[^2]);
        Assert.Equal("true", argv[^1]);
    }

    [Fact]
    public void Build_keeps_the_plans_extra_ssh_arguments_after_the_alias()
    {
        string[] plan = [.. Plan, "-o", "ConnectTimeout=5"];

        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], plan, "true");

        Assert.Equal(plan, argv.Skip(5).Take(plan.Length));
    }

    [Fact]
    public void The_remote_command_is_one_argv_element_after_the_separator()
    {
        const string command = "sh -c 'set -e; d=\"$HOME/.local/share/ntilde/bin\"; cat > \"$d/x\"'";

        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], Plan, command);

        Assert.Equal("--", argv[^2]);
        Assert.Equal(command, argv[^1]);
        Assert.Single(argv, a => a == "--");
    }

    [Fact]
    public void Build_rejects_missing_inputs()
    {
        Assert.Throws<ArgumentNullException>(() => OpenSshExecCommandLine.Build(null!, Plan, "true"));
        Assert.Throws<ArgumentNullException>(() => OpenSshExecCommandLine.Build([], null!, "true"));
        Assert.ThrowsAny<ArgumentException>(() => OpenSshExecCommandLine.Build([], Plan, " "));
    }
}
