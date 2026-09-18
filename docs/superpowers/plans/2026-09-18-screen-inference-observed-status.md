# Screen Inference (Observed Status Tier) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an `observed` confidence tier to agent-host session status, driven by one TypeSafe System One judgment over a pane's redacted visible text, and feed it to the MCP status tools and the tab strip.

**Architecture:** A new leaf project `Ntilde.Inference` holds the HTTP client and the screen classifier. In `Ntilde.App/AgentHost`, `AgentSessionStatusMachine` gains a `NotifyObserved` signal with explicit override rules, and a new `ObservedActivityMonitor` on its own 1 s timer captures quiet panes, redacts, classifies, and feeds the machine. Wire changes are additive. Settings, an SSH per-profile flag, and a vault-held API key gate the whole thing, default off.

**Tech Stack:** .NET 10, C# (`LangVersion latest`, nullable on, implicit usings on), System.Text.Json source generation (the app publishes NativeAOT), `System.Net.Http` from the shared framework, xunit.v3, NetArchTest.Rules, Avalonia 12 for the two UI touch points.

**Spec:** `docs/superpowers/specs/2026-09-17-screen-inference-observed-status-design.md`

## Global Constraints

- Work in the worktree `D:\projects\nova2\.worktrees\screen-inference` on branch `feat/screen-inference`. Never run git against the main checkout.
- Build and test only through the wrapper: `scripts/build.ps1 <dotnet args>` from the PowerShell tool (one project per call). Raw `dotnet build` hangs under a captured stdout. Example: `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~SystemOneClientTests" --blame-hang-timeout 5m`.
- Never run two `Ntilde.App.Tests` invocations concurrently.
- `Ntilde.Inference` has zero `ProjectReference`s and no `PackageReference`s. `System.Net.Http` comes from the `net10.0` shared framework; do not add a package for it.
- `Ntilde.CommandAssist` must stay free of networking; nothing in this plan touches it except reading `ISecretsFilter`.
- All JSON that crosses the AOT boundary uses a `JsonSerializerContext`. No reflection-based `JsonSerializer.Serialize(object)` anywhere.
- New `TerminalSettings` bool fields must be added to `SettingsTools.BoolFields` and `SettingsTools.KnownFields` in `src/Ntilde.McpServer/Tools/SettingsTools.cs`, or `SettingsToolsDriftGuardTests` fails.
- New `SshProfile` bool fields must be added to `src/Ntilde.McpServer/Tools/ConnectionProfileTools.cs` (doc row, example JSON, known-field list, `RequireBoolType` call), or `ConnectionProfileDriftGuardTests` fails.
- Test projects are not added; every new test lives in `tests/Ntilde.App.Tests`, `tests/Ntilde.McpServer.Tests`, or `tests/Ntilde.Architecture.Tests`, so `.github/workflows/ci.yml` is untouched.
- Wire constants: confidence `"observed"`; activities `"commandRunning"`, `"agentWorking"`, `"waitingForUser"`, `"idleShellPrompt"`, `"unknownBlank"`. Protocol version stays 1.
- Thresholds: `AgentSessionStatusMachine.ObservedOverrideThreshold = 0.85`, `TabStatusTracker.AttentionThreshold = 0.7`, monitor `QuietWindow` 2 s, `MinInterval` 5 s, `InitialBackoff` 5 s, `MaxBackoff` 5 min, HTTP timeout 5 s, model `"jev-latest"`, endpoint `https://api.typesafe.ai/v1/systemone`.
- Secret-store key for the API key: `ntilde/inference/typesafe-api-key`. The key never appears in `settings.json`, logs, or test output.
- Commit after every task with the trailer `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.

## File Structure

**Create**

| File | Responsibility |
|---|---|
| `src/Ntilde.Inference/Ntilde.Inference.csproj` | Leaf project, `TreatWarningsAsErrors`, `InternalsVisibleTo Ntilde.App.Tests` |
| `src/Ntilde.Inference/SystemOneModels.cs` | Request/response DTOs for `POST /v1/systemone` |
| `src/Ntilde.Inference/InferenceJsonContext.cs` | Source-generated JSON context |
| `src/Ntilde.Inference/IApiKeySource.cs` | `string? TryGetKey()` |
| `src/Ntilde.Inference/SystemOneClient.cs` | The HTTP call, typed outcomes, no retry |
| `src/Ntilde.Inference/ScreenActivity.cs` | `ScreenSample`, `ScreenActivity`, answer and result records, `IScreenActivityClassifier` |
| `src/Ntilde.Inference/ScreenActivityClassifier.cs` | The three questions, criteria text, answer mapping |
| `src/Ntilde.App/AgentHost/ScreenObservation.cs` | The record the status machine stores |
| `src/Ntilde.App/AgentHost/ObservedActivityMonitor.cs` | Timer, eligibility, due checks, capture, redact, send, stale drop, backoff |
| `src/Ntilde.App/AgentHost/ObservedActivityMonitorComposition.cs` | `Instance`, real classifier, `CaptureVisibleText`, `VaultApiKeySource` |
| `tests/Ntilde.App.Tests/Inference/SystemOneClientTests.cs` | Client tests over a fake handler |
| `tests/Ntilde.App.Tests/Inference/ScreenActivityClassifierTests.cs` | Question shape and answer mapping |
| `tests/Ntilde.App.Tests/AgentHost/AgentSessionStatusMachineObservedTests.cs` | Override rules |
| `tests/Ntilde.App.Tests/AgentHost/ObservedActivityMonitorTests.cs` | Monitor logic |
| `tests/Ntilde.App.Tests/Inference/ScreenActivityLiveTests.cs` | Env-gated live replay |

**Modify**

| File | Change |
|---|---|
| `Ntilde.sln` | Register the new project (three blocks) |
| `src/Ntilde.App/Ntilde.App.csproj` | `ProjectReference` to Inference |
| `tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj`, `LayeringTests.cs`, `ProjectFileLayeringTests.cs` | Leaf rules for Inference |
| `src/Ntilde.AgentHost.Contracts/AgentHostProtocol.cs`, `StatusContracts.cs`, `AgentHostJsonContext.cs` | `Observed` constant, `ObservedActivities`, `SessionObservationDto`, two DTO fields |
| `src/Ntilde.App/AgentHost/AgentSessionStatus.cs`, `AgentSessionStatusMachine.cs`, `AgentStatusWire.cs` | Observed tier, `NotifyObserved`, output sequence, DTO mapping |
| `src/Ntilde.McpServer/Tools/SessionTools.cs` | Formatter line, tool description |
| `src/Ntilde.App/Shell/TabStatusTracker.cs`, `src/Ntilde.App/MainWindow.axaml.cs` | `NoteObservedAttention`, refresh integration, wiring, SSH probe, indicator placement |
| `src/Ntilde.App/Shell/TerminalSettings.cs`, `VaultService.cs` | `ScreenInferenceEnabled`, API-key accessors |
| `src/Ntilde.Platform/Ssh/Models/SshProfile.cs`, `Storage/JsonSshProfileStore.cs`, `src/Ntilde.App/Services/Ssh/SshConnectionService.cs` | `AllowScreenInference` and its two mappings |
| `src/Ntilde.McpServer/Tools/SettingsTools.cs`, `ConnectionProfileTools.cs` | Drift-guard registrations |
| `src/Ntilde.App/SettingsWindow.axaml(.cs)`, `Views/Ssh/NewSshConnectionView.axaml`, `ViewModels/Ssh/NewSshConnectionViewModel.cs` | Toggle, key field, per-profile checkbox |
| `src/Ntilde.App/MainWindow.axaml` | Indicator button |
| `docs/plans/2026-07-07-agent-host-a2-status-design.md`, `docs/agent-host/known-limitations.md` | Amendments |

---

### Task 1: Create the `Ntilde.Inference` leaf project

**Files:**
- Create: `src/Ntilde.Inference/Ntilde.Inference.csproj`
- Create: `src/Ntilde.Inference/IApiKeySource.cs`
- Modify: `Ntilde.sln`
- Modify: `src/Ntilde.App/Ntilde.App.csproj:541`
- Modify: `tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj:31`
- Modify: `tests/Ntilde.Architecture.Tests/ProjectFileLayeringTests.cs` (after the `AgentHostContracts_csproj_must_have_no_project_references` fact, ~L142)

**Interfaces:**
- Produces: assembly `Ntilde.Inference`, namespace `Ntilde.Inference`, `public interface IApiKeySource { string? TryGetKey(); }`.

- [ ] **Step 1: Write the failing architecture test**

Add to `tests/Ntilde.Architecture.Tests/ProjectFileLayeringTests.cs`, next to the AgentHostContracts fact:

```csharp
    /// <summary>
    /// Ntilde.Inference is the assembly that talks to a remote model. It stays a leaf so the
    /// only thing it can send is what a caller hands it: no reach into settings, the vault,
    /// the grid, or the command history.
    /// </summary>
    [Fact]
    public void Inference_csproj_must_have_no_project_references()
    {
        var refs = ProjectReferences("src/Ntilde.Inference/Ntilde.Inference.csproj");
        Assert.Empty(refs);
    }
```

- [ ] **Step 2: Run it to verify it fails**

PowerShell: `scripts/build.ps1 test tests/Ntilde.Architecture.Tests --filter "FullyQualifiedName~Inference_csproj_must_have_no_project_references"`
Expected: FAIL (file not found when `ProjectReferences` reads the csproj).

- [ ] **Step 3: Create the project file**

`src/Ntilde.Inference/Ntilde.Inference.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    Leaf library for remote model calls (TypeSafe System One). Referenced by Ntilde.App only.
    Zero project references, zero package references: System.Net.Http comes from the shared
    framework. The leaf rule is enforced by Ntilde.Architecture.Tests
    (Inference_csproj_must_have_no_project_references, Inference_must_be_a_leaf_assembly).
  -->

  <PropertyGroup>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <RootNamespace>Ntilde.Inference</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Ntilde.App.Tests" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Create the key-source interface**

`src/Ntilde.Inference/IApiKeySource.cs`:

```csharp
namespace Ntilde.Inference;

/// <summary>
/// Where <see cref="SystemOneClient"/> gets its bearer token. Implemented by the App over the
/// OS secret store; the client never sees settings or the vault type.
/// </summary>
public interface IApiKeySource
{
    /// <summary>The key, or null when none is configured.</summary>
    string? TryGetKey();
}
```

- [ ] **Step 5: Register the project in the solution**

In `Ntilde.sln`, add after line 45 (the `EndProject` of `Ntilde.AgentHost.Contracts`):

```
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Ntilde.Inference", "src\Ntilde.Inference\Ntilde.Inference.csproj", "{3F1D2C77-9B4E-4A6B-8E52-1C0D7A9F6B21}"
EndProject
```

In `GlobalSection(ProjectConfigurationPlatforms)`, add after the twelve `{A7C4E9D1-...}` lines:

```
		{3F1D2C77-9B4E-4A6B-8E52-1C0D7A9F6B21}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{3F1D2C77-9B4E-4A6B-8E52-1C0D7A9F6B21}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{3F1D2C77-9B4E-4A6B-8E52-1C0D7A9F6B21}.Debug|x64.ActiveCfg = Debug|Any CPU
		{3F1D2C77-9B4E-4A6B-8E52-1C0D7A9F6B21}.Debug|x64.Build.0 = Debug|Any CPU
		{3F1D2C77-9B4E-4A6B-8E52-1C0D7A9F6B21}.Debug|x86.ActiveCfg = Debug|Any CPU
		{3F1D2C77-9B4E-4A6B-8E52-1C0D7A9F6B21}.Debug|x86.Build.0 = Debug|Any CPU
		{3F1D2C77-9B4E-4A6B-8E52-1C0D7A9F6B21}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{3F1D2C77-9B4E-4A6B-8E52-1C0D7A9F6B21}.Release|Any CPU.Build.0 = Release|Any CPU
		{3F1D2C77-9B4E-4A6B-8E52-1C0D7A9F6B21}.Release|x64.ActiveCfg = Release|Any CPU
		{3F1D2C77-9B4E-4A6B-8E52-1C0D7A9F6B21}.Release|x64.Build.0 = Release|Any CPU
		{3F1D2C77-9B4E-4A6B-8E52-1C0D7A9F6B21}.Release|x86.ActiveCfg = Release|Any CPU
		{3F1D2C77-9B4E-4A6B-8E52-1C0D7A9F6B21}.Release|x86.Build.0 = Release|Any CPU
```

In `GlobalSection(NestedProjects)`, add after the `{A7C4E9D1-...} = {827E0CD3-...}` line:

```
		{3F1D2C77-9B4E-4A6B-8E52-1C0D7A9F6B21} = {827E0CD3-B72D-47B6-A68D-7590B98EB39B}
```

- [ ] **Step 6: Reference it from the App and the architecture tests**

`src/Ntilde.App/Ntilde.App.csproj`, after line 541:

```xml
    <ProjectReference Include="..\Ntilde.Inference\Ntilde.Inference.csproj" />
```

`tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj`, after line 31:

```xml
    <ProjectReference Include="..\..\src\Ntilde.Inference\Ntilde.Inference.csproj" />
```

- [ ] **Step 7: Build and run the architecture tests**

PowerShell: `scripts/build.ps1 build src/Ntilde.Inference` then `scripts/build.ps1 test tests/Ntilde.Architecture.Tests`
Expected: build succeeds; all architecture tests PASS, including the new one and `Velopack_is_referenced_only_by_the_App` (the new csproj has no Velopack reference).

- [ ] **Step 8: Commit**

```bash
git add Ntilde.sln src/Ntilde.Inference src/Ntilde.App/Ntilde.App.csproj tests/Ntilde.Architecture.Tests
git commit -m "feat(inference): add Ntilde.Inference leaf project

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: System One DTOs, JSON context, and `SystemOneClient`

**Files:**
- Create: `src/Ntilde.Inference/SystemOneModels.cs`
- Create: `src/Ntilde.Inference/InferenceJsonContext.cs`
- Create: `src/Ntilde.Inference/SystemOneClient.cs`
- Test: `tests/Ntilde.App.Tests/Inference/SystemOneClientTests.cs`
- Modify: `tests/Ntilde.Architecture.Tests/LayeringTests.cs` (add assembly accessor + one fact)

**Interfaces:**
- Consumes: `IApiKeySource` (Task 1).
- Produces:
  - `SystemOneRequest { string Model; JsonElement State; Dictionary<string, SystemOneQuestion> Questions }`
  - `SystemOneQuestion { string Type; string Instructions; Dictionary<string,string>? Criteria }`
  - `SystemOneResponse { string? Model; Dictionary<string, SystemOneAnswer> Answers; SystemOneUsage? Usage }`
  - `SystemOneAnswer { string? Type; double? Noul; string? Choice; Dictionary<string,double>? Probabilities; double? Confidence; double? Score }`
  - `SystemOneUsage { int InputTokens; int OutputTokens }`
  - `enum SystemOneOutcome { Ok, NoKey, Unauthorized, Rejected, RateLimited, Overloaded, TransportFailure, Malformed }`
  - `sealed record SystemOneResult(SystemOneOutcome Outcome, SystemOneResponse? Response, string? Detail)`
  - `SystemOneClient(HttpClient http, IApiKeySource keySource, Uri? endpoint = null)` with `Task<SystemOneResult> EvaluateAsync(SystemOneRequest request, CancellationToken ct)`, `const string DefaultEndpoint`, `static readonly TimeSpan DefaultTimeout = 5 s`, `const string DefaultModel = "jev-latest"`.
  - `InferenceJsonContext.Default` with `SystemOneRequest`, `SystemOneResponse` type infos.

- [ ] **Step 1: Write the failing tests**

`tests/Ntilde.App.Tests/Inference/SystemOneClientTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.Inference;

namespace Ntilde.AppTests.Inference;

