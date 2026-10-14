using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Ntilde.Shell;
using Ntilde.Shell.TitleBar;

namespace Ntilde.Tests.Core;

/// <summary>
/// Save without touching anything must leave settings.json as it was. This guards the settings
/// redesign's page split: a field dropped in the move, or a readable label written in place of a
/// stored value, shows up here as a diff.
/// </summary>
/// <remarks>
/// Written against the old single-window Settings (PR 2's first commit) and kept unchanged through
/// every later PR, so it compares the new window with the old one rather than with itself.
/// </remarks>
public sealed class SettingsWindowRoundTripTests
{
    private const string ProfileId = "22222222-2222-2222-2222-222222222222";

    /// <summary>Every TerminalSettings field the Settings window reads and writes.</summary>
    private static readonly string[] WindowOwnedFields =
    [
        "FontSize", "MaxHistory", "FontFamily", "ThemeName", "WindowOpacity", "BlurEffect", "UiScale",
        "TabStripOrientation", "EnableLigatures", "EnableComplexShaping", "AgentIndicatorTabRollup",
        "Keybindings", "TitleBarItems", "TitleBarOrder", "BackgroundImagePath", "BackgroundImageOpacity",
        "BackgroundImageStretch", "CommandAssistEnabled", "CommandAssistHistoryEnabled",
        "CommandAssistPassiveBubbleEnabled", "CommandAssistShellIntegrationEnabled",
        "ExperimentalNativeSshEnabled", "AgentAccessObserveEnabled", "AgentReplayExportEnabled",
        "AgentAccessActEnabled", "LongCommandNotificationsEnabled", "Osc9NotificationsEnabled",
        "Osc9ProgressReportingEnabled", "AutomaticUpdateChecks", "SessionPersistence", "Profiles", "DefaultProfileId",
    ];

    private static readonly Dictionary<string, string> SeedTitleBarItems = new() { ["find"] = "Pinned", ["connections"] = "Hidden" };

    private static readonly List<string> SeedTitleBarOrder = ["new_tab", "find", "open_tab_list", "settings"];

    private static string NativeCommand => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";

    /// <summary>Every window-owned field away from its default, and every value one the window can show.</summary>
    private static string SeedJson() => $$"""
        {
          "FontSize": 17,
          "MaxHistory": 25000,
          "FontFamily": "RoundTrip Mono",
          "ThemeName": "GitHub Light",
          "WindowOpacity": 0.75,
          "BlurEffect": "Mica",
          "UiScale": 1.25,
          "TabStripOrientation": "Vertical",
          "EnableLigatures": true,
          "EnableComplexShaping": false,
          "AgentIndicatorTabRollup": "All",
          "Keybindings": { "settings": "Ctrl+Alt+Shift+F11" },
          "TitleBarItems": { "find": "Pinned", "connections": "Hidden" },
          "TitleBarOrder": [ "new_tab", "find", "open_tab_list", "settings" ],
          "BackgroundImagePath": "/roundtrip/background.png",
          "BackgroundImageOpacity": 0.35,
          "BackgroundImageStretch": "Uniform",
          "CommandAssistEnabled": true,
          "CommandAssistHistoryEnabled": false,
          "CommandAssistPassiveBubbleEnabled": false,
          "CommandAssistShellIntegrationEnabled": false,
          "ExperimentalNativeSshEnabled": false,
          "AgentAccessObserveEnabled": true,
          "AgentReplayExportEnabled": true,
          "AgentAccessActEnabled": true,
          "LongCommandNotificationsEnabled": true,
          "Osc9NotificationsEnabled": true,
          "Osc9ProgressReportingEnabled": false,
          "AutomaticUpdateChecks": false,
          "SessionPersistence": "Off",
          "DefaultProfileId": "{{ProfileId}}",
          "Profiles": [ {
            "Id": "{{ProfileId}}", "Name": "Round trip", "Command": "{{NativeCommand}}", "Arguments": "-l",
            "StartingDirectory": "/work/roundtrip", "FontFamily": "Profile Mono", "FontSize": 13,
            "ThemeName": "Dracula", "EnableLigatures": true, "Group": "Work", "Notes": "keep me",
            "AccentColor": "#ff8800", "Tags": [ "a", "b" ]
          } ]
        }
        """;

    [AvaloniaFact]
    public void SaveWithoutTouchingAnything_LeavesEverySettingAsItWas()
    {
        using var root = SettingsTestRoot.WithSettings(SeedJson());
        AssertSaveLeavesSettingsAsLoaded(root, requireEveryFieldNonDefault: true);
    }

    /// <summary>
    /// The same round trip with every window-owned bool at the opposite polarity. SaveAndClose reads
    /// IsChecked == true, so a checkbox a page forgot to Load also writes false, and a false seed
    /// alone cannot tell that from a loaded one.
    /// </summary>
    [AvaloniaFact]
    public void SaveWithoutTouchingAnything_LeavesEveryFlippedBoolAsItWas()
    {
        using var root = SettingsTestRoot.WithSettings(FlipWindowOwnedBools(SeedJson()));
        AssertSaveLeavesSettingsAsLoaded(root, requireEveryFieldNonDefault: false);
    }

