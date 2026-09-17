# Screen inference: an observed tier for session status

Date: 2026-09-17
Status: designed (not implemented)
Touches: `src/Ntilde.App/AgentHost` (status machine, new monitor), a new leaf project
`src/Ntilde.Inference`, `src/Ntilde.AgentHost.Contracts` (additive wire fields),
`src/Ntilde.McpServer/Tools/SessionTools.cs`, `TerminalSettings`, `SshProfile`, the SSH
connection view, the settings window, and the docs for the A2 status model.

## Summary

Add a third confidence tier, **observed**, to the agent-host session status model. It is
driven by one small AI judgment over the visible text of a pane, made by a TypeSafe
System One model (Jev) through its HTTP API, and it fills the two gaps the existing
tiers admit in their own remarks:

- the **heuristic** tier's child-process probe cannot see inside WSL or a remote SSH
  host, so a busy remote command reads as `awaitingInput`;
- the **precise** tier reports `running` for the whole life of an agent CLI such as
  Claude Code, because the shell sees one long command, even when the agent has finished
  its answer and is waiting at an empty input box.

The judgment runs only when a pane has just gone quiet after output, only for panes the
user has opted in, and only over text that has passed the existing secrets filter. It
gates status kinds, the tab dot, and the tab attention marker. It never triggers input,
actions, or notifications that execute anything.

## Decisions (already made with the owner)

| Question | Decision |
|---|---|
| Relation to the A2 rule "never derive status from screen text" | Amend it. Add an explicit `observed` tier so callers can see the source. |
| Which panes may have their screen sent | Master setting, default off, covers local panes. SSH panes additionally need a per-profile flag, default off. |
| API key storage | The OS secret store behind `ISecretStore` / `VaultService`, the same path SSH passwords take. |
| Consumers in this milestone | Status machine, MCP tools, and the tab strip, all fed from one integration point. |
| Where the client lives | A new leaf project `Ntilde.Inference`. The architecture tests forbid networking in `Ntilde.CommandAssist`. |
| Status-bar indicator | In scope. A small indicator while the feature is enabled, with a request count in its tooltip. |

## Measurements that motivated the design

Twenty-five calls against `jev-1.13.0` on 2026-09-17, using screens read from the
running app through the MCP server and the repo's own error fixtures. Pane activity was
classified correctly on 8 of 8 screens (four live, four constructed). Typical latency
from this machine was 650 to 800 ms, with one outlier at 2.7 s. Input was 700 to 1100
tokens per screen. The measurement script and the exact question wording are in the
session scratchpad as `measure.py`; the wording is reproduced in §3.

The live pane that motivated the precise-tier rule was reported `running (precise)` by
the status machine while showing a finished Claude Code answer and an empty `❯` box.
Jev answered `waiting_for_user` at confidence 0.92 and `needs_attention` at 0.79.

## 1. Status model

### 1.1 Types

`AgentSessionStatusConfidence` gains `Observed`. `AgentHostProtocol.StatusConfidences`
gains `public const string Observed = "observed"`. `AgentStatusWire.ToWire` maps it.

A new record in `Ntilde.App/AgentHost`:

```csharp
public sealed record ScreenObservation
{
    public required ObservedActivity Activity { get; init; }   // enum below
    public required double Confidence { get; init; }           // Choice confidence, 0..1
    public required double NeedsAttention { get; init; }       // Noul probability
    public required double LastCommandFailed { get; init; }    // Noul probability
    public required DateTimeOffset ObservedAt { get; init; }   // when the screen was captured
    public required long OutputSequence { get; init; }         // pane output counter at capture
}

public enum ObservedActivity { CommandRunning, AgentWorking, WaitingForUser, IdleShellPrompt, UnknownBlank }
```

`AgentSessionStatusSnapshot` gains `ScreenObservation? Observation` and
`int ObservedOverrideThresholdPercent` (constant, see §1.3).

### 1.2 New signal

