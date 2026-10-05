using Ntilde.Platform.Ssh.Exec;

namespace Ntilde.Platform.Tests.Ssh.Exec;

/// <summary>The exec argv (Phase 4 spec §8.2): the plan's arguments wrapped in the exec-only options.</summary>
public sealed class OpenSshExecCommandLineTests
{
    private static readonly string[] Plan = ["-F", @"C:\cfg\ntilde_ssh_config", "ntilde_0123abcd"];

    private static readonly string[] ProxyArgv =
    [
        "-T", "-o", "ClearAllForwardings=yes", "-o", "BatchMode=no", "-o", "ControlMaster=no",
        "-F", @"C:\cfg\ntilde_ssh_config", "ntilde_0123abcd",
        "--", "ntilde-mux proxy --stdio",
    ];

    private const int ExecOptionCount = 7;

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

        Assert.Equal(plan, argv.Skip(ExecOptionCount).Take(plan.Length));
    }

    [Fact]
    public void ControlMaster_no_precedes_the_plan_so_it_beats_the_generated_configs_ControlMaster_auto()
    {
        // First value wins in ssh: ahead of -F and of ExtraSshArgs, this exec never becomes a master.
        string[] plan = [.. Plan, "-o", "ControlMaster=auto"];

        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], plan, "true");

        int ours = IndexOfPair(argv, "-o", "ControlMaster=no");
        Assert.True(ours >= 0, "ControlMaster=no is missing");
        Assert.True(ours < IndexOfPair(argv, "-F", Plan[1]));
        Assert.True(ours < IndexOfPair(argv, "-o", "ControlMaster=auto"));
    }

    [Theory]
    [InlineData("-t")]
    [InlineData("-tt")]
    [InlineData("-ttt")]
    [InlineData("-T")]
    public void A_pty_flag_in_the_plans_extra_arguments_is_dropped_and_logged(string flag)
    {
        string[] plan = [.. Plan, flag, "-o", "ConnectTimeout=5"];
        var log = new List<string>();

        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], plan, "true", log.Add);

        string[] expected = [.. ProxyArgv[..ExecOptionCount], .. Plan, "-o", "ConnectTimeout=5", "--", "true"];
        Assert.Equal(expected, argv);
        string line = Assert.Single(log);
        Assert.Contains($"'{flag}'", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-tv")]
    [InlineData("-vt")]
    [InlineData("-t5")]
    public void Clustered_flags_are_not_parsed(string flag)
    {
        string[] plan = [.. Plan, flag];

        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], plan, "true");

        Assert.Contains(flag, argv);
    }

    [Fact]
    public void The_remote_command_is_never_filtered()
    {
        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], Plan, "-t");

        Assert.Equal("-t", argv[^1]);
    }

    private static int IndexOfPair(IReadOnlyList<string> argv, string option, string value)
    {
        for (int i = 0; i + 1 < argv.Count; i++)
        {
            if (argv[i] == option && argv[i + 1] == value) return i;
        }

        return -1;
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
