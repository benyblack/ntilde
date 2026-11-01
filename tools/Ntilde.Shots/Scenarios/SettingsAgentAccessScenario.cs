using Ntilde.Settings;

namespace Ntilde.Shots.Scenarios;

/// <summary>
/// The observe/act opt-in pair. This is the differentiating screenshot, so it shows the real
/// settings surface with act deliberately OFF — the honest default, and the one that makes the
/// separate-opt-in design legible at a glance.
/// </summary>
internal sealed class SettingsAgentAccessScenario : IScenario
{
    public ShotSpec Spec { get; } = new(
        Name: "settings-agent-access",
        Tier: 2,
        LogicalWidth: 1000,
        LogicalHeight: 700,
        Intent: "The settings window on the Agent Access page, with the observe toggle on, the act " +
                "toggle visibly off beneath it, and the explanatory text readable.");

    public Task RunAsync(ShotContext context) =>
        SettingsWindowScenario.RunAsync(
            context,
            SettingsPage.AgentAccess,
            Spec.LogicalWidth,
            Spec.LogicalHeight,
            (ctx, window) => ctx.CaptureOther(window, "tab"));
}