`AgentSessionStatusMachine.NotifyObserved(ScreenObservation observation)` stores the
observation under the gate and recomputes the kind. It is called from the monitor's
completion path, off the UI thread; the machine is already internally locked and
`RunUnderGate` already handles cross-thread signals.

`NotifyOutput` increments a new `_outputSequence` counter and exposes it via the
snapshot so the monitor can record it at capture time.

### 1.3 Override rules

`ComputeKind` applies these in order. "Fresh" means
`observation.OutputSequence == _outputSequence`: no output has arrived since the screen
was captured.

1. `_exited` → `Exited`. `_altScreenActive` → `Running`. Unchanged; PTY truth and a TUI
   owning the screen are never overridden.
2. **Precise tier, command in flight.** If `_precise && _commandInFlight` and there is a
   fresh observation with `Activity == WaitingForUser` and
   `Confidence >= ObservedOverrideThreshold`, the kind is `AwaitingInput` and the
   reported confidence is `Observed`. This is the only precise override. A precise
   `Running` is otherwise kept, and a precise prompt-ready (`!_commandInFlight`) is never
   overridden by any observation.
3. **Heuristic tier.** If `!_precise` and there is a fresh observation with
   `Confidence >= ObservedOverrideThreshold` and `Activity != UnknownBlank`, the
   observation decides:
   - `CommandRunning`, `AgentWorking` → `Running`
   - `WaitingForUser`, `IdleShellPrompt` → `AwaitingInput`, then the existing 60 s
     silence rule may promote to `Idle` exactly as today
   The reported confidence is `Observed`.
4. Otherwise the existing rules apply unchanged and the confidence is `Precise` or
   `Heuristic` as today.

`ObservedOverrideThreshold` is `0.85`, a `public const double` on the machine, reported
in the DTO as an integer percent so clients never hard-code it. Starting value from the
measurements; tuned later against captured screens.

Stall is unchanged: a `Running` kind at any confidence with 30 s of silence emits
`stalled`. Because an observed `Running` requires freshness and freshness requires no
new output, an observed `Running` that lasts 30 s will emit `stalled`, which is the
correct reading of a remote command that has gone silent.

### 1.4 What the tab strip reads

`MainWindow.RefreshTabStatuses` already snapshots every registration once per tick. Two
additions:

- `HasRunningCommand` for a tab becomes true when the snapshot kind is `Running` at any
  confidence, which now includes observed. No code change beyond the tier existing.
- When the snapshot carries a fresh observation with
  `NeedsAttention >= AttentionThreshold` (0.7, a constant on `TabStatusTracker`) and the
  tab is not selected, call a new `TabStatusTracker.NoteObservedAttention()`, which sets
  `_attention` exactly like `NoteBell()`. Selecting the tab clears it as today.

The 2 s working window and 5 s burst rule remain the fallback whenever there is no
fresh observation.

## 2. Components

### 2.1 `Ntilde.Inference` (new leaf project)

BCL plus `System.Net.Http`. No project references, no Avalonia, no `Ntilde.App`, no
`Ntilde.CommandAssist`. `TreatWarningsAsErrors` on, like `Ntilde.AgentHost.Contracts`.
Added to `Ntilde.sln` and referenced by `Ntilde.App`. Adding a source project needs no
CI change; the solution build picks it up.

Files:

- `SystemOneClient` — `POST https://api.typesafe.ai/v1/systemone` with
  `Authorization: Bearer <key>` and a source-generated JSON context
  (`InferenceJsonContext`) because the app publishes NativeAOT. Returns a
  `SystemOneResult` discriminated by outcome: `Ok(answers, usage)`, `Unauthorized`,
  `Rejected(422 body)`, `RateLimited`, `Overloaded`, `TransportFailure(exception)`.
  Timeout 5 s per request. It does not retry or back off; that policy belongs to the
  caller so it can be tested with a fake clock.
