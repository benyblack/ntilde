using Ntilde.Shots.Scenarios;

namespace Ntilde.Shots;

public static class ScenarioCatalog
{
    // RemoteFilesScenario is the one scenario that needs more than git and bash: it connects to a
    // throwaway SSH server in Docker (DemoSshHost), and fails with a plain message when Docker is
    // not running.
    //
    // The other three deferrals are gone. sixel-graphics and iterm2-inline-image were blocked twice
    // over - first because nothing under src/ assigned AnsiParser.ImageDecoder so a plain build
    // dropped the picture, then because the cursor was not returned to column 0 after an image and
    // the following prompt landed mid-row. Both are fixed upstream (#369 wired SkiaImageDecoder,
    // #405/#407 fixed the cursor), and InlineImageDecoding still asserts both properties on every
    // run. connection-manager was blocked by JsonSshProfileStore resolving its path from
    // LocalApplicationData and ignoring NTILDE_APPDATA_ROOT, so running it inside the isolated
    // demo world still wrote the machine's real profiles.json; #406/#408 routed Platform's SSH paths
    // through PlatformAppPaths, so a sandboxed run now touches only its own store.
    //
    private static readonly IScenario[] Scenarios =
    [
        new HeroSingleScenario(),
        new HeroSplitScenario(),
        new TabsVerticalScenario(),
        new FocusModeScenario(),
        new CommandPaletteScenario(),
        new SettingsAgentAccessScenario(),
        new SettingsAppearanceScenario(),
        new AgentSessionScenario(),
        new ClipAgentScenario(),
        new ThemesGridScenario(),
        new SearchOverlayScenario(),
        new TuiVimScenario(),
        new TuiMonitorScenario(),
        new CommandAssistScenario(),
        new SixelGraphicsScenario(),
        new Iterm2InlineImageScenario(),
        new ConnectionManagerScenario(),
        new RemoteFilesScenario(),
        new ClipPaletteScenario(),
        new ClipSplitScenario(),
        new ClipTuiScenario()
    ];

    public static IReadOnlyList<IScenario> All() => Scenarios;

    public static IScenario? Find(string name) =>
        Scenarios.FirstOrDefault(s => string.Equals(s.Spec.Name, name, StringComparison.OrdinalIgnoreCase));
}