    private static void AssertSaveLeavesSettingsAsLoaded(SettingsTestRoot root, bool requireEveryFieldNonDefault)
    {
        root.CopyShippedTheme("GitHubLight.json");
        AssertTitleBarSeedIsCanonical();

        // What a plain load and save writes: the file the window must reproduce.
        TerminalSettings.Load().Save();
        using JsonDocument expected = root.ReadSettings();
        if (requireEveryFieldNonDefault)
        {
            AssertEveryWindowOwnedFieldIsNonDefault(expected.RootElement);
        }

        var window = new Ntilde.SettingsWindow();
        window.FindControl<Button>("BtnSave")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        using JsonDocument actual = root.ReadSettings();
        AssertJsonEquivalent(expected.RootElement, actual.RootElement, "$");
    }

    /// <summary>Flips the top-level bool fields only; profile fields belong to the editor.</summary>
    private static string FlipWindowOwnedBools(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        foreach (string name in WindowOwnedFields)
        {
            if (node[name] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out bool current))
            {
                node[name] = !current;
            }
        }

        return node.ToJsonString();
    }
    /// <summary>
    /// Pins today's behaviour for the profile fields the editor does not own.
    /// BuildLocalProfilesForEditor copies 13 fields, so any Settings Save resets everything else on
    /// a local profile, the SSH fields included. Pinned so that removing the never-visible SSH panel
    /// (PR 3) provably changes nothing. Keeping those fields is a separate fix that will update this
    /// test (plan, "Corrections to the spec", item 1).
    /// </summary>
    [AvaloniaFact]
    public void SaveWithoutTouchingAnything_KeepsTheEditorsProfileFields_AndResetsTheRestAsBefore()
    {
        string seed = $$"""
            {
              "DefaultProfileId": "{{ProfileId}}",
              "Profiles": [ {
                "Id": "{{ProfileId}}", "Name": "Legacy", "Command": "{{NativeCommand}}", "Group": "Work",
                "SshHost": "legacy.example", "SshPort": 2222, "SshUser": "ops", "UseSshAgent": false,
                "IdentityFilePath": "/keys/id", "CursorStyle": "Block"
              } ]
            }
            """;
        using var root = SettingsTestRoot.WithSettings(seed);

        var window = new Ntilde.SettingsWindow();
        window.FindControl<Button>("BtnSave")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        using JsonDocument saved = root.ReadSettings();
        JsonElement profile = saved.RootElement.GetProperty("Profiles").EnumerateArray()
            .Single(p => p.GetProperty("Id").GetString() == ProfileId);
        Assert.Equal("Legacy", profile.GetProperty("Name").GetString());
        Assert.Equal("Work", profile.GetProperty("Group").GetString());
        Assert.Equal("", profile.GetProperty("SshHost").GetString());
        Assert.Equal(22, profile.GetProperty("SshPort").GetInt32());
        Assert.Equal("", profile.GetProperty("SshUser").GetString());
        Assert.True(profile.GetProperty("UseSshAgent").GetBoolean());
        Assert.True(IsNullOrMissing(profile, "IdentityFilePath"));
        Assert.True(IsNullOrMissing(profile, "CursorStyle"));
    }

    private static bool IsNullOrMissing(JsonElement element, string name)
        => !element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null;

    /// <summary>
    /// The window writes back the resolver's normalized order. The seed must already be in that
    /// form, or the test would flag the normalization instead of a regression.
    /// </summary>
    private static void AssertTitleBarSeedIsCanonical()
    {
        TitleBarLayout layout = TitleBarLayoutResolver.Resolve(SeedTitleBarItems, SeedTitleBarOrder, null);
        Assert.Equal(SeedTitleBarOrder, layout.Pinned.Select(entry => entry.Id));
    }

    /// <summary>A field left at its default could be dropped and the comparison would still pass.</summary>
    private static void AssertEveryWindowOwnedFieldIsNonDefault(JsonElement saved)
    {
        string defaultsJson = JsonSerializer.Serialize(new TerminalSettings(), AppJsonContext.Default.TerminalSettings);
        using JsonDocument defaults = JsonDocument.Parse(defaultsJson);
        foreach (string field in WindowOwnedFields)
        {
            Assert.True(
                defaults.RootElement.GetProperty(field).GetRawText() != saved.GetProperty(field).GetRawText(),
                $"The seed leaves {field} at its default.");
        }
    }

    private static void AssertJsonEquivalent(JsonElement expected, JsonElement actual, string path)
    {
        Assert.True(expected.ValueKind == actual.ValueKind, $"{path}: {expected.ValueKind} became {actual.ValueKind}");
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedNames = expected.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
                var actualNames = actual.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
                Assert.True(expectedNames.SequenceEqual(actualNames), $"{path}: properties {string.Join(",", expectedNames)} became {string.Join(",", actualNames)}");
                foreach (string name in expectedNames)
                {
                    AssertJsonEquivalent(expected.GetProperty(name), actual.GetProperty(name), $"{path}.{name}");
                }

                break;
            case JsonValueKind.Array:
                Assert.True(expected.GetArrayLength() == actual.GetArrayLength(), $"{path}: length {expected.GetArrayLength()} became {actual.GetArrayLength()}");
                for (int i = 0; i < expected.GetArrayLength(); i++)
                {
                    AssertJsonEquivalent(expected[i], actual[i], $"{path}[{i}]");
                }

                break;
            case JsonValueKind.Number:
                // Sliders snap to their tick, so floating-point noise is allowed and nothing more.
                Assert.True(Math.Abs(expected.GetDouble() - actual.GetDouble()) < 1e-9, $"{path}: {expected.GetRawText()} became {actual.GetRawText()}");
                break;
            default:
                Assert.True(expected.GetRawText() == actual.GetRawText(), $"{path}: {expected.GetRawText()} became {actual.GetRawText()}");
                break;
        }
    }
}