- `IApiKeySource` — `string? TryGetKey()`. The App implements it over the vault.
- `IScreenActivityClassifier` — `Task<ScreenActivityAnswer?> ClassifyAsync(ScreenSample sample, CancellationToken ct)`.
  `ScreenSample` is `{ string Text; int Rows; int Cols }`. The answer carries the
  activity, confidence, the two probabilities, and token usage; `null` means "no answer
  this time" with the reason recorded on the classifier for the log.
- `ScreenActivityClassifier` — builds the three questions (§3), calls the client, maps
  the answer. The criteria text lives here as constants with their own tests.
- `InferenceModels` — the request and response DTOs.

### 2.2 `ObservedActivityMonitor` (`src/Ntilde.App/AgentHost`)

One instance, owned like `AgentHostService.Instance`, started and stopped from the same
window code path that applies agent-host settings. It has its own 1 s timer and does not
depend on the IPC endpoint being up, so the tab strip benefits with agent access off.

Constructor takes: the registry, an `IScreenActivityClassifier`, an `ISecretsFilter`,
a `Func<Guid, bool>` SSH-profile probe, a `Func<DateTimeOffset>` clock, and an
`Action<string>` log sink. Every dependency is injectable for tests.

Per tick, for each registration:

1. **Eligible?** Master setting on, a key is present (the classifier reports this), and
   either `registration.Kind == "local"` or `registration.ProfileId` passes the SSH
   probe. Ineligible registrations are skipped without touching the buffer.
2. **Due?** All of:
   - the registration's status snapshot `OutputSequence` differs from the sequence of the
     last observation sent for this pane (new output since last time);
   - `now - snapshot.LastOutputAt >= QuietWindow` (2 s);
   - `now - lastRequestAt >= MinInterval` (5 s) for this pane;
   - no request is in flight for this pane;
   - the global backoff (§4) has expired.
3. **Capture.** Under `buffer.Lock.EnterReadLock()`, exactly as `HandleReadScreen` does,
   capture `BufferSnapshot.Capture(buffer, includeAttributes: false)`, rows and cols.
   Join the lines with `\n`, trim trailing whitespace per line. Record the snapshot's
   `OutputSequence`.
4. **Skip if unchanged.** If the joined text equals the last text sent for this pane,
   record `lastRequestAt` and skip. If the text is blank, skip likewise.
5. **Redact.** `secretsFilter.Redact(text).RedactedText`. This is the same filter the
   command-assist capture path uses; it is the audited boundary and a test asserts it is
   applied.
6. **Send.** Fire the classifier on the thread pool. On completion:
   - `null` → log the reason, apply backoff if the reason is rate-limit or overload,
     disable for the session if unauthorized (§4).
   - answer → if the registration still exists and its current `OutputSequence` still
     equals the captured one, call `StatusMachine.NotifyObserved(...)`. Otherwise drop
     the answer as stale and log it as such.
   Either way, log one line: pane id, bytes sent, latency, outcome, tokens. Never the
   text.

Per-pane bookkeeping (`lastRequestAt`, last sequence sent, last text hash, in-flight
flag) lives in a dictionary keyed by registration, pruned on `SessionUnregistered`.

### 2.3 Wire protocol and MCP (additive; protocol version stays 1)

`SessionStatusDto` gains:

```json
"observedOverrideThresholdPercent": 85,
"observation": {
  "activity": "waitingForUser",
  "confidence": 0.92,
  "needsAttention": 0.79,
  "lastCommandFailed": 0.04,
  "ageMs": 1830
}
```

`observation` is omitted when there is none or it is stale. `SessionInfo.confidence`
may now be `"observed"`.

`SessionTools.FormatStatus` prints one extra line when `observation` is present:
`observed: waitingForUser (0.92) · attention 0.79 · failed 0.04 · 1.8 s ago`. The
`list_sessions` column already prints the confidence string and needs no change beyond
the new value. The `get_session_status` description's limitation paragraph is rewritten
to describe when the observed tier applies and what it means.

### 2.4 Settings and opt-in

