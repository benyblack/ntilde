using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>
/// Spec R9: on a Windows Velopack install the local daemon runs from its own copy under the app-data root, so an
/// update (which kills every process whose image is under the install root) leaves it running. All but the last two
/// tests run against an in-memory file system; the paths are built with the platform's separators, so the logic is
/// exercised on every CI leg even though only Windows wires it up (<see cref="MuxDaemonImage.ResolverFor"/>).
/// </summary>
public sealed class MuxDaemonImageTests
{
    private const string Version = "0.12.0";

    private static readonly string Base = Path.Combine(Path.GetTempPath(), "nmxi-fake");
    private static readonly string InstallRoot = Path.Combine(Base, "NtildeApp");
    private static readonly string Current = Path.Combine(InstallRoot, "current");
    private static readonly string Exe = Path.Combine(Current, "Ntilde.exe");
    private static readonly string DataRoot = Path.Combine(Base, "data");
    private static readonly string Bin = Path.Combine(DataRoot, "bin");

    private readonly List<string> _log = new();

    private void Log(string line) => _log.Add(line);

    /// <summary>What a Windows install's <c>current\</c> holds (an AOT bundle, as release.yml packs it), plus Update.exe.</summary>
    private static FakeImageFileSystem Installed()
    {
        var fs = new FakeImageFileSystem();
        fs.AddFile(Exe, "exe-v1");
        fs.AddFile(Path.Combine(Current, "rusty_pty.dll"), "pty");
        fs.AddFile(Path.Combine(Current, "conpty.dll"), "conpty-v1");
        fs.AddFile(Path.Combine(Current, "libSkiaSharp.dll"), "skia");
        fs.AddFile(Path.Combine(Current, "x64", "OpenConsole.exe"), "openconsole-x64");
        fs.AddFile(Path.Combine(Current, "arm64", "OpenConsole.exe"), "openconsole-arm64");
        // Not part of the daemon's image.
        fs.AddFile(Path.Combine(Current, "ntilde.com"), "launcher");
        fs.AddFile(Path.Combine(Current, "sq.version"), "nuspec");
        fs.AddFile(Path.Combine(Current, "libSkiaSharp.pdb"), "symbols");
        fs.AddFile(Path.Combine(Current, "themes", "Dracula.json"), "{}");
        fs.AddFile(Path.Combine(Current, "Assets", "Fonts", "JetBrainsMonoNL-Regular.ttf"), "font");
        fs.AddFile(Path.Combine(InstallRoot, "Update.exe"), "update");
        return fs;
    }

    private static readonly string[] ImageFiles =
    [
        "Ntilde.exe",
        "rusty_pty.dll",
        "conpty.dll",
        "libSkiaSharp.dll",
        Path.Combine("x64", "OpenConsole.exe"),
        Path.Combine("arm64", "OpenConsole.exe"),
    ];

    private string Resolve(FakeImageFileSystem fs, string version = Version) => MuxDaemonImage.Resolve(Exe, DataRoot, version, fs, Log);

    [Fact]
    public void The_install_root_is_found_only_for_current_beside_Update_exe()
    {
        FakeImageFileSystem fs = Installed();

        Assert.Equal(InstallRoot, MuxDaemonImage.VelopackInstallRoot(Exe, fs.FileExists));
        Assert.Equal(InstallRoot, MuxDaemonImage.VelopackInstallRoot(Path.Combine(InstallRoot, "Current", "Ntilde.exe"), fs.FileExists));
        Assert.Null(MuxDaemonImage.VelopackInstallRoot(Path.Combine(InstallRoot, "Ntilde.exe"), fs.FileExists));
        Assert.Null(MuxDaemonImage.VelopackInstallRoot(Path.Combine(Base, "portable", "current", "Ntilde.exe"), fs.FileExists));
        Assert.Null(MuxDaemonImage.VelopackInstallRoot(Path.Combine(Current, "x64", "OpenConsole.exe"), fs.FileExists));
    }

    [Fact]
    public void Outside_a_Velopack_install_the_daemon_runs_the_executable_itself()
    {
        FakeImageFileSystem fs = Installed();
        fs.Remove(Path.Combine(InstallRoot, "Update.exe")); // a portable zip or a dev build: current\ alone is not an install

        Assert.Equal(Exe, Resolve(fs));

        Assert.Empty(fs.Copies);
        Assert.False(fs.DirectoryExists(Bin));
    }

