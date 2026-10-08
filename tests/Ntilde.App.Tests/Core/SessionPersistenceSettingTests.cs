using System.Text.Json;
using Avalonia.Headless.XUnit;
using Ntilde.Shell;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Core;

public sealed class SessionPersistenceSettingTests
{
    [Fact] public void Default_is_the_named_default() => Assert.Equal(TerminalSettings.DefaultSessionPersistence, new TerminalSettings().SessionPersistence);

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
        Assert.Equal(TerminalSettings.DefaultSessionPersistence, JsonSerializer.Deserialize("{}", AppJsonContext.Default.TerminalSettings)!.SessionPersistence);
    }

    [Fact]
    public void A_settings_file_without_the_key_gets_the_default() =>
        Assert.Equal(TerminalSettings.DefaultSessionPersistence, JsonSerializer.Deserialize("{}", AppJsonContext.Default.TerminalSettings)!.SessionPersistence);

    [Theory]
    [InlineData("Off")]
    [InlineData("KeepOnClose")]
    public void An_explicit_value_survives_load_and_save(string value)
    {
        var loaded = JsonSerializer.Deserialize($"{{\"SessionPersistence\":\"{value}\"}}", AppJsonContext.Default.TerminalSettings)!;
        string saved = JsonSerializer.Serialize(loaded, AppJsonContext.Default.TerminalSettings);
        Assert.Equal(value, JsonSerializer.Deserialize(saved, AppJsonContext.Default.TerminalSettings)!.SessionPersistence);
    }

    [Fact]
    public void The_designer_settings_never_persist() =>
        Assert.Equal("Off", AppServices.BuildForDesigner().Settings.SessionPersistence);

    [AvaloniaFact]
    public void A_test_window_spawns_no_daemon()
    {
        MainWindow window = TestMainWindowFactory.Create();
        try { Assert.Null(window.MuxHost); }
        finally { TestMainWindowFactory.DisposeCreatedWindows(); }
    }
}
