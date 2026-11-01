using Ntilde.Platform.Ssh.Launch;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.OpenSsh;
using Ntilde.Platform.Ssh.Storage;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class SshLaunchSnapshotTests
{
    [Fact]
    public void Plan_uses_one_coherent_revision_when_the_store_changes_after_its_first_read()
    {
        WithStore((root, inner, original) =>
        {
            var edited = Edited(original.Id);
            var store = new AdvancingStore(inner, () => inner.SaveProfile(edited));
            SshLaunchPlan plan = Planner(root, store).Plan(original.Id);
            string config = Config(plan);
            if (config.Contains("HostName old.example", StringComparison.Ordinal))
            {
                Assert.Contains("User=alice", plan.Arguments);
                Assert.Equal("/old-local", plan.WorkingDirectory);
            }
            else
            {
                Assert.Contains("HostName new.example", config);
                Assert.DoesNotContain("User=alice", plan.Arguments);
                Assert.Equal("/new-local", plan.WorkingDirectory);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlanFor_keeps_the_requested_snapshot_when_the_validated_store_changes(bool delete)
    {
        WithStore((root, inner, original) =>
        {
            var store = new AdvancingStore(inner, () =>
            {
                if (delete) inner.DeleteProfile(original.Id);
                else inner.SaveProfile(Edited(original.Id));
            });
            SshLaunchPlan plan = Planner(root, store).PlanFor(original);
            Assert.Contains("HostName old.example", Config(plan));
            Assert.Contains("User=alice", plan.Arguments);
            Assert.Equal("/old-local", plan.WorkingDirectory);
        });
    }

    [Fact]
    public void PlanFor_keeps_the_requested_working_directory_when_only_that_field_changed()
    {
        WithStore((root, inner, original) =>
        {
            inner.SaveProfile(new SshProfile
            {
                Id = original.Id,
                Host = original.Host,
                User = original.User,
                ExtraSshArgs = original.ExtraSshArgs,
                WorkingDirectory = "/new-local"
            });
            SshLaunchPlan plan = Planner(root, inner).PlanFor(original);
            Assert.Equal("/old-local", plan.WorkingDirectory);
            Assert.Contains("HostName old.example", Config(plan));
        });
    }

    private static SshLaunchPlanner Planner(string root, ISshProfileStore store) =>
        new(store, new OpenSshConfigCompiler(root), () => "ssh");

    private static string Config(SshLaunchPlan plan) => plan.ConfigFilePath == "none"
        ? string.Join('\n', plan.Arguments)
        : File.ReadAllText(plan.ConfigFilePath);

    private static SshProfile Edited(Guid id) => new()
    {
        Id = id,
        Host = "new.example",
        User = "bob",
        WorkingDirectory = "/new-local"
    };

    private static void WithStore(Action<string, JsonSshProfileStore, SshProfile> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "ntilde-launch-snapshot-" + Guid.NewGuid().ToString("N"));
        try
        {
            var inner = new JsonSshProfileStore(Path.Combine(root, "profiles.json"));
            var original = new SshProfile { Host = "old.example", User = "alice", ExtraSshArgs = "-o User=alice", WorkingDirectory = "/old-local" };
            inner.SaveProfile(original);
            test(root, inner, original);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // A real whole-file store edit after whichever read the planner performs first.
    // Tests accept either coherent normal-plan revision instead of requiring a call order.
    private sealed class AdvancingStore(ISshProfileStore inner, Action advance) : ISshProfileStore
    {
        private bool _advanced;
        public IReadOnlyList<SshProfile> GetProfiles()
        {
            IReadOnlyList<SshProfile> captured = inner.GetProfiles();
            Advance();
            return captured;
        }
        public SshProfile? GetProfile(Guid id)
        {
            SshProfile? captured = inner.GetProfile(id);
            Advance();
            return captured;
        }
        private void Advance()
        {
            if (_advanced) return;
            _advanced = true;
            advance();
        }
        public void SaveProfile(SshProfile profile) => inner.SaveProfile(profile);
        public bool DeleteProfile(Guid id) => inner.DeleteProfile(id);
    }
}