    [Fact]
    public void A_Velopack_install_stages_a_copy_and_runs_it()
    {
        FakeImageFileSystem fs = Installed();

        string resolved = Resolve(fs);

        string copy = Path.Combine(Bin, Version);
        Assert.Equal(Path.Combine(copy, "Ntilde.exe"), resolved);
        foreach (string file in ImageFiles)
        {
            Assert.Equal(fs.ReadAllText(Path.Combine(Current, file)), fs.ReadAllText(Path.Combine(copy, file)));
        }

        Assert.True(fs.FileExists(Path.Combine(copy, MuxDaemonImage.CompleteFileName)));
        Assert.False(fs.FileExists(Path.Combine(copy, "ntilde.com")));
        Assert.False(fs.FileExists(Path.Combine(copy, "sq.version")));
        Assert.False(fs.FileExists(Path.Combine(copy, "libSkiaSharp.pdb")));
        Assert.False(fs.DirectoryExists(Path.Combine(copy, "themes")));
        Assert.False(fs.DirectoryExists(Path.Combine(copy, "Assets")));
        // Staged in a temporary sibling, which the move consumed.
        Assert.All(fs.Copies, c => Assert.StartsWith(Path.Combine(Bin, "." + Version + "."), c.Destination, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fs.GetDirectories(Bin), d => Path.GetFileName(d).StartsWith('.'));
    }

    [Fact]
    public void The_complete_file_is_written_after_every_copy_and_before_the_move()
    {
        FakeImageFileSystem fs = Installed();

        Resolve(fs);

        int lastCopy = fs.Operations.FindLastIndex(o => o.StartsWith("copy ", StringComparison.Ordinal));
        int complete = fs.Operations.FindIndex(o => o.StartsWith("write ", StringComparison.Ordinal) && o.EndsWith(MuxDaemonImage.CompleteFileName, StringComparison.Ordinal));
        int move = fs.Operations.FindIndex(o => o.StartsWith("move ", StringComparison.Ordinal));
        Assert.Equal(ImageFiles.Length, fs.Copies.Count);
        Assert.True(lastCopy < complete, string.Join(Environment.NewLine, fs.Operations));
        Assert.True(complete < move, string.Join(Environment.NewLine, fs.Operations));
        Assert.EndsWith(Path.Combine(Bin, Version), fs.Operations[move], StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_call_reuses_the_copy_without_copying()
    {
        FakeImageFileSystem fs = Installed();
        string first = Resolve(fs);
        int copies = fs.Copies.Count;

        string second = Resolve(fs);

        Assert.Equal(Path.Combine(Bin, Version, "Ntilde.exe"), first);
        Assert.Equal(first, second);
        Assert.Equal(copies, fs.Copies.Count);
    }

    [Fact]
    public void A_partial_copy_without_its_complete_file_is_replaced()
    {
        FakeImageFileSystem fs = Installed();
        string copy = Path.Combine(Bin, Version);
        fs.AddFile(Path.Combine(copy, "Ntilde.exe"), "half"); // a copy that never got its .complete

        string resolved = Resolve(fs);

        Assert.Equal(Path.Combine(copy, "Ntilde.exe"), resolved);
        Assert.Equal("exe-v1", fs.ReadAllText(resolved));
        Assert.True(fs.FileExists(Path.Combine(copy, MuxDaemonImage.CompleteFileName)));
    }

    [Fact]
    public void A_copy_whose_files_no_longer_match_its_complete_file_is_replaced()
    {
        FakeImageFileSystem fs = Installed();
        string copy = Path.Combine(Bin, Version);
        Resolve(fs);
        fs.Remove(Path.Combine(copy, "conpty.dll")); // e.g. quarantined by an antivirus

        string resolved = Resolve(fs);

        Assert.Equal(Path.Combine(copy, "Ntilde.exe"), resolved);
        Assert.Equal("conpty-v1", fs.ReadAllText(Path.Combine(copy, "conpty.dll")));
    }

    [Fact]
    public void A_repack_under_the_same_version_stages_a_folder_of_its_own()
    {
        FakeImageFileSystem fs = Installed();
        Resolve(fs);
        fs.AddFile(Path.Combine(Current, "conpty.dll"), "conpty-v1-rebuilt"); // same version, other bytes

        string resolved = Resolve(fs);

        Assert.Equal(Path.Combine(Bin, Version + "-1", "Ntilde.exe"), resolved);
        Assert.Equal("conpty-v1-rebuilt", fs.ReadAllText(Path.Combine(Bin, Version + "-1", "conpty.dll")));
        // The first copy may be running a daemon: left alone.
        Assert.Equal("conpty-v1", fs.ReadAllText(Path.Combine(Bin, Version, "conpty.dll")));
        Assert.Equal(resolved, Resolve(fs));
    }

    [Fact]
    public void A_copy_failure_runs_the_executable_itself_and_logs()
    {
        FakeImageFileSystem fs = Installed();
        fs.FailCopy = destination => destination.EndsWith("conpty.dll", StringComparison.Ordinal) ? new IOException("There is not enough space on the disk.") : null;

        string resolved = Resolve(fs);

        Assert.Equal(Exe, resolved);
        string line = Assert.Single(_log);
        Assert.Contains("not enough space", line, StringComparison.Ordinal);
        Assert.False(fs.DirectoryExists(Path.Combine(Bin, Version)));
        Assert.DoesNotContain(fs.GetDirectories(Bin), d => Path.GetFileName(d).StartsWith('.')); // the temporary folder is cleaned up
    }

    [Fact]
    public void An_app_data_root_inside_the_install_root_runs_the_executable_itself()
    {
        FakeImageFileSystem fs = Installed();

        // A copy in there would die with the update all the same (NTILDE_APPDATA_ROOT pointed into the install).
        Assert.Equal(Exe, MuxDaemonImage.Resolve(Exe, Path.Combine(InstallRoot, "data"), Version, fs, Log));

        Assert.Empty(fs.Copies);
        Assert.Single(_log);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("1.0/evil")]
    public void A_version_that_cannot_name_a_folder_runs_the_executable_itself(string version)
    {
        FakeImageFileSystem fs = Installed();

        Assert.Equal(Exe, Resolve(fs, version));

        Assert.Empty(fs.Copies);
        Assert.Single(_log);
    }

    [Fact]
    public void A_stager_that_loses_the_race_uses_the_winners_copy()
    {
        FakeImageFileSystem fs = Installed();
        bool raced = false;
        fs.BeforeMove = (_, destination) =>
        {
            if (raced) return;
            raced = true;
            // Another process staged the same version first: its move lands, ours then fails.
            Assert.Equal(Path.Combine(Bin, Version, "Ntilde.exe"), MuxDaemonImage.Resolve(Exe, DataRoot, Version, fs, _ => { }));
            Assert.True(fs.DirectoryExists(destination));
        };

        string resolved = Resolve(fs);

        Assert.True(raced);
        Assert.Equal(Path.Combine(Bin, Version, "Ntilde.exe"), resolved);
        Assert.Empty(_log);
        Assert.DoesNotContain(fs.GetDirectories(Bin), d => Path.GetFileName(d).StartsWith('.'));
    }

    [Fact]
    public void A_rename_refused_for_a_moment_is_tried_again()
    {
        FakeImageFileSystem fs = Installed();
        int refusals = 0;
        fs.BeforeMove = (_, _) =>
        {
            if (refusals++ == 0) throw new UnauthorizedAccessException("Access to the path is denied."); // an antivirus holds a fresh file
        };

        string resolved = Resolve(fs);

        Assert.Equal(Path.Combine(Bin, Version, "Ntilde.exe"), resolved);
        Assert.Equal(2, refusals);
    }

    [Fact]
    public void A_rename_refused_every_time_runs_the_executable_itself_and_logs()
    {
        FakeImageFileSystem fs = Installed();
        fs.BeforeMove = (_, _) => throw new IOException("The process cannot access the file because it is being used by another process.");

        Assert.Equal(Exe, Resolve(fs));

        Assert.Contains("being used by another process", Assert.Single(_log), StringComparison.Ordinal);
        Assert.DoesNotContain(fs.GetDirectories(Bin), d => Path.GetFileName(d).StartsWith('.'));
    }

    [Fact]
    public void An_incomplete_copy_another_launch_deletes_first_is_still_replaced()
    {
        FakeImageFileSystem fs = Installed();
        string copy = Path.Combine(Bin, Version);
        fs.AddFile(Path.Combine(copy, "Ntilde.exe"), "half");
        fs.BeforeDeleteFile = path =>
        {
            // Another launch removes the whole folder between our look and our delete.
            if (fs.DirectoryExists(copy)) fs.DeleteDirectory(copy);
            throw new DirectoryNotFoundException(path);
        };

        string resolved = Resolve(fs);

        Assert.Equal(Path.Combine(copy, "Ntilde.exe"), resolved);
        Assert.Equal("exe-v1", fs.ReadAllText(resolved));
        Assert.DoesNotContain(_log, l => !l.Contains("staged", StringComparison.Ordinal));
    }

    [Fact]
    public void Pruning_deletes_other_versions_and_keeps_the_current_ones_and_one_in_use()
    {
        FakeImageFileSystem fs = Installed();
        foreach (string v in new[] { "0.10.0", "0.11.0", Version, Version + "-1", Version + "-beta" })
        {
            fs.AddFile(Path.Combine(Bin, v, "Ntilde.exe"), "exe " + v);
            fs.AddFile(Path.Combine(Bin, v, "x64", "OpenConsole.exe"), "host " + v);
            fs.AddFile(Path.Combine(Bin, v, MuxDaemonImage.CompleteFileName), "");
        }

        fs.InUse.Add(Path.Combine(Bin, "0.11.0", "Ntilde.exe")); // a daemon an older build started still runs from it

        MuxDaemonImage.PruneOldCopies(DataRoot, Version, fs, Log);

        Assert.False(fs.DirectoryExists(Path.Combine(Bin, "0.10.0")));
        Assert.False(fs.DirectoryExists(Path.Combine(Bin, Version + "-beta"))); // another version, not a re-pack of this one
        Assert.True(fs.DirectoryExists(Path.Combine(Bin, Version)));
        Assert.True(fs.DirectoryExists(Path.Combine(Bin, Version + "-1")));
        // Kept whole: nothing of a copy in use is deleted, not even the files no process holds.
        Assert.True(fs.FileExists(Path.Combine(Bin, "0.11.0", "Ntilde.exe")));
        Assert.True(fs.FileExists(Path.Combine(Bin, "0.11.0", "x64", "OpenConsole.exe")));
        Assert.True(fs.FileExists(Path.Combine(Bin, "0.11.0", MuxDaemonImage.CompleteFileName)));
        Assert.Contains(_log, l => l.Contains("0.11.0", StringComparison.Ordinal));
    }

    [Fact]
    public void Pruning_deletes_a_temporary_folder_only_once_it_is_stale()
    {
        FakeImageFileSystem fs = Installed();
        string stale = Path.Combine(Bin, ".0.11.0." + Guid.NewGuid().ToString("N") + ".tmp");
        string fresh = Path.Combine(Bin, "." + Version + "." + Guid.NewGuid().ToString("N") + ".tmp"); // another process stages right now
        fs.AddFile(Path.Combine(stale, "Ntilde.exe"), "half");
        fs.AddFile(Path.Combine(fresh, "Ntilde.exe"), "half");
        fs.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(2));
        fs.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);

        MuxDaemonImage.PruneOldCopies(DataRoot, Version, fs, Log);

        Assert.False(fs.DirectoryExists(stale));
        Assert.True(fs.DirectoryExists(fresh));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    public void Pruning_without_a_usable_current_version_deletes_nothing(string version)
    {
        FakeImageFileSystem fs = Installed();
        fs.AddFile(Path.Combine(Bin, "0.10.0", "Ntilde.exe"), "exe");

        MuxDaemonImage.PruneOldCopies(DataRoot, version, fs, Log);

        Assert.True(fs.FileExists(Path.Combine(Bin, "0.10.0", "Ntilde.exe")));
    }

    [Fact]
    public void Pruning_without_any_copies_does_nothing()
    {
        FakeImageFileSystem fs = Installed();

        MuxDaemonImage.PruneOldCopies(DataRoot, Version, fs, Log);

        Assert.Empty(_log);
    }

    /// <summary>The real file system, end to end: a fake <c>current\</c> with a dummy exe, two DLLs and a console host.</summary>
    [Fact]
    public void The_real_file_system_stages_reuses_and_prunes()
    {
        string root = Path.Combine(Path.GetTempPath(), "nmxi" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string current = Path.Combine(root, "NtildeApp", "current");
            string exe = Path.Combine(current, "Ntilde.exe");
            Directory.CreateDirectory(Path.Combine(current, "x64"));
            File.WriteAllBytes(exe, [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(current, "rusty_pty.dll"), [5, 6]);
            File.WriteAllBytes(Path.Combine(current, "conpty.dll"), [7]);
            File.WriteAllBytes(Path.Combine(current, "x64", "OpenConsole.exe"), [8, 9, 10]);
            File.WriteAllBytes(Path.Combine(current, "ntilde.com"), [11]);
            File.WriteAllBytes(Path.Combine(root, "NtildeApp", "Update.exe"), []);
            string data = Path.Combine(root, "data");
            string old = Path.Combine(data, "bin", "0.0.1");
            Directory.CreateDirectory(old);
            File.WriteAllBytes(Path.Combine(old, "Ntilde.exe"), [0]);

            string resolved = MuxDaemonImage.Resolve(exe, data, Version, MuxImageFileSystem.Instance, Log);

            string copy = Path.Combine(data, "bin", Version);
            Assert.Equal(Path.Combine(copy, "Ntilde.exe"), resolved);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(resolved));
            Assert.Equal(new byte[] { 5, 6 }, File.ReadAllBytes(Path.Combine(copy, "rusty_pty.dll")));
            Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(copy, "conpty.dll")));
            Assert.Equal(new byte[] { 8, 9, 10 }, File.ReadAllBytes(Path.Combine(copy, "x64", "OpenConsole.exe")));
            Assert.True(File.Exists(Path.Combine(copy, MuxDaemonImage.CompleteFileName)));
            Assert.False(File.Exists(Path.Combine(copy, "ntilde.com")));
            Assert.Equal(new[] { "0.0.1", Version }, Directory.GetDirectories(Path.Combine(data, "bin")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.Equal(resolved, MuxDaemonImage.Resolve(exe, data, Version, MuxImageFileSystem.Instance, Log));

            MuxDaemonImage.PruneOldCopies(data, Version, MuxImageFileSystem.Instance, Log);

            Assert.False(Directory.Exists(old));
            Assert.True(File.Exists(resolved));
            Assert.DoesNotContain(_log, l => !l.Contains("staged", StringComparison.OrdinalIgnoreCase) && !l.Contains("deleted", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The GUI, <c>ntilde mux</c> and ntilde.com (which runs Ntilde.exe) all spawn through a resolver.</summary>
    [Fact]
    public void The_CLI_host_carries_the_resolver()
    {
        Assert.NotNull(MuxCommand.CreateHost(DataRoot).DaemonImageResolver);
    }

    [Fact]
    public void Off_Windows_or_outside_an_install_the_resolver_returns_the_executable()
    {
        Func<string, string> resolver = MuxDaemonImage.ResolverFor(DataRoot, Log);

        // Not a Velopack layout on disk (nothing exists under the fake base), so no platform copies anything.
        Assert.Equal(Exe, resolver(Exe));
        Assert.False(Directory.Exists(Bin));
    }

    [Fact]
    public async Task The_local_host_runs_its_after_connect_step_once_after_the_first_success()
    {
        int calls = 0, ran = 0;
        Func<CancellationToken, Task<int>> connect = _ => ++calls == 1 ? Task.FromException<int>(new IOException("no daemon")) : Task.FromResult(calls);
        Func<CancellationToken, Task<int>> wrapped = MuxConnectionHost.AfterFirstConnect(connect, () => ran++);

        await Assert.ThrowsAsync<IOException>(() => wrapped(CancellationToken.None));
        Assert.Equal(0, ran);
        Assert.Equal(2, await wrapped(CancellationToken.None));
        Assert.Equal(3, await wrapped(CancellationToken.None));
        Assert.Equal(1, ran);
    }

    /// <summary>An in-memory <see cref="IMuxImageFileSystem"/>: files hold text, and every write is recorded in order.</summary>
    private sealed class FakeImageFileSystem : IMuxImageFileSystem
    {
        private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
        private readonly Dictionary<string, string> _files = new(Cmp);
        private readonly HashSet<string> _dirs = new(Cmp);
        private readonly Dictionary<string, DateTime> _times = new(Cmp);

        public List<string> Operations { get; } = new();
        public List<(string Source, string Destination)> Copies { get; } = new();

        /// <summary>Files a process holds (a running image): deleting one, or a folder holding one, fails.</summary>
        public HashSet<string> InUse { get; } = new(Cmp);

        public Func<string, Exception?>? FailCopy { get; set; }
        public Action<string, string>? BeforeMove { get; set; }

        public void AddFile(string path, string content)
        {
            CreateDirectory(Path.GetDirectoryName(path)!);
            _files[path] = content;
        }

        public void Remove(string path) => _files.Remove(path);

        public void SetLastWriteTimeUtc(string path, DateTime time) => _times[path] = time;

        public bool FileExists(string path) => _files.ContainsKey(path);

        public bool DirectoryExists(string path) => _dirs.Contains(path);

        public long GetFileLength(string path) =>
            _files.TryGetValue(path, out string? c) ? c.Length : throw new FileNotFoundException(path);

        public IReadOnlyList<string> GetFiles(string directory, string searchPattern)
        {
            if (!DirectoryExists(directory)) throw new DirectoryNotFoundException(directory);
            string? extension = searchPattern == "*" ? null : searchPattern.TrimStart('*');
            return _files.Keys
                .Where(f => Cmp.Equals(Path.GetDirectoryName(f), directory) && (extension is null || f.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        public IReadOnlyList<string> GetDirectories(string directory)
        {
            if (!DirectoryExists(directory)) throw new DirectoryNotFoundException(directory);
            return _dirs.Where(d => Cmp.Equals(Path.GetDirectoryName(d), directory)).ToList();
        }

        public DateTime GetLastWriteTimeUtc(string path) => _times.TryGetValue(path, out DateTime t) ? t : DateTime.UtcNow;

        public void CreateDirectory(string path)
        {
            for (string? d = path; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d)) _dirs.Add(d);
        }

        public void CopyFile(string source, string destination)
        {
            if (FailCopy?.Invoke(destination) is { } failure) throw failure;
            if (!_files.TryGetValue(source, out string? content)) throw new FileNotFoundException(source);
            if (!DirectoryExists(Path.GetDirectoryName(destination)!)) throw new DirectoryNotFoundException(destination);
            if (_files.ContainsKey(destination)) throw new IOException($"{destination} exists");
            _files[destination] = content;
            Copies.Add((source, destination));
            Operations.Add($"copy {destination}");
        }

        public string ReadAllText(string path) => _files.TryGetValue(path, out string? c) ? c : throw new FileNotFoundException(path);

        public void WriteAllText(string path, string contents)
        {
            if (!DirectoryExists(Path.GetDirectoryName(path)!)) throw new DirectoryNotFoundException(path);
            _files[path] = contents;
            Operations.Add($"write {path}");
        }

        public void MoveDirectory(string source, string destination)
        {
            BeforeMove?.Invoke(source, destination);
            if (!DirectoryExists(source)) throw new DirectoryNotFoundException(source);
            if (DirectoryExists(destination) || FileExists(destination)) throw new IOException($"Cannot create '{destination}' because a file or directory with the same name already exists.");
            foreach (string f in Under(_files.Keys, source)) { _files[destination + f[source.Length..]] = _files[f]; _files.Remove(f); }
            foreach (string d in Under(_dirs, source)) { _dirs.Add(destination + d[source.Length..]); _dirs.Remove(d); }
            _dirs.Remove(source);
            _dirs.Add(destination);
            Operations.Add($"move {source} -> {destination}");
        }

        public Action<string>? BeforeDeleteFile { get; set; }

        public void DeleteFile(string path)
        {
            BeforeDeleteFile?.Invoke(path);
            if (InUse.Contains(path)) throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
            _files.Remove(path);
            Operations.Add($"delete {path}");
        }

        public void DeleteDirectory(string path)
        {
            if (!DirectoryExists(path)) throw new DirectoryNotFoundException(path);
            if (InUse.Any(f => f.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
            foreach (string f in Under(_files.Keys, path)) _files.Remove(f);
            foreach (string d in Under(_dirs, path)) _dirs.Remove(d);
            _dirs.Remove(path);
            Operations.Add($"rmdir {path}");
        }

        private static List<string> Under(IEnumerable<string> paths, string directory) =>
            paths.Where(p => p.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