public class SystemOneClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OkBody, Encoding.UTF8, "application/json") };
        public Exception? Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (Throw != null) throw Throw;
            return Respond(request);
        }
    }

    private sealed class FixedKey(string? key) : IApiKeySource
    {
        public string? TryGetKey() => key;
    }

    private const string OkBody =
        """
        {"model":"jev-1.13.0","answers":{
          "activity":{"type":"choice","choice":"waiting_for_user","confidence":0.92,
                      "probabilities":{"waiting_for_user":0.94,"agent_working":0.05,"idle_shell_prompt":0.01}},
          "needs_attention":{"type":"noul","noul":0.79},
          "last_command_failed":{"type":"noul","noul":0.04}},
         "usage":{"input_tokens":1073,"output_tokens":59}}
        """;

    private static SystemOneRequest SampleRequest() => new()
    {
        Model = SystemOneClient.DefaultModel,
        State = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["screen"] = "$ " }, InferenceJsonContext.Default.DictionaryStringString),
        Questions = new Dictionary<string, SystemOneQuestion>
        {
            ["activity"] = new() { Type = "choice", Instructions = "What?", Criteria = new() { ["a"] = "A", ["b"] = "B" } },
            ["needs_attention"] = new() { Type = "noul", Instructions = "Need?" },
        },
    };

    private static (SystemOneClient Client, FakeHandler Handler) Make(string? key = "k-test")
    {
        var handler = new FakeHandler();
        var client = new SystemOneClient(new HttpClient(handler), new FixedKey(key));
        return (client, handler);
    }

    [Fact]
    public async Task Posts_to_the_endpoint_with_bearer_and_json_body()
    {
        var (client, handler) = Make();

        var result = await client.EvaluateAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal(SystemOneOutcome.Ok, result.Outcome);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(SystemOneClient.DefaultEndpoint, handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("k-test", handler.LastRequest.Headers.Authorization.Parameter);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("jev-latest", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("$ ", doc.RootElement.GetProperty("state").GetProperty("screen").GetString());
        var q = doc.RootElement.GetProperty("questions");
        Assert.Equal("choice", q.GetProperty("activity").GetProperty("type").GetString());
        Assert.Equal("A", q.GetProperty("activity").GetProperty("criteria").GetProperty("a").GetString());
        Assert.False(q.GetProperty("needs_attention").TryGetProperty("criteria", out _), "null criteria must be omitted");
    }

    [Fact]
    public async Task Parses_choice_and_noul_answers_and_usage()
    {
        var (client, _) = Make();

        var result = await client.EvaluateAsync(SampleRequest(), CancellationToken.None);

        var answers = result.Response!.Answers;
        Assert.Equal("waiting_for_user", answers["activity"].Choice);
        Assert.Equal(0.92, answers["activity"].Confidence!.Value, 3);
        Assert.Equal(0.94, answers["activity"].Probabilities!["waiting_for_user"], 3);
        Assert.Equal(0.79, answers["needs_attention"].Noul!.Value, 3);
        Assert.Equal(1073, result.Response.Usage!.InputTokens);
    }

    [Fact]
    public async Task Missing_key_is_reported_without_a_request()
    {
        var (client, handler) = Make(key: null);

        var result = await client.EvaluateAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal(SystemOneOutcome.NoKey, result.Outcome);
        Assert.Null(handler.LastRequest);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SystemOneOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.UnprocessableEntity, SystemOneOutcome.Rejected)]
    [InlineData(HttpStatusCode.TooManyRequests, SystemOneOutcome.RateLimited)]
    [InlineData((HttpStatusCode)529, SystemOneOutcome.Overloaded)]
    [InlineData(HttpStatusCode.InternalServerError, SystemOneOutcome.TransportFailure)]
    public async Task Maps_status_codes_to_outcomes(HttpStatusCode code, SystemOneOutcome expected)
    {
        var (client, handler) = Make();
        handler.Respond = _ => new HttpResponseMessage(code) { Content = new StringContent("{\"detail\":\"x\"}") };

        var result = await client.EvaluateAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task Transport_exception_is_a_transport_failure_not_a_throw()
    {
        var (client, handler) = Make();
        handler.Throw = new HttpRequestException("boom");

        var result = await client.EvaluateAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal(SystemOneOutcome.TransportFailure, result.Outcome);
        Assert.Contains("boom", result.Detail);
    }

    [Fact]
    public async Task Cancellation_is_a_transport_failure()
    {
        var (client, handler) = Make();
        handler.Throw = new TaskCanceledException("timed out");

        var result = await client.EvaluateAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal(SystemOneOutcome.TransportFailure, result.Outcome);
    }

    [Fact]
    public async Task Malformed_success_body_is_malformed()
    {
        var (client, handler) = Make();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") };

        var result = await client.EvaluateAsync(SampleRequest(), CancellationToken.None);

        Assert.Equal(SystemOneOutcome.Malformed, result.Outcome);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

PowerShell: `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~SystemOneClientTests" --blame-hang-timeout 5m`
Expected: FAIL to compile (types missing).

- [ ] **Step 3: Write the DTOs**

`src/Ntilde.Inference/SystemOneModels.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ntilde.Inference;

/// <summary>Body of <c>POST /v1/systemone</c>. See https://docs.typesafe.ai/api.</summary>
public sealed class SystemOneRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = SystemOneClient.DefaultModel;

    /// <summary>Pre-serialized state. Callers serialize their own typed state through a context so this assembly stays state-agnostic.</summary>
    [JsonPropertyName("state")]
    public JsonElement State { get; set; }

    [JsonPropertyName("questions")]
    public Dictionary<string, SystemOneQuestion> Questions { get; set; } = new();
}

public sealed class SystemOneQuestion
{
    /// <summary>"choice", "noul" or "score".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "noul";

    [JsonPropertyName("instructions")]
    public string Instructions { get; set; } = string.Empty;

    /// <summary>Choice: option → description. Noul: "true"/"false" → description. Omitted when null.</summary>
    [JsonPropertyName("criteria")]
    public Dictionary<string, string>? Criteria { get; set; }
}

public sealed class SystemOneResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("answers")]
    public Dictionary<string, SystemOneAnswer> Answers { get; set; } = new();

    [JsonPropertyName("usage")]
    public SystemOneUsage? Usage { get; set; }
}

public sealed class SystemOneAnswer
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("noul")]
    public double? Noul { get; set; }

    [JsonPropertyName("choice")]
    public string? Choice { get; set; }

    [JsonPropertyName("probabilities")]
    public Dictionary<string, double>? Probabilities { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("score")]
    public double? Score { get; set; }
}

public sealed class SystemOneUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}

public enum SystemOneOutcome
{
    Ok,
    /// <summary>No API key configured; no request was made.</summary>
    NoKey,
    /// <summary>HTTP 401.</summary>
    Unauthorized,
    /// <summary>HTTP 422: the request failed validation.</summary>
    Rejected,
    /// <summary>HTTP 429.</summary>
    RateLimited,
    /// <summary>HTTP 529.</summary>
    Overloaded,
    /// <summary>Any other non-success status, a thrown transport exception, or a timeout.</summary>
    TransportFailure,
    /// <summary>200 with a body that did not parse.</summary>
    Malformed,
}

public sealed record SystemOneResult(SystemOneOutcome Outcome, SystemOneResponse? Response, string? Detail);
```

`src/Ntilde.Inference/InferenceJsonContext.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Ntilde.Inference;

/// <summary>Source-generated context: the app publishes NativeAOT, so no reflection serialization.</summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SystemOneRequest))]
[JsonSerializable(typeof(SystemOneResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, SystemOneQuestion>))]
[JsonSerializable(typeof(Dictionary<string, SystemOneAnswer>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
public sealed partial class InferenceJsonContext : JsonSerializerContext
{
}
```

- [ ] **Step 4: Write the client**

`src/Ntilde.Inference/SystemOneClient.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Ntilde.Inference;

/// <summary>
/// One POST to the TypeSafe System One endpoint. Typed outcomes, never throws for a server or
/// transport failure. No retry and no backoff: that policy belongs to the caller so it can be
/// tested with a fake clock.
/// </summary>
public sealed class SystemOneClient
{
    public const string DefaultEndpoint = "https://api.typesafe.ai/v1/systemone";
    public const string DefaultModel = "jev-latest";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly IApiKeySource _keySource;
    private readonly Uri _endpoint;

    public SystemOneClient(HttpClient http, IApiKeySource keySource, Uri? endpoint = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _keySource = keySource ?? throw new ArgumentNullException(nameof(keySource));
        _endpoint = endpoint ?? new Uri(DefaultEndpoint);
        if (_http.Timeout == Timeout.InfiniteTimeSpan || _http.Timeout > DefaultTimeout)
        {
            _http.Timeout = DefaultTimeout;
        }
    }

    /// <summary>True when a key is available right now; lets callers skip work before capturing anything.</summary>
    public bool HasCredentials => !string.IsNullOrEmpty(_keySource.TryGetKey());

    public async Task<SystemOneResult> EvaluateAsync(SystemOneRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = _keySource.TryGetKey();
        if (string.IsNullOrEmpty(key))
        {
            return new SystemOneResult(SystemOneOutcome.NoKey, null, "no API key configured");
        }

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var json = JsonSerializer.Serialize(request, InferenceJsonContext.Default.SystemOneRequest);
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize(body, InferenceJsonContext.Default.SystemOneResponse);
                    return parsed == null
                        ? new SystemOneResult(SystemOneOutcome.Malformed, null, "empty body")
                        : new SystemOneResult(SystemOneOutcome.Ok, parsed, null);
                }
                catch (JsonException ex)
                {
                    return new SystemOneResult(SystemOneOutcome.Malformed, null, ex.Message);
                }
            }

            var outcome = (int)response.StatusCode switch
            {
                401 => SystemOneOutcome.Unauthorized,
                422 => SystemOneOutcome.Rejected,
                429 => SystemOneOutcome.RateLimited,
                529 => SystemOneOutcome.Overloaded,
                _ => SystemOneOutcome.TransportFailure,
            };
            return new SystemOneResult(outcome, null, $"HTTP {(int)response.StatusCode}: {Truncate(body)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            return new SystemOneResult(SystemOneOutcome.TransportFailure, null, ex.Message);
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200];
}
```

- [ ] **Step 5: Add the IL-level leaf rule**

In `tests/Ntilde.Architecture.Tests/LayeringTests.cs`, add an accessor beside the others:

```csharp
    private static Assembly Inference => typeof(global::Ntilde.Inference.SystemOneClient).Assembly;
```

and a fact after `CommandAssist_must_not_depend_on_networking`:

```csharp
    /// <summary>
    /// Inference is the one assembly allowed to talk to a remote model, so it must not be able
    /// to reach anything worth leaking: no UI, no settings, no vault, no command history.
    /// </summary>
    [Fact]
    public void Inference_must_be_a_leaf_assembly()
    {
        var result = Types.InAssembly(Inference)
            .Should()
            .NotHaveDependencyOnAny(
                "Avalonia",
                "SkiaSharp",
                "Ntilde.Shell",
                "Ntilde.Controls",
                "Ntilde.AgentHost",
                "Ntilde.CommandAssist",
                "Ntilde.Platform",
                "Ntilde.VT",
                "Ntilde.Pty",
                "Ntilde.Replay",
                "Ntilde.Rendering")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Inference must stay a leaf. Offenders: {Join(result.FailingTypeNames)}");
    }
```

- [ ] **Step 6: Run the tests**

PowerShell: `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~SystemOneClientTests" --blame-hang-timeout 5m` then `scripts/build.ps1 test tests/Ntilde.Architecture.Tests`
Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Ntilde.Inference tests/Ntilde.App.Tests/Inference tests/Ntilde.Architecture.Tests/LayeringTests.cs
git commit -m "feat(inference): System One client with typed outcomes

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 3: `ScreenActivityClassifier` and its contract types

**Files:**
- Create: `src/Ntilde.Inference/ScreenActivity.cs`
- Create: `src/Ntilde.Inference/ScreenActivityClassifier.cs`
- Modify: `src/Ntilde.Inference/InferenceJsonContext.cs` (add `ScreenState`)
- Test: `tests/Ntilde.App.Tests/Inference/ScreenActivityClassifierTests.cs`

**Interfaces:**
- Consumes: `SystemOneClient`, `SystemOneRequest`, `SystemOneResult` (Task 2).
- Produces (namespace `Ntilde.Inference`):
  - `sealed record ScreenSample(string Text, int Rows, int Cols)`
  - `enum ScreenActivity { CommandRunning, AgentWorking, WaitingForUser, IdleShellPrompt, UnknownBlank }`
  - `sealed record ScreenActivityAnswer(ScreenActivity Activity, double Confidence, double NeedsAttention, double LastCommandFailed, int InputTokens)`
  - `enum ScreenClassificationOutcome { Answered, NoKey, Unauthorized, RateLimited, Overloaded, Rejected, TransportFailure, Malformed }`
  - `sealed record ScreenClassificationResult(ScreenClassificationOutcome Outcome, ScreenActivityAnswer? Answer, string? Detail)`
  - `interface IScreenActivityClassifier { bool HasCredentials { get; } Task<ScreenClassificationResult> ClassifyAsync(ScreenSample sample, CancellationToken ct); }`
  - `ScreenActivityClassifier(SystemOneClient client)`; `internal static SystemOneRequest BuildRequest(ScreenSample sample)`; `internal static ScreenActivityAnswer? MapAnswer(SystemOneResponse response)`.
  - `sealed class ScreenState { string Screen; int Rows; int Cols }` (JSON names `screen`, `rows`, `cols`).

- [ ] **Step 1: Write the failing tests**

`tests/Ntilde.App.Tests/Inference/ScreenActivityClassifierTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.Inference;

namespace Ntilde.AppTests.Inference;

public class ScreenActivityClassifierTests
{
    private sealed class FixedKey(string? key) : IApiKeySource { public string? TryGetKey() => key; }

    private sealed class CannedHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static ScreenActivityClassifier Make(HttpStatusCode code, string body, string? key = "k")
        => new(new SystemOneClient(new HttpClient(new CannedHandler(code, body)), new FixedKey(key)));

    private static SystemOneResponse Response(string choice, double confidence, double attention, double failed) => new()
    {
        Answers = new Dictionary<string, SystemOneAnswer>
        {
            [ScreenActivityClassifier.ActivityQuestionId] = new() { Type = "choice", Choice = choice, Confidence = confidence },
            [ScreenActivityClassifier.AttentionQuestionId] = new() { Type = "noul", Noul = attention },
            [ScreenActivityClassifier.FailedQuestionId] = new() { Type = "noul", Noul = failed },
        },
        Usage = new SystemOneUsage { InputTokens = 900, OutputTokens = 50 },
    };

    [Fact]
    public void Request_has_exactly_screen_rows_cols_and_three_questions()
    {
        var request = ScreenActivityClassifier.BuildRequest(new ScreenSample("user@host:~$ ", 24, 80));

        Assert.Equal(SystemOneClient.DefaultModel, request.Model);
        var props = request.State.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "cols", "rows", "screen" }, props);
        Assert.Equal("user@host:~$ ", request.State.GetProperty("screen").GetString());
        Assert.Equal(24, request.State.GetProperty("rows").GetInt32());
        Assert.Equal(80, request.State.GetProperty("cols").GetInt32());

        Assert.Equal(3, request.Questions.Count);
        var activity = request.Questions[ScreenActivityClassifier.ActivityQuestionId];
        Assert.Equal("choice", activity.Type);
        Assert.Equal(
            new[] { "agent_working", "command_running", "idle_shell_prompt", "unknown_blank", "waiting_for_user" },
            activity.Criteria!.Keys.OrderBy(k => k).ToArray());
        Assert.Equal("noul", request.Questions[ScreenActivityClassifier.AttentionQuestionId].Type);
        Assert.Equal("noul", request.Questions[ScreenActivityClassifier.FailedQuestionId].Type);
    }

    [Fact]
    public void Criteria_texts_are_non_empty_and_distinct()
    {
        var activity = ScreenActivityClassifier.BuildRequest(new ScreenSample("x", 1, 1)).Questions[ScreenActivityClassifier.ActivityQuestionId];
        var texts = activity.Criteria!.Values.ToArray();
        Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t)));
        Assert.Equal(texts.Length, texts.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("command_running", ScreenActivity.CommandRunning)]
    [InlineData("agent_working", ScreenActivity.AgentWorking)]
    [InlineData("waiting_for_user", ScreenActivity.WaitingForUser)]
    [InlineData("idle_shell_prompt", ScreenActivity.IdleShellPrompt)]
    [InlineData("unknown_blank", ScreenActivity.UnknownBlank)]
    public void Maps_each_choice_to_the_enum(string choice, ScreenActivity expected)
    {
        var answer = ScreenActivityClassifier.MapAnswer(Response(choice, 0.9, 0.2, 0.1));

        Assert.NotNull(answer);
        Assert.Equal(expected, answer!.Activity);
        Assert.Equal(0.9, answer.Confidence, 3);
        Assert.Equal(0.2, answer.NeedsAttention, 3);
        Assert.Equal(0.1, answer.LastCommandFailed, 3);
        Assert.Equal(900, answer.InputTokens);
    }

    [Fact]
    public void Unknown_choice_or_missing_question_maps_to_null()
    {
        Assert.Null(ScreenActivityClassifier.MapAnswer(Response("something_else", 0.9, 0.2, 0.1)));

        var missing = Response("waiting_for_user", 0.9, 0.2, 0.1);
        missing.Answers.Remove(ScreenActivityClassifier.AttentionQuestionId);
        Assert.Null(ScreenActivityClassifier.MapAnswer(missing));
    }

    [Fact]
    public async Task Classify_returns_answered_on_success()
    {
        var body = JsonSerializer.Serialize(Response("agent_working", 0.88, 0.24, 0.08), InferenceJsonContext.Default.SystemOneResponse);
        var classifier = Make(HttpStatusCode.OK, body);

        var result = await classifier.ClassifyAsync(new ScreenSample("✻ Crunching…", 34, 101), CancellationToken.None);

        Assert.Equal(ScreenClassificationOutcome.Answered, result.Outcome);
        Assert.Equal(ScreenActivity.AgentWorking, result.Answer!.Activity);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ScreenClassificationOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, ScreenClassificationOutcome.RateLimited)]
    [InlineData((HttpStatusCode)529, ScreenClassificationOutcome.Overloaded)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ScreenClassificationOutcome.Rejected)]
    [InlineData(HttpStatusCode.BadGateway, ScreenClassificationOutcome.TransportFailure)]
    public async Task Classify_forwards_client_outcomes(HttpStatusCode code, ScreenClassificationOutcome expected)
    {
        var classifier = Make(code, "{}");

        var result = await classifier.ClassifyAsync(new ScreenSample("x", 1, 1), CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Answer);
    }

    [Fact]
    public async Task Unmappable_success_is_malformed_and_no_key_is_no_key()
    {
        var bad = JsonSerializer.Serialize(Response("nope", 0.5, 0.5, 0.5), InferenceJsonContext.Default.SystemOneResponse);
        Assert.Equal(ScreenClassificationOutcome.Malformed,
            (await Make(HttpStatusCode.OK, bad).ClassifyAsync(new ScreenSample("x", 1, 1), CancellationToken.None)).Outcome);

        var noKey = Make(HttpStatusCode.OK, "{}", key: null);
        Assert.False(noKey.HasCredentials);
        Assert.Equal(ScreenClassificationOutcome.NoKey,
            (await noKey.ClassifyAsync(new ScreenSample("x", 1, 1), CancellationToken.None)).Outcome);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

PowerShell: `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~ScreenActivityClassifierTests" --blame-hang-timeout 5m`
Expected: FAIL to compile.

- [ ] **Step 3: Write the contract types**

`src/Ntilde.Inference/ScreenActivity.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Ntilde.Inference;

/// <summary>What the classifier is shown: the redacted visible text of one pane and its size. Nothing else.</summary>
public sealed record ScreenSample(string Text, int Rows, int Cols);

/// <summary>The JSON state object sent as <c>state</c>. Exactly three fields, by design.</summary>
public sealed class ScreenState
{
    [JsonPropertyName("screen")]
    public string Screen { get; set; } = string.Empty;

    [JsonPropertyName("rows")]
    public int Rows { get; set; }

    [JsonPropertyName("cols")]
    public int Cols { get; set; }
}

public enum ScreenActivity
{
    CommandRunning,
    AgentWorking,
    WaitingForUser,
    IdleShellPrompt,
    UnknownBlank,
}

public sealed record ScreenActivityAnswer(
    ScreenActivity Activity,
    double Confidence,
    double NeedsAttention,
    double LastCommandFailed,
    int InputTokens);

public enum ScreenClassificationOutcome
{
    Answered,
    NoKey,
    Unauthorized,
    RateLimited,
    Overloaded,
    Rejected,
    TransportFailure,
    Malformed,
}

public sealed record ScreenClassificationResult(ScreenClassificationOutcome Outcome, ScreenActivityAnswer? Answer, string? Detail);

public interface IScreenActivityClassifier
{
    /// <summary>False when no key is configured, so callers can skip capturing a screen at all.</summary>
    bool HasCredentials { get; }

    Task<ScreenClassificationResult> ClassifyAsync(ScreenSample sample, CancellationToken cancellationToken);
}
```

Add to `InferenceJsonContext.cs` attributes: `[JsonSerializable(typeof(ScreenState))]`.

- [ ] **Step 4: Write the classifier**

`src/Ntilde.Inference/ScreenActivityClassifier.cs`:

```csharp
using System.Text.Json;

namespace Ntilde.Inference;

/// <summary>
/// The three questions asked of a pane's visible text, and the mapping of the answer onto
/// <see cref="ScreenActivityAnswer"/>. The wording is the one measured on 2026-09-17 (8/8 on
/// live and constructed screens); tune it here, with the tests, never inline at a call site.
/// </summary>
public sealed class ScreenActivityClassifier : IScreenActivityClassifier
{
    public const string ActivityQuestionId = "activity";
    public const string AttentionQuestionId = "needs_attention";
    public const string FailedQuestionId = "last_command_failed";

    internal const string ChoiceCommandRunning = "command_running";
    internal const string ChoiceAgentWorking = "agent_working";
    internal const string ChoiceWaitingForUser = "waiting_for_user";
    internal const string ChoiceIdleShellPrompt = "idle_shell_prompt";
    internal const string ChoiceUnknownBlank = "unknown_blank";

    private readonly SystemOneClient _client;

    public ScreenActivityClassifier(SystemOneClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public bool HasCredentials => _client.HasCredentials;

    public async Task<ScreenClassificationResult> ClassifyAsync(ScreenSample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var result = await _client.EvaluateAsync(BuildRequest(sample), cancellationToken).ConfigureAwait(false);
        if (result.Outcome != SystemOneOutcome.Ok || result.Response == null)
        {
            return new ScreenClassificationResult(Map(result.Outcome), null, result.Detail);
        }

        var answer = MapAnswer(result.Response);
        return answer == null
            ? new ScreenClassificationResult(ScreenClassificationOutcome.Malformed, null, "answer did not match the question set")
            : new ScreenClassificationResult(ScreenClassificationOutcome.Answered, answer, null);
    }

    internal static SystemOneRequest BuildRequest(ScreenSample sample)
    {
        var state = new ScreenState { Screen = sample.Text, Rows = sample.Rows, Cols = sample.Cols };
        return new SystemOneRequest
        {
            Model = SystemOneClient.DefaultModel,
            State = JsonSerializer.SerializeToElement(state, InferenceJsonContext.Default.ScreenState),
            Questions = new Dictionary<string, SystemOneQuestion>
            {
                [ActivityQuestionId] = new()
                {
                    Type = "choice",
                    Instructions = "Look at `screen`, the visible text of a terminal pane (last line is the bottom). What is happening in this pane right now?",
                    Criteria = new Dictionary<string, string>
                    {
                        [ChoiceCommandRunning] = "a shell command or build is still in progress: progress output, spinners, log lines, and NO shell prompt or input box as the final content",
                        [ChoiceAgentWorking] = "an AI coding agent (Claude Code, Codex, Aider, etc.) is actively thinking or running tools: a 'thinking/crunching' spinner with elapsed time, tool-call lines, no idle input box",
                        [ChoiceWaitingForUser] = "a program is stopped and waits for the user to type: a yes/no question, a password prompt, a pager, or an AI agent that finished its answer and shows an empty input box",
                        [ChoiceIdleShellPrompt] = "the shell prompt is the last thing on screen with nothing typed after it; nothing is running",
                        [ChoiceUnknownBlank] = "the screen is empty or has no readable content to decide from",
                    },
                },
                [AttentionQuestionId] = new()
                {
                    Type = "noul",
                    Instructions = "A user stepped away from this pane. Based on `screen`, does the pane now need them to come back and act (answer a question, review a finished result, fix an error)? Idle shell prompts with nothing new do NOT need attention.",
                    Criteria = new Dictionary<string, string>
                    {
                        ["true"] = "something finished or is asking for input and the user has not responded",
                        ["false"] = "nothing is waiting on the user; idle prompt or still working",
                    },
                },
                [FailedQuestionId] = new()
                {
                    Type = "noul",
                    Instructions = "Did the MOST RECENT completed command in `screen` end with an error? Judge only the last command, not earlier ones.",
                    Criteria = new Dictionary<string, string>
                    {
                        ["true"] = "the final command's output is an error/failure message",
                        ["false"] = "the final command succeeded or there is no command output",
                    },
                },
            },
        };
    }

    internal static ScreenActivityAnswer? MapAnswer(SystemOneResponse response)
    {
        if (!response.Answers.TryGetValue(ActivityQuestionId, out var activity) ||
            !response.Answers.TryGetValue(AttentionQuestionId, out var attention) ||
            !response.Answers.TryGetValue(FailedQuestionId, out var failed))
        {
            return null;
        }

        ScreenActivity? kind = activity.Choice switch
        {
            ChoiceCommandRunning => ScreenActivity.CommandRunning,
            ChoiceAgentWorking => ScreenActivity.AgentWorking,
            ChoiceWaitingForUser => ScreenActivity.WaitingForUser,
            ChoiceIdleShellPrompt => ScreenActivity.IdleShellPrompt,
            ChoiceUnknownBlank => ScreenActivity.UnknownBlank,
            _ => null,
        };
        if (kind == null || activity.Confidence is not { } confidence || attention.Noul is not { } needs || failed.Noul is not { } fail)
        {
            return null;
        }

        return new ScreenActivityAnswer(kind.Value, confidence, needs, fail, response.Usage?.InputTokens ?? 0);
    }

    private static ScreenClassificationOutcome Map(SystemOneOutcome outcome) => outcome switch
    {
        SystemOneOutcome.NoKey => ScreenClassificationOutcome.NoKey,
        SystemOneOutcome.Unauthorized => ScreenClassificationOutcome.Unauthorized,
        SystemOneOutcome.RateLimited => ScreenClassificationOutcome.RateLimited,
        SystemOneOutcome.Overloaded => ScreenClassificationOutcome.Overloaded,
        SystemOneOutcome.Rejected => ScreenClassificationOutcome.Rejected,
        SystemOneOutcome.Malformed => ScreenClassificationOutcome.Malformed,
        _ => ScreenClassificationOutcome.TransportFailure,
    };
}
```

- [ ] **Step 5: Run the tests**

PowerShell: `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~ScreenActivityClassifierTests" --blame-hang-timeout 5m`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Ntilde.Inference tests/Ntilde.App.Tests/Inference
git commit -m "feat(inference): screen activity classifier with the measured question set

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Observed tier in `AgentSessionStatusMachine`

**Files:**
- Modify: `src/Ntilde.AgentHost.Contracts/AgentHostProtocol.cs:146-154`
- Modify: `src/Ntilde.App/AgentHost/AgentSessionStatus.cs`
- Create: `src/Ntilde.App/AgentHost/ScreenObservation.cs`
- Modify: `src/Ntilde.App/AgentHost/AgentSessionStatusMachine.cs`
- Modify: `src/Ntilde.App/AgentHost/AgentStatusWire.cs:22-27`
- Test: `tests/Ntilde.App.Tests/AgentHost/AgentSessionStatusMachineObservedTests.cs`

**Interfaces:**
- Consumes: `ScreenActivity` (Task 3).
- Produces:
  - `AgentHostProtocol.StatusConfidences.Observed = "observed"`.
  - `AgentSessionStatusConfidence.Observed`.
  - `ScreenObservation { ScreenActivity Activity; double Confidence; double NeedsAttention; double LastCommandFailed; DateTimeOffset ObservedAt; long OutputSequence }`.
  - `AgentSessionStatusMachine.ObservedOverrideThreshold = 0.85` (`public const double`), `bool NotifyObserved(ScreenObservation)`, snapshot fields `long OutputSequence`, `ScreenObservation? Observation` (fresh only), `long? ObservationAgeMs`, `int ObservedOverrideThresholdPercent`.

- [ ] **Step 1: Write the failing tests**

`tests/Ntilde.App.Tests/AgentHost/AgentSessionStatusMachineObservedTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Ntilde.AgentHost;
using Ntilde.Inference;

namespace Ntilde.AppTests.AgentHost;

/// <summary>
/// The observed tier (docs/superpowers/specs/2026-09-17-screen-inference-observed-status-design.md §1.3).
/// A fake clock drives thresholds; observations are handed in directly, no network.
/// </summary>
public class AgentSessionStatusMachineObservedTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => Now += by;
        public Func<DateTimeOffset> Provider => () => Now;
    }

    private static (AgentSessionStatusMachine Machine, FakeClock Clock, List<AgentSessionStatusEvent> Events) Make()
    {
        var clock = new FakeClock();
        var machine = new AgentSessionStatusMachine(clock.Provider);
        var events = new List<AgentSessionStatusEvent>();
        machine.EventEmitted += events.Add;
        return (machine, clock, events);
    }

    private static ScreenObservation Obs(AgentSessionStatusMachine machine, ScreenActivity activity, double confidence = 0.95, double attention = 0.5)
        => new()
        {
            Activity = activity,
            Confidence = confidence,
            NeedsAttention = attention,
            LastCommandFailed = 0.05,
            ObservedAt = machine.Snapshot().LastOutputAt,
            OutputSequence = machine.Snapshot().OutputSequence,
        };

    [Fact]
    public void Output_increments_the_sequence()
    {
        var (machine, _, _) = Make();
        var before = machine.Snapshot().OutputSequence;
        machine.NotifyOutput();
        Assert.Equal(before + 1, machine.Snapshot().OutputSequence);
    }

    [Fact]
    public void Heuristic_tier_takes_a_confident_running_observation()
    {
        var (machine, _, _) = Make();
        machine.Sweep(hasActiveChildProcesses: false); // heuristic says nothing is running

        Assert.True(machine.NotifyObserved(Obs(machine, ScreenActivity.CommandRunning)));

        var s = machine.Snapshot();
        Assert.Equal(AgentSessionStatusKind.Running, s.Kind);
        Assert.Equal(AgentSessionStatusConfidence.Observed, s.Confidence);
        Assert.NotNull(s.Observation);
    }

    [Fact]
    public void Heuristic_tier_takes_waiting_and_idle_promotion_still_applies()
    {
        var (machine, clock, _) = Make();
        machine.Sweep(hasActiveChildProcesses: true); // heuristic would say running

        machine.NotifyObserved(Obs(machine, ScreenActivity.WaitingForUser));
        Assert.Equal(AgentSessionStatusKind.AwaitingInput, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Observed, machine.Snapshot().Confidence);

        clock.Advance(TimeSpan.FromSeconds(AgentSessionStatusMachine.IdleThresholdSeconds));
        Assert.Equal(AgentSessionStatusKind.Idle, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Observed, machine.Snapshot().Confidence);
    }

    [Fact]
    public void Below_threshold_observation_is_ignored()
    {
        var (machine, _, _) = Make();
        machine.Sweep(hasActiveChildProcesses: true);

        machine.NotifyObserved(Obs(machine, ScreenActivity.WaitingForUser, confidence: AgentSessionStatusMachine.ObservedOverrideThreshold - 0.01));

        Assert.Equal(AgentSessionStatusKind.Running, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Heuristic, machine.Snapshot().Confidence);
    }

    [Fact]
    public void Threshold_is_inclusive()
    {
        var (machine, _, _) = Make();
        machine.Sweep(hasActiveChildProcesses: true);

        machine.NotifyObserved(Obs(machine, ScreenActivity.WaitingForUser, confidence: AgentSessionStatusMachine.ObservedOverrideThreshold));

        Assert.Equal(AgentSessionStatusConfidence.Observed, machine.Snapshot().Confidence);
    }

    [Fact]
    public void Unknown_blank_is_ignored()
    {
        var (machine, _, _) = Make();
        machine.Sweep(hasActiveChildProcesses: true);

        machine.NotifyObserved(Obs(machine, ScreenActivity.UnknownBlank));

        Assert.Equal(AgentSessionStatusKind.Running, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Heuristic, machine.Snapshot().Confidence);
    }

    [Fact]
    public void New_output_makes_the_observation_stale()
    {
        var (machine, _, _) = Make();
        machine.Sweep(hasActiveChildProcesses: true);
        machine.NotifyObserved(Obs(machine, ScreenActivity.WaitingForUser));
        Assert.Equal(AgentSessionStatusKind.AwaitingInput, machine.Snapshot().Kind);

        machine.NotifyOutput();

        var s = machine.Snapshot();
        Assert.Equal(AgentSessionStatusKind.Running, s.Kind);
        Assert.Equal(AgentSessionStatusConfidence.Heuristic, s.Confidence);
        Assert.Null(s.Observation);
    }

    [Fact]
    public void Observation_from_an_older_sequence_is_rejected()
    {
        var (machine, _, _) = Make();
        var stale = Obs(machine, ScreenActivity.WaitingForUser);
        machine.NotifyOutput();

        Assert.False(machine.NotifyObserved(stale));
        Assert.Null(machine.Snapshot().Observation);
    }

    [Fact]
    public void Precise_running_command_is_refined_to_awaiting_input_only_for_waiting_for_user()
    {
        var (machine, _, _) = Make();
        machine.NotifyPromptReady();
        machine.NotifyCommandStarted();

        machine.NotifyObserved(Obs(machine, ScreenActivity.CommandRunning));
        Assert.Equal(AgentSessionStatusKind.Running, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Precise, machine.Snapshot().Confidence);

        machine.NotifyObserved(Obs(machine, ScreenActivity.WaitingForUser));
        Assert.Equal(AgentSessionStatusKind.AwaitingInput, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Observed, machine.Snapshot().Confidence);
    }

    [Fact]
    public void Precise_prompt_is_never_overridden()
    {
        var (machine, _, _) = Make();
        machine.NotifyPromptReady();

        machine.NotifyObserved(Obs(machine, ScreenActivity.CommandRunning));

        Assert.Equal(AgentSessionStatusKind.AwaitingInput, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Precise, machine.Snapshot().Confidence);
    }

    [Fact]
    public void Alt_screen_and_exit_beat_any_observation()
    {
        var (machine, _, _) = Make();
        machine.NotifyAltScreenChanged(true);
        machine.NotifyObserved(Obs(machine, ScreenActivity.WaitingForUser));
        Assert.Equal(AgentSessionStatusKind.Running, machine.Snapshot().Kind);
        Assert.Equal(AgentSessionStatusConfidence.Heuristic, machine.Snapshot().Confidence);

        machine.NotifyAltScreenChanged(false);
        machine.NotifyExited(0);
        machine.NotifyObserved(Obs(machine, ScreenActivity.CommandRunning));
        Assert.Equal(AgentSessionStatusKind.Exited, machine.Snapshot().Kind);
    }

    [Fact]
    public void Observed_running_still_stalls_after_the_stall_threshold()
    {
        var (machine, clock, events) = Make();
        machine.Sweep(hasActiveChildProcesses: false);
        machine.NotifyObserved(Obs(machine, ScreenActivity.AgentWorking));
        Assert.Equal(AgentSessionStatusKind.Running, machine.Snapshot().Kind);

        clock.Advance(TimeSpan.FromSeconds(AgentSessionStatusMachine.StallThresholdSeconds));
        machine.Sweep(hasActiveChildProcesses: false);

        Assert.Contains(events, e => e.Type == AgentSessionEventType.Stalled);
        Assert.True(machine.Snapshot().IsStalled);
    }

    [Fact]
    public void Status_changed_event_fires_when_an_observation_changes_the_kind()
    {
        var (machine, _, events) = Make();
        machine.Sweep(hasActiveChildProcesses: false);
        events.Clear();

        machine.NotifyObserved(Obs(machine, ScreenActivity.CommandRunning));

        var evt = Assert.Single(events, e => e.Type == AgentSessionEventType.StatusChanged);
        Assert.Equal(AgentSessionStatusKind.Running, evt.Status);
    }

    [Fact]
    public void Snapshot_reports_age_and_threshold()
    {
        var (machine, clock, _) = Make();
        machine.NotifyObserved(Obs(machine, ScreenActivity.IdleShellPrompt));
        clock.Advance(TimeSpan.FromMilliseconds(1500));

        var s = machine.Snapshot();
        Assert.Equal(1500, s.ObservationAgeMs);
        Assert.Equal(85, s.ObservedOverrideThresholdPercent);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

PowerShell: `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~AgentSessionStatusMachineObservedTests" --blame-hang-timeout 5m`
Expected: FAIL to compile.

- [ ] **Step 3: Add the wire constant**

`src/Ntilde.AgentHost.Contracts/AgentHostProtocol.cs`, inside `StatusConfidences` after `Heuristic`:

```csharp
        /// <summary>
        /// A judgment over the pane's visible text decided the status (opt-in screen
        /// inference). Overrides the heuristic tier, and refines a precise running
        /// command into awaitingInput when a program inside it is waiting on the user.
        /// </summary>
        public const string Observed = "observed";
```

- [ ] **Step 4: Add the enum value and the observation record**

`src/Ntilde.App/AgentHost/AgentSessionStatus.cs`: change the confidence enum and the snapshot.

```csharp
    public enum AgentSessionStatusConfidence
    {
        Heuristic,
        Precise,
        /// <summary>A screen observation decided the kind (see <see cref="ScreenObservation"/>).</summary>
        Observed,
    }
```

Add to `AgentSessionStatusSnapshot`:

```csharp
        /// <summary>Monotonic count of output notifications; observations are fresh only while it matches theirs.</summary>
        public long OutputSequence { get; init; }

        /// <summary>The current observation, only when still fresh (no output since it was captured).</summary>
        public ScreenObservation? Observation { get; init; }

        /// <summary>Age of <see cref="Observation"/> at snapshot time, or null.</summary>
        public long? ObservationAgeMs { get; init; }

        public int ObservedOverrideThresholdPercent { get; init; } = (int)Math.Round(AgentSessionStatusMachine.ObservedOverrideThreshold * 100);
```

`src/Ntilde.App/AgentHost/ScreenObservation.cs`:

```csharp
using System;
using Ntilde.Inference;

namespace Ntilde.AgentHost
{
    /// <summary>
    /// One judgment over a pane's visible text, as handed to
    /// <see cref="AgentSessionStatusMachine.NotifyObserved"/>. <see cref="OutputSequence"/> is the
    /// machine's output counter at the moment the screen was captured; the machine consults the
    /// observation only while no output has arrived since.
    /// </summary>
    public sealed record ScreenObservation
    {
        public required ScreenActivity Activity { get; init; }
        public required double Confidence { get; init; }
        public required double NeedsAttention { get; init; }
        public required double LastCommandFailed { get; init; }
        public required DateTimeOffset ObservedAt { get; init; }
        public required long OutputSequence { get; init; }
    }
}
```

- [ ] **Step 5: Add the signal and the rules to the machine**

In `src/Ntilde.App/AgentHost/AgentSessionStatusMachine.cs`:

Add the constant after `IdleThresholdSeconds`:

```csharp
        /// <summary>Minimum Choice confidence for a screen observation to decide the kind.</summary>
        public const double ObservedOverrideThreshold = 0.85;
```

Add fields after `_commandStartedAt`:

```csharp
        private long _outputSequence;
        private ScreenObservation? _observation;
```

In `NotifyOutput`, as the first line inside the lambda: `_outputSequence++;`

Add the signal after `NotifyExited`:

```csharp
        /// <summary>
        /// Stores a screen observation. Returns false and stores nothing when output has
        /// arrived since the screen was captured (the observation is already stale).
        /// Any thread.
        /// </summary>
        public bool NotifyObserved(ScreenObservation observation)
        {
            ArgumentNullException.ThrowIfNull(observation);
            bool accepted = false;
            RunUnderGate(_ =>
            {
                if (observation.OutputSequence != _outputSequence) return null;
                _observation = observation;
                accepted = true;
                return null;
            });
            return accepted;
        }
```

Replace `ComputeKind` with the pair below, and keep `ComputeKind` as a thin wrapper so every existing call site compiles:

```csharp
        private AgentSessionStatusKind ComputeKind(DateTimeOffset now) => Compute(now).Kind;

        private (AgentSessionStatusKind Kind, AgentSessionStatusConfidence Confidence) Compute(DateTimeOffset now)
        {
            var baseConfidence = _precise ? AgentSessionStatusConfidence.Precise : AgentSessionStatusConfidence.Heuristic;
            if (_exited) return (AgentSessionStatusKind.Exited, baseConfidence);
            if (_altScreenActive) return (AgentSessionStatusKind.Running, baseConfidence);

            var fresh = FreshObservation();
            bool confident = fresh != null && fresh.Confidence >= ObservedOverrideThreshold;

            if (_precise)
            {
                if (!_commandInFlight) return (PromptKind(now), AgentSessionStatusConfidence.Precise);
                // The one precise override: a program inside the running command is waiting on the user.
                if (confident && fresh!.Activity == ScreenActivity.WaitingForUser)
                {
                    return (PromptKind(now), AgentSessionStatusConfidence.Observed);
                }
                return (AgentSessionStatusKind.Running, AgentSessionStatusConfidence.Precise);
            }

            if (confident && fresh!.Activity != ScreenActivity.UnknownBlank)
            {
                return fresh.Activity is ScreenActivity.CommandRunning or ScreenActivity.AgentWorking
                    ? (AgentSessionStatusKind.Running, AgentSessionStatusConfidence.Observed)
                    : (PromptKind(now), AgentSessionStatusConfidence.Observed);
            }

            return _hasActiveChildren
                ? (AgentSessionStatusKind.Running, AgentSessionStatusConfidence.Heuristic)
                : (PromptKind(now), AgentSessionStatusConfidence.Heuristic);
        }

        private AgentSessionStatusKind PromptKind(DateTimeOffset now)
            => now - _lastOutputAt >= TimeSpan.FromSeconds(IdleThresholdSeconds)
                ? AgentSessionStatusKind.Idle
                : AgentSessionStatusKind.AwaitingInput;

        private ScreenObservation? FreshObservation()
            => _observation is { } o && o.OutputSequence == _outputSequence ? o : null;
```

Replace the body of `Snapshot()`:

```csharp
            lock (_gate)
            {
                var now = _now();
                var (kind, confidence) = Compute(now);
                var fresh = FreshObservation();
                return new AgentSessionStatusSnapshot
                {
                    Kind = kind,
                    Confidence = confidence,
                    ExitCode = _exitCode,
                    CurrentCommand = _currentCommand,
                    StatusSince = _statusSince,
                    LastOutputAt = _lastOutputAt,
                    IsStalled = _stalled,
                    OutputSequence = _outputSequence,
                    Observation = fresh,
                    ObservationAgeMs = fresh == null ? null : (long)(now - fresh.ObservedAt).TotalMilliseconds,
                };
            }
```

Add `using Ntilde.Inference;` at the top of the file.

- [ ] **Step 6: Map the new tier on the wire**

`src/Ntilde.App/AgentHost/AgentStatusWire.cs`, in the confidence switch:

```csharp
            AgentSessionStatusConfidence.Observed => AgentHostProtocol.StatusConfidences.Observed,
```

- [ ] **Step 7: Run the new and the existing machine tests**

PowerShell: `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~AgentSessionStatusMachine" --blame-hang-timeout 5m`
Expected: all PASS (both the new class and the pre-existing `AgentSessionStatusMachineTests`).

- [ ] **Step 8: Commit**

```bash
git add src/Ntilde.AgentHost.Contracts/AgentHostProtocol.cs src/Ntilde.App/AgentHost tests/Ntilde.App.Tests/AgentHost/AgentSessionStatusMachineObservedTests.cs
git commit -m "feat(agent-host): observed confidence tier in the session status machine

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 5: Wire DTO fields and the MCP status formatter

**Files:**
- Modify: `src/Ntilde.AgentHost.Contracts/AgentHostProtocol.cs` (new `ObservedActivities` class after `StatusConfidences`)
- Modify: `src/Ntilde.AgentHost.Contracts/StatusContracts.cs:12-51`
- Modify: `src/Ntilde.AgentHost.Contracts/AgentHostJsonContext.cs`
- Modify: `src/Ntilde.App/AgentHost/AgentStatusWire.cs` (`ToDto`, new `ToWire(ScreenActivity)`)
- Modify: `src/Ntilde.McpServer/Tools/SessionTools.cs:162-163, 391-402`
- Test: `tests/Ntilde.McpServer.Tests/AgentHostClientTests.cs` (add two facts)

**Interfaces:**
- Consumes: snapshot fields from Task 4.
- Produces:
  - `AgentHostProtocol.ObservedActivities { CommandRunning="commandRunning", AgentWorking="agentWorking", WaitingForUser="waitingForUser", IdleShellPrompt="idleShellPrompt", UnknownBlank="unknownBlank" }`
  - `SessionObservationDto { string Activity; double Confidence; double NeedsAttention; double LastCommandFailed; long AgeMs }` (JSON: `activity`, `confidence`, `needsAttention`, `lastCommandFailed`, `ageMs`)
  - `SessionStatusDto.ObservedOverrideThresholdPercent` (int, default 85, JSON `observedOverrideThresholdPercent`) and `SessionStatusDto.Observation` (nullable, JSON `observation`).

- [ ] **Step 1: Write the failing formatter tests**

Add to `tests/Ntilde.McpServer.Tests/AgentHostClientTests.cs` after `FormatStatus_renders_command_stall_and_thresholds`:

```csharp
    [Fact]
    public void FormatStatus_renders_an_observation_line_when_present()
    {
        var text = SessionTools.FormatStatus(new SessionStatusDto
        {
            PaneId = Guid.NewGuid(),
            Status = AgentHostProtocol.StatusKinds.AwaitingInput,
            Confidence = AgentHostProtocol.StatusConfidences.Observed,
            StatusSinceMs = 1_800_000_000_000,
            LastOutputAtMs = 1_800_000_030_000,
            IsStalled = false,
            StallThresholdSeconds = 30,
            IdleThresholdSeconds = 60,
            ObservedOverrideThresholdPercent = 85,
            Observation = new SessionObservationDto
            {
                Activity = AgentHostProtocol.ObservedActivities.WaitingForUser,
                Confidence = 0.92,
                NeedsAttention = 0.79,
                LastCommandFailed = 0.04,
                AgeMs = 1830,
            },
        });

        Assert.Contains("awaitingInput (observed confidence)", text, StringComparison.Ordinal);
        Assert.Contains("Observed: waitingForUser (0.92)", text, StringComparison.Ordinal);
        Assert.Contains("attention 0.79", text, StringComparison.Ordinal);
        Assert.Contains("last command failed 0.04", text, StringComparison.Ordinal);
        Assert.Contains("1.8s ago", text, StringComparison.Ordinal);
        Assert.Contains("override at 85%", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatStatus_omits_the_observation_line_when_absent()
    {
        var text = SessionTools.FormatStatus(new SessionStatusDto
        {
            PaneId = Guid.NewGuid(),
            Status = AgentHostProtocol.StatusKinds.Idle,
            Confidence = AgentHostProtocol.StatusConfidences.Heuristic,
            StatusSinceMs = 1_800_000_000_000,
            LastOutputAtMs = 1_800_000_000_000,
            IsStalled = false,
            StallThresholdSeconds = 30,
            IdleThresholdSeconds = 60,
        });

        Assert.DoesNotContain("Observed:", text, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run to verify it fails**

PowerShell: `scripts/build.ps1 test tests/Ntilde.McpServer.Tests --filter "FullyQualifiedName~FormatStatus"`
Expected: FAIL to compile (`SessionObservationDto`, `Observation` missing).

- [ ] **Step 3: Add the wire constants and DTO**

`src/Ntilde.AgentHost.Contracts/AgentHostProtocol.cs`, after the `StatusConfidences` class:

```csharp
    /// <summary>Wire values for <c>observation.activity</c> (screen inference).</summary>
    public static class ObservedActivities
    {
        public const string CommandRunning = "commandRunning";
        public const string AgentWorking = "agentWorking";
        public const string WaitingForUser = "waitingForUser";
        public const string IdleShellPrompt = "idleShellPrompt";
        public const string UnknownBlank = "unknownBlank";
    }
```

`src/Ntilde.AgentHost.Contracts/StatusContracts.cs`, add to `SessionStatusDto` after `IdleThresholdSeconds`:

```csharp
    /// <summary>Minimum observation confidence (percent) for the observed tier to decide the status.</summary>
    [JsonPropertyName("observedOverrideThresholdPercent")]
    public int ObservedOverrideThresholdPercent { get; init; } = 85;

    /// <summary>The current screen observation, omitted when none is fresh.</summary>
    [JsonPropertyName("observation")]
    public SessionObservationDto? Observation { get; init; }
```

and a new record at the end of the file:

```csharp
/// <summary>One screen-inference judgment (see <see cref="AgentHostProtocol.ObservedActivities"/>).</summary>
public sealed record SessionObservationDto
{
    [JsonPropertyName("activity")]
    public required string Activity { get; init; }

    [JsonPropertyName("confidence")]
    public required double Confidence { get; init; }

    [JsonPropertyName("needsAttention")]
    public required double NeedsAttention { get; init; }

    [JsonPropertyName("lastCommandFailed")]
    public required double LastCommandFailed { get; init; }

    /// <summary>Milliseconds since the screen was captured.</summary>
    [JsonPropertyName("ageMs")]
    public required long AgeMs { get; init; }
}
```

`src/Ntilde.AgentHost.Contracts/AgentHostJsonContext.cs`: add `[JsonSerializable(typeof(SessionObservationDto))]` after the `SessionStatusDto` line.

- [ ] **Step 4: Map the snapshot to the DTO**

`src/Ntilde.App/AgentHost/AgentStatusWire.cs`: add `using Ntilde.Inference;`, a new mapping, and extend `ToDto`:

```csharp
        public static string ToWire(this ScreenActivity activity) => activity switch
        {
            ScreenActivity.CommandRunning => AgentHostProtocol.ObservedActivities.CommandRunning,
            ScreenActivity.AgentWorking => AgentHostProtocol.ObservedActivities.AgentWorking,
            ScreenActivity.WaitingForUser => AgentHostProtocol.ObservedActivities.WaitingForUser,
            ScreenActivity.IdleShellPrompt => AgentHostProtocol.ObservedActivities.IdleShellPrompt,
            ScreenActivity.UnknownBlank => AgentHostProtocol.ObservedActivities.UnknownBlank,
            _ => throw new ArgumentOutOfRangeException(nameof(activity), activity, null),
        };
```

In `ToDto`, add after `IdleThresholdSeconds = snapshot.IdleThresholdSeconds,`:

```csharp
            ObservedOverrideThresholdPercent = snapshot.ObservedOverrideThresholdPercent,
            Observation = snapshot.Observation is { } o
                ? new SessionObservationDto
                {
                    Activity = o.Activity.ToWire(),
                    Confidence = o.Confidence,
                    NeedsAttention = o.NeedsAttention,
                    LastCommandFailed = o.LastCommandFailed,
                    AgeMs = snapshot.ObservationAgeMs ?? 0,
                }
                : null,
```

- [ ] **Step 5: Extend the formatter and the tool description**

`src/Ntilde.McpServer/Tools/SessionTools.cs`, in `FormatStatus`, replace the final `sb.Append($"Thresholds: ...")` line with:

```csharp
        sb.Append($"Thresholds: stall after {dto.StallThresholdSeconds}s of silence while running, idle after {dto.IdleThresholdSeconds}s at a prompt, observed override at {dto.ObservedOverrideThresholdPercent}%.");
        if (dto.Observation is { } o)
        {
            sb.AppendLine();
            sb.Append($"Observed: {o.Activity} ({o.Confidence:0.00}) · attention {o.NeedsAttention:0.00} · last command failed {o.LastCommandFailed:0.00} · {o.AgeMs / 1000.0:0.0}s ago");
        }
```

Replace the `Description` of `ntilde.get_session_status` (line 163) with:

```csharp
     Description("Reports what a live Ntilde session is doing right now: running / awaitingInput / idle / exited, with a confidence tier, the in-flight command when known, exit code, stall state, and the latest screen observation when screen inference is enabled. Tiers: precise = shell-integration events; heuristic = PTY signals (child processes, alt screen), which cannot see processes inside WSL or on a remote SSH host; observed = an opt-in judgment over the pane's visible text decided the status. The observed tier closes the WSL/SSH gap and also detects an agent CLI (Claude Code etc.) that has finished and is waiting at its input box while the shell still sees one long command. It is off by default and never used for SSH profiles unless the profile allows it. Read-only. Get paneId from ntilde.list_sessions.")]
```

- [ ] **Step 6: Run the MCP server tests and build the App**

PowerShell: `scripts/build.ps1 test tests/Ntilde.McpServer.Tests` then `scripts/build.ps1 build src/Ntilde.App`
Expected: all MCP tests PASS (including the drift guards, which are unchanged by this task); the App builds.

- [ ] **Step 7: Commit**

```bash
git add src/Ntilde.AgentHost.Contracts src/Ntilde.App/AgentHost/AgentStatusWire.cs src/Ntilde.McpServer/Tools/SessionTools.cs tests/Ntilde.McpServer.Tests/AgentHostClientTests.cs
git commit -m "feat(agent-host): observation fields on the status DTO and MCP formatter

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: Tab-strip attention from observations

**Files:**
- Modify: `src/Ntilde.App/Shell/TabStatusTracker.cs`
- Modify: `src/Ntilde.App/MainWindow.axaml.cs:619-665` (`RefreshTabStatuses`) and the `TabState` class near line 278
- Test: `tests/Ntilde.App.Tests/TabStatusTrackerTests.cs` (add two facts)

**Interfaces:**
- Consumes: `AgentSessionStatusSnapshot.Observation` (Task 4).
- Produces: `TabStatusTracker.AttentionThreshold = 0.7` (`internal const double`), `TabStatusTracker.NoteObservedAttention()`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Ntilde.App.Tests/TabStatusTrackerTests.cs` inside the class:

```csharp
    [Fact]
    public void ObservedAttention_RaisesAttentionWhileUnselected()
    {
        var tracker = new TabStatusTracker();
        tracker.NoteObservedAttention();
        Assert.Equal(TabTrackerStatus.Attention, tracker.Evaluate(T0, isSelected: false));
    }

    [Fact]
    public void ObservedAttention_ClearsOnSelection()
    {
        var tracker = new TabStatusTracker();
        tracker.NoteObservedAttention();
        Assert.Equal(TabTrackerStatus.Idle, tracker.Evaluate(T0, isSelected: true));
        Assert.Equal(TabTrackerStatus.Idle, tracker.Evaluate(T0.AddSeconds(1), isSelected: false));
    }
```

- [ ] **Step 2: Run to verify it fails**

PowerShell: `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~TabStatusTrackerTests" --blame-hang-timeout 5m`
Expected: FAIL to compile.

- [ ] **Step 3: Add the method and constant**

In `src/Ntilde.App/Shell/TabStatusTracker.cs`, after `MinAttentionBurst`:

```csharp
        /// <summary>Minimum <c>needsAttention</c> probability from a screen observation to raise Attention.</summary>
        internal const double AttentionThreshold = 0.7;
```

after `NoteBell()`:

```csharp
        /// <summary>A screen observation says the pane is waiting on the user. Same effect as a bell.</summary>
        public void NoteObservedAttention() => _attention = true;
```

- [ ] **Step 4: Feed it from the window's 1 Hz refresh**

In `MainWindow.axaml.cs`, in the per-tab state class that declares `public TabStatusTracker Status { get; } = new();` (~L278), add:

```csharp
            /// <summary>ObservedAt of the last observation that raised Attention, so one observation raises it once.</summary>
            public DateTimeOffset? LastObservedAttentionAt { get; set; }
```

In `RefreshTabStatuses`, extend the registry pass. Replace the `runningTabIds` loop with:

```csharp
            var runningTabIds = new HashSet<Guid>();
            var attentionByTab = new Dictionary<Guid, DateTimeOffset>();
            foreach (var registration in AgentHost.AgentSessionRegistry.Instance.GetRegistrations())
            {
                var tabId = registration.TabId;
                if (!tabId.HasValue) continue;
                var snapshot = registration.StatusMachine.Snapshot();
                if (snapshot.Kind == AgentHost.AgentSessionStatusKind.Running)
                {
                    runningTabIds.Add(tabId.Value);
                }
                if (snapshot.Observation is { } observation
                    && observation.NeedsAttention >= TabStatusTracker.AttentionThreshold)
                {
                    attentionByTab[tabId.Value] = observation.ObservedAt;
                }
            }
```

Then, inside the per-tab loop, before `var status = state.Status.Evaluate(now, isSelected: tab.IsSelected);`:

```csharp
                if (!tab.IsSelected
                    && attentionByTab.TryGetValue(GetPersistentTabId(tab), out var observedAt)
                    && state.LastObservedAttentionAt != observedAt)
                {
                    state.LastObservedAttentionAt = observedAt;
                    state.Status.NoteObservedAttention();
                }
```

`GetPersistentTabId(tab)` is already used a few lines below for `runningTabIds`; reuse it, do not add a second lookup helper.

- [ ] **Step 5: Run the tracker tests and build the App**

PowerShell: `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~TabStatusTrackerTests" --blame-hang-timeout 5m` then `scripts/build.ps1 build src/Ntilde.App`
Expected: PASS; App builds.

- [ ] **Step 6: Commit**

```bash
git add src/Ntilde.App/Shell/TabStatusTracker.cs src/Ntilde.App/MainWindow.axaml.cs tests/Ntilde.App.Tests/TabStatusTrackerTests.cs
git commit -m "feat(tabs): raise the attention marker from screen observations

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 7: `ObservedActivityMonitor` core: eligibility, due checks, capture, redact, send, stale drop

**Files:**
- Create: `src/Ntilde.App/AgentHost/ObservedActivityMonitor.cs`
- Test: `tests/Ntilde.App.Tests/AgentHost/ObservedActivityMonitorTests.cs`

**Interfaces:**
- Consumes: `AgentSessionRegistry`, `AgentSessionRegistration` (`Kind`, `ProfileId`, `PaneId`, `StatusMachine`), `AgentSessionStatusMachine.Snapshot()/NotifyObserved` (Task 4), `IScreenActivityClassifier`, `ScreenSample`, `ScreenClassificationResult` (Task 3), `Ntilde.CommandAssist.Domain.ISecretsFilter`.
- Produces (namespace `Ntilde.AgentHost`):
  - `public sealed class ObservedActivityMonitor : IDisposable`
  - ctor `(AgentSessionRegistry registry, IScreenActivityClassifier classifier, ISecretsFilter secretsFilter, Func<AgentSessionRegistration, ScreenSample?> capture, Func<DateTimeOffset>? nowProvider = null, Action<string>? log = null)`
  - `static readonly TimeSpan TickInterval (1 s), QuietWindow (2 s), MinInterval (5 s), InitialBackoff (5 s), MaxBackoff (5 min)`; `const string LocalKind = "local"`
  - `void SetSshProfileAllowlist(Func<Guid,bool>? probe)`, `void Apply(bool enabled)`, `void Stop()`, `bool IsRunning`, `int RequestCount`, `bool IsDisabledUnauthorized`, `event Action? StateChanged`
  - `internal Task TickAsync()` (one sweep; returned task completes when every send started by that sweep has completed)
  - `internal TimeSpan CurrentBackoff`, `internal DateTimeOffset BackoffUntil`

- [ ] **Step 1: Write the failing tests**

`tests/Ntilde.App.Tests/AgentHost/ObservedActivityMonitorTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.AgentHost;
using Ntilde.CommandAssist.Domain;
using Ntilde.Inference;
using Ntilde.VT;

namespace Ntilde.AppTests.AgentHost;

/// <summary>
/// The monitor's policy (spec §2.2, §4), with a fake classifier, fake clock, fake capture and an
/// isolated registry. No timers run: tests call <see cref="ObservedActivityMonitor.TickAsync"/>.
/// </summary>
public class ObservedActivityMonitorTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => Now += by;
        public Func<DateTimeOffset> Provider => () => Now;
    }

    private sealed class FakeClassifier : IScreenActivityClassifier
    {
        public bool HasCredentials { get; set; } = true;
        public List<ScreenSample> Samples { get; } = new();
        public Func<ScreenSample, ScreenClassificationResult> Respond { get; set; } = _ => Answered(ScreenActivity.WaitingForUser, 0.95, 0.9);
        public TaskCompletionSource<ScreenClassificationResult>? Hold { get; set; }

        public async Task<ScreenClassificationResult> ClassifyAsync(ScreenSample sample, CancellationToken cancellationToken)
        {
            Samples.Add(sample);
            if (Hold != null) return await Hold.Task;
            return Respond(sample);
        }

        public static ScreenClassificationResult Answered(ScreenActivity activity, double confidence, double attention)
            => new(ScreenClassificationOutcome.Answered, new ScreenActivityAnswer(activity, confidence, attention, 0.05, 900), null);
    }

    private sealed class MarkingFilter : ISecretsFilter
    {
        public RedactionResult Redact(string commandText) => new("[R]" + commandText, true);
    }

    private sealed class Harness
    {
        public FakeClock Clock { get; } = new();
        public FakeClassifier Classifier { get; } = new();
        public AgentSessionRegistry Registry { get; } = new();
        public Dictionary<Guid, string?> Screens { get; } = new();
        public List<string> Log { get; } = new();
        public ObservedActivityMonitor Monitor { get; }

        public Harness()
        {
            Monitor = new ObservedActivityMonitor(
                Registry,
                Classifier,
                new MarkingFilter(),
                reg => Screens.TryGetValue(reg.PaneId, out var text) && text != null ? new ScreenSample(text, 24, 80) : null,
                Clock.Provider,
                Log.Add);
        }

        public AgentSessionRegistration AddPane(string kind = "local", Guid? profileId = null, string screen = "user@host:~$ ")
        {
            var reg = new AgentSessionRegistration(
                paneId: Guid.NewGuid(),
                buffer: new TerminalBuffer(80, 24),
                title: "pane",
                profileName: "Terminal",
                kind: kind,
                isActive: false,
                nowProvider: Clock.Provider,
                profileId: profileId);
            Registry.Register(reg);
            Screens[reg.PaneId] = screen;
            return reg;
        }

        /// <summary>Output now, then advance past the quiet window so the pane is due.</summary>
        public void OutputThenQuiet(AgentSessionRegistration reg)
        {
            reg.StatusMachine.NotifyOutput();
            Clock.Advance(ObservedActivityMonitor.QuietWindow);
        }
    }

    [Fact]
    public async Task Quiet_local_pane_with_new_output_is_classified_and_observed()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.OutputThenQuiet(reg);

        await h.Monitor.TickAsync();

        var sample = Assert.Single(h.Classifier.Samples);
        Assert.Equal("[R]user@host:~$ ", sample.Text);
        Assert.Equal(24, sample.Rows);
        Assert.Equal(80, sample.Cols);
        var snapshot = reg.StatusMachine.Snapshot();
        Assert.NotNull(snapshot.Observation);
        Assert.Equal(ScreenActivity.WaitingForUser, snapshot.Observation!.Activity);
        Assert.Equal(AgentSessionStatusConfidence.Observed, snapshot.Confidence);
        Assert.Equal(1, h.Monitor.RequestCount);
    }

    [Fact]
    public async Task Not_due_before_the_quiet_window()
    {
        var h = new Harness();
        var reg = h.AddPane();
        reg.StatusMachine.NotifyOutput();
        h.Clock.Advance(ObservedActivityMonitor.QuietWindow - TimeSpan.FromMilliseconds(1));

        await h.Monitor.TickAsync();

        Assert.Empty(h.Classifier.Samples);
    }

    [Fact]
    public async Task Not_due_without_new_output_since_the_last_send()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.OutputThenQuiet(reg);
        await h.Monitor.TickAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(1));

        await h.Monitor.TickAsync();

        Assert.Single(h.Classifier.Samples);
    }

    [Fact]
    public async Task Not_due_within_the_minimum_interval_even_with_new_output()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.OutputThenQuiet(reg);
        await h.Monitor.TickAsync();

        h.Screens[reg.PaneId] = "user@host:~$ ls\nfile\nuser@host:~$ ";
        h.OutputThenQuiet(reg); // +2 s: total 2 s since last send, under the 5 s minimum
        await h.Monitor.TickAsync();
        Assert.Single(h.Classifier.Samples);

        h.Clock.Advance(ObservedActivityMonitor.MinInterval);
        await h.Monitor.TickAsync();
        Assert.Equal(2, h.Classifier.Samples.Count);
    }

    [Fact]
    public async Task Unchanged_text_and_blank_text_are_not_sent()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.OutputThenQuiet(reg);
        await h.Monitor.TickAsync();

        h.Clock.Advance(ObservedActivityMonitor.MinInterval);
        h.OutputThenQuiet(reg); // same screen text
        await h.Monitor.TickAsync();
        Assert.Single(h.Classifier.Samples);

        var blank = h.AddPane(screen: "   \n\n  ");
        h.OutputThenQuiet(blank);
        await h.Monitor.TickAsync();
        Assert.Single(h.Classifier.Samples);
    }

    [Fact]
    public async Task Ssh_pane_needs_the_profile_allowlist()
    {
        var h = new Harness();
        var allowed = Guid.NewGuid();
        var denied = Guid.NewGuid();
        var a = h.AddPane(kind: "ssh", profileId: allowed, screen: "a$ ");
        var d = h.AddPane(kind: "ssh", profileId: denied, screen: "d$ ");
        var noProfile = h.AddPane(kind: "ssh", profileId: null, screen: "n$ ");
        foreach (var r in new[] { a, d, noProfile }) h.OutputThenQuiet(r);

        await h.Monitor.TickAsync();
        Assert.Empty(h.Classifier.Samples); // no probe published: fail closed

        h.Monitor.SetSshProfileAllowlist(id => id == allowed);
        await h.Monitor.TickAsync();

        var sample = Assert.Single(h.Classifier.Samples);
        Assert.Equal("[R]a$ ", sample.Text);
    }

    [Fact]
    public async Task No_credentials_means_no_capture_at_all()
    {
        var h = new Harness();
        h.Classifier.HasCredentials = false;
        var reg = h.AddPane();
        h.OutputThenQuiet(reg);
        int captures = 0;
        var monitor = new ObservedActivityMonitor(h.Registry, h.Classifier, new MarkingFilter(),
            _ => { captures++; return new ScreenSample("x", 1, 1); }, h.Clock.Provider, h.Log.Add);

        await monitor.TickAsync();

        Assert.Equal(0, captures);
        Assert.Empty(h.Classifier.Samples);
    }

    [Fact]
    public async Task Answer_arriving_after_new_output_is_dropped_as_stale()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.OutputThenQuiet(reg);
        h.Classifier.Hold = new TaskCompletionSource<ScreenClassificationResult>();

        var tick = h.Monitor.TickAsync();
        reg.StatusMachine.NotifyOutput(); // output while the request is in flight
        h.Classifier.Hold.SetResult(FakeClassifier.Answered(ScreenActivity.WaitingForUser, 0.95, 0.9));
        await tick;

        Assert.Null(reg.StatusMachine.Snapshot().Observation);
        Assert.Contains(h.Log, line => line.Contains("stale", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Only_one_request_in_flight_per_pane()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.OutputThenQuiet(reg);
        h.Classifier.Hold = new TaskCompletionSource<ScreenClassificationResult>();

        var first = h.Monitor.TickAsync();
        h.Screens[reg.PaneId] = "changed$ ";
        h.OutputThenQuiet(reg);
        h.Clock.Advance(ObservedActivityMonitor.MinInterval);
        await h.Monitor.TickAsync();

        Assert.Single(h.Classifier.Samples);
        h.Classifier.Hold.SetResult(FakeClassifier.Answered(ScreenActivity.IdleShellPrompt, 0.99, 0.1));
        await first;
    }

    [Fact]
    public async Task Log_line_never_contains_screen_text()
    {
        var h = new Harness();
        var reg = h.AddPane(screen: "SECRET-MARKER-TEXT$ ");
        h.OutputThenQuiet(reg);

        await h.Monitor.TickAsync();

        Assert.NotEmpty(h.Log);
        Assert.All(h.Log, line => Assert.DoesNotContain("SECRET-MARKER", line, StringComparison.Ordinal));
        Assert.Contains(h.Log, line => line.Contains("outcome=Answered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unregistered_pane_state_is_pruned()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.OutputThenQuiet(reg);
        await h.Monitor.TickAsync();
        Assert.Equal(1, h.Monitor.TrackedPaneCount);

        h.Registry.Unregister(reg.PaneId);
        await h.Monitor.TickAsync();

        Assert.Equal(0, h.Monitor.TrackedPaneCount);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

PowerShell: `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~ObservedActivityMonitorTests" --blame-hang-timeout 5m`
Expected: FAIL to compile.

- [ ] **Step 3: Write the monitor**

`src/Ntilde.App/AgentHost/ObservedActivityMonitor.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.CommandAssist.Domain;
using Ntilde.Inference;

namespace Ntilde.AgentHost
{
    /// <summary>
    /// Feeds <see cref="AgentSessionStatusMachine.NotifyObserved"/> from one screen judgment per
    /// quiet pane (docs/superpowers/specs/2026-09-17-screen-inference-observed-status-design.md §2.2).
    /// Own 1 s timer, independent of the agent-host IPC endpoint, so the tab strip benefits with
    /// agent access off. Every dependency is injected; tests drive <see cref="TickAsync"/> directly.
    /// </summary>
    public sealed class ObservedActivityMonitor : IDisposable
    {
        public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
        /// <summary>A pane must be silent this long after output before its screen is judged.</summary>
        public static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(2);
        /// <summary>Per-pane floor between requests; bounds cost on a pane that streams continuously.</summary>
        public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
        public const string LocalKind = "local";

        private sealed class PaneState
        {
            public DateTimeOffset LastRequestAt = DateTimeOffset.MinValue;
            public long LastSequenceSent = -1;
            public string? LastTextSent;
            public bool InFlight;
        }

        private readonly AgentSessionRegistry _registry;
        private readonly IScreenActivityClassifier _classifier;
        private readonly ISecretsFilter _secretsFilter;
        private readonly Func<AgentSessionRegistration, ScreenSample?> _capture;
        private readonly Func<DateTimeOffset> _now;
        private readonly Action<string> _log;

        private readonly object _gate = new();
        private readonly Dictionary<AgentSessionRegistration, PaneState> _panes = new(ReferenceEqualityComparer.Instance);
        private volatile Func<Guid, bool>? _sshAllowlist;
        private Timer? _timer;
        private int _tickRunning;
        private int _requestCount;
        private bool _disabledUnauthorized;
        private TimeSpan _currentBackoff = TimeSpan.Zero;
        private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;

        public ObservedActivityMonitor(
            AgentSessionRegistry registry,
            IScreenActivityClassifier classifier,
            ISecretsFilter secretsFilter,
            Func<AgentSessionRegistration, ScreenSample?> capture,
            Func<DateTimeOffset>? nowProvider = null,
            Action<string>? log = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
            _secretsFilter = secretsFilter ?? throw new ArgumentNullException(nameof(secretsFilter));
            _capture = capture ?? throw new ArgumentNullException(nameof(capture));
            _now = nowProvider ?? (() => DateTimeOffset.UtcNow);
            _log = log ?? (_ => { });
        }

        /// <summary>Raised (any thread) when running/disabled state or the request count changes.</summary>
        public event Action? StateChanged;

        public bool IsRunning { get { lock (_gate) { return _timer != null; } } }
        public int RequestCount => Volatile.Read(ref _requestCount);
        public bool IsDisabledUnauthorized { get { lock (_gate) { return _disabledUnauthorized; } } }
        internal TimeSpan CurrentBackoff { get { lock (_gate) { return _currentBackoff; } } }
        internal DateTimeOffset BackoffUntil { get { lock (_gate) { return _backoffUntil; } } }
        internal int TrackedPaneCount { get { lock (_gate) { return _panes.Count; } } }

        /// <summary>Per-profile SSH opt-in probe (fail closed when null). UI thread publishes it.</summary>
        public void SetSshProfileAllowlist(Func<Guid, bool>? probe) => _sshAllowlist = probe;

        /// <summary>Starts or stops the timer to match the setting. Safe to call repeatedly.</summary>
        public void Apply(bool enabled)
        {
            if (enabled) Start(); else Stop();
        }

        private void Start()
        {
            lock (_gate)
            {
                if (_timer != null) return;
                _disabledUnauthorized = false;
                _currentBackoff = TimeSpan.Zero;
                _backoffUntil = DateTimeOffset.MinValue;
                _timer = new Timer(_ => _ = TickAsync(), null, TickInterval, TickInterval);
            }
            StateChanged?.Invoke();
        }

        public void Stop()
        {
            bool changed;
            lock (_gate)
            {
                changed = _timer != null;
                _timer?.Dispose();
                _timer = null;
                _panes.Clear();
            }
            if (changed) StateChanged?.Invoke();
        }

        public void Dispose() => Stop();

        /// <summary>
        /// One sweep. Returns a task that completes when every request this sweep started has
        /// completed; the timer discards it, tests await it. Re-entrant calls while a sweep is
        /// walking the registry return immediately (requests in flight do not block sweeps).
        /// </summary>
        internal Task TickAsync()
        {
            if (Interlocked.Exchange(ref _tickRunning, 1) == 1) return Task.CompletedTask;
            var sends = new List<Task>();
            try
            {
                var now = _now();
                lock (_gate)
                {
                    if (_disabledUnauthorized || now < _backoffUntil) return Task.CompletedTask;
                }
                if (!_classifier.HasCredentials) return Task.CompletedTask;

                var registrations = _registry.GetRegistrations();
                foreach (var registration in registrations)
                {
                    try
                    {
                        var send = TryObserve(registration, now);
                        if (send != null) sends.Add(send);
                    }
                    catch (Exception ex)
                    {
                        _log($"[ScreenInference] pane={registration.PaneId} tick failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                Prune(registrations);
            }
            finally
            {
                Volatile.Write(ref _tickRunning, 0);
            }
            return Task.WhenAll(sends);
        }

        private Task? TryObserve(AgentSessionRegistration registration, DateTimeOffset now)
        {
            if (!IsEligible(registration)) return null;

            var snapshot = registration.StatusMachine.Snapshot();
            // Read the sequence BEFORE capturing: output that lands between the two makes the
            // answer look stale (dropped), never fresh for a screen it did not see.
            long sequence = snapshot.OutputSequence;

            PaneState state;
            lock (_gate)
            {
                if (!_panes.TryGetValue(registration, out state!))
                {
                    state = new PaneState();
                    _panes[registration] = state;
                }
                if (state.InFlight) return null;
                if (sequence == state.LastSequenceSent) return null;
                if (now - snapshot.LastOutputAt < QuietWindow) return null;
                if (now - state.LastRequestAt < MinInterval) return null;
            }

            var sample = _capture(registration);
            if (sample == null || string.IsNullOrWhiteSpace(sample.Text))
            {
                lock (_gate) { state.LastSequenceSent = sequence; }
                return null;
            }

            lock (_gate)
            {
                if (string.Equals(sample.Text, state.LastTextSent, StringComparison.Ordinal))
                {
                    state.LastSequenceSent = sequence;
                    state.LastRequestAt = now;
                    return null;
                }
                state.InFlight = true;
                state.LastRequestAt = now;
                state.LastSequenceSent = sequence;
                state.LastTextSent = sample.Text;
            }

            var redacted = _secretsFilter.Redact(sample.Text).RedactedText;
            Interlocked.Increment(ref _requestCount);
            StateChanged?.Invoke();
            return SendAsync(registration, state, sample with { Text = redacted }, sequence, now);
        }

        private bool IsEligible(AgentSessionRegistration registration)
        {
            if (string.Equals(registration.Kind, LocalKind, StringComparison.Ordinal)) return true;
            var probe = _sshAllowlist;
            if (probe == null) return false; // fail closed
            if (registration.ProfileId is not { } profileId) return false;
            try { return probe(profileId); } catch { return false; }
        }

        private async Task SendAsync(AgentSessionRegistration registration, PaneState state, ScreenSample sample, long sequence, DateTimeOffset startedAt)
        {
            ScreenClassificationResult result;
            try
            {
                result = await _classifier.ClassifyAsync(sample, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new ScreenClassificationResult(ScreenClassificationOutcome.TransportFailure, null, $"{ex.GetType().Name}: {ex.Message}");
            }
            lock (_gate) { state.InFlight = false; }

            var latency = _now() - startedAt;
            string note = string.Empty;
            switch (result.Outcome)
            {
                case ScreenClassificationOutcome.Answered:
                    var a = result.Answer!;
                    var accepted = registration.StatusMachine.NotifyObserved(new ScreenObservation
                    {
                        Activity = a.Activity,
                        Confidence = a.Confidence,
                        NeedsAttention = a.NeedsAttention,
                        LastCommandFailed = a.LastCommandFailed,
                        ObservedAt = startedAt,
                        OutputSequence = sequence,
                    });
                    note = accepted ? $"activity={a.Activity} conf={a.Confidence:0.00} tokens={a.InputTokens}" : "dropped: stale (output arrived during the request)";
                    ResetBackoff();
                    break;
                case ScreenClassificationOutcome.Unauthorized:
                    DisableUnauthorized();
                    note = "API key rejected; screen inference disabled until a key is saved again";
                    break;
                case ScreenClassificationOutcome.RateLimited:
                case ScreenClassificationOutcome.Overloaded:
                    note = $"backing off {ApplyBackoff().TotalSeconds:0}s";
                    break;
                default:
                    note = result.Detail ?? string.Empty;
                    break;
            }
            _log($"[ScreenInference] pane={registration.PaneId} bytes={sample.Text.Length} latency={latency.TotalMilliseconds:0}ms outcome={result.Outcome} {note}");
        }

        private void Prune(AgentSessionRegistration[] live)
        {
            lock (_gate)
            {
                if (_panes.Count == 0) return;
                var liveSet = new HashSet<AgentSessionRegistration>(live, ReferenceEqualityComparer.Instance);
                var dead = new List<AgentSessionRegistration>();
                foreach (var key in _panes.Keys)
                {
                    if (!liveSet.Contains(key)) dead.Add(key);
                }
                foreach (var key in dead) _panes.Remove(key);
            }
        }

        private TimeSpan ApplyBackoff()
        {
            lock (_gate)
            {
                _currentBackoff = _currentBackoff == TimeSpan.Zero
                    ? InitialBackoff
                    : TimeSpan.FromTicks(Math.Min(_currentBackoff.Ticks * 2, MaxBackoff.Ticks));
                _backoffUntil = _now() + _currentBackoff;
                return _currentBackoff;
            }
        }

        private void ResetBackoff()
        {
            lock (_gate)
            {
                _currentBackoff = TimeSpan.Zero;
                _backoffUntil = DateTimeOffset.MinValue;
            }
        }

        private void DisableUnauthorized()
        {
            lock (_gate) { _disabledUnauthorized = true; }
            StateChanged?.Invoke();
        }
    }
}
```

- [ ] **Step 4: Run the tests**

PowerShell: `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~ObservedActivityMonitorTests" --blame-hang-timeout 5m`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Ntilde.App/AgentHost/ObservedActivityMonitor.cs tests/Ntilde.App.Tests/AgentHost/ObservedActivityMonitorTests.cs
git commit -m "feat(agent-host): observed activity monitor with quiescence and staleness rules

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Monitor failure policy tests (backoff, unauthorized, exception safety)

The behaviour shipped in Task 7; this task pins it with tests so a later edit cannot silently drop it.

**Files:**
- Test: `tests/Ntilde.App.Tests/AgentHost/ObservedActivityMonitorTests.cs` (append)

**Interfaces:**
- Consumes: `ObservedActivityMonitor` internals `CurrentBackoff`, `BackoffUntil`, `IsDisabledUnauthorized` (Task 7).

- [ ] **Step 1: Write the tests**

Append inside the `ObservedActivityMonitorTests` class:

```csharp
    [Fact]
    public async Task Rate_limit_backs_off_doubling_to_the_cap_and_resets_on_success()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.Classifier.Respond = _ => new ScreenClassificationResult(ScreenClassificationOutcome.RateLimited, null, "429");

        var expected = ObservedActivityMonitor.InitialBackoff;
        for (int i = 0; i < 8; i++)
        {
            h.Screens[reg.PaneId] = $"screen {i}$ ";
            h.OutputThenQuiet(reg);
            h.Clock.Advance(ObservedActivityMonitor.MaxBackoff); // clear any pending backoff and the min interval
            await h.Monitor.TickAsync();
            Assert.Equal(expected, h.Monitor.CurrentBackoff);
            Assert.Equal(h.Clock.Now + expected, h.Monitor.BackoffUntil);
            expected = TimeSpan.FromTicks(Math.Min(expected.Ticks * 2, ObservedActivityMonitor.MaxBackoff.Ticks));
        }
        Assert.Equal(ObservedActivityMonitor.MaxBackoff, h.Monitor.CurrentBackoff);

        // While backed off, no pane is even considered.
        h.Screens[reg.PaneId] = "later$ ";
        h.OutputThenQuiet(reg);
        int before = h.Classifier.Samples.Count;
        await h.Monitor.TickAsync();
        Assert.Equal(before, h.Classifier.Samples.Count);

        // After the backoff, a success resets it.
        h.Clock.Advance(ObservedActivityMonitor.MaxBackoff);
        h.Classifier.Respond = _ => FakeClassifier.Answered(ScreenActivity.IdleShellPrompt, 0.99, 0.1);
        await h.Monitor.TickAsync();
        Assert.Equal(TimeSpan.Zero, h.Monitor.CurrentBackoff);
    }

    [Fact]
    public async Task Overloaded_also_backs_off()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.Classifier.Respond = _ => new ScreenClassificationResult(ScreenClassificationOutcome.Overloaded, null, "529");
        h.OutputThenQuiet(reg);

        await h.Monitor.TickAsync();

        Assert.Equal(ObservedActivityMonitor.InitialBackoff, h.Monitor.CurrentBackoff);
    }

    [Fact]
    public async Task Unauthorized_disables_the_monitor_until_apply_restarts_it()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.Classifier.Respond = _ => new ScreenClassificationResult(ScreenClassificationOutcome.Unauthorized, null, "401");
        h.OutputThenQuiet(reg);
        int stateChanges = 0;
        h.Monitor.StateChanged += () => stateChanges++;

        await h.Monitor.TickAsync();
        Assert.True(h.Monitor.IsDisabledUnauthorized);
        Assert.True(stateChanges >= 1);

        h.Screens[reg.PaneId] = "again$ ";
        h.OutputThenQuiet(reg);
        h.Clock.Advance(ObservedActivityMonitor.MinInterval);
        await h.Monitor.TickAsync();
        Assert.Single(h.Classifier.Samples);

        h.Monitor.Apply(true);  // a re-saved key re-enables: Start() clears the flag
        Assert.False(h.Monitor.IsDisabledUnauthorized);
        h.Monitor.Stop();
    }

    [Fact]
    public async Task Transport_failure_leaves_status_untouched_and_does_not_back_off()
    {
        var h = new Harness();
        var reg = h.AddPane();
        h.Classifier.Respond = _ => new ScreenClassificationResult(ScreenClassificationOutcome.TransportFailure, null, "timed out");
        h.OutputThenQuiet(reg);
        var before = reg.StatusMachine.Snapshot();

        await h.Monitor.TickAsync();

        var after = reg.StatusMachine.Snapshot();
        Assert.Equal(before.Kind, after.Kind);
        Assert.Equal(before.Confidence, after.Confidence);
        Assert.Null(after.Observation);
        Assert.Equal(TimeSpan.Zero, h.Monitor.CurrentBackoff);
        Assert.Contains(h.Log, line => line.Contains("outcome=TransportFailure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_throwing_classifier_does_not_kill_the_sweep()
    {
        var h = new Harness();
        var bad = h.AddPane(screen: "bad$ ");
        var good = h.AddPane(screen: "good$ ");
        h.Classifier.Respond = s => s.Text.Contains("bad", StringComparison.Ordinal)
            ? throw new InvalidOperationException("kaboom")
            : FakeClassifier.Answered(ScreenActivity.IdleShellPrompt, 0.99, 0.1);
        h.OutputThenQuiet(bad);
        h.OutputThenQuiet(good);

        await h.Monitor.TickAsync();

        Assert.NotNull(good.StatusMachine.Snapshot().Observation);
        Assert.Null(bad.StatusMachine.Snapshot().Observation);
        Assert.Contains(h.Log, line => line.Contains("kaboom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_throwing_capture_is_logged_and_the_sweep_continues()
    {
        var h = new Harness();
        var a = h.AddPane(screen: "a$ ");
        var b = h.AddPane(screen: "b$ ");
        var monitor = new ObservedActivityMonitor(h.Registry, h.Classifier, new MarkingFilter(),
            reg => reg.PaneId == a.PaneId ? throw new InvalidOperationException("capture failed") : new ScreenSample("b$ ", 24, 80),
            h.Clock.Provider, h.Log.Add);
        h.OutputThenQuiet(a);
        h.OutputThenQuiet(b);

        await monitor.TickAsync();

        Assert.Single(h.Classifier.Samples);
        Assert.Contains(h.Log, line => line.Contains("capture failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_and_stop_toggle_running_and_raise_state_changed()
    {
        var h = new Harness();
        int changes = 0;
        h.Monitor.StateChanged += () => changes++;

        h.Monitor.Apply(true);
        Assert.True(h.Monitor.IsRunning);
        h.Monitor.Apply(true); // idempotent
        h.Monitor.Apply(false);
        Assert.False(h.Monitor.IsRunning);

        Assert.Equal(2, changes); // start, stop; the no-op second Apply(true) returns before raising
    }
```

- [ ] **Step 2: Run the tests**

PowerShell: `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~ObservedActivityMonitorTests" --blame-hang-timeout 5m`
Expected: all PASS. If `Unauthorized_disables_the_monitor_until_apply_restarts_it` fails on the second tick, check that `TickAsync` reads `_disabledUnauthorized` under the gate before touching the classifier.

- [ ] **Step 3: Commit**

```bash
git add tests/Ntilde.App.Tests/AgentHost/ObservedActivityMonitorTests.cs src/Ntilde.App/AgentHost/ObservedActivityMonitor.cs
git commit -m "test(agent-host): pin monitor backoff, unauthorized, and exception-safety policy

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 9: Settings flag, vault-held API key, SSH profile flag, and the drift-guard registrations

**Files:**
- Modify: `src/Ntilde.App/Shell/TerminalSettings.cs:170` (after `LongCommandNotificationsEnabled`)
- Modify: `src/Ntilde.App/Shell/VaultService.cs` (after `RemoveSecret`, ~L291)
- Modify: `src/Ntilde.Platform/Ssh/Models/SshProfile.cs` (after `AllowAgentAccess`)
- Modify: `src/Ntilde.Platform/Ssh/Storage/JsonSshProfileStore.cs:342`
- Modify: `src/Ntilde.App/Services/Ssh/SshConnectionService.cs:296, 601`
- Modify: `src/Ntilde.McpServer/Tools/SettingsTools.cs` (doc row ~L68, example ~L147, `BoolFields` ~L170, `KnownFields` ~L198)
- Modify: `src/Ntilde.McpServer/Tools/ConnectionProfileTools.cs` (doc row L58, example L125, known list L141, `RequireBoolType` L291)
- Test: `tests/Ntilde.App.Tests/Core/VaultServiceInferenceKeyTests.cs`

**Interfaces:**
- Produces:
  - `TerminalSettings.ScreenInferenceEnabled` (bool, default false)
  - `SshProfile.AllowScreenInference` (bool, default false)
  - `VaultService.InferenceApiKeySecretKey = "ntilde/inference/typesafe-api-key"`, `string? GetInferenceApiKey()`, `void SetInferenceApiKey(string? value)` (null or empty removes), `bool HasInferenceApiKey()`

- [ ] **Step 1: Write the failing vault test**

`tests/Ntilde.App.Tests/Core/VaultServiceInferenceKeyTests.cs`:

```csharp
using Ntilde.Shell;
using Ntilde.Shell.Secrets;

namespace Ntilde.Tests.Core;

public class VaultServiceInferenceKeyTests
{
    private sealed class UnavailableStore : ISecretStore
    {
        public bool IsAvailable => false;
        public string? Read(string key) => "must-not-be-read";
        public void Write(string key, string value) => throw new InvalidOperationException("must not write");
        public bool Delete(string key) => throw new InvalidOperationException("must not delete");
    }

    [Fact]
    public void Set_get_and_clear_round_trip_through_the_store()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);

        Assert.False(vault.HasInferenceApiKey());
        Assert.Null(vault.GetInferenceApiKey());

        vault.SetInferenceApiKey("apikey_test_123");
        Assert.True(vault.HasInferenceApiKey());
        Assert.Equal("apikey_test_123", vault.GetInferenceApiKey());
        Assert.Equal("apikey_test_123", store.Read(VaultService.InferenceApiKeySecretKey));

        vault.SetInferenceApiKey(null);
        Assert.False(vault.HasInferenceApiKey());
        Assert.Null(store.Read(VaultService.InferenceApiKeySecretKey));
    }

    [Fact]
    public void Whitespace_is_trimmed_and_empty_clears()
    {
        var store = new InMemorySecretStore();
        var vault = new VaultService(store);

        vault.SetInferenceApiKey("  k  ");
        Assert.Equal("k", vault.GetInferenceApiKey());

        vault.SetInferenceApiKey("   ");
        Assert.Null(vault.GetInferenceApiKey());
    }

    [Fact]
    public void Unavailable_store_reads_null_and_writes_nothing()
    {
        var vault = new VaultService(new UnavailableStore());

        vault.SetInferenceApiKey("k");
        Assert.Null(vault.GetInferenceApiKey());
        Assert.False(vault.HasInferenceApiKey());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

PowerShell: `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~VaultServiceInferenceKeyTests" --blame-hang-timeout 5m`
Expected: FAIL to compile.

- [ ] **Step 3: Add the vault accessors**

In `src/Ntilde.App/Shell/VaultService.cs`, after `RemoveSecret`:

```csharp
        /// <summary>
        /// Secret-store key for the TypeSafe API key used by screen inference
        /// (docs/superpowers/specs/2026-09-17-screen-inference-observed-status-design.md §2.4).
        /// The value never round-trips through settings.json.
        /// </summary>
        public const string InferenceApiKeySecretKey = "ntilde/inference/typesafe-api-key";

        public string? GetInferenceApiKey()
        {
            string? value = GetSecret(InferenceApiKeySecretKey);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        public bool HasInferenceApiKey() => GetInferenceApiKey() != null;

        /// <summary>Stores the key trimmed; null, empty or whitespace removes it.</summary>
        public void SetInferenceApiKey(string? value)
        {
            string? trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                RemoveSecret(InferenceApiKeySecretKey);
                return;
            }

            SetSecret(InferenceApiKeySecretKey, trimmed);
        }
```

- [ ] **Step 4: Add the settings flag**

`src/Ntilde.App/Shell/TerminalSettings.cs`, after `LongCommandNotificationsEnabled`:

```csharp
        // Screen inference (docs/superpowers/specs/2026-09-17-screen-inference-observed-status-design.md):
        // when true, the visible text of quiet local panes is redacted and sent to the TypeSafe
        // System One API to derive an "observed" status tier. SSH panes additionally need
        // SshProfile.AllowScreenInference. Requires an API key in the OS secret store. Off by default.
        public bool ScreenInferenceEnabled { get; set; } = false;
```

- [ ] **Step 5: Add the SSH profile flag and its two mappings**

`src/Ntilde.Platform/Ssh/Models/SshProfile.cs`, after `AllowAgentAccess`:

```csharp
    // Screen inference: when true (and the global ScreenInferenceEnabled setting is on), the
    // redacted visible text of panes on this profile may be sent to a remote model to derive
    // session status. Default false — a remote host's screen leaving the machine is a separate
    // consent from letting agents type into it. Absent in older stores deserializes to false.
    public bool AllowScreenInference { get; set; }
```

`src/Ntilde.Platform/Ssh/Storage/JsonSshProfileStore.cs`, in `CloneProfile` after `AllowAgentAccess = profile.AllowAgentAccess` (add a trailing comma to that line):

```csharp
            AllowScreenInference = profile.AllowScreenInference
```

`src/Ntilde.App/Services/Ssh/SshConnectionService.cs`:
- after line 296 `merged.AllowAgentAccess = incoming.AllowAgentAccess;` add `merged.AllowScreenInference = incoming.AllowScreenInference;`
- after line 601 `AllowAgentAccess = profile.AllowAgentAccess` (add a comma) add `AllowScreenInference = profile.AllowScreenInference`.

- [ ] **Step 6: Register both fields with the MCP drift guards**

`src/Ntilde.McpServer/Tools/SettingsTools.cs`:
- doc table, after the `LongCommandNotificationsEnabled` row:
  ```
  | `ScreenInferenceEnabled` | bool | Default false. Sends the redacted visible text of quiet local panes to the TypeSafe API to derive the "observed" session-status tier. SSH profiles need `AllowScreenInference` too. Requires an API key stored via Settings. |
  ```
- example JSON, after `"LongCommandNotificationsEnabled": false,`: `"ScreenInferenceEnabled": false,`
- `BoolFields`: change the line `"AgentAccessActEnabled", "LongCommandNotificationsEnabled",` to `"AgentAccessActEnabled", "LongCommandNotificationsEnabled", "ScreenInferenceEnabled",`
- `KnownFields`: same edit on its copy of that line.

`src/Ntilde.McpServer/Tools/ConnectionProfileTools.cs`:
- doc table, after the `AllowAgentAccess` row:
  ```
  | `AllowScreenInference` | bool | Default false. Lets the redacted visible text of panes on this profile be sent to a remote model for the "observed" status tier; requires the global `ScreenInferenceEnabled` setting too. |
  ```
- example JSON: change `"AllowAgentAccess": false` to `"AllowAgentAccess": false,` and add `"AllowScreenInference": false` after it.
- known-field list: change `"AllowAgentAccess",` to `"AllowAgentAccess", "AllowScreenInference",`
- after `RequireBoolType(p, path, "AllowAgentAccess", errors);` add `RequireBoolType(p, path, "AllowScreenInference", errors);`

- [ ] **Step 7: Run the vault test and both drift-guard suites**

PowerShell:
1. `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~VaultServiceInferenceKeyTests" --blame-hang-timeout 5m`
2. `scripts/build.ps1 test tests/Ntilde.McpServer.Tests`

Expected: all PASS, including `SettingsToolsDriftGuardTests` and `ConnectionProfileDriftGuardTests`. If a drift guard fails, its message names the field it cannot find; fix the list rather than the test.

- [ ] **Step 8: Commit**

```bash
git add src/Ntilde.App/Shell/TerminalSettings.cs src/Ntilde.App/Shell/VaultService.cs src/Ntilde.Platform/Ssh src/Ntilde.App/Services/Ssh/SshConnectionService.cs src/Ntilde.McpServer/Tools tests/Ntilde.App.Tests/Core/VaultServiceInferenceKeyTests.cs
git commit -m "feat(settings): screen-inference toggle, per-profile SSH flag, vault-held API key

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: Composition and UI wiring

**Files:**
- Create: `src/Ntilde.App/AgentHost/ObservedActivityMonitorComposition.cs`
- Modify: `src/Ntilde.App/MainWindow.axaml.cs:3702-3713` (startup block), `:5695-5705` (`ApplyAgentHostSettingsLive`), `:5125-5139` (add sibling probe)
- Modify: `src/Ntilde.App/SettingsWindow.axaml:1013-1022` (after the act row) and `SettingsWindow.axaml.cs:2991-2999`, `:3291-3296`
- Modify: `src/Ntilde.App/Views/Ssh/NewSshConnectionView.axaml:168-188`, `src/Ntilde.App/ViewModels/Ssh/NewSshConnectionViewModel.cs:179-188, 425-431, 488-495`

**Interfaces:**
- Consumes: `ObservedActivityMonitor` (Task 7), `ScreenActivityClassifier`, `SystemOneClient`, `IApiKeySource` (Tasks 2-3), `VaultService` accessors and `TerminalSettings.ScreenInferenceEnabled` and `SshProfile.AllowScreenInference` (Task 9), `BufferSnapshot` (`Ntilde.Replay`), `SecretsFilter` (`Ntilde.CommandAssist.Domain`).
- Produces: `ObservedActivityMonitor.Instance` (static, App-composed), `ObservedActivityMonitorComposition.CaptureVisibleText(AgentSessionRegistration)`, `VaultApiKeySource : IApiKeySource`, `MainWindow.IsSshProfileScreenInferenceAllowed(Guid)`.

- [ ] **Step 1: Write the composition file**

`src/Ntilde.App/AgentHost/ObservedActivityMonitorComposition.cs`:

```csharp
using System;
using System.Linq;
using System.Net.Http;
using Ntilde.CommandAssist.Domain;
using Ntilde.Inference;
using Ntilde.Replay;
using Ntilde.Shell;

namespace Ntilde.AgentHost
{
    /// <summary>Reads the TypeSafe key from the OS secret store on every call, so a key saved in Settings takes effect without a restart.</summary>
    internal sealed class VaultApiKeySource : IApiKeySource
    {
        private readonly VaultService _vault;
        public VaultApiKeySource(VaultService vault) => _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        public string? TryGetKey() => _vault.GetInferenceApiKey();
    }

    /// <summary>
    /// The process-wide monitor with its real dependencies. Kept apart from
    /// <see cref="ObservedActivityMonitor"/> so the monitor's logic stays constructible with fakes.
    /// </summary>
    public static class ObservedActivityMonitorComposition
    {
        private static readonly Lazy<ObservedActivityMonitor> LazyInstance = new(Create);

        public static ObservedActivityMonitor Instance => LazyInstance.Value;

        private static ObservedActivityMonitor Create()
        {
            var http = new HttpClient { Timeout = SystemOneClient.DefaultTimeout };
            var client = new SystemOneClient(http, new VaultApiKeySource(new VaultService()));
            return new ObservedActivityMonitor(
                AgentSessionRegistry.Instance,
                new ScreenActivityClassifier(client),
                new SecretsFilter(),
                CaptureVisibleText,
                () => DateTimeOffset.UtcNow,
                AppLogger.Log);
        }

        /// <summary>
        /// The visible viewport as text, captured under the buffer read lock exactly like the
        /// agent-host <c>readScreen</c> handler. Trailing whitespace per line is trimmed and
        /// trailing blank lines dropped so a mostly-empty screen costs few tokens. Any thread.
        /// </summary>
        public static ScreenSample? CaptureVisibleText(AgentSessionRegistration registration)
        {
            var buffer = registration.Buffer;
            string[] lines;
            int rows, cols;
            buffer.Lock.EnterReadLock();
            try
            {
                lines = BufferSnapshot.Capture(buffer, includeAttributes: false).Lines;
                rows = buffer.Rows;
                cols = buffer.Cols;
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }

            var text = string.Join('\n', lines.Select(l => l.TrimEnd())).TrimEnd('\n');
            return new ScreenSample(text, rows, cols);
        }
    }
}
```

- [ ] **Step 2: Build the App to check the composition compiles**

PowerShell: `scripts/build.ps1 build src/Ntilde.App`
Expected: success. If `BufferSnapshot` is not found, confirm `Ntilde.App.csproj` references `Ntilde.Replay` (it does at line 540) and the `using Ntilde.Replay;` is present.

- [ ] **Step 3: Wire the monitor into MainWindow**

In `src/Ntilde.App/MainWindow.axaml.cs`, after `IsSshProfileAgentAllowed` (~L5139) add:

```csharp
        // Screen-inference per-profile SSH probe, handed to the observed-activity monitor. Same
        // contract as IsSshProfileAgentAllowed: thread-safe store read, fail closed.
        private bool IsSshProfileScreenInferenceAllowed(Guid profileId)
        {
            try
            {
                return _sshConnectionService?.GetStoredProfile(profileId)?.AllowScreenInference == true;
            }
            catch
            {
                return false;
            }
        }
```

In the startup block (after `RefreshAgentObserveIndicator();` at ~L3713) add:

```csharp
            // Screen inference (observed status tier): its own timer, independent of the IPC
            // endpoint, so the tab strip benefits with agent access off. Off unless opted in.
            AgentHost.ObservedActivityMonitorComposition.Instance.SetSshProfileAllowlist(IsSshProfileScreenInferenceAllowed);
            AgentHost.ObservedActivityMonitorComposition.Instance.Apply(_settings.ScreenInferenceEnabled);
```

In `ApplyAgentHostSettingsLive` (~L5695), before `RefreshTabAgentAttention();` add the same two lines.

- [ ] **Step 4: Settings window: toggle and API key field**

In `src/Ntilde.App/SettingsWindow.axaml`, after the `AgentAccessActToggle` row and its `<Border .../>` separator (~L1022), add:

```xml
                            <Grid ColumnDefinitions="*,360">
                                <StackPanel Grid.Column="0" Spacing="2">
                                    <TextBlock Classes="RowLabel" Text="Screen inference"/>
                                    <TextBlock Classes="RowDesc" Text="After a pane goes quiet, send its visible text (secrets redacted) to the TypeSafe API to tell whether it is running, waiting for you, or idle. Improves session status for WSL, SSH and agent CLIs. Local panes only, unless an SSH connection is allowlisted individually. A dot in the title bar shows while this is on."/>
                                </StackPanel>
                                <CheckBox Name="ScreenInferenceToggle" Grid.Column="1" Classes="Toggle" Content="" HorizontalAlignment="Right" VerticalAlignment="Center"/>
                            </Grid>
                            <Border BorderBrush="{StaticResource NtHairline}" BorderThickness="0,0,0,1" Margin="0,14,0,14"/>

                            <Grid ColumnDefinitions="*,360" Margin="24,0,0,0">
                                <StackPanel Grid.Column="0" Spacing="2">
                                    <TextBlock Classes="RowLabel" Text="TypeSafe API key"/>
                                    <TextBlock Classes="RowDesc" Text="Stored in the operating system's secret store, never in settings.json. Paste a key and press Set; Clear removes it."/>
                                    <TextBlock Name="InferenceApiKeyStatus" Classes="RowDesc" Text=""/>
                                </StackPanel>
                                <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6" HorizontalAlignment="Right" VerticalAlignment="Center">
                                    <TextBox Name="InferenceApiKeyBox" Width="220" PasswordChar="•" Watermark="apikey_…"/>
                                    <Button Name="InferenceApiKeySetButton" Content="Set"/>
                                    <Button Name="InferenceApiKeyClearButton" Content="Clear"/>
                                </StackPanel>
                            </Grid>
                            <Border BorderBrush="{StaticResource NtHairline}" BorderThickness="0,0,0,1" Margin="0,14,0,14"/>
```

In `src/Ntilde.App/SettingsWindow.axaml.cs`:

Add a field near the other private fields of the class:

```csharp
        private readonly VaultService _inferenceVault = new();
```

In the load block (after the `agentAccessActToggle` lines, ~L2999):

```csharp
            var screenInferenceToggle = this.FindControl<CheckBox>("ScreenInferenceToggle");
            if (screenInferenceToggle != null) screenInferenceToggle.IsChecked = _settings.ScreenInferenceEnabled;
            WireInferenceApiKeyControls();
```

In the save block (after the `agentAccessActToggle` lines, ~L3296):

```csharp
            var screenInferenceToggle = this.FindControl<CheckBox>("ScreenInferenceToggle");
            if (screenInferenceToggle != null) _settings.ScreenInferenceEnabled = screenInferenceToggle.IsChecked == true;
```

Add the helper methods to the class:

```csharp
        private bool _inferenceApiKeyControlsWired;

        private void WireInferenceApiKeyControls()
        {
            RefreshInferenceApiKeyStatus();
            if (_inferenceApiKeyControlsWired) return;
            _inferenceApiKeyControlsWired = true;

            var box = this.FindControl<TextBox>("InferenceApiKeyBox");
            var set = this.FindControl<Button>("InferenceApiKeySetButton");
            var clear = this.FindControl<Button>("InferenceApiKeyClearButton");
            if (box == null || set == null || clear == null) return;

            set.Click += (_, _) =>
            {
                _inferenceVault.SetInferenceApiKey(box.Text);
                box.Text = string.Empty;
                RefreshInferenceApiKeyStatus();
                // A re-saved key re-enables a monitor that 401'd: Apply(true) restarts it.
                AgentHost.ObservedActivityMonitorComposition.Instance.Apply(_settings.ScreenInferenceEnabled);
            };
            clear.Click += (_, _) =>
            {
                _inferenceVault.SetInferenceApiKey(null);
                RefreshInferenceApiKeyStatus();
            };
        }

        private void RefreshInferenceApiKeyStatus()
        {
            var status = this.FindControl<TextBlock>("InferenceApiKeyStatus");
            if (status == null) return;
            if (!_inferenceVault.IsVaultAvailable)
            {
                status.Text = "Secret store unavailable on this system; the key cannot be saved.";
            }
            else if (AgentHost.ObservedActivityMonitorComposition.Instance.IsDisabledUnauthorized)
            {
                status.Text = "The API rejected the stored key. Paste a new one and press Set.";
            }
            else
            {
                status.Text = _inferenceVault.HasInferenceApiKey() ? "A key is stored." : "No key stored.";
            }
        }
```

`VaultService` is in `Ntilde.Shell`; add `using Ntilde.Shell;` if the file does not already have it.

- [ ] **Step 5: SSH connection view: per-profile checkbox**

`src/Ntilde.App/Views/Ssh/NewSshConnectionView.axaml`: change `RowDefinitions="Auto,Auto,Auto"` to `RowDefinitions="Auto,Auto,Auto,Auto"` and add after the existing `<StackPanel Grid.Row="2" ...>...</StackPanel>`:

```xml
                    <StackPanel Grid.Row="3" Spacing="2" Margin="0,8,0,0">
                        <CheckBox IsChecked="{Binding AllowScreenInference}"
                                  Content="Allow screen inference for this connection" />
                        <TextBlock Text="When 'Screen inference' is enabled in Settings, the visible text of panes on this connection (secrets redacted) may be sent to the TypeSafe API to derive session status. Off by default; remote screens never leave the machine without this."
                                   Opacity="0.7" TextWrapping="Wrap" />
                    </StackPanel>
```

`src/Ntilde.App/ViewModels/Ssh/NewSshConnectionViewModel.cs`:

After the `AllowAgentAccess` property (~L188):

```csharp
    private bool _allowScreenInference;

    /// <summary>Per-profile opt-in for screen inference on this SSH connection. Default false.</summary>
    public bool AllowScreenInference
    {
        get => _allowScreenInference;
        set => SetField(ref _allowScreenInference, value);
    }
```

In the save builder (~L429), change `AllowAgentAccess = AllowAgentAccess` to `AllowAgentAccess = AllowAgentAccess,` and add `AllowScreenInference = AllowScreenInference`.

In the load block (~L495), after `AllowAgentAccess = sshProfile.AllowAgentAccess;` add `AllowScreenInference = sshProfile.AllowScreenInference;`.

- [ ] **Step 6: Build and run the App-level suites that touch these files**

PowerShell:
1. `scripts/build.ps1 build src/Ntilde.App`
2. `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~Settings|FullyQualifiedName~Ssh|FullyQualifiedName~MainWindow" --blame-hang-timeout 5m`
3. `scripts/build.ps1 test tests/Ntilde.Architecture.Tests` (`CompiledBindingTests` checks the new `{Binding AllowScreenInference}` resolves against the view model)

Expected: build succeeds; all selected tests PASS.

- [ ] **Step 7: Manual smoke test (record the result in the commit message)**

Run the app from the worktree (`scripts/build.ps1 run --project src/Ntilde.App` or launch the built exe). Then:
1. Settings → Agent Access: the "Screen inference" row and the API key row are visible; status says "No key stored."
2. Paste the key, press Set: status says "A key is stored." and the box is empty.
3. Turn "Screen inference" on, save. Open a local pane, run `ls`, wait 3 s. The debug log (`AppLogger.GetLogFilePath()`) gains one `[ScreenInference] pane=... outcome=Answered` line with no screen text.
4. In the MCP server or via `ntilde.get_session_status`, the pane's status carries `observed` confidence and an `Observed:` line.
5. Turn the toggle off, save: no further log lines.

- [ ] **Step 8: Commit**

```bash
git add src/Ntilde.App/AgentHost/ObservedActivityMonitorComposition.cs src/Ntilde.App/MainWindow.axaml.cs src/Ntilde.App/SettingsWindow.axaml src/Ntilde.App/SettingsWindow.axaml.cs src/Ntilde.App/Views/Ssh/NewSshConnectionView.axaml src/Ntilde.App/ViewModels/Ssh/NewSshConnectionViewModel.cs
git commit -m "feat(app): wire the observed-activity monitor, settings toggle, API key field, and SSH opt-in

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 11: Title-bar indicator while screen inference is on

**Files:**
- Modify: `src/Ntilde.App/MainWindow.axaml:265-287` (beside `AgentObserveIndicator`)
- Modify: `src/Ntilde.App/MainWindow.axaml.cs` (`PlaceAgentObserveIndicator` ~L9175, `RefreshAgentObserveIndicator` ~L5614, startup block ~L3713, `ApplyAgentHostSettingsLive` ~L5695)

**Interfaces:**
- Consumes: `ObservedActivityMonitorComposition.Instance` (`IsRunning`, `RequestCount`, `IsDisabledUnauthorized`, `StateChanged`) from Tasks 7 and 10.
- Produces: `MainWindow.RefreshScreenInferenceIndicator()` (internal, UI thread).

- [ ] **Step 1: Add the XAML element**

In `src/Ntilde.App/MainWindow.axaml`, immediately before the `<Button Name="AgentObserveIndicator"` element, add:

```xml
                <!-- Screen inference indicator: visible while pane text may be leaving the machine
                     (spec 2026-09-17 §2.5). Same non-catalog, locked placement rule as
                     AgentObserveIndicator: MainWindow.PlaceAgentObserveIndicator re-inserts it
                     before the observe dot after every RebuildTitleBar, so it has no off switch
                     other than the setting itself. -->
                <Button Name="ScreenInferenceIndicator"
                        IsVisible="False"
                        Background="Transparent"
                        BorderThickness="0"
                        Padding="6,0"
                        Focusable="False"
                        VerticalAlignment="Center"
                        ToolTip.Tip="Screen inference is on.">
                    <TextBlock Name="ScreenInferenceIndicatorGlyph" Text="◉" FontSize="10" Foreground="#4FB0D4" VerticalAlignment="Center"/>
                </Button>
```

- [ ] **Step 2: Place it on every title-bar rebuild**

In `PlaceAgentObserveIndicator(Panel host)` in `MainWindow.axaml.cs`, before the existing `var indicator = this.FindControl<Button>("AgentObserveIndicator");` add:

```csharp
            // Screen-inference light rides the same locked slot, placed first so the observe dot
            // stays the final child (its position guarantee is documented above).
            var inference = this.FindControl<Button>("ScreenInferenceIndicator");
            if (inference != null)
            {
                if (inference.Parent is Panel inferenceParent)
                {
                    inferenceParent.Children.Remove(inference);
                }
                host.Children.Add(inference);
            }
```

- [ ] **Step 3: Add the refresh method and subscribe**

After `RefreshAgentObserveIndicator()` add:

```csharp
        /// <summary>Shows the screen-inference light while the monitor runs. UI thread.</summary>
        internal void RefreshScreenInferenceIndicator()
        {
            var indicator = this.FindControl<Button>("ScreenInferenceIndicator");
            var glyph = this.FindControl<TextBlock>("ScreenInferenceIndicatorGlyph");
            if (indicator == null || glyph == null) return;

            var monitor = AgentHost.ObservedActivityMonitorComposition.Instance;
            indicator.IsVisible = monitor.IsRunning;
            if (!monitor.IsRunning) return;

            if (monitor.IsDisabledUnauthorized)
            {
                glyph.Foreground = new SolidColorBrush(Color.Parse("#D48A4F"));
                ToolTip.SetTip(indicator, "Screen inference is on, but the API rejected the stored key. Open Settings → Agent Access to replace it.");
            }
            else
            {
                glyph.Foreground = new SolidColorBrush(Color.Parse("#4FB0D4"));
                ToolTip.SetTip(indicator, $"Screen inference is on · {monitor.RequestCount} request(s) this session. Pane text (secrets redacted) is sent to the TypeSafe API when a pane goes quiet.");
            }
        }

        private void OnScreenInferenceStateChanged()
            => Dispatcher.UIThread.Post(RefreshScreenInferenceIndicator);
```

In the startup block, after the two monitor lines added in Task 10, add:

```csharp
            AgentHost.ObservedActivityMonitorComposition.Instance.StateChanged += OnScreenInferenceStateChanged;
            RefreshScreenInferenceIndicator();
```

In `ApplyAgentHostSettingsLive`, after the two monitor lines, add `RefreshScreenInferenceIndicator();`.

Wire the click once, next to the existing `agentObserveIndicator.Click` wiring (~L3908-3911):

```csharp
            var screenInferenceIndicator = this.FindControl<Button>("ScreenInferenceIndicator");
            if (screenInferenceIndicator != null)
            {
                screenInferenceIndicator.Click += (_, _) => OpenSettingsWindow();
            }
```

`OpenSettingsWindow` is whatever method the existing "Settings" menu item invokes; find it with `grep -n "MenuSettings\|OpenSettings" src/Ntilde.App/MainWindow.axaml.cs` and call that exact method. If it takes arguments (a tab name, for example), pass the value that opens the Agent Access tab if one exists, otherwise its default.

- [ ] **Step 4: Build and run the title-bar tests**

PowerShell:
1. `scripts/build.ps1 build src/Ntilde.App`
2. `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~TitleBar|FullyQualifiedName~MainWindow" --blame-hang-timeout 5m`
3. `scripts/build.ps1 test tests/Ntilde.Architecture.Tests` (`TitleBarUiFreedomTests` checks the title bar contract)

Expected: all PASS. If `TitleBarUiFreedomTests` fails, read its message: it enforces that non-catalog controls are placed by `PlaceAgentObserveIndicator`-style code, which this task follows. Adjust placement, not the test.

- [ ] **Step 5: Manual check**

Launch, enable Screen inference in Settings, save. The ◉ appears left of the observe dot; hovering shows the request count. Disable, save: it disappears.

- [ ] **Step 6: Commit**

```bash
git add src/Ntilde.App/MainWindow.axaml src/Ntilde.App/MainWindow.axaml.cs
git commit -m "feat(app): title-bar light while screen inference is on

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 12: Live replay test, documentation, and the full gate

**Files:**
- Create: `tests/Ntilde.App.Tests/Inference/ScreenActivityLiveTests.cs`
- Modify: `docs/plans/2026-07-07-agent-host-a2-status-design.md` (new section after "Status Model", ~L70)
- Modify: `docs/agent-host/known-limitations.md:7-52`
- Modify: `docs/mcp-dev-companion.md` only if it lists `get_session_status` tiers (grep for `heuristic`; add `observed` where the tiers are enumerated)

**Interfaces:**
- Consumes: `ScreenActivityClassifier`, `SystemOneClient`, `IApiKeySource` (Tasks 2-3).

- [ ] **Step 1: Write the env-gated live test**

`tests/Ntilde.App.Tests/Inference/ScreenActivityLiveTests.cs`:

```csharp
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.Inference;

namespace Ntilde.AppTests.Inference;

/// <summary>
/// Replays the screens measured on 2026-09-17 against the live API. Runs only when
/// TYPESAFE_API_KEY is set in the environment; skipped otherwise, so CI never calls out.
/// Costs about 8 requests of ~1k input tokens.
/// </summary>
[Trait("Category", "Live")]
public class ScreenActivityLiveTests
{
    private sealed class EnvKey : IApiKeySource
    {
        public string? TryGetKey() => Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
    }

    private static readonly bool HasKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TYPESAFE_API_KEY"));

    private const string ClaudeDone =
        "  Merged PR #468, ran 3 shell commands\n\n● Done. Both pieces are in place.\n\n  Release notes are rewritten and published.\n\n✻ Crunched for 49s · done 3:42 PM\n\n※ recap: released. Nothing is in progress.\n─────────────────────────────\n❯\n─────────────────────────────\n  ⏵⏵ auto mode on (shift+tab to cycle) · ← for agents";

    private const string ClaudeWorking =
        "● Read(src/Ntilde.App/Shell/TabStatusTracker.cs)\n  ⎿  Read 120 lines\n\n● Grep(pattern: \"MinAttentionBurst\")\n  ⎿  Found 3 files\n\n✻ Crunching… (12s · ↓ 1.4k tokens · esc to interrupt)\n─────────────────────────────\n❯\n─────────────────────────────\n  ⏵⏵ auto mode on (shift+tab to cycle) · ← for agents";

    private const string CargoRunning =
        "   Compiling syn v2.0.87\n   Compiling serde_derive v1.0.215\n   Compiling russh v0.45.0\n    Building [=======>                 ] 112/389: russh, tokio(build)";

    private const string AptPrompt =
        "Reading package lists... Done\nThe following NEW packages will be installed:\n  htop\nNeed to get 148 kB of archives.\nDo you want to continue? [Y/n] ";

    private const string SudoPrompt = "user@host:~$ sudo systemctl restart nginx\n[sudo] password for user: ";

    private const string PipelineIdle =
        "===BQ_JOB===\nUser does not have bigquery.jobs.create permission in project example-analytics.\nuser@host:~/pipeline$ echo SA_AUTH_VERIFIED\nSA_AUTH_VERIFIED\nuser@host:~/pipeline$ ";

    public static TheoryData<string, string, ScreenActivity> Screens => new()
    {
        { "ssh pipeline idle", PipelineIdle, ScreenActivity.IdleShellPrompt },
        { "ssh empty prompt", "user@ai-gateway:~$ ", ScreenActivity.IdleShellPrompt },
        { "claude finished", ClaudeDone, ScreenActivity.WaitingForUser },
        { "blank", "", ScreenActivity.UnknownBlank },
        { "claude working", ClaudeWorking, ScreenActivity.AgentWorking },
        { "cargo running", CargoRunning, ScreenActivity.CommandRunning },
        { "apt prompt", AptPrompt, ScreenActivity.WaitingForUser },
        { "sudo prompt", SudoPrompt, ScreenActivity.WaitingForUser },
    };

    [Theory]
    [MemberData(nameof(Screens))]
    public async Task Live_classifier_matches_the_measured_activity(string name, string screen, ScreenActivity expected)
    {
        Assert.SkipUnless(HasKey, "TYPESAFE_API_KEY not set; live test skipped.");

        var classifier = new ScreenActivityClassifier(new SystemOneClient(new HttpClient(), new EnvKey()));
        var result = await classifier.ClassifyAsync(new ScreenSample(screen, 34, 101), CancellationToken.None);

        Assert.Equal(ScreenClassificationOutcome.Answered, result.Outcome);
        Assert.Equal(expected, result.Answer!.Activity);
        Assert.True(result.Answer.Confidence >= 0.8, $"{name}: confidence {result.Answer.Confidence:0.00} below 0.8");
    }
}
```

`Assert.SkipUnless` is xunit.v3 (the repo already uses it for Skia gating). If `Trait` requires a `using Xunit;`, the project's global using covers it.

- [ ] **Step 2: Run it both ways**

PowerShell, without the variable: `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~ScreenActivityLiveTests" --blame-hang-timeout 5m`
Expected: 8 skipped.

Then with the key for this one invocation: `$env:TYPESAFE_API_KEY = (Get-Content C:\Users\behna\AppData\Local\Temp\claude\D--projects-nova2\e67f7234-75e2-4748-931b-6bcc80fbb0a1\scratchpad\typesafe.key); scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~ScreenActivityLiveTests" --blame-hang-timeout 5m; Remove-Item Env:TYPESAFE_API_KEY`
Expected: 8 passed. If one screen fails, record the answer in the commit message; do not loosen the assertion in this task, the wording lives in `ScreenActivityClassifier` and a change there is its own commit with the classifier tests.

- [ ] **Step 3: Amend the A2 design doc**

In `docs/plans/2026-07-07-agent-host-a2-status-design.md`, replace the bullet

```
- derive status from **owned signals** (PTY process state, shell-integration
  events, alt-screen state) — never from screen scraping
```

with

```
- derive status from **owned signals** (PTY process state, shell-integration
  events, alt-screen state). Screen text is never consulted by default; the
  opt-in **observed** tier (2026-09-17 amendment, below) is the one exception
  and reports itself as such
```

and add after the "Status Model" table's stall paragraph (before `## Architecture`):

```
### Observed tier (2026-09-17 amendment)

A third confidence, `observed`, added by
`docs/superpowers/specs/2026-09-17-screen-inference-observed-status-design.md`.
When the user enables Screen inference (and, for SSH, allowlists the profile),
a quiet pane's redacted visible text is judged once by a TypeSafe System One
model. The judgment is consulted only while no output has arrived since the
screen was captured, and only above `ObservedOverrideThreshold` (0.85):

| Tier before | Observation | Result |
|---|---|---|
| heuristic | `command_running` / `agent_working` | `running` (observed) |
| heuristic | `waiting_for_user` / `idle_shell_prompt` | `awaitingInput` → `idle` after 60 s (observed) |
| precise, command in flight | `waiting_for_user` | `awaitingInput` (observed): a program inside the command waits on the user |
| precise, at prompt | any | unchanged (precise) |
| any | `unknown_blank`, or below threshold, or stale | unchanged |

Exited and alt-screen are never overridden. The status DTO carries the
observation and the threshold so clients never hard-code either.
```

- [ ] **Step 4: Update known-limitations**

In `docs/agent-host/known-limitations.md`, under "Heuristic session status can't see inside WSL or remote SSH", replace the `**Planned fix:**` paragraph with:

```
**Mitigation (shipped):** the opt-in **observed** tier. With Settings → Agent
Access → Screen inference on (and `AllowScreenInference` on the SSH profile), a
quiet pane's redacted visible text is judged by the TypeSafe API and the
answer overrides the heuristic tier. It also refines a precise `running` for an
agent CLI (Claude Code, Codex) into `awaitingInput` when the agent has finished
and is waiting at its input box. Status then reports `observed` confidence and
an `observation` object. Off by default; nothing leaves the machine otherwise.

**Still planned:** foreground-process reporting for WSL/SSH so the heuristic
tier is accurate without sending any text anywhere.
```

- [ ] **Step 5: Run the full App.Tests gate once**

PowerShell: `scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m 2>&1 | Tee-Object -FilePath $env:TEMP\apptests-screen-inference.log`
Expected: all pass except entries listed in `tests/app-tests-known-flaky.txt`. Takes about 4 minutes. Do not start any other App.Tests run while it is going.

Then: `scripts/build.ps1 test tests/Ntilde.McpServer.Tests` and `scripts/build.ps1 test tests/Ntilde.Architecture.Tests` and `scripts/build.ps1 test tests/Ntilde.Platform.Tests`.
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add tests/Ntilde.App.Tests/Inference/ScreenActivityLiveTests.cs docs/plans/2026-07-07-agent-host-a2-status-design.md docs/agent-host/known-limitations.md
git commit -m "docs(agent-host): observed tier amendment, known-limitations update, live replay test

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Done criteria

- All twelve tasks committed on `feat/screen-inference`.
- `Ntilde.App.Tests`, `Ntilde.McpServer.Tests`, `Ntilde.Architecture.Tests`, `Ntilde.Platform.Tests` green locally.
- Manual smoke test from Task 10 step 7 and Task 11 step 5 performed and noted in the commit messages.
- The release-notes line for the next GitHub release: "Screen inference (opt-in): an `observed` session-status tier driven by a TypeSafe judgment over a quiet pane's redacted screen. Fixes WSL/SSH status and detects a waiting agent CLI. Off by default; SSH profiles opt in individually." Add it when the release is cut; there is no notes file in the repo.