- `TerminalSettings.ScreenInferenceEnabled` (bool, default false). Registered in the MCP
  `SettingsTools` field lists (two drift-guard tests fail otherwise) and documented in
  its schema table. Not needed in `TerminalPane.ApplySettings`, since the pane does not
  read it.
- `SshProfile.AllowScreenInference` (bool, default false) in `Ntilde.Platform`. Persisted
  by `JsonSshProfileStore`, surfaced as a checkbox beside `AllowAgentAccess` in
  `NewSshConnectionView`, wired through `NewSshConnectionViewModel`. Registered in the
  MCP `ConnectionProfileTools` schema and `RequireBoolType` list.
- The API key: `VaultService` gains `GetInferenceApiKey()`, `SetInferenceApiKey(string)`,
  `ClearInferenceApiKey()` over a fixed secret-store key `ntilde/inference/typesafe-api-key`.
  The settings window gets a password-style field with Set and Clear buttons in the
  agent-access section; the value is written straight to the vault and never placed on
  `TerminalSettings`. `settings.json` never contains the key. The drift guards that warn
  on a `Password`-shaped field stay untouched because no such field is added.
- `MainWindow` applies the setting by calling `ObservedActivityMonitor.Instance.Apply(enabled)`
  next to the existing `AgentHostService.Instance.Apply(...)` calls, and hands it the
  SSH probe `IsSshProfileScreenInferenceAllowed` (sibling of `IsSshProfileAgentAllowed`).

### 2.5 Indicator

A status-bar item in the style of the agent-observe indicator, visible while the monitor
is running. Tooltip: `Screen inference on · N requests this session`. Clicking it opens
the settings page. It is the visible cue that pane text may leave the machine.

## 3. The judgment

State sent: `{ "screen": "<redacted text>", "rows": 34, "cols": 101 }`. Nothing else.

Three questions over that state, asked together in one request:

- **`activity`** (Choice): "Look at `screen`, the visible text of a terminal pane (last
  line is the bottom). What is happening in this pane right now?" with criteria
  `command_running`, `agent_working`, `waiting_for_user`, `idle_shell_prompt`,
  `unknown_blank`, each defined contrastively (the wording in `measure.py` is the
  starting point and is copied into `ScreenActivityClassifier` verbatim).
- **`needs_attention`** (Noul): "A user stepped away from this pane. Based on `screen`,
  does the pane now need them to come back and act? Idle shell prompts with nothing new
  do NOT need attention."
- **`last_command_failed`** (Noul): "Did the MOST RECENT completed command in `screen`
  end with an error? Judge only the last command, not earlier ones."

`last_command_failed` is carried in the observation and the DTO for MCP callers but has
no consumer in the app in this milestone.

## 4. Privacy, failure handling, cost

**What leaves the machine.** The redacted visible screen text, rows, and cols. Never the
title, profile name, hostname, cwd, scrollback, typed input, or pane id. A test on the
monitor asserts the sample it hands the classifier has exactly those three fields.

**Visibility.** The indicator (§2.5) while enabled, and one `AppLogger` line per request.

**Failure.**

- Any transport failure or timeout: the observation is not updated, the status machine is
  untouched, and the tier stays whatever it was. Logged.
- `Unauthorized`: the monitor disables itself for the process and logs once. The settings
  page shows "API key rejected" on next open. Re-saving a key re-enables it.
- `RateLimited` or `Overloaded`: process-wide exponential backoff, 5 s doubling to a cap
  of 5 min, reset on the next success.
- Malformed or partial answers: dropped, logged.
- Nothing in the monitor's tick may throw; each registration is wrapped like
  `SweepStatuses` wraps its probes.

**Cost bound.** `MinInterval` 5 s and the unchanged-text check bound a continuously
streaming pane to 12 requests a minute, about 1,000 input tokens each. At the published
rate ($42 per billion input tokens) that is under a cent an hour for that pane. Idle
panes cost nothing.

