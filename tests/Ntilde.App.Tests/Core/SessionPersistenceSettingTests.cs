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
    public void A_settings_file_without_the_key_gets_the_default()
    {
        string loaded = JsonSerializer.Deserialize("{}", AppJsonContext.Default.TerminalSettings)!.SessionPersistence;
        Assert.Equal(TerminalSettings.DefaultSessionPersistence, loaded);
        Assert.Equal("KeepOnClose", loaded); // spec R3: local shells keep running by default
    }

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

    // ---- The startup update gate reads the persisted setting straight from settings.json (Phase 5 Task 22): it must
    // ---- say "off" exactly when loading the settings would.

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"SessionPersistence\":\"Off\"}")]
    [InlineData("{\"SessionPersistence\":\"KeepOnClose\"}")]
    [InlineData("{\"SessionPersistence\":\" keeponclose \"}")]
    [InlineData("{\"SessionPersistence\":\"KeepOnClos\"}")]
    [InlineData("{\"SessionPersistence\":\"\"}")]
    [InlineData("{\"SessionPersistence\":null}")]
    [InlineData("{\"sessionPersistence\":\"Off\"}")]
    [InlineData("{\"ShellExitPolicy\":\"Graceful\",\"SessionPersistence\":\"Off\",\"Keybindings\":{}}")]
    [InlineData("{\"SessionPersistence\":\"KeepOnClose\",\"SessionPersistence\":\"Off\"}")]
    [InlineData("{\"SessionPersistence\":\"Off\",\"SessionPersistence\":\"KeepOnClose\"}")]
    public void Reading_the_file_agrees_with_loading_the_settings(string json)
    {
        bool loadedOff = !SessionPersistenceMode.IsKeepOnClose(JsonSerializer.Deserialize(json, AppJsonContext.Default.TerminalSettings)!.SessionPersistence);
        string path = WriteSettings(json);
        try
        {
            Assert.Equal(loadedOff, SessionPersistenceMode.IsOffInSettingsFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A file that cannot be read is not evidence of "off": the gate keeps R10's behaviour, the default's.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[\"Off\"]")]
    [InlineData("{\"SessionPersistence\":0}")]
    [InlineData("// a comment\n{\"SessionPersistence\":\"Off\"}")]
    public void A_settings_file_that_cannot_be_read_is_not_off(string content)
    {
        string path = WriteSettings(content);
        try
        {
            Assert.False(SessionPersistenceMode.IsOffInSettingsFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void No_settings_file_is_the_default_not_off()
    {
        Assert.False(SessionPersistenceMode.IsOffInSettingsFile(Path.Combine(Path.GetTempPath(), $"ntilde_no_settings_{Guid.NewGuid():N}.json")));
    }

    private static string WriteSettings(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ntilde_settings_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }
}
