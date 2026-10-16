using Ntilde.Settings.Pages;
using Ntilde.Shell;
using Ntilde.Platform;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

public sealed class SettingsWindowProfileSeparationTests
{
    [Fact]
    public void CopyLocalProfiles_FiltersOutSshProfiles()
    {
        var localId = Guid.Parse("a6ed5c42-0464-469d-97f2-f87d2d2875bc");
        var source = new List<TerminalProfile>
        {
            new TerminalProfile
            {
                Id = localId,
                Name = "PowerShell",
                Type = ConnectionType.Local,
                Command = "pwsh.exe"
            },
            new TerminalProfile
            {
                Id = Guid.Parse("30f4c7d5-6d5c-43cb-b94a-23a7d331c883"),
                Name = "Prod SSH",
                Type = ConnectionType.SSH,
                SshHost = "prod.internal"
            }
        };

        List<TerminalProfile> result = ProfilesPage.CopyLocalProfiles(source);

        Assert.Single(result);
        Assert.Equal(localId, result[0].Id);
        Assert.Equal(ConnectionType.Local, result[0].Type);
        Assert.Equal("PowerShell", result[0].Name);
    }

    [Fact]
    public void BothCopies_KeepTheFieldsTheEditorDoesNotShow()
    {
        TerminalProfile source = LocalProfileWithEveryKindOfField();

        TerminalProfile forEditor = Assert.Single(ProfilesPage.CopyLocalProfiles([source]));
        TerminalProfile forSave = Assert.Single(ProfilesPage.CopyLocalProfiles([forEditor]));

        foreach (TerminalProfile copy in new[] { forEditor, forSave })
        {
            Assert.Equal("Block", copy.CursorStyle);
            Assert.False(copy.CursorBlink);
            Assert.Equal(ShellOverride.Posix, copy.ShellOverride);
            Assert.Equal("legacy.example", copy.SshHost);
            Assert.Equal(2222, copy.SshPort);
            Assert.Equal("/work", copy.DefaultLocalDir);
            Assert.Equal("Server", copy.Icon);
            Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), copy.LastUsed);
            Assert.Equal("8080", Assert.Single(copy.Forwards).LocalAddress);
            Assert.Equal(["work", "prod"], copy.Tags);
        }
    }

    [Fact]
    public void TheEditorsCopy_IsIndependentOfTheSettingsItCameFrom()
    {
        TerminalProfile source = LocalProfileWithEveryKindOfField();

        TerminalProfile forEditor = Assert.Single(ProfilesPage.CopyLocalProfiles([source]));
        forEditor.Name = "Renamed";
        forEditor.Tags.Add("new");
        forEditor.Forwards.Clear();

        Assert.NotSame(source, forEditor);
        Assert.Equal("Legacy", source.Name);
        Assert.Equal(["work", "prod"], source.Tags);
        Assert.Single(source.Forwards);
    }

    private static TerminalProfile LocalProfileWithEveryKindOfField() => new()
    {
        Id = Guid.Parse("5b1f0a52-1d7e-4a43-9d0e-6d4b3b8f2c11"),
        Name = "Legacy",
        Type = ConnectionType.Local,
        Command = "/bin/bash",
        CursorStyle = "Block",
        CursorBlink = false,
        ShellOverride = ShellOverride.Posix,
        SshHost = "legacy.example",
        SshPort = 2222,
        DefaultLocalDir = "/work",
        Icon = "Server",
        LastUsed = new DateTime(2026, 1, 2, 3, 4, 5),
        Tags = ["work", "prod"],
        Forwards = [new ForwardingRule { LocalAddress = "8080", RemoteAddress = "10.0.0.5:80" }],
    };

    [Fact]
    public void ResolveDefaultLocalProfileId_FallsBackToFirstLocalWhenCurrentMissing()
    {
        var firstId = Guid.Parse("3f2b1878-cb5d-48d6-bb71-100b2ef5bf2e");
        var profiles = new List<TerminalProfile>
        {
            new TerminalProfile { Id = firstId, Name = "PowerShell", Type = ConnectionType.Local },
            new TerminalProfile { Id = Guid.Parse("24337edd-e9a8-43f5-b8fd-8cd8ce7cd77a"), Name = "Bash", Type = ConnectionType.Local }
        };

        Guid resolved = ProfilesPage.ResolveDefaultLocalProfileId(Guid.Parse("6a5d526d-6d5e-4e20-b2d9-3f1443d5a56c"), profiles);

        Assert.Equal(firstId, resolved);
    }
}