**Adversarial text.** The TypeSafe docs state that instructions inside state can steer
answers. An observation gates only status kinds, the tab dot, and an attention marker.
No code path executes anything on the strength of an observation.

## 5. Testing

All new tests live in `tests/Ntilde.App.Tests` so no test project is added and `ci.yml`
is untouched. `Ntilde.App.Tests` already references `Ntilde.App`, which will reference
`Ntilde.Inference`.

- **Client** (`SystemOneClientTests`): fake `HttpMessageHandler`. Asserts the request
  URL, bearer header, JSON shape (state, model `jev-latest`, three questions); parses a
  recorded success body; maps 401, 422, 429, 529 and a thrown `HttpRequestException` to
  the right outcome; times out.
- **Classifier** (`ScreenActivityClassifierTests`): maps each `activity` string to the
  enum; handles a missing question in the answer; the criteria constants are non-empty
  and distinct.
- **Status machine** (`AgentSessionStatusMachineObservedTests`): with the injectable
  clock, each rule in §1.3: precise override only when a command is in flight; never
  overrides a precise prompt; heuristic replacement; staleness after `NotifyOutput`;
  threshold boundary; `UnknownBlank` ignored; stall still fires on an observed Running;
  `Observed` appears in the snapshot only when the observation decided the kind.
- **Monitor** (`ObservedActivityMonitorTests`): fake classifier, fake clock, in-memory
  registry. Not eligible when the setting is off, when the key is absent, or for an SSH
  pane without the profile flag. Not due until the quiet window, the minimum interval,
  and new output all hold. Unchanged text skips. Blank text skips. Redaction is applied
  (a fake filter marks the text). Stale answers are dropped. Backoff after a rate-limit
  answer. Disabled after unauthorized. A throwing classifier does not kill the tick.
- **Tab strip** (`TabStatusTrackerTests` addition): `NoteObservedAttention` sets and
  selection clears.
- **Wire** (`AgentStatusWireTests`, `Ntilde.McpServer.Tests` formatter test): the
  `observed` string, the DTO fields, and the extra formatted line.
- **Architecture** (`LayeringTests`): `Ntilde.Inference` must not depend on Avalonia,
  `Ntilde.Shell`, `Ntilde.Controls`, `Ntilde.CommandAssist`, or any test assembly. The
  existing `CommandAssist_must_not_depend_on_networking` test stays untouched and green.
- **Live** (`ScreenActivityLiveTests`): one test that runs only when
  `TYPESAFE_API_KEY` is set, otherwise `Assert.Skip`. Replays the eight screens from the
  measurement and asserts the expected activity for each. Marked so it is never run in
  CI.

## 6. Documentation

- Amend `docs/plans/2026-07-07-agent-host-a2-status-design.md` with an "Observed tier
  (2026-09-17 amendment)" section that replaces the "never from screen scraping"
  sentence with the opt-in rule and links here.
- Update `docs/agent-host/known-limitations.md`: the WSL/SSH limitation now says the
  observed tier closes it when enabled, and the agent-CLI case is described.
- Update the MCP tool descriptions and the settings and connection-profile schema tables.
- Release notes line for the next release.

## 7. Out of scope

- The error-triage provider behind `IAssistContentProvider` (item 2 of the survey).
- A per-pane toggle, or threshold controls in the settings UI.
- Any consumer for `last_command_failed` beyond the DTO.
- OS notifications or toasts driven by observations.
- Sending scrollback, attributes, or anything beyond the visible text.
- A model or provider selector; `jev-latest` is the only model.

## 8. Open items to settle during planning

- The exact `BufferSnapshot` field that gives the visible lines, and whether an output
  counter already exists on the buffer or the pane that the machine can reuse instead of
  adding `_outputSequence`.
- Where the indicator sits in the title-bar catalog and whether it reuses the
  agent-observe glyph.
- Whether `ISecretsFilter` is reachable from the monitor's composition point, or the
  monitor takes the fallback instance `TerminalPane` already keeps for headless cases.
