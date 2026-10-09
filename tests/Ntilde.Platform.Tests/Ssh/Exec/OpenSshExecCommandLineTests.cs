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

    // Release hardening item 3: a listing connects for a moment; with ForwardAgent yes in the user's config, the proxy would
    // repoint the daemon's agent link at an agent that dies with it. ClearAllForwardings does not clear ForwardAgent.
    [Fact]
    public void A_listing_turns_agent_forwarding_off_ahead_of_the_plan()
    {
        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], Plan, "ntilde-mux proxy --stdio --no-spawn", batchMode: true, noAgentForwarding: true);

        int off = IndexOfOption(argv, "ForwardAgent=no");
        Assert.True(off >= 0 && off < argv.ToList().IndexOf(Plan[0]), string.Join(' ', argv));
    }

    [Theory]
    [InlineData("-A", null)]
    [InlineData("-vA", "-v")]
    [InlineData("-Av", "-v")]
    public void A_listing_drops_an_A_flag_which_would_beat_ForwardAgent_no(string extra, string? kept)
    {
        // ssh's -A sets forwarding outright, whatever an earlier -o said: so it goes, the rest of its cluster kept.
        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], [.. Plan, extra], "true", noAgentForwarding: true);

        Assert.DoesNotContain(argv, a => a.StartsWith('-') && !a.StartsWith("--", StringComparison.Ordinal) && a.Contains('A', StringComparison.Ordinal) && a.Length <= 3);
        if (kept is not null) Assert.Contains(kept, argv);
    }

    [Fact]
    public void Without_a_listing_agent_forwarding_is_the_users()
    {
        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], [.. Plan, "-A"], "true");

        Assert.Equal(-1, IndexOfOption(argv, "ForwardAgent=no"));
        Assert.Contains("-A", argv);
    }

    private static int IndexOfOption(IReadOnlyList<string> argv, string option)
    {
        for (int i = 0; i + 1 < argv.Count; i++)
        {
            if (argv[i] == "-o" && argv[i + 1] == option) return i;
        }

        return -1;
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

    /// <summary>
    /// An automatic reconnect must never prompt (Phase 4 ruling: those attempts are non-interactive).
    /// The first value of an ssh option wins, so batch mode replaces the BatchMode=no token in place:
    /// appending BatchMode=yes after it would be ignored.
    /// </summary>
    [Fact]
    public void Batch_mode_replaces_BatchMode_no_with_yes_in_the_same_place()
    {
        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], Plan, "ntilde-mux proxy --stdio", batchMode: true);

        string[] expected = [.. ProxyArgv];
        expected[Array.IndexOf(expected, "BatchMode=no")] = "BatchMode=yes";
        Assert.Equal(expected, argv);
        Assert.DoesNotContain("BatchMode=no", argv);
    }

    [Fact]
    public void Batch_mode_still_precedes_a_BatchMode_in_the_plans_extra_arguments()
    {
        string[] plan = [.. Plan, "-o", "BatchMode=no"];

        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], plan, "true", batchMode: true);

        int ours = IndexOfPair(argv, "-o", "BatchMode=yes");
        Assert.True(ours >= 0, "BatchMode=yes is missing");
        Assert.True(ours < IndexOfPair(argv, "-F", Plan[1]));
        Assert.True(ours < IndexOfPair(argv, "-o", "BatchMode=no"));
    }

    /// <summary>
    /// An automatic reconnect of a profile whose password is saved: ssh may prompt (<c>BatchMode=no</c>, in its usual
    /// place), so the askpass helper can answer the password from the vault, but a refused one is not asked for again
    /// (<c>NumberOfPasswordPrompts=1</c>, next to it and ahead of the plan, since ssh keeps an option's first value).
    /// </summary>
    [Fact]
    public void Saved_password_only_keeps_BatchMode_no_and_allows_one_password_prompt()
    {
        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], Plan, "ntilde-mux proxy --stdio", savedPasswordOnly: true);

        string[] expected =
        [
            "-T", "-o", "ClearAllForwardings=yes", "-o", "BatchMode=no", "-o", "NumberOfPasswordPrompts=1", "-o", "ControlMaster=no",
            .. Plan,
            "--", "ntilde-mux proxy --stdio",
        ];
        Assert.Equal(expected, argv);
    }

    [Fact]
    public void Saved_password_only_precedes_a_NumberOfPasswordPrompts_in_the_plans_extra_arguments()
    {
        string[] plan = [.. Plan, "-o", "NumberOfPasswordPrompts=3"];

        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], plan, "true", savedPasswordOnly: true);

        int ours = IndexOfPair(argv, "-o", "NumberOfPasswordPrompts=1");
        Assert.True(ours >= 0, "NumberOfPasswordPrompts=1 is missing");
        Assert.True(ours < IndexOfPair(argv, "-F", Plan[1]));
        Assert.True(ours < IndexOfPair(argv, "-o", "NumberOfPasswordPrompts=3"));
    }

    [Fact]
    public void Batch_mode_and_saved_password_only_are_not_both_possible()
    {
        Assert.ThrowsAny<ArgumentException>(() => OpenSshExecCommandLine.Build([], Plan, "true", batchMode: true, savedPasswordOnly: true));
        Assert.DoesNotContain("NumberOfPasswordPrompts=1", OpenSshExecCommandLine.Build([], Plan, "true", batchMode: true));
        Assert.DoesNotContain("NumberOfPasswordPrompts=1", OpenSshExecCommandLine.Build([], Plan, "true"));
    }

    /// <summary>
    /// The profile's extra arguments; what is left of them; and the pieces dropped, one log line each, in order.
    /// A PTY (<c>-t</c>, and our own <c>-T</c> kept single), no command (<c>-N</c>), the background (<c>-f</c>), stdin
    /// from /dev/null (<c>-n</c>), a subsystem (<c>-s</c>), print-and-exit (<c>-G</c>, <c>-V</c>), master mode
    /// (<c>-M</c>), stdio forwarding (<c>-W</c>), a control command (<c>-O</c>), a query (<c>-Q</c>), and the
    /// <c>-o</c> keywords that do the same.
    /// </summary>
    public static TheoryData<string[], string[], string[]> Breakers => new()
    {
        { ["-t"], [], ["-t"] },
        { ["-tt"], [], ["-t", "-t"] },
        { ["-T"], [], ["-T"] },
        { ["-N"], [], ["-N"] },
        { ["-f"], [], ["-f"] },
        { ["-tv"], ["-v"], ["-t"] },
        { ["-vt"], ["-v"], ["-t"] },
        { ["-Nv"], ["-v"], ["-N"] },
        { ["-fN"], [], ["-f", "-N"] },
        { ["-n"], [], ["-n"] },
        { ["-s"], [], ["-s"] },
        { ["-G"], [], ["-G"] },
        { ["-V"], [], ["-V"] },
        { ["-M"], [], ["-M"] },
        { ["-qMv"], ["-qv"], ["-M"] },
        { ["-W", "h:22"], [], ["-W"] },
        { ["-Wh:22"], [], ["-W"] },
        { ["-vW", "h:22"], ["-v"], ["-W"] },
        { ["-O", "check"], [], ["-O"] },
        { ["-Q", "cipher"], [], ["-Q"] },
        { ["-o", "RequestTTY=force"], [], ["-o RequestTTY"] },
        { ["-oRequestTTY=yes"], [], ["-o RequestTTY"] },
        { ["-o", "SessionType none"], [], ["-o SessionType"] },
        { ["-o", "sessiontype=none"], [], ["-o sessiontype"] },
        { ["-o", " RequestTTY = yes"], [], ["-o RequestTTY"] },
        { ["-o", "ForkAfterAuthentication=yes"], [], ["-o ForkAfterAuthentication"] },
        { ["-o", "StdinNull=yes"], [], ["-o StdinNull"] },
        { ["-o", "RemoteCommand=x"], [], ["-o RemoteCommand"] },
        { ["-o", "PermitLocalCommand=yes"], [], ["-o PermitLocalCommand"] },
        { ["-vo", "RequestTTY=force"], ["-v"], ["-o RequestTTY"] },
        { ["-voStdinNull=yes"], ["-v"], ["-o StdinNull"] },
        // ssh's own keyword split (readconf's strdelim): leading whitespace, one optional '=' with whitespace around it,
        // a quoted keyword unquoted, a quote inside it joining what is around it. OpenSSH 10.0p2 resolves each of these
        // to "sessiontype none" (codex residual round).
        { ["-o", "=SessionType=none"], [], ["-o SessionType"] },
        { ["-o", "\"SessionType\" none"], [], ["-o SessionType"] },
        { ["-o", " = SessionType none"], [], ["-o SessionType"] },
        { ["-o", " =SessionType none"], [], ["-o SessionType"] },
        { ["-o", "\tSessionType none"], [], ["-o SessionType"] },
        { ["-o", "Session\"Type\" none"], [], ["-o SessionType"] },
    };

    /// <summary>
    /// Codex C3: the profile's ssh arguments are read with ssh's own getopt rules, so a breaking flag is found inside
    /// a cluster too (OpenSSH 9.6: <c>ssh -G -tv</c> says <c>requesttty true</c>, <c>-Nv</c> <c>sessiontype none</c>),
    /// and an option that takes an argument goes with it. The <c>-o ConnectTimeout=5</c> after each row shows the
    /// parse carries on in step. Replaces the I6 tests, which matched whole tokens only.
    /// </summary>
    [Theory]
    [MemberData(nameof(Breakers))]
    public void A_piece_that_would_break_the_exec_channel_is_dropped_and_logged(string[] extra, string[] kept, string[] dropped)
    {
        var log = new List<string>();

        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], [.. Plan, .. extra, "-o", "ConnectTimeout=5"], "true", log.Add);

        string[] expected = [.. ProxyArgv[..ExecOptionCount], .. Plan, .. kept, "-o", "ConnectTimeout=5", "--", "true"];
        Assert.Equal(expected, argv);
        Assert.Equal(dropped.Length, log.Count);
        for (int i = 0; i < dropped.Length; i++) Assert.Contains($"'{dropped[i]}'", log[i], StringComparison.Ordinal);
    }

    /// <summary>The profile's extra arguments that must reach ssh exactly as they are.</summary>
    public static TheoryData<string[]> Harmless => new()
    {
        { ["-p", "2222"] },
        { ["-p2222"] },
        { ["-p2tN"] },                                  // port "2tN": an argument is never scanned for flags
        { ["-i", "key", "-v"] },
        { ["-o", "ProxyCommand=ssh -W %h:%p jump"] },   // one token, -W inside a value
        { ["-oProxyCommand=ssh -W %h:%p jump"] },
        { ["-o", "-t"] },                               // an argument that looks like a flag
        { ["-J", "jump"] },
        { ["-L", "8080:h:80"] },
        { ["-vvv"] },
        { ["-46AaCgKkqXxYy"] },
        { ["-E", "ssh.log", "-o", "ServerAliveInterval=15"] },
        { ["-o", "= = SessionType none"] },             // a second '=': ssh reads an empty keyword and ignores the line
        { ["-o", "\"SessionType none"] },               // an unmatched quote: ssh ignores the line too
    };

    [Theory]
    [MemberData(nameof(Harmless))]
    public void Everything_else_reaches_ssh_as_it_was(string[] extra)
    {
        var log = new List<string>();

        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], [.. Plan, .. extra], "true", log.Add);

        string[] expected = [.. ProxyArgv[..ExecOptionCount], .. Plan, .. extra, "--", "true"];
        Assert.Equal(expected, argv);
        Assert.Empty(log);
    }

    [Fact]
    public void What_is_kept_keeps_its_order()
    {
        string[] extra = ["-4", "-tv", "-p", "2222", "-Nq", "-o", "RequestTTY=yes", "-J", "jump", "-W", "h:22", "-oServerAliveInterval=5", "-C"];

        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], [.. Plan, .. extra], "true");

        string[] expected = [.. ProxyArgv[..ExecOptionCount], .. Plan, "-4", "-v", "-p", "2222", "-q", "-J", "jump", "-oServerAliveInterval=5", "-C", "--", "true"];
        Assert.Equal(expected, argv);
    }

    /// <summary>
    /// ssh reads options before the destination and again after it (it restarts getopt after the host): both are
    /// filtered, and the destination keeps its place.
    /// </summary>
    [Fact]
    public void Options_on_either_side_of_the_destination_are_read_and_the_destination_stays_put()
    {
        string[] plan = ["-F", Plan[1], "-tv", Plan[2], "-N", "-p", "22"];

        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], plan, "true");

        string[] expected = [.. ProxyArgv[..ExecOptionCount], "-F", Plan[1], "-v", Plan[2], "-p", "22", "--", "true"];
        Assert.Equal(expected, argv);
    }

    /// <summary>
    /// After the destination ssh reads options only until <c>--</c> or the next word; what follows is the remote
    /// command, never an option. It is kept as written - no <c>-t</c> in it is dropped - and the log says why the
    /// channel's command will not run as given.
    /// </summary>
    [Theory]
    [InlineData("uptime")]
    [InlineData("--")]
    public void After_the_end_of_sshs_options_nothing_is_read_as_an_option(string end)
    {
        var log = new List<string>();

        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], [.. Plan, "-v", end, "-t", "-N"], "true", log.Add);

        string[] expected = [.. ProxyArgv[..ExecOptionCount], .. Plan, "-v", end, "-t", "-N", "--", "true"];
        Assert.Equal(expected, argv);
        Assert.Contains("remote command", Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public void The_remote_command_is_never_filtered()
    {
        IReadOnlyList<string> argv = OpenSshExecCommandLine.Build([], Plan, "-t");

        Assert.Equal("-t", argv[^1]);
    }

    /// <summary>
    /// A plan whose own arguments send ssh through another host - <c>-J</c>, <c>ProxyJump</c>, <c>ProxyCommand</c> - reaches
    /// the target through a jump host, which an automatic reconnect must not hand the saved password (on OpenSSH before 8.4
    /// a jump host's keyboard-interactive text carries no "(user@host) " prefix, so it could ask as the target). Read the
    /// way ssh reads the plan: clusters, an option's argument wherever it is, <c>-o</c> keywords as readconf reads them.
    /// </summary>
    public static TheoryData<string[]> Proxied => new()
    {
        { [.. Plan, "-J", "bastion"] },
        { [.. Plan, "-Jbastion"] },
        { [.. Plan, "-vJ", "bastion"] },
        { [.. Plan, "-qJ", "bastion", "-p", "2222"] },
        { ["-J", "bastion", .. Plan] },
        { [.. Plan, "-o", "ProxyJump=bastion"] },
        { [.. Plan, "-oProxyJump=bastion"] },
        { [.. Plan, "-o", "proxyjump bastion"] },
        { [.. Plan, "-o", "PROXYCOMMAND=ssh -W %h:%p bastion"] },
        { [.. Plan, "-o", "ProxyCommand nc %h %p"] },
        { [.. Plan, "-o", "=ProxyJump=bastion"] },
        { [.. Plan, "-o", " = ProxyJump bastion"] },
        { [.. Plan, "-o", "\"ProxyJump\" bastion"] },
        { [.. Plan, "-vo", "ProxyJump=bastion"] },
    };

    [Theory]
    [MemberData(nameof(Proxied))]
    public void A_plan_that_goes_through_another_host_names_a_proxy(string[] plan)
    {
        Assert.True(OpenSshExecCommandLine.NamesAProxy(plan));
    }

    public static TheoryData<string[]> NotProxied => new()
    {
        { Plan },
        { [.. Plan, "-p", "22", "-o", "ServerAliveInterval=5"] },
        { [.. Plan, "-i", "-J"] },                      // -J is the identity file's name
        { [.. Plan, "-l", "J"] },
        { [.. Plan, "-o", "ProxyJumpy=bastion"] },      // another keyword
        { [.. Plan, "-o", "LocalCommand=ssh -J bastion"] },
        { [.. Plan, "--", "-J", "bastion"] },           // the remote command, never an option
        { [.. Plan, "uptime", "-J", "bastion"] },
    };

    [Theory]
    [MemberData(nameof(NotProxied))]
    public void A_plan_that_goes_straight_to_the_target_names_no_proxy(string[] plan)
    {
        Assert.False(OpenSshExecCommandLine.NamesAProxy(plan));
    }

    /// <summary>
    /// Re-review item 6: the profile's own ssh arguments may change who signs in or where - <c>-l</c>, <c>-o User</c>, a
    /// config of their own (<c>-F</c>, which beats ours: ssh keeps the last), <c>-o HostName</c> or <c>-o HostKeyAlias</c>
    /// (both change the host ssh names in its prompts). The askpass helper then cannot recognise the target's prompt, so
    /// an automatic reconnect must not count on it. Read as ssh reads options after the destination.
    /// </summary>
    [Theory]
    [InlineData("-l other")]
    [InlineData("-lother")]
    [InlineData("-vl other")]
    [InlineData("-o User=other")]
    [InlineData("-oUser=other")]
    [InlineData("-o \"user other\"")]
    [InlineData("-o =User=other")]
    [InlineData("-F /home/me/.ssh/other_config")]
    [InlineData("-o HostName=10.0.0.9")]
    [InlineData("-o hostname 10.0.0.9")]
    [InlineData("-o HostKeyAlias=prod")]
    [InlineData("-p 2222 -o ServerAliveInterval=5 -l other")]
    public void Extra_arguments_that_change_who_or_where_are_found(string extraSshArgs)
    {
        Assert.True(OpenSshExecCommandLine.ExtraArgumentsChangeWhoOrWhere(extraSshArgs));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-p 2222")]
    [InlineData("-o ServerAliveInterval=5 -C")]
    [InlineData("-i -l")]                        // -l is the identity file's name
    [InlineData("-o Username=x")]                // another keyword
    [InlineData("-o \"LocalCommand=ssh -l other\"")]
    [InlineData("-- -l other")]                  // the remote command, never an option
    [InlineData("uptime -l other")]
    public void Extra_arguments_that_keep_who_and_where_are_not(string? extraSshArgs)
    {
        Assert.False(OpenSshExecCommandLine.ExtraArgumentsChangeWhoOrWhere(extraSshArgs));
    }

    [Theory]
    [InlineData("-J x")]
    [InlineData("-Jx")]
    [InlineData("-o ProxyJump=x")]
    [InlineData("-oProxyCommand=nc %h %p")]
    [InlineData("-p 2222 -o proxyjump=a,b")]
    public void Extra_arguments_that_name_a_proxy_are_found(string extraSshArgs)
    {
        Assert.True(OpenSshExecCommandLine.ExtraArgumentsNameAProxy(extraSshArgs));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-p 2222 -C")]
    [InlineData("-o ServerAliveInterval=5")]
    [InlineData("uptime -J x")]   // the remote command, never an option
    public void Extra_arguments_without_a_proxy_are_not(string? extraSshArgs)
    {
        Assert.False(OpenSshExecCommandLine.ExtraArgumentsNameAProxy(extraSshArgs));
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
