using System.Text.Json;
using Ntilde.Shell;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Core;

public sealed class SessionPersistenceSettingTests
{
    [Fact] public void Default_is_off() => Assert.Equal("Off", new TerminalSettings().SessionPersistence);

    [Theory]
    [InlineData("KeepOnClose", true)]
    [InlineData(" keeponclose ", true)]
    [InlineData("Off", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Always", false)]
    public void Only_KeepOnClose_turns_it_on(string? value, bool expected) => Assert.Equal(expected, SessionPersistenceMode.IsKeepOnClose(value));

    [Fact]
    public void Round_trips_through_the_source_generated_context()
    {
        var s = new TerminalSettings { SessionPersistence = "KeepOnClose" };
        string json = JsonSerializer.Serialize(s, AppJsonContext.Default.TerminalSettings);
        Assert.Equal("KeepOnClose", JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalSettings)!.SessionPersistence);
        Assert.Equal("Off", JsonSerializer.Deserialize("{}", AppJsonContext.Default.TerminalSettings)!.SessionPersistence);
    }
}
