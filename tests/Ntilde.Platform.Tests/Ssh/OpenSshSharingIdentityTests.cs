using Ntilde.Platform.Ssh.Launch;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.OpenSsh;
using Ntilde.Platform.Ssh.Storage;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class OpenSshSharingIdentityTests
{
    [Theory]
    [InlineData("host")]
    [InlineData("user")]
    [InlineData("port")]
    [InlineData("jump-host")]
    [InlineData("jump-user")]
    [InlineData("jump-port")]
    [InlineData("jump-order")]
    [InlineData("identity")]
    [InlineData("auth")]
    [InlineData("forward")]
    [InlineData("extra-args")]
    public void Connection_changes_do_not_join_the_previous_automatic_master(string change)
    {
        var compiler = new OpenSshConfigCompiler(Path.GetTempPath());
        SshProfile profile = Profile();
        string first = Socket(compiler, profile);
        switch (change)
        {
            case "host": profile.Host = "new.example"; break;
            case "user": profile.User = "bob"; break;
            case "port": profile.Port = 2201; break;
            case "jump-host": profile.JumpHops[0].Host = "new-bastion"; break;
            case "jump-user": profile.JumpHops[0].User = "other"; break;
            case "jump-port": profile.JumpHops[0].Port = 2202; break;
            case "jump-order": profile.JumpHops.Reverse(); break;
            case "identity": profile.IdentityFilePath = "/keys/new"; break;
            case "auth": profile.AuthMode = SshAuthMode.Agent; break;
            case "forward": profile.Forwards[0].DestinationHost = "other.internal"; break;
            case "extra-args": profile.ExtraSshArgs = "-o HostName=override.example"; break;
        }
        Assert.NotEqual(first, Socket(compiler, profile));
    }

    [Fact]
    public void Display_shell_and_persistence_changes_keep_the_automatic_master()
    {
        var compiler = new OpenSshConfigCompiler(Path.GetTempPath());
        SshProfile profile = Profile();
        string first = Socket(compiler, profile);
        profile.Name = "renamed";
        profile.GroupPath = "another";
        profile.Notes = "notes";
        profile.WorkingDirectory = "/other/local/dir";
        profile.RemoteShellKind = RemoteShellKind.Fish;
        profile.MuxOptions.ControlPersistSeconds = 600;
        Assert.Equal(first, Socket(compiler, profile));
        Assert.Equal(first, Socket(new OpenSshConfigCompiler(Path.GetTempPath()), Profile()));
    }

    [Fact]
    public void Automatic_socket_is_compact_and_separates_profiles()
    {
        var compiler = new OpenSshConfigCompiler(Path.GetTempPath());
        SshProfile profile = Profile();
        string first = Socket(compiler, profile);
        Assert.Matches("cm_[0-9a-f]{32}", first);
        profile.Id = Guid.NewGuid();
        string second = Socket(compiler, profile);
        Assert.NotEqual(first, second);
        Assert.Equal(first.Length, second.Length);
    }

    [Fact]
    public void Custom_socket_templates_keep_their_explicit_sharing_contract()
    {
        var compiler = new OpenSshConfigCompiler(Path.GetTempPath());
        SshProfile profile = Profile();
        profile.MuxOptions.ControlPath = "custom-%ID%-%C";
        string first = Socket(compiler, profile);
        profile.Host = "new.example";
        Assert.Equal(first, Socket(compiler, profile));
        Assert.Equal("ControlPath custom-" + profile.Id.ToString("N") + "-%C", first);
    }

    [Fact]
    public void Compiled_and_self_contained_launches_choose_the_same_new_socket()
    {
        string root = Path.Combine(Path.GetTempPath(), "ntilde-sharing-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonSshProfileStore(Path.Combine(root, "profiles.json"));
            SshProfile profile = Profile();
            store.SaveProfile(profile);
            var compiler = new OpenSshConfigCompiler(root);
            var planner = new SshLaunchPlanner(store, compiler, () => "ssh");
            SshLaunchPlan first = planner.Plan(profile.Id);
            string firstText = File.ReadAllText(first.ConfigFilePath);
            profile.Host = "new.example";
            store.SaveProfile(profile);
            SshLaunchPlan next = planner.Plan(profile.Id);
            string socket = Socket(compiler, profile);
            Assert.DoesNotContain(socket, firstText);
            Assert.Contains(socket, File.ReadAllText(next.ConfigFilePath));
            Assert.Contains(socket, planner.PlanFor(profile, selfContained: true).Arguments);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string Socket(OpenSshConfigCompiler compiler, SshProfile profile) =>
        compiler.BuildHostOptions(profile).Single(x => x.StartsWith("ControlPath ", StringComparison.Ordinal));

    private static SshProfile Profile() => new()
    {
        Id = Guid.Parse("f8f19f37-da64-4de3-8c4c-7f9227eaaf20"),
        Host = "old.example",
        User = "alice",
        AuthMode = SshAuthMode.IdentityFile,
        IdentityFilePath = "/keys/old",
        JumpHops = { new() { Host = "bastion", User = "ops", Port = 2200 }, new() { Host = "second", User = "ops" } },
        Forwards = { new() { SourcePort = 8080, DestinationHost = "internal", DestinationPort = 80 } },
        MuxOptions = new() { Enabled = true, ControlPersistSeconds = 90 }
    };
}
