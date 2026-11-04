using System;
using System.Threading.Tasks;
using System.Threading;
using Ntilde.AgentHost;

namespace Ntilde.AppTests.AgentHost;

/// <summary>
/// Shared <see cref="IAgentActionExecutor"/> stub for the protocol tests that go
/// through the UI-thread bridge (spawnSession / closeSession, and captureScreen's
/// live mode). Defaults to failing every call until a test wires
/// <see cref="OnSpawn"/> / <see cref="OnClose"/> / <see cref="OnCaptureLive"/> —
/// which is also the "no pane on screen" case the live path has to handle.
/// </summary>
internal sealed class StubExecutor : IAgentActionExecutor
{
    public Func<string?, (AgentSpawnResult?, AgentSpawnError?)>? OnSpawn;
    public Func<Guid, bool>? OnClose;
    public Func<Guid, int, double, AgentLiveCapture?>? OnCaptureLive;
    public string? LastSpawnProfile;
    public Guid? LastClosePane;
    public int SpawnCalls;

    public Task<(AgentSpawnResult? Result, AgentSpawnError? Error)> SpawnAsync(string? profileName)
    {
        SpawnCalls++;
        LastSpawnProfile = profileName;
        var r = OnSpawn?.Invoke(profileName) ?? (null, AgentSpawnError.SpawnFailed);
        return Task.FromResult(r);
    }

    public Task<bool> ClosePaneAsync(Guid paneId)
    {
        LastClosePane = paneId;
        return Task.FromResult(OnClose?.Invoke(paneId) ?? false);
    }

    public Func<Guid, int, int, CancellationToken, Task<AgentPaneResizeResult?>>? OnResize;
    public (Guid PaneId, int Cols, int Rows)? LastResize;
    public int ResizeCalls;

    public Task<AgentPaneResizeResult?> ResizePaneAsync(Guid paneId, int cols, int rows, CancellationToken cancellationToken)
    {
        ResizeCalls++;
        LastResize = (paneId, cols, rows);
        return OnResize?.Invoke(paneId, cols, rows, cancellationToken) ?? Task.FromResult<AgentPaneResizeResult?>(null);
    }

    public Func<Ntilde.AgentHost.Contracts.GeneratePaneInputParams, AgentSessionRegistration, Func<bool>, CancellationToken,
        Task<Ntilde.AgentHost.Contracts.GeneratePaneInputResult?>>? OnGenerateInput;
    public int GenerateInputCalls;
    public async Task<Ntilde.AgentHost.Contracts.GeneratePaneInputResult?> GeneratePaneInputAsync(
        Ntilde.AgentHost.Contracts.GeneratePaneInputParams input, AgentSessionRegistration registration,
        Func<bool> isCurrent, Action onInputSubmitted, CancellationToken token)
    {
        GenerateInputCalls++;
        var result = await (OnGenerateInput?.Invoke(input, registration, isCurrent, token)
            ?? Task.FromResult<Ntilde.AgentHost.Contracts.GeneratePaneInputResult?>(null));
        if (result?.InputSubmitted == true) onInputSubmitted();
        return result;
    }

    public Guid? LastLiveCapturePane;
    public int? LastLiveCaptureMaxWidth;
    public double? LastLiveCaptureScale;

    public Task<AgentLiveCapture?> CaptureLiveAsync(Guid paneId, int maxWidth, double scale)
    {
        LastLiveCapturePane = paneId;
        LastLiveCaptureMaxWidth = maxWidth;
        LastLiveCaptureScale = scale;
        return Task.FromResult(OnCaptureLive?.Invoke(paneId, maxWidth, scale));
    }
}
