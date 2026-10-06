using Ntilde.Platform.Ssh.Launch;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.OpenSsh;
using Ntilde.Platform.Ssh.Storage;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class SshLaunchPlannerTests
{
    [Fact]
    public void Plan_UsesGeneratedConfigAliasLaunchShape()
    {
        string root = CreateTempDirectory();
        try
        {
            var store = new JsonSshProfileStore(Path.Combine(root, "profiles.json"));
            var profile = new SshProfile
            {
                Id = Guid.Parse("d17c9e8a-9a56-4b26-9bcf-afc6d6b8c0f3"),
                Host = "example.com"
            };
            store.SaveProfile(profile);

            var compiler = new OpenSshConfigCompiler(root);
            var planner = new SshLaunchPlanner(store, compiler);

            SshLaunchPlan plan = planner.Plan(profile.Id);

            Assert.False(string.IsNullOrWhiteSpace(plan.SshExecutablePath));
            Assert.Equal(profile.Id, plan.ProfileId);
            Assert.Equal($"ntilde_{profile.Id:N}", plan.Alias);
            Assert.Equal(3, plan.Arguments.Count);
            Assert.Equal("-F", plan.Arguments[0]);
            Assert.Equal(plan.ConfigFilePath, plan.Arguments[1]);
            Assert.Equal(plan.Alias, plan.Arguments[2]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Plan_WithDiagnostics_AppendsVerbosityFlagAfterAlias()
    {
        string root = CreateTempDirectory();
        try
        {
            var store = new JsonSshProfileStore(Path.Combine(root, "profiles.json"));
            var profile = new SshProfile
            {
                Id = Guid.Parse("f3e45de6-895e-4b18-bb6e-7a0265f398db"),
                Host = "example.com"
            };
            store.SaveProfile(profile);

            var compiler = new OpenSshConfigCompiler(root);
            var planner = new SshLaunchPlanner(store, compiler);

            SshLaunchPlan plan = planner.Plan(profile.Id, SshDiagnosticsLevel.VeryVerbose.ToArguments());

            Assert.Equal(4, plan.Arguments.Count);
            Assert.Equal(plan.Alias, plan.Arguments[2]);
            Assert.Equal("-vv", plan.Arguments[3]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Plan_WithProfileExtraArgs_AppendsTokenizedArguments()
    {
        string root = CreateTempDirectory();
        try
        {
            var store = new JsonSshProfileStore(Path.Combine(root, "profiles.json"));
            var profile = new SshProfile
            {
                Id = Guid.Parse("2ec84ef4-4140-447f-b9a9-10f9a9cd5e9f"),
                Host = "example.com",
                ExtraSshArgs = "-o StrictHostKeyChecking=no -o \"UserKnownHostsFile /dev/null\""
            };
            store.SaveProfile(profile);

            var compiler = new OpenSshConfigCompiler(root);
            var planner = new SshLaunchPlanner(store, compiler);

            SshLaunchPlan plan = planner.Plan(profile.Id);

            Assert.Equal(7, plan.Arguments.Count);
            Assert.Equal("-F", plan.Arguments[0]);
            Assert.Equal(plan.Alias, plan.Arguments[2]);
            Assert.Equal("-o", plan.Arguments[3]);
            Assert.Equal("StrictHostKeyChecking=no", plan.Arguments[4]);
            Assert.Equal("-o", plan.Arguments[5]);
            Assert.Equal("UserKnownHostsFile /dev/null", plan.Arguments[6]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Codex D2: a profile the store holds as it is - its block and extra arguments unchanged; its name or install
    /// metadata may differ - gets the usual plan, over the shared generated config.
    /// </summary>
    [Fact]
    public void PlanFor_a_profile_the_store_holds_as_it_is_is_the_usual_plan()
    {
        string root = CreateTempDirectory();
        try
        {
            var store = new JsonSshProfileStore(Path.Combine(root, "profiles.json"));
            SshProfile stored = Snapshot("first.example");
            store.SaveProfile(stored);
            SshProfile copy = Snapshot("first.example");
            copy.Id = stored.Id;
            copy.Name = "renamed";
            copy.MuxOptions.RemoteDaemonPath = "/home/nova/.local/share/ntilde/bin/ntilde-mux";

            SshLaunchPlan plan = new SshLaunchPlanner(store, new OpenSshConfigCompiler(root), AnySsh).PlanFor(copy, ["-v"]);

            string[] expected = ["-F", plan.ConfigFilePath, $"ntilde_{stored.Id:N}", "-o", "ServerAliveInterval=7", "-v"];
            Assert.Equal(expected, plan.Arguments);
            Assert.Equal(Path.Combine(root, "ssh_config.generated"), plan.ConfigFilePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Codex D2: a snapshot the store no longer holds as it is - the profile was edited to another host - gets a plan
    /// that reads no config file: its own block as <c>-o</c> options, after its extra arguments, which ssh lets win
    /// as over a config file. The shared generated file, every other launch's, is not written.
    /// </summary>
    [Fact]
    public void PlanFor_an_edited_profiles_snapshot_reads_no_config_file_and_passes_its_block_as_options()
    {
        string root = CreateTempDirectory();
        try
        {
            var store = new JsonSshProfileStore(Path.Combine(root, "profiles.json"));
            SshProfile snapshot = Snapshot("first.example");
            SshProfile edited = Snapshot("second.example");
            edited.Id = snapshot.Id;
            store.SaveProfile(edited);
            var compiler = new OpenSshConfigCompiler(root);

            SshLaunchPlan plan = new SshLaunchPlanner(store, compiler, AnySsh).PlanFor(snapshot, ["-v"]);

            string[] options = compiler.BuildHostOptions(snapshot).SelectMany(option => new[] { "-o", option }).ToArray();
            string[] expected = ["-F", "none", $"ntilde_{snapshot.Id:N}", "-o", "ServerAliveInterval=7", "-v", .. options];
            Assert.Equal(expected, plan.Arguments);
            Assert.Contains("HostName first.example", compiler.BuildHostOptions(snapshot));
            Assert.Contains("ProxyJump ops@bastion.example:2200", compiler.BuildHostOptions(snapshot));
            Assert.Equal("none", plan.ConfigFilePath);
            Assert.False(File.Exists(Path.Combine(root, "ssh_config.generated")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Codex D2: a snapshot of a profile deleted since gets the same plan, rather than failing to find it.</summary>
    [Fact]
    public void PlanFor_a_deleted_profiles_snapshot_reads_no_config_file()
    {
        string root = CreateTempDirectory();
        try
        {
            var store = new JsonSshProfileStore(Path.Combine(root, "profiles.json"));
            SshProfile snapshot = Snapshot("first.example");

            SshLaunchPlan plan = new SshLaunchPlanner(store, new OpenSshConfigCompiler(root), AnySsh).PlanFor(snapshot);

            string[] expected = ["-F", "none", $"ntilde_{snapshot.Id:N}"];
            Assert.Equal(expected, plan.Arguments.Take(3));
            Assert.Contains("HostName first.example", plan.Arguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The compiled block is the options <see cref="OpenSshConfigCompiler.BuildHostOptions"/> lists, indented, under the alias.</summary>
    [Fact]
    public void BuildHostOptions_is_the_compiled_block()
    {
        string root = CreateTempDirectory();
        try
        {
            var compiler = new OpenSshConfigCompiler(root);
            SshProfile profile = Snapshot("first.example");
            profile.Forwards.Add(new PortForward { Kind = PortForwardKind.Local, SourcePort = 8080, DestinationHost = "db", DestinationPort = 5432 });
            profile.MuxOptions.Enabled = true;

            OpenSshCompilationResult result = compiler.Compile([profile], profile.Id);

            string block = string.Concat(compiler.BuildHostOptions(profile).Select(option => "  " + option + Environment.NewLine));
            Assert.Contains($"Host ntilde_{profile.Id:N}{Environment.NewLine}{block}{Environment.NewLine}", File.ReadAllText(result.ConfigFilePath), StringComparison.Ordinal);
            Assert.Contains("LocalForward 8080 db:5432", compiler.BuildHostOptions(profile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The ssh a plan names: these tests only read the plan, so none need be installed.</summary>
    private static string AnySsh() => "/usr/bin/ssh";

    /// <summary>A profile with every piece of a block: a user, a port, a key, a jump hop, and an extra argument.</summary>
    private static SshProfile Snapshot(string host) => new()
    {
        Host = host,
        User = "nova",
        Port = 2201,
        AuthMode = SshAuthMode.IdentityFile,
        IdentityFilePath = "/home/nova/.ssh/id_ed25519",
        JumpHops = { new SshJumpHop { Host = "bastion.example", User = "ops", Port = 2200 } },
        ExtraSshArgs = "-o ServerAliveInterval=7",
    };

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nova_ssh_planner_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
