namespace Ntilde.Mux.Tests.Contracts;

/// <summary>
/// The confinement of Kitty file transport (#887): the path arrives from a child's byte stream, so each rule here is a boundary against turning an
/// inline-image sequence into an arbitrary file read, in a direct pane and in a persistent session's daemon alike.
/// </summary>
public sealed class KittyTransportFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ntilde-kt-" + Guid.NewGuid().ToString("N"));

    public KittyTransportFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] ReadOrFail(string path)
    {
        byte[]? read = KittyTransportFile.Read(path);
        Assert.NotNull(read);
        return read!;
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("frame.rgba")]                     // not rooted
    [InlineData("../frame.rgba")]                  // relative traversal
    public void A_path_that_is_not_rooted_is_refused(string? path) => Assert.Null(KittyTransportFile.Read(path));

    [Fact]
    public void A_rooted_path_outside_the_temp_directory_is_refused()
    {
        string outside = OperatingSystem.IsWindows() ? @"C:\Windows\win.ini" : "/etc/hosts";
        Assert.Null(KittyTransportFile.Read(outside));
        Assert.Null(KittyTransportFile.Read(Path.Combine(Path.GetTempPath(), "..", "..", "etc", "hosts"))); // collapses to a path outside temp
    }

    [Fact]
    public void A_file_inside_the_temp_directory_is_read_and_a_missing_one_or_a_directory_is_not()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        Assert.Equal(payload, ReadOrFail(Write("frame.rgba", payload)));
        Assert.Null(KittyTransportFile.Read(Path.Combine(_dir, "missing.rgba")));
        Assert.Null(KittyTransportFile.Read(_dir));
        Assert.Empty(ReadOrFail(Write("empty.rgba", [])));
    }

    [Fact]
    public void The_size_cap_is_exact_and_a_smaller_cap_than_the_default_is_honoured()
    {
        string path = Write("ten.rgba", new byte[10]);
        Assert.Equal(10, KittyTransportFile.Read(path, 10)!.Length);
        Assert.Null(KittyTransportFile.Read(path, 9));
        Assert.Equal(10, KittyTransportFile.Read(path)!.Length);
        Assert.Equal(64L * 1024 * 1024, KittyTransportFile.MaxFileBytes);
    }

    [Fact]
    public void A_file_the_writer_still_holds_open_is_read()
    {
        string path = Write("held.rgba", [9, 8, 7]);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal([9, 8, 7], ReadOrFail(path));
    }

    [Fact]
    public void A_symbolic_link_out_of_temp_is_refused_whether_it_is_the_file_or_a_directory_on_the_way()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "creating symbolic links needs a privilege on Windows");
        string outside = "/etc/hosts";
        Assert.SkipUnless(File.Exists(outside), "no /etc/hosts to point a link at");
        string fileLink = Path.Combine(_dir, "link.rgba"), dirLink = Path.Combine(_dir, "etc");
        File.CreateSymbolicLink(fileLink, outside);
        Directory.CreateSymbolicLink(dirLink, "/etc");
        Assert.Null(KittyTransportFile.Read(fileLink));
        Assert.Null(KittyTransportFile.Read(Path.Combine(dirLink, "hosts")));
    }

    [Fact]
    public void A_chain_of_links_whose_middle_directory_leaves_temp_is_refused()
    {
        // /tmp/dir/image -> /tmp/dir/escape/hosts, with /tmp/dir/escape -> /etc: following only the final link would read /etc/hosts.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "creating symbolic links needs a privilege on Windows");
        Assert.SkipUnless(File.Exists("/etc/hosts"), "no /etc/hosts to point a link at");
        string escape = Path.Combine(_dir, "escape"), image = Path.Combine(_dir, "image.rgba");
        Directory.CreateSymbolicLink(escape, "/etc");
        File.CreateSymbolicLink(image, Path.Combine(escape, "hosts"));
        Assert.Null(KittyTransportFile.Read(image));
        // A relative link that climbs out of temp, and one that climbs and comes back inside, are told apart.
        string up = Path.Combine(_dir, "up.rgba"), back = Path.Combine(_dir, "back.rgba");
        File.CreateSymbolicLink(up, Path.Combine("..", "..", "..", "..", "etc", "hosts"));
        Assert.Null(KittyTransportFile.Read(up));
        string real = Write("real2.rgba", [1, 2]);
        File.CreateSymbolicLink(back, Path.Combine("..", Path.GetFileName(_dir), "real2.rgba"));
        Assert.Equal([1, 2], ReadOrFail(back));
    }

    [Fact]
    public void Dot_dot_after_a_link_means_the_parent_of_the_links_target_as_the_kernel_resolves_it()
    {
        // d/jump -> sub/deep, d/image -> jump/../frame.png: the kernel reads d/sub/frame.png, not d/frame.png (what collapsing ".." before the link would give).
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows normalises .. lexically; creating links needs a privilege there");
        Directory.CreateDirectory(Path.Combine(_dir, "sub", "deep"));
        Write(Path.Combine("sub", "frame.png"), [1, 1]);
        Write("frame.png", [2, 2]);
        File.CreateSymbolicLink(Path.Combine(_dir, "jump"), Path.Combine("sub", "deep"));
        File.CreateSymbolicLink(Path.Combine(_dir, "image"), Path.Combine("jump", "..", "frame.png"));
        Assert.Equal([1, 1], ReadOrFail(Path.Combine(_dir, "image")));
        Assert.Equal([1, 1], ReadOrFail(Path.Combine(_dir, "jump", "..", "frame.png")));      // the same with the ".." written in the path itself
        Assert.Equal([2, 2], ReadOrFail(Path.Combine(_dir, "sub", "..", "frame.png")));       // no link before it: an ordinary lexical ".."
    }

    [Fact]
    public void Dot_dot_after_a_file_or_a_missing_component_fails_as_the_kernel_fails_it()
    {
        // frame.png/../other.png is ENOTDIR and missing/../frame.png is ENOENT: neither names the file next to the component in front of "..".
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows normalises .. lexically");
        Write("frame.png", [1]);
        Write("other.png", [2]);
        Assert.Null(KittyTransportFile.Read(Path.Combine(_dir, "frame.png", "..", "other.png")));
        Assert.Null(KittyTransportFile.Read(Path.Combine(_dir, "missing", "..", "frame.png")));
        File.CreateSymbolicLink(Path.Combine(_dir, "image"), Path.Combine("frame.png", "..", "other.png"));
        Assert.Null(KittyTransportFile.Read(Path.Combine(_dir, "image")));
        Directory.CreateDirectory(Path.Combine(_dir, "real-dir"));
        Assert.Equal([2], ReadOrFail(Path.Combine(_dir, "real-dir", "..", "other.png")));      // an existing directory in front of ".." is an ordinary parent
    }

    [Fact]
    public void A_loop_of_links_is_refused_and_not_followed_forever()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "creating symbolic links needs a privilege on Windows");
        string a = Path.Combine(_dir, "a.rgba"), b = Path.Combine(_dir, "b.rgba");
        File.CreateSymbolicLink(a, b);
        File.CreateSymbolicLink(b, a);
        Assert.Null(KittyTransportFile.Read(a));
    }

    [Fact]
    public void A_FIFO_with_no_writer_is_refused_at_once_instead_of_blocking_the_reader()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "no FIFOs on Windows");
        string fifo = Path.Combine(_dir, "pipe.rgba");
        using (var make = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mkfifo", fifo) { UseShellExecute = false }))
        {
            Assert.NotNull(make);
            make!.WaitForExit();
            Assert.SkipUnless(make.ExitCode == 0, "mkfifo is not available");
        }

        Task<byte[]?> read = Task.Run(() => KittyTransportFile.Read(fifo));
        Assert.True(read.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the reader blocked on a FIFO that has no writer");
        Assert.Null(read.Result);
    }

    [Fact]
    public void A_symbolic_link_that_stays_inside_temp_is_followed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "creating symbolic links needs a privilege on Windows");
        string target = Write("real.rgba", [4, 3, 2, 1]), link = Path.Combine(_dir, "alias.rgba");
        File.CreateSymbolicLink(link, target);
        Assert.Equal([4, 3, 2, 1], ReadOrFail(link));
    }

    [Fact]
    public void A_case_variant_of_the_temp_directory_is_a_different_directory_on_Unix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "these filesystems are case-insensitive by default");
        string path = Write("Frame.rgba", [1]);
        Assert.Null(KittyTransportFile.Read(path.Replace(Path.GetTempPath().TrimEnd('/'), Path.GetTempPath().TrimEnd('/').ToUpperInvariant(), StringComparison.Ordinal)));
    }
}
