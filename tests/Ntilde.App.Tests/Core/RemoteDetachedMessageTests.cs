using System;
using Xunit;

namespace Ntilde.Tests.Core;

public sealed class RemoteDetachedMessageTests
{
    private static readonly Guid Id = Guid.Parse("0f8e3b8a-1c2d-4e5f-8a9b-0c1d2e3f4a5b");

    [Fact]
    public void NotReopenable_NamesTheRecordedInstallPath()
    {
        Assert.Equal(
            $"Shell kept running on me@box — run '/home/me/.data/ntilde/bin/ntilde-mux attach {Id}' on that host to get it back",
            MainWindow.RemoteDetachedMessage("me@box", Id, reopenable: false, "/home/me/.data/ntilde/bin/ntilde-mux"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/ntilde-mux")] // not a path the install flow would record: RemoteMuxCommand refuses it too
    public void NotReopenable_WithoutAUsableRecordedPath_NamesTheDefaultInstallPath(string? recorded)
    {
        Assert.Equal(
            $"Shell kept running on me@box — run '~/.local/share/ntilde/bin/ntilde-mux attach {Id}' on that host to get it back",
            MainWindow.RemoteDetachedMessage("me@box", Id, reopenable: false, recorded));
    }

    [Fact]
    public void Reopenable_PointsAtTheSessionPicker()
    {
        Assert.Equal(
            "Shell kept running on me@box — Attach to session… reopens it",
            MainWindow.RemoteDetachedMessage("me@box", Id, reopenable: true, "/home/me/.data/ntilde/bin/ntilde-mux"));
    }
}
