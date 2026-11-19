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
