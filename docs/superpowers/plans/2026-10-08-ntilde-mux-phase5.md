# ntilde multiplexer Phase 5 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the multiplexer on `dev-mux` into a shipped product: close the Phase 4 review items, let agents see windowless sessions, make the default-on world usable, keep sessions alive across app updates, list remote sessions, give persisted native tabs SFTP, and prepare the `dev-mux` → `main` release.

**Architecture:** Every change is additive on the existing Phase 0–4 design (ARCHITECTURE §8.1–§8.2): the daemon protocol gains optional fields and one optional method (`readScreen`), still Min 1 / Max 2; the GUI keeps one `MuxConnectionHosts` per window; the agent host gains a windowless-session source behind a seam; the update path keeps a compatible daemon instead of shutting it down.

**Tech Stack:** .NET 10 (C#, NativeAOT for releases), Avalonia 12, xunit v3 (`[AvaloniaFact]` for UI), rusty_ssh (Rust, russh) over P/Invoke, Velopack 1.2.0, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-08-ntilde-mux-phase5.md` (the maintainer's brief, verbatim, plus §R rulings). Background: `docs/superpowers/specs/2026-10-05-ntilde-mux-phase4.md` (§14 follow-ups, §15 as-built).

## Global Constraints

- Build and test only through `scripts/build.ps1` (Windows) / `scripts/build.sh`; one project per invocation; never raw `dotnet build`/`dotnet test`.
- App.Tests runs as two lanes, never unfiltered and never concurrently with another build: `--filter "Category!=Replay&Category!=RenderMetrics&Category!=PtySmoke&Category!=Stress&Category!=GoldenSharedPng&Lane!=PlatformBoot" --blame-hang-timeout 5m`, then `--filter "Lane=PlatformBoot&Category!=GoldenSharedPng" --blame-hang-timeout 5m`. Other projects: `--filter "Category!=Replay&Category!=RenderMetrics&Category!=PtySmoke&Category!=Stress&Category!=GoldenSharedPng"`.
- Mux protocol: `MuxProtocol.MinSupportedVersion = 1`, `MaxSupportedVersion = 2` stay. New wire members are optional (absent from a v1/v2 peer, `JsonIgnore(WhenWritingNull/WhenWritingDefault)` so they stay off the wire when unset); the new `readScreen` method is answered by an older daemon with `protocol_error`, which the client maps to "unsupported". Every new wire DTO is registered in `src/Ntilde.Mux.Contracts/MuxJsonContext.cs` (NativeAOT).
- Layering (Architecture.Tests must pass unchanged): `Ntilde.Mux` references no App/Avalonia/Platform; `Ntilde.Mux.Daemon` references only `Ntilde.Mux`; `Ntilde.Platform` references only Pty; `Ntilde.Mux.Contracts` and `Ntilde.AgentHost.Contracts` are leaves (no project references).
- Credentials: no credential-path behaviour change except Task 2's jump-host fix. The once-per-attempt rule (a saved password is offered at most once per attempt) and the never-empty-password rule (a password prompt is never answered with an empty string — abort instead) stand.
- With `SessionPersistence` explicitly `"Off"`, nothing in the app changes (no daemon spawned, no new dialogs, no new commands visible).
- Pane lines and toasts built from server-controlled text must strip control characters (Task 5); pane status lines are built from cause enums, never from server text.
- No TCP listener; the local endpoint stays a 0600 UDS in a 0700 directory (Unix) / a current-user pipe (Windows). No thread-pool work on the output path. The proxy stays a dumb pump.
- Never touch the user's real Windows Credential Manager entries or real ntilde install in tests or experiments. Never use `git stash`.
- Every behaviour change lands with a test that fails before the change (run it red first, record that in the task report).
- New `TerminalSettings` fields are avoided (R1 uses a flag file). If one becomes unavoidable, it must be added to `TerminalPane.ApplySettings`' whitelist only if panes read it, and to `src/Ntilde.McpServer/Tools/SettingsTools.cs` `BoolFields`/`StringFields` and `KnownFields` (two gating drift-guard tests).
- Copy style: user-facing strings are plain sentences, no "please", `…` for "opens a dialog", em dash `—` in banners as existing ones do.
- Commits: one or more per task, message `type(scope): summary`, body ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. A remote host whose link stalls (proxy alive, no bytes read) while the user types, resizes, closes or detaches several panes: the UI must stay responsive, and after the link recovers the frames arrive in call order with nothing duplicated. (Task 1: order + promptness test with a stalled `FakeMuxServerEnd`.)
2. An agent listing sessions while a remote host is reconnecting or prompting for a password: the agent path must never start a connection or a prompt, and must answer within the MCP 10 s round trip. (Task 12: `CurrentClient`-only test with a host whose `GetClient` would throw.)
3. An app update applied while shells run, on a machine where the daemon was started by the *previous* build: the shells survive, the new window reattaches, and the user is told the multiplexer is from the previous build — once, not per pane. (Tasks 20–24.)
4. A session-file restore after a reboot for a default-on user: fresh shells, no loss toast; but a daemon crash since boot is still announced. (Task 19.)
5. A user who closes a tab that shares a remote session while its link is down: the other client's shell must not be killed. (Task 26: share-close disposition test.)

---

## Part A — §4 review items

### Task 1: Non-blocking, ordered sends in `MuxClient`

Every UI-thread send can block today: `MuxClient`'s outbound queue is a `BlockingCollection<MuxOutboundFrame>` bounded at `OutboundCapacity = 1024` frames (`src/Ntilde.Mux/MuxClient.cs:26,33`), and both `Enqueue` (`:309-320`, used by every request, which enqueues before its first await) and `Post` (`:322-332`, fire-and-forget: input, resize, detach, kill) call `_outbound.Add`, which blocks while the queue is full. A stalled link therefore freezes the UI thread in: local `MainWindow.KillMuxSessionOnClose` (`MainWindow.axaml.cs:~7356` `mux.KillAsync()`), Detach/Leave (`DisposeControlTree` `~:7295-7301` `detaching.Detach(...)`), Reconnect (`TerminalPane.axaml.cs:~5396-5397` `faulted.Kill(); session.Dispose();`), input (`TerminalView.cs:1240,1252,1684,2971`; `TerminalPane.axaml.cs:900,2910,3663`; `MainWindow.axaml.cs:3768,3785,6664-6665,8085,8108`) and resize (`TerminalPane.axaml.cs:4451,4814,4843,5049`). B1 (`MuxConnectionHost.HandOffKill`, `_killSends`) fixed only remote close kills. Ruling R6: fix it once, in `MuxClient`.

**Files:**
- Modify: `src/Ntilde.Mux/MuxClient.cs` (the send path: `Enqueue`, `Post`, `OnDisconnected`, new overflow pump)
- Modify: `src/Ntilde.Mux/MuxClientOptions.cs` (new `MaxOverflowBytes`, default 8 MiB)
- Test: `tests/Ntilde.Mux.Tests/Client/MuxClientNonBlockingSendTests.cs` (new)
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxLocalCloseStalledLinkTests.cs` (new)
- Reuse: `tests/Ntilde.App.Tests/Shell/Mux/Remote/FullSendQueue.cs` (`FillAsync`, `ReturnsPromptlyAsync`, `DrainUntilRequestAsync`), `tests/Ntilde.Mux.Tests/Support/FakeMuxServerEnd.cs` (`Create(pipeCapacityBytes)`, `AcceptHelloAsync`, `ReadRequestAsync`). If `FullSendQueue` is App.Tests-only, move it to `tests/Ntilde.Mux.Tests/Support/` and link it into App.Tests the way `MuxTestHost` is linked (`tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj`, `Link="MuxSupport\…"`).

**Interfaces:**
- Produces: `MuxClient` sends never block the caller. Semantics: frames reach the wire in the order their calls were made (across `Post` and `Enqueue`); once the overflow exceeds `MuxClientOptions.MaxOverflowBytes` the client faults (`Dispose`-equivalent disconnect with reason `"send overflow"`), which the existing reconnect/drop paths handle. No public signature changes.

- [ ] **Step 1: Write the failing tests** (`MuxClientNonBlockingSendTests`, xunit `[Fact]`, Category none):
  - `Every_send_returns_promptly_while_the_outbound_queue_is_full`: `var daemon = FakeMuxServerEnd.Create(pipeCapacityBytes: 4096)`; connect a `MuxClient` over `daemon.ClientEnd`, `await daemon.AcceptHelloAsync(2)`; open a session (`client.OpenSession(id, …, MuxAttachMode.Shared)` against a fake attach reply, or use `client.SendInput` via a `MuxClientSession` built the way `MuxClientSessionTests` does); `await FullSendQueue.FillAsync(client)` (the daemon reads nothing); then each of these must complete within `FullSendQueue.ReturnsPromptlyAsync` (5 s): `session.SendInput("x")`, `session.Resize(100, 30)`, `session.Detach(userDetached: true)`, `session.Kill()`, and `client.KillAsync(otherId)` — for `KillAsync` assert only that the call *returns a task* promptly (`Task t = client.KillAsync(id)` measured around the call), not that it completes.
  - `Frames_reach_the_wire_in_call_order_after_a_stall`: same setup, fill, then call in order `SendInput(s1,"a")`, `KillAsync(s2)` (request), `SendInput(s1,"b")`, `Resize(s1,…)`, `Detach(s1)`; start draining on the daemon side; read frames until the detach request; assert the sequence of (kind, session, payload/method) after the filler frames is exactly `Input a`, `Request kill s2`, `Input b`, `ResizeEvent`/resize request, `Request detach s1`.
  - `A_send_overflow_past_the_cap_disconnects_the_client`: `MaxOverflowBytes = 64 * 1024`; fill; post 100 × 1 KiB inputs; assert `client.IsConnected` becomes false within 5 s and a pending request's task faults with `IOException`.
  - `Sends_after_disconnect_are_dropped_without_throwing`: dispose the daemon end; `SendInput` returns; `KillAsync` faults with `IOException` (today's contract).

- [ ] **Step 2: Run them red.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~MuxClientNonBlockingSendTests"`. Expected: the promptness test fails on the first call (timeout), the order test cannot run past the fill (timeout), the overflow test fails (no cap). Record the output in the report.

- [ ] **Step 3: Implement the overflow pump in `MuxClient`.** One gate, one FIFO, one pump work item at a time; the frame stays in the overflow queue until `Add` returned so a later `TryAdd` cannot overtake it:

```csharp
private readonly object _sendGate = new();
private readonly Queue<MuxOutboundFrame> _overflow = new();
private long _overflowBytes;          // guarded by _sendGate
private bool _pumping;                // guarded by _sendGate

// Called by Enqueue and Post instead of _outbound.Add. Never blocks.
private bool Send(MuxOutboundFrame frame)
{
    lock (_sendGate)
    {
        if (_outbound.IsAddingCompleted) return false;
        if (_overflow.Count == 0 && _outbound.TryAdd(frame)) return true;
        _overflow.Enqueue(frame);
        _overflowBytes += frame.PayloadLength;            // add a PayloadLength to MuxOutboundFrame if absent
        if (_overflowBytes > _options.MaxOverflowBytes) { overflowed = true; }
        else if (!_pumping) { _pumping = true; ThreadPool.UnsafeQueueUserWorkItem(static c => c.PumpOverflow(), this, preferLocal: false); }
    }
    if (overflowed) { FaultFromSendOverflow(); return false; }   // outside the lock: disconnect path takes other locks
    return true;
}

private void PumpOverflow()
{
    while (true)
    {
        MuxOutboundFrame next;
        lock (_sendGate)
        {
            if (_overflow.Count == 0) { _pumping = false; return; }
            next = _overflow.Peek();
        }
        try { _outbound.Add(next); }                          // blocks this pool thread only
        catch (InvalidOperationException) { DropOverflow(); return; }   // CompleteAdding: disconnected
        lock (_sendGate) { _overflow.Dequeue(); _overflowBytes -= next.PayloadLength; }
    }
}
```
  `Enqueue` keeps its contract (a request whose frame could not be queued because the client is disconnected fails its TCS with `IOException`); `Post` keeps swallowing. `OnDisconnected` (`:565-569`) clears `_overflow` under `_sendGate` after `CompleteAdding()` (pending request TCSs are already failed there). `FaultFromSendOverflow` logs `"[MuxClient] outbound overflow past {cap} bytes; disconnecting"` and runs the same teardown a read error runs. Only one pool thread per client can be parked in the pump.

- [ ] **Step 4: Run the Mux tests green**, then the whole `tests/Ntilde.Mux.Tests` project (all 900+ must pass).

- [ ] **Step 5: App-level pin** (`MuxLocalCloseStalledLinkTests`, `[AvaloniaFact]`): a *local* `MuxConnectionHost` built over a `FakeMuxServerEnd` (`new MuxConnectionHost(ct => MuxClient.ConnectAsync(daemon.ClientEnd, null, ct), "test", null)` pattern from `MainWindowMuxSharingTests`), a `TestMainWindowFactory` window with `SessionPersistence = "KeepOnClose"` and that host injected, one mux pane attached; fill the send queue; close the pane's tab on the UI thread and assert `CloseTab` returns within 2 s (stopwatch around the call on the UI thread); then drain and assert the daemon receives `kill` for that session. Run red by temporarily reverting Step 3 (or before Step 3), then green.

- [ ] **Step 6: Commit.** `fix(mux): never block a caller on a full outbound queue` — body names the call sites now covered and that B1's `HandOffKill` stays (harmless).

### Task 2: Jump-host passwords on the interactive OpenSSH path

The native interactive path is fixed by main's d1972b7 (merged in §0): a native prompt names its hop (`SshInteractionRequest.IsJumpHop/Host/Port/User`), and both `NativeSshPromptResponder` and `SshInteractionService.IsProfileTargetPrompt` keep the vault from a hop's prompt. What remains is OpenSSH's askpass: `SshAskPassCommand.IsTargetPasswordPrompt` (`src/Ntilde.App/Shell/SshAskPassCommand.cs:316-339`) fills the vault password for `user@host's password:` or `(user@host) …password:` when `user@host` is the profile's target. Two holes: (a) before OpenSSH 8.4 keyboard-interactive prompts carry no `(user@host)` prefix, so a bastion can send keyboard-interactive text reading exactly `ops@prod.internal's password: ` and get the target's password (Windows 10's built-in OpenSSH 8.1 is affected); (b) a hop whose `user@host` equals the target's (different port) is indistinguishable, since ssh prompts omit the port. The automatic remote path already refuses old ssh (`RemoteMuxHostFactory.PrefixesKeyboardInteractivePrompts`); the interactive paths — remote-mux user attempts and plain OpenSSH tabs — do not.

Rule: **when the connection goes through a jump host, and either the local ssh does not prefix keyboard-interactive prompts (< 8.4, or unknown) or a hop's `user@host` equals the target's, the askpass helper runs without the vault** (`NTILDE_SSH_ASKPASS_NO_VAULT=1`, the existing "without saved password" mode) — the user types the password.

**Files:**
- Modify: `src/Ntilde.App/Shell/Mux/Remote/RemoteMuxHostFactory.cs` (`CreateTransport`, interactive branch: `withoutSavedPassword` when the rule holds)
- Modify: the plain OpenSSH tab's askpass environment builder (find where a plain OpenSSH `TerminalPane`/`SshConnectionService.BuildLaunchDetailsFor` sets `SSH_ASKPASS` + `NTILDE_SSH_ASKPASS_PROFILE*` — `grep -rn "NTILDE_SSH_ASKPASS" src`) so plain tabs apply the same rule
- Create: `src/Ntilde.App/Shell/SshAskPassVaultPolicy.cs` — one pure function both call
- Test: `tests/Ntilde.App.Tests/Shell/SshAskPassVaultPolicyTests.cs` (new), `tests/Ntilde.App.Tests/Shell/Mux/Remote/RemoteMuxHostFactoryTests.cs` (add), and the plain-tab builder's test file
- Docs: Phase 4 spec §14 — mark "Vault password to a jump host (pre-existing, plain native tabs too)" resolved (native by d1972b7, OpenSSH by this task)

**Interfaces:**
- Produces: `internal static bool SshAskPassVaultPolicy.MayOfferVault(SshProfile profile, bool sshPrefixesKeyboardInteractivePrompts)` — false when `profile.JumpHosts` (or the generated config's ProxyJump/ProxyCommand) is non-empty and (`!sshPrefixesKeyboardInteractivePrompts` or any hop's `User@Host` equals the target's, case-insensitive; a hop's empty user means the profile's user).
- Consumes: `OpenSshClientVersionCache.Shared` / `OpenSshClientVersion` (A4) for the prefix capability (unknown version ⇒ false).

- [ ] **Step 1: Failing tests.** `SshAskPassVaultPolicyTests`: `[Theory]` rows — no jump hosts + 8.1 → true; jump host + 8.4 + distinct hop → true; jump host + 8.1 → false; jump host + unknown version → false; jump host + 8.4 + hop `ops@prod.internal` equal to target `OPS@prod.internal` → false; hop with empty user and host equal to target's → false. `RemoteMuxHostFactoryTests`: an *interactive* OpenSSH request for a profile with one jump host and a version cache reporting 8.1 → the built transport request's `WithoutSavedPassword` is true (follow the pattern at `RemoteMuxHostFactoryTests.cs:~536-576`); same profile with 9.6 → false. Plain tab: the launch environment for the same profile with 8.1 contains `NTILDE_SSH_ASKPASS_NO_VAULT=1`.
- [ ] **Step 2: Run red** (policy class missing → compile error counts as red; then the factory/plain-tab tests fail on the assertion once the class exists with `return true`).
- [ ] **Step 3: Implement** the policy and call it from both builders (the factory call already runs off the UI thread; the version probe for the plain-tab builder must not run on the UI thread — if the builder runs on the UI thread, use `OpenSshClientVersionCache.Shared.TryGetCached(path)` and treat "not probed yet" as unknown ⇒ no vault, and kick a background probe).
- [ ] **Step 4: Green**, plus `SshAskPassTargetPromptTests`, `SshAskPassVaultOnlyTests`, `RemoteMuxHostFactoryTests` all green.
- [ ] **Step 5: Commit** `fix(ssh): no vault password for askpass when a jump host could ask as the target`.

### Task 3: One native known-hosts store, atomic `TrustHost`

`new NativeKnownHostsStore` exists per `SshInteractionService` (`src/Ntilde.App/Services/Ssh/SshInteractionService.cs:39`, one per window) and as a static read-only `Lazy` in `RemoteMuxInteractionHandler.cs:277`. Each instance has its own `_syncRoot` (`src/Ntilde.Platform/Ssh/Native/NativeKnownHostsStore.cs:15`). `TrustHost` (`:45-79`) reads, modifies and `PersistEntriesLocked` (`:99-114`) writes with `File.WriteAllText` (truncate-and-write). `LoadEntriesLocked` (`:81-97`) swallows parse errors and returns an empty list, so the next `TrustHost` after a torn read wipes every trusted key.

**Files:**
- Modify: `src/Ntilde.Platform/Ssh/Native/NativeKnownHostsStore.cs`
- Modify: `src/Ntilde.App/Services/Ssh/SshInteractionService.cs:39`, `src/Ntilde.App/Shell/Mux/Remote/RemoteMuxInteractionHandler.cs:277` (use the shared instance)
- Test: `tests/Ntilde.Platform.Tests/Ssh/NativeKnownHostsStoreTests.cs`

**Interfaces:**
- Produces: `public static NativeKnownHostsStore NativeKnownHostsStore.ForPath(string storeFilePath)` — one instance per full path (normalised with `Path.GetFullPath`, OrdinalIgnoreCase on Windows) from a static `ConcurrentDictionary`; `public static NativeKnownHostsStore Default` = `ForPath(<the default path the parameterless ctor uses today>)`. The public ctor stays for tests but delegates locking to a static per-path lock object so even two `new` instances on one path serialise.

- [ ] **Step 1: Failing tests.**
  - `Concurrent_trusts_from_two_instances_keep_every_entry`: two instances (`new NativeKnownHostsStore(path)` twice) on one temp path; `Parallel.For(0, 64, i => (i % 2 == 0 ? a : b).TrustHost($"h{i}", 22, "ssh-ed25519", $"SHA256:{i}"))`; then a fresh instance's `CheckHost` returns `Trusted` for all 64.
  - `A_corrupt_store_is_kept_aside_not_overwritten`: write `"{not json"` to the path; `TrustHost("h",22,…)`; assert a file `<path>.corrupt-*` exists with the original bytes, and the store now holds `h`.
  - `TrustHost_never_leaves_a_partial_file`: trust 200 entries while a reader loop calls `CheckHost` on another instance on another thread; the reader never sees a parse failure (instrument via an internal `LoadFailures` counter, or assert every read returns ≥ the count trusted before it started).
- [ ] **Step 2: Run red** (`scripts/build.ps1 test tests/Ntilde.Platform.Tests --filter "FullyQualifiedName~NativeKnownHostsStoreTests"`).
- [ ] **Step 3: Implement.** Static `ConcurrentDictionary<string, object> PathLocks`; all reads/writes lock the path's object. Write: serialise to `"{path}.{Guid.NewGuid():N}.tmp"`, `File.Move(tmp, path, overwrite: true)`, retry up to 5 × 20 ms on `IOException`/`UnauthorizedAccessException` on Windows (pattern: `MuxDiscovery.WriteDescriptor`, `src/Ntilde.Mux.Contracts/MuxDiscovery.cs:82-102`). Load: on `JsonException`, `File.Move(path, $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}")` once, log, and return empty. Point both App sites at `NativeKnownHostsStore.Default`.
- [ ] **Step 4: Green** (whole Platform.Tests project, then `SshInteractionServiceTests` and `RemoteMuxInteractionHandlerTests` in App.Tests).
- [ ] **Step 5: Commit** `fix(ssh): one known-hosts store per file, written atomically`.

### Task 4: `RemoteMuxCommand` escapes a recorded path instead of replacing it

`src/Ntilde.App/Shell/Mux/Remote/RemoteMuxCommand.cs:43-50`: a recorded `RemoteDaemonPath` outside ASCII `[A-Za-z0-9._/+-]` (`IsSafeAbsolutePath`, `:32-41`) is silently replaced by `sh -c 'exec "$HOME/.local/share/ntilde/bin/ntilde-mux" …'`. A safe path is passed unquoted.

**Files:**
- Modify: `src/Ntilde.App/Shell/Mux/Remote/RemoteMuxCommand.cs`
- Modify: `src/Ntilde.App/Shell/Mux/Remote/RemoteMuxConnector.cs` (log when a non-empty recorded path is refused)
- Test: `tests/Ntilde.App.Tests/Shell/Mux/Remote/RemoteMuxCommandTests.cs`

**Interfaces:**
- Produces: `RemoteMuxCommand.For(options, arguments)` returns, for any absolute recorded path that contains no `'`, `\`, `!`, control character (`char.IsControl`) or Unicode format character (category `Cf`): `sh -c 'exec "<path>" <arguments>'` with `$`, `` ` ``, `"` inside `<path>` escaped by a backslash (inside sh double quotes). A path containing any refused character, a relative path, or an empty path falls back to the default install path (Task 6's expression) and `RemoteMuxCommand.RefusedRecordedPath(string)` returns true so the connector logs `"[RemoteMux] recorded ntilde-mux path refused (unsafe characters); using the default install path"`. `IsSafeAbsolutePath` is renamed `IsQuotableAbsolutePath` with the new rule.

- [ ] **Step 1: Failing tests.** Change `[InlineData("/home/a b/x", ProxyFallback)]` to expect `sh -c 'exec "/home/a b/x" proxy --stdio'`; add rows: `/home/José/.local/share/ntilde/bin/ntilde-mux` → quoted as is; `/home/a$b/x` → `sh -c 'exec "/home/a\$b/x" proxy --stdio'`; `/home/a"b/x` → `\"`; `/home/a'b/x` → fallback; `/home/a\b/x` → fallback; `/x/‮evil` → fallback; `relative/x` → fallback. The quoted commands are also run through the existing "runs in sh" test pattern if one exists (`Each_step_works_in_the_directory_the_remote_command_runs_from` in `RemoteMuxInstallCommandsTests` shows how the tests execute sh under WSL/Git Bash; skip when no sh).
- [ ] **Step 2: Red.** **Step 3: Implement.** **Step 4: Green** (`RemoteMuxCommandTests`, `RemoteMuxConnectorTests`). **Step 5: Commit** `fix(mux): quote a recorded ntilde-mux path instead of replacing it`.

### Task 5: Askpass marker folder 0700, control characters out of toasts, cleared response payload

Three small hardening items in one task (one reviewer can judge them together; each has its own test).

(a) `SshAskPassSessionMarkers` (`src/Ntilde.App/Shell/SshAskPassSessionMarkers.cs:46,71,107`) creates `<app-data>/askpass` with a plain `Directory.CreateDirectory` (0755 under a usual umask). (b) `RemoteMuxFailureClassifier.Quote` (`src/Ntilde.App/Shell/Mux/Remote/RemoteMuxFailureClassifier.cs:292-296`) only trims and caps; ESC, BEL, CR and bidi controls (U+202E etc.) reach the toast through `TerminalPane.RemoteMuxUnavailableMessage` (`TerminalPane.axaml.cs:~4267`). `RemoteOutputText.Quote` already drops `char.IsControl` for the installer. (c) `NativeSshPromptResponder.RespondAsync` (`src/Ntilde.Platform/Ssh/Native/NativeSshPromptResponder.cs:72-73`) never clears the response payload (a password in JSON), and `NativeSshInterop.SubmitResponse` (`src/Ntilde.Platform/Ssh/Native/NativeSshInterop.cs:641-662`) makes a second copy (`data.ToArray()`, `:648`).

**Files:**
- Create: `src/Ntilde.Mux.Contracts/PrivateDirectory.cs` — lift `MuxDiscovery.CreatePrivateDirectory` (`MuxDiscovery.cs:278-291`) to `public static class PrivateDirectory { public static DirectoryInfo Create(string path) }` (0700 off Windows, re-asserted with `File.SetUnixFileMode`), and make `MuxDiscovery` call it. (Mux.Contracts is a leaf every App/Mux assembly already references; this adds no project reference.)
- Modify: `src/Ntilde.App/Shell/SshAskPassSessionMarkers.cs` (use `PrivateDirectory.Create`)
- Modify: `src/Ntilde.App/Shell/Mux/Remote/RemoteOutputText.cs` (`Quote` also drops `UnicodeCategory.Format` characters: U+200E/F, U+202A–U+202E, U+2066–U+2069, U+FEFF), `RemoteMuxFailureClassifier.cs` (use `RemoteOutputText.Quote`), `TerminalPane.axaml.cs` `RemoteMuxUnavailableMessage` (sanitize `reason` with the same function)
- Modify: `src/Ntilde.Platform/Ssh/Native/NativeSshPromptResponder.cs`, `NativeSshInterop.cs` (`SubmitResponse` takes `ReadOnlySpan<byte>` and pins it — `fixed (byte* p = data)` with a `byte*` P/Invoke overload — instead of copying; if the P/Invoke signature is generated/shared, zero the copy in `finally`)
- Test: `tests/Ntilde.App.Tests/Shell/SshAskPassVaultOnlyTests.cs` (add, Unix-only), `tests/Ntilde.App.Tests/Shell/Mux/Remote/RemoteMuxFailureClassifierTests.cs` (add), `tests/Ntilde.Platform.Tests/Ssh/NativeSshSessionInteractionTests.cs` or a new `NativeSshPromptResponderTests.cs`

- [ ] **Step 1: Failing tests.**
  - `The_marker_folder_is_private` (`Assert.SkipWhen(OperatingSystem.IsWindows(), …)`): fresh temp root; `TryClaim`; `File.GetUnixFileMode(dir) == UserRead|UserWrite|UserExecute`. Fails on ubuntu CI today (the local Windows run skips it — note in the report that red is proven by CI or WSL).
  - `Server_text_in_a_failure_reason_loses_control_and_bidi_characters`: `RemoteMuxFailureClassifier.Classify(255, "", "x\u001b[2J\u0007y‮z\r", null)` → `Reason` contains none of `\u001b`, `\u0007`, `‮`, `\r`, and still contains `x`, `y`, `z`. And `TerminalPane.RemoteMuxUnavailableMessage("box", "a\u001bb")` contains no ESC.
  - `The_response_payload_is_cleared_after_submit`: a fake `INativeSshInterop` whose `SubmitResponse(handle, kind, ReadOnlySpan<byte> data)` copies `data` to a captured array *and* keeps a reference to the array the responder passed (expose an internal `NativeSshPromptResponder.PayloadObserver` `Action<byte[]>?` test seam invoked with the payload array right before submit); after `RespondAsync` with `SshInteractionResponse.FromSecret("hunter2")`, the observed array is all zeros, while the fake's copy contains `hunter2` (proving the submit saw the real bytes).
- [ ] **Step 2: Red** (the bidi test fails on U+202E even after a naive `IsControl` filter — that is why Format is included).
- [ ] **Step 3: Implement**; `RespondAsync`: `byte[] payload = …; try { PayloadObserver?.Invoke(payload) /* test only, before submit */; interop.SubmitResponse(handle, kind, payload); } finally { CryptographicOperations.ZeroMemory(payload); PayloadObserver… }` — order the seam so the observer sees the array object and the test inspects it after `RespondAsync` returns.
- [ ] **Step 4: Green** (App.Tests lane filter `FullyQualifiedName~SshAskPass|FullyQualifiedName~RemoteMuxFailureClassifier|FullyQualifiedName~RemoteOutputText`, Platform.Tests whole project, Mux.Tests whole project — `MuxDiscovery` changed).
- [ ] **Step 5: Commit** `fix(mux): private askpass folder, clean server text in toasts, cleared prompt answers`.

### Task 6: The remote install dir honours `$XDG_DATA_HOME`

`.local/share/ntilde/bin` is hard-coded in `RemoteMuxCommand.cs:20,49` (`DefaultRelativePath`), `RemoteMuxInstallCommands.cs:103` (`OfflineOneLiner`), `:110` (`DirectoryVariable = "d=\"$HOME/.local/share/ntilde/bin\"; "`), and UI copy `Views/Ssh/RemoteMuxInstallDialog.cs:196`, `Views/Ssh/NewSshConnectionView.axaml:172`. The daemon root is `${XDG_DATA_HOME:-$HOME/.local/share}/ntilde/ntilde-mux` on Linux (.NET's `LocalApplicationData`, which ignores a relative `XDG_DATA_HOME`) and `~/Library/Application Support/ntilde/ntilde-mux` on macOS.

Rule: the install dir is `<data>/ntilde/bin` with `<data>` = `$XDG_DATA_HOME` when it is set and absolute, else `$HOME/.local/share` — on Linux **and** macOS (keep `.local/share` on macOS: `Application Support` has a space and macOS sets no XDG variable, so nothing changes there).

**Files:**
- Create: `src/Ntilde.App/Shell/Mux/Remote/RemoteInstallDir.cs` with `internal const string ShellExpression = "${XDG_DATA_HOME:-}"`-free POSIX snippet:
  ```csharp
  // POSIX sh; matches .NET's rule (an unset, empty or relative XDG_DATA_HOME means $HOME/.local/share).
  internal const string Assign = "case \"${XDG_DATA_HOME-}\" in /*) d=\"$XDG_DATA_HOME/ntilde/bin\";; *) d=\"$HOME/.local/share/ntilde/bin\";; esac; ";
  internal const string Display = "$XDG_DATA_HOME/ntilde/bin (default ~/.local/share/ntilde/bin)";
  ```
- Modify: `RemoteMuxInstallCommands.cs` (`DirectoryVariable` → `RemoteInstallDir.Assign`; `OfflineOneLiner` uses it: `sh -c '<Assign> mkdir -p "$d" && cd "$d" && …'` — keep it one line a user can paste), `RemoteMuxCommand.cs` fallback → `sh -c '<Assign> exec "$d/ntilde-mux" <args>'`, the dialog and tooltip copy → `RemoteInstallDir.Display`
- Test: `tests/Ntilde.App.Tests/Shell/Mux/Remote/RemoteMuxInstallCommandsTests.cs`, `RemoteMuxCommandTests.cs`

- [ ] **Step 1: Failing tests.** Exact-string tests updated to the new expression; a new behavioural test runs the `Assign` snippet under `sh` (skip when no `sh` on PATH — on Windows CI Git Bash provides it) with env `XDG_DATA_HOME=/tmp/x` → `echo "$d"` prints `/tmp/x/ntilde/bin`; with `XDG_DATA_HOME=rel` → `$HOME/.local/share/ntilde/bin`; unset → same; empty → same.
- [ ] **Step 2: Red. Step 3: Implement. Step 4: Green** (`RemoteMuxInstallCommandsTests`, `RemoteMuxCommandTests`, `RemoteMuxInstallerTests`, `NewSshConnectionViewLayoutTests`). The Docker E2E (`RemoteMuxDockerE2eTests`, Category=DockerE2E) covers a real install in CI; Task 9 makes sure it runs on this PR.
- [ ] **Step 5: Commit** `fix(mux): install ntilde-mux under $XDG_DATA_HOME like the daemon root`.

### Task 7: CI runs the remote-persistence E2E when its tests change

`aot_gate_detect` (`.github/workflows/ci.yml:~1530-1595`) decides whether `mux_daemon_aot` runs on a PR with `grep -qE '^(src/|Directory\.(Build|Packages)\.props$|global\.json$|\.github/workflows/[^/]+\.ya?ml$|scripts/mux-daemon-smoke\.sh$)'`. A PR that changes only `tests/Ntilde.App.Tests/Shell/Mux/Remote/RemoteMuxDockerE2eTests.cs`, `tests/Ntilde.Platform.Tests/Ssh/DockerSshFixture.cs` or `tests/Ntilde.ExternalSuites/NativeSsh/` gets no binary, and `native_ssh_docker_e2e` skips the remote-persistence step with a notice and stays green.

**Files:**
- Create: `scripts/ci/aot-gate-paths.sh` — reads changed paths on stdin, prints `run=true|false`; the regex adds directory-scoped entries `^tests/Ntilde\.App\.Tests/Shell/Mux/|^tests/Ntilde\.Mux\.Tests/|^tests/Ntilde\.Platform\.Tests/Ssh/|^tests/Ntilde\.ExternalSuites/NativeSsh/` (scope by directory, not file spellings — repo rule "guards parse, don't match spellings")
- Modify: `.github/workflows/ci.yml` `aot_gate_detect` to call it
- Test: `scripts/tests/test_aot_gate_paths.sh` (or a Python test next to `scripts/tests/check_app_tests_baseline_tests.py`, matching what that folder uses) — feeds path lists and asserts the output; wire it into the CI job that runs `scripts/tests` (find it: `grep -n "scripts/tests" .github/workflows/ci.yml`)

- [ ] **Step 1: Failing test**: `tests/Ntilde.App.Tests/Shell/Mux/Remote/RemoteMuxDockerE2eTests.cs` alone → expect `run=true`; `docs/x.md` alone → `run=false`; `src/Ntilde.Mux/MuxClient.cs` → true; `tests/Ntilde.VT.Tests/x.cs` → false. Red: the script does not exist; then with today's regex the first row is false.
- [ ] **Step 2: Implement; Step 3: Green** (run the test script locally under Git Bash: `bash scripts/tests/test_aot_gate_paths.sh`).
- [ ] **Step 4: Commit** `ci: build ntilde-mux for the E2E when its tests change`.

### Task 8: A stable `SSH_AUTH_SOCK` for daemon-spawned remote shells

`ntilde-mux serve` (spawned on demand by the first `proxy --stdio`, `src/Ntilde.Mux/Daemon/MuxDaemonSpawner.cs:102-122`) inherits that proxy's environment, including `SSH_AUTH_SOCK=/tmp/ssh-XXXX/agent.N` when that connection forwarded an agent. Every shell it spawns later inherits the same path, which is dead once that SSH connection closes. Fix (tmux's pattern): the standalone daemon sets `SSH_AUTH_SOCK=<endpoint dir>/agent.sock` for its shells, and every `proxy --stdio` repoints that symlink to its own `SSH_AUTH_SOCK` before pumping.

**Files:**
- Create: `src/Ntilde.Mux/Daemon/AgentSocketLink.cs`:
  ```csharp
  /// The stable agent socket path a remote daemon's shells use, and its repointing (Unix only).
  public static class AgentSocketLink
  {
      public const string FileName = "agent.sock";
      public static string PathFor(string endpointDirectory) => Path.Combine(endpointDirectory, FileName);
      /// Points the link at target when target is an existing socket owned by this user; atomic (symlink to tmp + rename). Returns false (and changes nothing) otherwise.
      public static bool TryRepoint(string linkPath, string? target);
  }
  ```
- Modify: `src/Ntilde.Mux/Cli/MuxProxyCommand.cs` (`Run`, after `connectDaemon` succeeds: `AgentSocketLink.TryRepoint(link, Environment.GetEnvironmentVariable("SSH_AUTH_SOCK"))` — the endpoint directory comes from the descriptor/`MuxPaths`), standalone serve host (`MuxServeHost` when `MuxPaths.IsStandalone`, or `LocalShellSessionFactory`): add `SSH_AUTH_SOCK=<link>` to every spawned shell's environment overrides — only when the daemon was started with an `SSH_AUTH_SOCK` or the link exists, so hosts without agent forwarding see no change
- Test: `tests/Ntilde.Mux.Tests/Daemon/AgentSocketLinkTests.cs` (Unix-only, `Assert.SkipWhen(OperatingSystem.IsWindows(), …)`), `tests/Ntilde.Mux.Tests/Cli/MuxProxyTests.cs` (add), `tests/Ntilde.Mux.Tests/Daemon/LocalShellSessionFactoryTests.cs` (add)

- [ ] **Step 1: Failing tests.** `TryRepoint` with two real Unix sockets `/tmp/x/a.sock`, `/tmp/x/b.sock` (bind a `Socket(AddressFamily.Unix…)`): repoint to a → `readlink == a`; to b → `== b`; to a regular file → false, link unchanged; to null → false. Proxy test: run the in-process proxy with env `SSH_AUTH_SOCK=<a>` (inject an environment reader seam if `Run` reads `Environment` directly) → link points at a. Factory test: a spawn's overrides contain `SSH_AUTH_SOCK=<endpoint dir>/agent.sock` when the daemon's own environment had `SSH_AUTH_SOCK`.
- [ ] **Step 2: Red** (on Windows these skip: prove red in WSL — `wsl.exe` has no dotnet here, so rely on CI ubuntu; note this in the report). **Step 3: Implement. Step 4: Green (Mux.Tests).**
- [ ] **Step 5: Commit** `fix(mux): give remote shells an agent socket link the proxy keeps current`.

### Task 9: The native exec poll waits instead of sleeping

`NativeSshExecTransport`'s `PollLoop` (`src/Ntilde.Platform/Ssh/Exec/NativeSshExecTransport.cs:371-389`) sleeps `PollDelay = 10 ms` when `PollEvent` returns null (the ~15 ms attach floor in spec §14). `nova_ssh_poll_event` (`src/Ntilde.App/native/rusty_ssh/src/lib.rs:1275-1300`) is non-blocking; `SharedState` (`:278-302`) has `events: Mutex<VecDeque<QueuedEvent>>` and a `response_cv: Condvar` pattern (`wait_for_response` `:1018-1034`), but no event-side wakeup.

**Files:**
- Modify: `src/Ntilde.App/native/rusty_ssh/src/lib.rs` — add `events_cv: Condvar` to `SharedState`; `notify_all` in `queue_event` (`:~892-901`, after the push) and in `mark_closed` (`:~1036-1043`); new export:
  ```rust
  /// Waits up to timeout_ms for an event to be queued (or the session to close). Returns
  /// NOVA_SSH_OK when one is ready, NOVA_SSH_TIMEOUT when none came, NOVA_SSH_CLOSED when closed.
  #[no_mangle]
  pub extern "C" fn nova_ssh_wait_event(handle: *mut NovaSshHandle, timeout_ms: u32) -> i32
  ```
  (reuse the existing status-code constants; if there is no TIMEOUT code, add one at the end of the enum — append-only.) Clamp `timeout_ms` to 1000.
- Modify: `src/Ntilde.Platform/Ssh/Native/INativeSshInterop.cs` — `bool WaitForEvent(NovaSshSafeHandle handle, TimeSpan timeout) { Thread.Sleep(timeout > TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : timeout); return true; }` as a **default interface method** (keeps every test fake compiling and behaving as today); `NativeSshInterop` overrides it with the P/Invoke.
- Modify: `NativeSshExecTransport.PollLoop`: `if (next is null) { _interop.WaitForEvent(handle, TimeSpan.FromMilliseconds(100)); continue; }` (bounded so `_stop` is observed).
- Test: Rust `mod event_wait_tests` next to `mod event_queue_budget_tests` (`lib.rs:~6478`): wait returns OK promptly after `queue_event` from another thread; TIMEOUT when empty (≥ timeout elapsed); CLOSED after `mark_closed`. C#: `tests/Ntilde.Platform.Tests/Ssh/Exec/NativeSshExecTransportTests.cs` — a scripted interop counting `PollEvent` calls over 500 ms idle with a `WaitForEvent` that blocks until an event is scripted: assert ≤ 10 calls (today ~50) and that an event scripted mid-wait is delivered within 50 ms.
- [ ] **Step 1: Failing tests** (C# first: the count assertion fails with the sleep loop). **Step 2: Red** (`cargo test --manifest-path src/Ntilde.App/native/rusty_ssh/Cargo.toml event_wait` fails to compile → red). **Step 3: Implement. Step 4: Green**: `cargo test` (whole crate), Platform.Tests, Mux.Tests; then rebuild App (`scripts/build.ps1 build src/Ntilde.App` builds the crate). **Step 5: Commit** `perf(ssh): wait for native exec events instead of sleeping 10 ms`.

### Task 10: Sign and notarize `ntilde-mux` on macOS; pin its SHA-256 in the App

`publish_mux_daemon` (`.github/workflows/release.yml:~1910-2124`) builds `ntilde-mux-osx-arm64` with only the linker's ad-hoc signature and writes `ntilde-mux-<rid>.sha256` next to each asset. The App (`src/Ntilde.App/Shell/Mux/Remote/GitHubReleaseMuxAssetSource.cs:79-127`) trusts whatever `.sha256` sits next to the binary. The app itself is signed and notarized inside `vpk pack` using `./.github/actions/setup-mac-signing` and the `MAC_*` secrets (`release.yml:~563-597`, `:~1116-1136`).

**Files:**
- Modify: `.github/workflows/release.yml`:
  - `publish_mux_daemon`: copy `publish_aot`'s `MAC_SIGNING_ENABLED` env gate and the two-checkout + `setup-mac-signing` steps (osx-arm64 only, `if: env.MAC_SIGNING_ENABLED == 'true'`). After the "Assert" step and **before** "Stage": `codesign --force --timestamp --options runtime --keychain "$KEYCHAIN" --sign "$MAC_SIGN_APP_IDENTITY" "$bin"`; `codesign --verify --strict --verbose=2 "$bin"`; `ditto -c -k --keepParent "$bin" mux.zip && xcrun notarytool submit mux.zip --keychain-profile "$MAC_NOTARY_PROFILE" --keychain "$KEYCHAIN" --wait`; re-run `"$bin" --version --json`. Raise the job's `timeout-minutes` to 90. When signing is disabled (forks), log `::notice::ntilde-mux is not signed (no signing secrets)`.
  - `publish_mux_daemon`: `actions/upload-artifact` `mux-sha256-<rid>` with the `.sha256` file.
  - `publish_aot` (already `needs: publish_mux_daemon`) and `publish_linux` (add `publish_mux_daemon` to `needs`): `actions/download-artifact` `pattern: mux-sha256-*`, `merge-multiple: true`, into `$RUNNER_TEMP/mux-sha256`, and pass `-p:NtildeMuxSha256Dir=$RUNNER_TEMP/mux-sha256` to the App publish.
- Modify: `src/Ntilde.App/Ntilde.App.csproj`: `<EmbeddedResource Include="$(NtildeMuxSha256Dir)/ntilde-mux-*.sha256" LogicalName="Ntilde.Resources.mux-sha256.%(Filename)%(Extension)" Condition="'$(NtildeMuxSha256Dir)' != ''" />` (pattern: the existing `vt-conformance-report.json` resource, `:~97-99`).
- Create: `src/Ntilde.App/Shell/Mux/Remote/MuxAssetPins.cs` — `internal static IReadOnlyDictionary<string, string> Load()` reads every `Ntilde.Resources.mux-sha256.ntilde-mux-<rid>.sha256` resource with `MuxDaemonAsset.TryParseChecksum` → `rid → hex`; empty in dev builds.
- Modify: `GitHubReleaseMuxAssetSource` — optional ctor parameter `IReadOnlyDictionary<string,string>? pins` (production passes `MuxAssetPins.Load()`): with a pin for the rid, the expected hash is the pin; the downloaded `.sha256` must equal it (else `InvalidDataException("The release's checksum does not match the one built into this app.")`); a cache hit is re-verified against the pin. No pin: today's behaviour.
- Docs: `docs/USER_MANUAL.md` (the remote install section) — one sentence: the macOS `ntilde-mux` is signed and notarized; a copy dragged in through Finder from a browser download still gets Gatekeeper's prompt the first time (that is the quarantine attribute, not the binary).
- Test: `tests/Ntilde.App.Tests/Shell/Mux/Remote/MuxAssetSourceTests.cs` (add: pinned hash ≠ consistent served pair → throws; pinned hash = served → ok; cached copy with a different hash than the pin → re-downloads or throws per the code path; no pins → today's behaviour); `tests/Ntilde.App.Tests/Shell/Mux/Remote/MuxAssetPinsTests.cs` (parses `<hash>  ntilde-mux-linux-x64` lines; ignores malformed).
- [ ] **Step 1: Failing tests** (C#). **Step 2: Red. Step 3: Implement code + workflow. Step 4: Green** (`MuxAssetSourceTests`, `MuxAssetPinsTests`); validate the workflow YAML with `python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/release.yml'))"` and, if `actionlint` is available, run it. The signing steps cannot run locally: say so in the report; the PR's reviewer checks them by reading.
- [ ] **Step 5: Commit** `ci(release): sign and notarize ntilde-mux on macOS and pin its hashes in the app`.

---

## Part B — §3 agent host sees windowless sessions

Rulings R4 (what "windowless" means; ids are mux session ids; `CurrentClient` only) and R5 (windowless reads are journaled; windowless act on a remote endpoint needs the profile's agent allowlist for `send_input` and `close_session`).

### Task 11: `readScreen` on the daemon and public client calls

**Files:**
- Modify: `src/Ntilde.Mux.Contracts/MuxProtocol.cs` (`MuxMethods.ReadScreen = "readScreen"`), `src/Ntilde.Mux.Contracts/MuxMessages.cs` (new DTOs), `src/Ntilde.Mux.Contracts/MuxJsonContext.cs` (register them)
- Modify: `src/Ntilde.Mux/MuxServerConnection.cs` (`HandleRequest` case), `src/Ntilde.Mux/HeadlessTerminalSession.cs` (`PostReadScreen`, `LastOutputUnixMs`)
- Modify: `src/Ntilde.Mux/MuxClient.cs` (public `ReadScreenAsync`, `GetSessionInfoAsync`, `SendInputTo`)
- Test: `tests/Ntilde.Mux.Tests/Server/MuxServerReadScreenTests.cs` (new), `tests/Ntilde.Mux.Tests/Client/MuxClientReadScreenTests.cs` (new), `tests/Ntilde.Mux.Tests/Contracts/MuxJsonTests.cs` (wire shape)

**Interfaces (Produces):**
```csharp
// Ntilde.Mux.Contracts (leaf: no VT types; the snapshot travels as TerminalStateSerializer bytes, base64 in JSON)
public sealed class ReadScreenParams { public Guid SessionId { get; set; } public int MaxScrollbackRows { get; set; } }
public sealed class ReadScreenResult
{
    public byte[] Snapshot { get; set; } = [];          // TerminalStateSerializer.ToBytes(CaptureSnapshot(rows))
    public bool Running { get; set; }
    public int? ExitCode { get; set; }
    public bool HasActiveChildProcesses { get; set; }
    public int AttachedClients { get; set; }
    public int? InteractiveClients { get; set; }
    public string? Title { get; set; }
    public string? Cwd { get; set; }
    public long? LastOutputUnixMs { get; set; }          // null: no output yet
}
public static class MuxReadScreenLimits { public const int MaxScrollbackRows = 2000; public const int MaxSnapshotBytes = 4 * 1024 * 1024; }

// Ntilde.Mux
public sealed record MuxScreenRead(TerminalStateSnapshot Snapshot, ReadScreenResult Status);
public Task<MuxScreenRead?> MuxClient.ReadScreenAsync(Guid sessionId, int maxScrollbackRows, CancellationToken ct); // null = daemon does not know readScreen
public Task<SessionInfoResult> MuxClient.GetSessionInfoAsync(Guid sessionId, CancellationToken ct);
public void MuxClient.SendInputTo(Guid sessionId, string text);   // unattached input; non-blocking (Task 1)
```
Daemon rules:
- `MaxScrollbackRows` is clamped to `[0, MuxReadScreenLimits.MaxScrollbackRows]`, never `protocol_error`, so "protocol_error means unsupported" stays unambiguous.
- The work is posted to the session's parse thread like `PostAttach` (`HeadlessTerminalSession.cs:396-406`, `ExecuteAttachCore` `:749-850`), which calls `CaptureSnapshot` there.
- Error codes: unknown id gives `unknown_session`; a faulted session gives `internal_error`; a dropped work item (session stopped) gives `session_exited`.
- A serialized snapshot larger than `MaxSnapshotBytes` gives `snapshot_too_large`. The reply therefore never risks the 16 MB `ClientSendBudgetBytes` abort (`MuxServerConnection.cs:109-135`).
- An exited but unreaped session answers with its last screen and `Running = false`.
- `LastOutputUnixMs` is a `Volatile.Write` of `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` once per parsed output batch on the parse thread, not per byte.

Client rules:
- A `MuxProtocolException` with code `protocol_error` from `readScreen` returns null ("unsupported"), on any negotiated version.
- Decode with the same `MuxAttachLimits` checks `DecodeSnapshot` uses (`MuxClient.cs:478-563`); make that decode helper reusable.

- [ ] **Step 1: Failing tests.**
  - Server (`MuxServerReadScreenTests`, `MuxTestHost` + `RawMuxConnection`, pattern `MuxServerRequestTests.cs:107-130`):
    - Spawn a scripted session that wrote `"hello"`. `readScreen{sessionId, maxScrollbackRows: 0}` decodes (`TerminalStateSerializer.FromBytes`) to a snapshot whose main-screen row 0 starts with `hello`, with `Running == true` and `LastOutputUnixMs` within 10 s of now.
    - `maxScrollbackRows: -5` is treated as 0.
    - `1_000_000` is clamped: after writing 3000 lines, the snapshot has at most 2000 scrollback rows.
    - An unknown id gives error `unknown_session`.
    - An oversized snapshot (lower the cap through an internal `MuxServerOptions.MaxReadScreenBytes` used by the test) gives `snapshot_too_large`, and the connection stays usable (`ping` afterwards).
  - Client (`MuxClientReadScreenTests`):
    - Against `MuxTestHost`, `ReadScreenAsync` returns a snapshot equal (`TerminalStateAssert`) to an attach snapshot of the same session at the same point.
    - Against `FakeMuxServerEnd` replying `protocol_error` to `readScreen`, it returns null.
    - `SendInputTo` reaches a session the connection never attached (the scripted session records the input).
  - Wire (`MuxJsonTests`): `ReadScreenParams` serializes as `{"sessionId":"…","maxScrollbackRows":0}`, and the `MuxProtocol` range is still 1..2 (`The_protocol_range_is_1_to_2` unchanged).
- [ ] **Step 2: Red** (the method is unknown, so `protocol_error`; the client methods are missing, so compile errors).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Green.** Run the whole Mux.Tests project. Also build `src/Ntilde.Mux.Daemon` (NativeAOT-relevant source-gen registration) and run `scripts/build.ps1 test tests/Ntilde.Architecture.Tests`.
- [ ] **Step 5: Commit** `feat(mux): readScreen, and public session info and unattached input on MuxClient`.

### Task 12: The window's windowless-session source

**Files:**
- Create: `src/Ntilde.App/AgentHost/IWindowlessSessionSource.cs`
- Create: `src/Ntilde.App/Shell/Mux/MuxWindowlessSessions.cs`: the implementation over `MuxConnectionHosts`. It is UI-free except for a `Func<Task<IReadOnlySet<(string Endpoint, Guid Id)>>> shownHere` delegate the window supplies.
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`:
  - Build one `MuxWindowlessSessions` when `_muxHosts` exists.
  - Publish it to `AgentHostService` (`SetWindowlessSource`) where `SetActionExecutor` is published (ctor `~:3874-3880`, `ApplyAgentHostSettingsLive` `~:6922-6932`), and clear it on teardown.
  - `shownHere` is `Dispatcher.UIThread.InvokeAsync(...)`. It collects `(MuxEndpointId.Parse(p.MuxEndpoint).ToString(), id)` for every pane's `Session is MuxClientSession m` (`m.Id`) and `MuxSessionIdToRestore`. This is the `OfferMuxSessionsAsync` logic (`~:5283-5291`), applied to every endpoint.
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxWindowlessSessionsTests.cs` (new)

**Interfaces (Produces):**
```csharp
namespace Ntilde.AgentHost;
internal sealed record WindowlessSessionInfo(Guid SessionId, string Endpoint, Guid? SshProfileId, string HostDisplayName,
    string Title, int Cols, int Rows, bool Running, int? ExitCode);
internal enum WindowlessOutcome { Ok, NotFound, Unsupported, Unreachable, NotRunning }
internal sealed record WindowlessScreen(WindowlessOutcome Outcome, WindowlessSessionInfo? Session, TerminalStateSnapshot? Snapshot, ReadScreenResult? Status);
internal interface IWindowlessSessionSource
{
    Task<IReadOnlyList<WindowlessSessionInfo>> ListAsync(CancellationToken ct);
    Task<WindowlessScreen> ReadScreenAsync(Guid sessionId, int maxScrollbackRows, CancellationToken ct);
    Task<WindowlessOutcome> SendInputAsync(Guid sessionId, string text, CancellationToken ct);
    Task<WindowlessOutcome> KillAsync(Guid sessionId, CancellationToken ct);
    /// The endpoint's SSH profile id for an act check; null for local or unknown.
    Task<(WindowlessOutcome Outcome, Guid? SshProfileId)> ResolveAsync(Guid sessionId, CancellationToken ct);
}
```
Rules:
- Only hosts in `_muxHosts.All` whose `CurrentClient` is non-null are asked: never `GetClient`, never `GetOrCreate`.
- Each daemon call has its own 4 s timeout (the MCP round trip is 10 s), and hosts are asked in parallel.
- A host that fails or times out contributes nothing to `ListAsync` (logged once per call), and makes a lookup on it `Unreachable`.
- Sessions in `shownHere` are excluded, and so is a `Faulted` summary.
- An id found on two endpoints is ambiguous: `NotFound`, logged.
- When `ReadScreenAsync` gets null from the client, it falls back to `GetSessionInfoAsync` for status and reports `Unsupported` for the snapshot.
- `SendInputAsync` / `KillAsync` first check `Running` via `listSessions` (`NotFound` / `NotRunning`), then call `SendInputTo` / `KillAsync`.
- `HostDisplayName` is "this computer" for local, else `host.Policy.DisplayName`.

- [ ] **Step 1: Failing tests.** Use `MuxTestHost` daemons wired as a local host and a fake remote host (pattern `MainWindowMuxRemoteTests.cs:~92`), with a `shownHere` stub. Cases:
  - It lists only sessions not shown, and excludes faulted sessions.
  - A host whose `CurrentClient` is null is skipped, **and** a host whose `GetClient` throws is never called. Use a `MuxConnectionHost` test double that records `GetClient` calls, or assert the connect factory is never invoked a second time.
  - A host that hangs (a `FakeMuxServerEnd` that never answers `listSessions`) costs at most 4.5 s, and the other host's sessions are still listed.
  - A read on a v1-style fake (`protocol_error`) gives `Unsupported`, with `Status` from `sessionInfo`.
  - Input to an exited session gives `NotRunning`.
  - After a kill, the daemon's session list no longer has the session.
  - An ambiguous id gives `NotFound`.
- [ ] **Step 2: Red.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Green:** the new test class, plus `MainWindowMux*Tests`.
- [ ] **Step 5: Commit** `feat(agent): a window-owned source of windowless mux sessions`.

### Task 13: Agent host tools on windowless sessions

**Files:**
- Modify: `src/Ntilde.AgentHost.Contracts/SessionContracts.cs`: `SessionInfo` gets `public bool? Windowless { get; set; }` and `public string? Endpoint { get; set; }`. Both are null for panes, so pane JSON is unchanged under `WhenWritingNull`.
- Modify: `src/Ntilde.AgentHost.Contracts/AgentHostProtocol.cs`: append `ErrorCodes.Unsupported = "unsupported"` ("the daemon that holds this session is too old for this request").
- Modify: `src/Ntilde.App/AgentHost/AgentHostService.cs`:
  - `SetWindowlessSource(IWindowlessSessionSource?)`, cleared in `StopLocked` like the executor;
  - the handlers below;
  - a windowless-read decay flag for the window light.
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`:
  - `RefreshAgentObserveIndicator` / `ComputeObserveIndicatorState` take the service's `WindowlessWatched` as a third input;
  - the "Agent Activity" dialog copy "Actions taken…" becomes "Actions and windowless reads…".
- Test: `tests/Ntilde.App.Tests/AgentHost/AgentHostWindowlessProtocolTests.cs` (new; `HandleRequestLineAsync` pattern, `AgentHostActProtocolTests.cs:20-50`).
- Test: `AgentHostWindowlessCaptureTests.cs` (new; `[Collection("GoldenPng")]`, `[Trait("Lane","PlatformBoot")]`, `SnapshotService.EnsureAvaloniaInitialized()` as in `AgentHostCaptureProtocolTests.cs:31-58`).

Handler rules. The registry lookup comes first; only if the pane id is not in the registry *and* a windowless source is set does the windowless path run.
- `listSessions`:
  - Panes as today. Then `await source.ListAsync(ct)`, appended as `SessionInfo { PaneId = SessionId, Title, ProfileName = HostDisplayName, Kind = SshProfileId is null ? "local" : "ssh", Rows, Cols, IsActive = false, Windowless = true, Endpoint, Status = Running ? null : "exited" }`.
  - Journal `Record("listSessions", null, "windowless", "ok")`, only when the windowless list is non-empty.
- `readScreen`:
  - Call `source.ReadScreenAsync(id, 0)`. Build `new TerminalBuffer(snapshot.Cols, snapshot.Rows)`, `ImportState(snapshot)`, then run the same `BufferSnapshot.Capture` + DTO code as panes. Extract the pane path's DTO building into a helper that takes a buffer.
  - `Unsupported` maps to `ErrorCodes.Unsupported`; `NotFound` / `Unreachable` map to `SessionNotFound`.
  - Journaled (R5). Sets the windowless-watched flag.
- `readScrollback`:
  - Call `source.ReadScreenAsync(id, MuxReadScreenLimits.MaxScrollbackRows)`, then page over the imported buffer's scrollback exactly as the pane handler does.
  - `totalLines` is what the snapshot holds. Document that windowless scrollback is the newest 2000 rows.
- `getSessionStatus`, built from `ReadScreenResult` (or the `sessionInfo` fallback):
  - `status`: `"exited"` when not `Running`; `"running"` when `HasActiveChildProcesses` or `snapshot.IsAltScreenActive`; otherwise `"awaitingInput"`. Use the existing status string constants.
  - `confidence = "heuristic"`.
  - `lastOutputAtMs` and `statusSinceMs` are both `LastOutputUnixMs ?? 0`.
  - `isStalled = false`; thresholds as the pane DTO reports them; `exitCode`.
- `captureScreen`:
  - `mode == "live"` gives `CaptureUnavailable` ("a windowless session has no window to capture").
  - `render` takes metrics from the active pane's `RenderParameters` (any registration with `RenderParameters.IsUsable`); with none it gives `CaptureUnavailable` ("no open pane to take font metrics from").
  - Render the imported buffer through `TerminalSnapshotRenderer.Capture` with the same budget and file-writing code as panes. Journaled.
- `sendInput`:
  - After the size-cap and `actEnabled` checks, call `ResolveAsync`.
  - A remote endpoint (`SshProfileId` non-null) requires `AllowsAgentActOnProfile(profileId)`, else `ProfileNotAllowed`.
  - Then `SendInputAsync`: `NotFound` maps to `SessionNotFound`, `NotRunning` to `SessionNotRunning`. Journaled.
- `closeSession`: `actEnabled`, `ResolveAsync`, the allowlist for remote endpoints (R5), then `KillAsync`. Journaled.
- `waitForEvents`: unchanged. Windowless sessions emit no events (documented in Task 14).
- Window light:
  - A windowless read sets `_windowlessWatchedUntil = now + AgentAttentionMachine.ReadDecaySeconds`.
  - `SweepStatuses` (1 s timer) clears it and raises `ObserveActivityChanged` on each transition.
  - `WindowlessWatched` exposes it.

- [ ] **Step 1: Failing tests,** using a `StubWindowlessSource` in the test project:
  - **list:** windowless rows (`windowless: true`) come after panes, and pane JSON has no `windowless` key (assert on the raw response line).
  - **readScreen:**
    - on a windowless id it returns the snapshot's text;
    - on a pane id it never calls the source;
    - `Unsupported` maps to `unsupported`.
  - **getSessionStatus:** maps running, alt-screen and exited.
  - **sendInput:**
    - with act off it returns `actDisabled` and the source is never called;
    - to a remote windowless session whose profile is not allowlisted it returns `profileNotAllowed`;
    - with the allowlist it succeeds and is journaled.
  - **closeSession:** the same three cases.
  - **journal:** a windowless readScreen appends a journal entry; a pane readScreen does not (the existing `Capture_is_not_journaled_because_it_is_an_observe_tier_read` stays green).
  - **window light:** `WindowlessWatched` becomes true after a read and false after the decay. Drive the sweep through the existing timer test hook, or an internal `Sweep(DateTime now)`.
  - **observe off:** with the service stopped, the source is cleared and nothing is called.
  - **capture (PlatformBoot lane):**
    - render mode on a windowless session, with a stub registration supplying `RenderParameters`, returns a PNG whose dimensions match cols × rows × cell size;
    - live mode gives `captureUnavailable`.
- [ ] **Step 2: Red.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Green:** the AgentHost test folder in both lanes, plus `MainWindowAgent*` tests if present.
- [ ] **Step 5: Commit** `feat(agent): list, read, status, capture, input and close for windowless sessions`.

### Task 14: MCP surface and docs for windowless sessions

**Files:**
- Modify: `src/Ntilde.McpServer/Tools/SessionTools.cs`:
  - `FormatSessionList` adds a `windowless` marker: the `kind` cell reads `local (windowless)` / `ssh (windowless)`, with a footnote "windowless: running in the multiplexer with no window here".
  - The empty message becomes "no terminal sessions are open".
  - The descriptions of `list_sessions`, `read_screen`, `get_session_status`, `send_input`, `close_session` and `capture_screen` each mention windowless sessions in one clause.
  - `TryUnwrap` maps `unsupported` like the other codes.
- Modify: `docs/mcp/tools.md` (live-tools tables, ~L41-67) and `src/Ntilde.McpServer/README.md`.
- Modify: `docs/agent-host/DIRECTION.md`: add a "Windowless sessions" paragraph covering what they are, R4/R5, scrollback limited to the newest 2000 rows, no events, and that render capture borrows a pane's font metrics.
- Test: `tests/Ntilde.McpServer.Tests/SessionToolsFormattingTests.cs`: add a list with one pane and one windowless row (both format), and the empty message.
- Test: `AgentHostClientTests.cs`: a fake endpoint returning a windowless `SessionInfo` round-trips `windowless: true`.
- [ ] **Step 1: Failing tests.**
- [ ] **Step 2: Red.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Green:** the whole McpServer.Tests project, with `McpServerStdioE2ETests.ExpectedToolNames` unchanged.
- [ ] **Step 5: Commit** `feat(mcp): show windowless mux sessions to agents`.

---

## Part C — §1 the default

Rulings R1 (a dialog; the remembered answer lives in a flag file), R2 (quiet restore after a reboot) and R3 (the flip is its own commit; designer and test windows pin Off).

### Task 15: Name the default, pin test windows to Off, migration tests

**Files:**
- Modify: `src/Ntilde.App/Shell/TerminalSettings.cs:~103-110`: `public const string DefaultSessionPersistence = SessionPersistenceMode.Off;` and `public string SessionPersistence { get; set; } = DefaultSessionPersistence;`. The flip in Task 19 changes only the constant's value.
- Modify: `src/Ntilde.App/Shell/AppServices.cs:~30-33`: `BuildForDesigner()` returns `new TerminalSettings { SessionPersistence = SessionPersistenceMode.Off }`, with a comment that the designer and every test window built from it must never spawn a daemon (the test host would be spawned as `mux serve`).
- Test: `tests/Ntilde.App.Tests/Core/SessionPersistenceSettingTests.cs`
- [ ] **Step 1: Tests.** These pass today and must keep passing across the flip; that is their purpose.
  - `A_settings_file_without_the_key_gets_the_default`: `Deserialize("{}")` gives `TerminalSettings.DefaultSessionPersistence`.
  - `An_explicit_off_survives_load_and_save`: `{"SessionPersistence":"Off"}` → load → save → reload gives `"Off"`.
  - `An_explicit_keep_survives`: the same for `KeepOnClose`.
  - `The_designer_settings_never_persist`: `AppServices.BuildForDesigner().Settings.SessionPersistence == "Off"`.
  - `Default_is_off` becomes `Default_is_the_named_default`: `new TerminalSettings().SessionPersistence == TerminalSettings.DefaultSessionPersistence`.
  - Add `A_test_window_spawns_no_daemon`, asserting `TestMainWindowFactory.Create().MuxHost is null`.
  - Red check: temporarily set the constant to `KeepOnClose` and confirm that `The_designer_settings_never_persist` still passes, and that `A_test_window_spawns_no_daemon` fails once the `BuildForDesigner` pin is also removed.
- [ ] **Step 2: Implement.**
- [ ] **Step 3: Green** (App.Tests main lane).
- [ ] **Step 4: Commit** `refactor(settings): name the session persistence default; designer windows never persist`.

### Task 16: First-close dialog (R1)

**Files:**
- Create: `src/Ntilde.App/Shell/Mux/MuxCloseChoiceStore.cs`:
  ```csharp
  internal enum MuxCloseChoice { Keep, Close }
  /// The remembered answer to the first-close dialog: a flag file under the app-data root, not a setting (R1).
  internal sealed class MuxCloseChoiceStore(string rootDirectory)
  {
      public const string FileName = "mux-close-choice";
      public static MuxCloseChoiceStore Default { get; } = new(AppPaths.RootDirectory);
      public MuxCloseChoice? Read();            // "keep" / "close" (trimmed, ordinal-ignore-case); anything else or no file -> null
      public void Remember(MuxCloseChoice c);   // atomic write (tmp + move); IO errors logged, not thrown
      public void Forget();                     // delete; IO errors logged
  }
  ```
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`:
  - **Extract** the `kept` expression at `~:9824` into `CountKeptLocalSessions()`.
  - **`OnClosing`:** before `PerformAppTeardown()`, ask only when all of these hold: `!_closeConfirmed`, `e.CloseReason != WindowCloseReason.OSShutdown`, `IsMuxPersistenceActive`, and `CountKeptLocalSessions() > 0`. Then:
    - a remembered `Keep` → proceed;
    - a remembered `Close` → `EndLocalSessionsOnTeardown()`, then proceed;
    - no remembered choice → `e.Cancel = true; _ = AskFirstCloseAsync(count);`.
  - **`AskFirstCloseAsync`** awaits the seam `ConfirmFirstClose` (`Func<int, Task<FirstCloseAnswer>>`, default `ShowFirstCloseDialogAsync`, assigned in the ctor next to `ConfirmSharedClose`). Then:
    - `Cancel` → nothing;
    - `Keep` → `Remember(Keep)` if asked, `_closeConfirmed = true; Close();`;
    - `Close` → `Remember(Close)` if asked, `EndLocalSessionsOnTeardown(); _closeConfirmed = true; Close();`.
  - **`EndLocalSessionsOnTeardown()`** queues `host.KillWhenConnected(id)` for every local mux pane's live session. Tracked kills are flushed by `MuxConnectionHosts.Dispose`'s `FlushAndClose`, so teardown then detaches nothing that should live.
  - **Types:** `internal readonly record struct FirstCloseAnswer(FirstCloseAction Action, bool Remember)` and `internal enum FirstCloseAction { Cancel, Keep, Close }`.
  - **`BuildFirstCloseDialog(int count)`**, headless-testable like `BuildSharedCloseDialog` (`~:6535-6560`):
    - title "Close Ntilde";
    - heading "Your shells keep running in the background.";
    - body "Reopen ntilde to get them back.", plus "({count} shells)" when count > 1;
    - a CheckBox "Don't ask again";
    - buttons **Keep running** (`IsDefault`) and **Close them**; Escape or closing the dialog means Cancel;
    - a small hint line "Turn this off in Settings → Keep shells running when the window closes."
- Modify: `src/Ntilde.App/SettingsWindow.axaml.cs` save path (`~:3410-3414`): when the saved `SessionPersistence` differs from the loaded one, call `MuxCloseChoiceStore.Default.Forget()`.
- Modify: `docs/CONFIG_STORAGE_CONTRACT.md` inventory (`~:86-100`): add a row for `mux-close-choice` (root; "keep"/"close"; written by the first-close dialog; deleted when the persistence setting changes; safe to delete).
- Test: `tests/Ntilde.App.Tests/Core/MainWindowFirstCloseTests.cs` (new, `[AvaloniaFact]`). The window has `SessionPersistence = "KeepOnClose"`, an injected in-process `MuxTestHost` daemon as in `MainWindowMuxLifecycleTests`, and the store pointed at a temp root via an internal settable `MuxCloseChoiceStore` property on the window.
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxCloseChoiceStoreTests.cs`
- [ ] **Step 1: Failing tests.**
  - `Closing_with_live_local_shells_asks_once`: one live local mux pane; the seam returns `Cancel`. The window is still open (`IsVisible`), the seam was called once with count 1, and no kill reached the daemon.
  - `Keep_running_detaches_and_the_shell_survives`: the seam returns `(Keep, false)`. The window is closed, the daemon still lists the session as running, and there is no flag file.
  - `Close_them_ends_the_shells`: `(Close, false)`. The daemon received `kill` for the session before the client disconnected.
  - `Dont_ask_again_remembers_the_answer`:
    - `(Keep, true)`: the flag file contains `keep`, and a second window over the same daemon with a new live session closes without calling the seam and keeps the session.
    - `(Close, true)`: the second close kills without asking.
  - `No_question_without_live_local_shells`: with only exited sessions, a remote-only window, or persistence Off, the seam is never called.
  - `OS_shutdown_never_asks`: raise closing with `WindowCloseReason.OSShutdown`, through the internal test hook the window already uses for close reasons or by calling `OnClosing` via a test subclass. The seam is not called, and the sessions are detached (kept).
  - `Changing_the_setting_forgets_the_choice`: a `SettingsWindow` save with a changed value deletes the file.
  - Dialog build test: `BuildFirstCloseDialog(3)` contains the heading text, a CheckBox "Don't ask again", and buttons "Keep running" (`IsDefault`) and "Close them"; pressing Escape completes with `Cancel`.
  - Store tests: read / remember / forget round trip; garbage reads as null; an unwritable root does not throw.
- [ ] **Step 2: Red.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Green.** App.Tests main lane, filter `FullyQualifiedName~MainWindowFirstClose|FullyQualifiedName~MuxCloseChoice|FullyQualifiedName~MainWindowMux|FullyQualifiedName~UpdateClosesMux`; then the full lane at the end of Part C.
- [ ] **Step 5: Commit** `feat(mux): ask once whether closing the window keeps the shells running`.

### Task 17: "Quit and close all shells"

**Files:**
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`:
  - **Palette command** `"Session: Quit and Close All Shells"`, category "General". Give it the id `ShortcutCatalog.QuitAndCloseAllShellsId` if the catalog lists palette-only commands; otherwise no shortcut. Register it inside the `if (IsMuxPersistenceActive)` block in `SetupCommandPalette` (`~:8345`).
  - **`internal async Task QuitAndCloseAllShellsAsync()`:**
    1. Count the local daemon's running sessions: `_muxHosts.Local.GetClient(3 s)` off the UI thread, then `ListSessionsAsync`.
    2. Confirm through a seam `ConfirmQuitAndCloseAll` (`Func<int, Task<bool>>`) whose default is `ShowConfirmationDialogAsync("Quit Ntilde", "Close every shell?", "{N} shell(s) running in the background will be closed, including detached ones.", "Quit and close", 140)`.
    3. `EndLocalSessionsOnTeardown()`.
    4. After the kills are flushed, send `shutdown` to the local daemon. Reuse `ShutdownMuxDaemonForUpdateAsync`'s probe-and-shutdown, extracted as `ShutdownLocalDaemonAsync(TimeSpan wait)`.
    5. `_closeConfirmed = true`, then `Close()`.
  - **Remote sessions** are not touched. When remote panes exist, the dialog says "Remote shells keep running."
- Modify: `src/Ntilde.App/SettingsWindow.axaml` (`~:709-719`):
  - Under the `SessionPersistenceList` row, add a `Button` styled as a link, "Quit and close all shells…", visible only while the main window reports `IsMuxPersistenceActive`.
  - Clicking it closes Settings (saving nothing) and invokes the window's `QuitAndCloseAllShellsAsync`, through the owner-callback pattern the Settings window already uses for other main-window actions. Find one with `grep -n "Owner\|MainWindow" src/Ntilde.App/SettingsWindow.axaml.cs | head`.
- Test: `tests/Ntilde.App.Tests/Core/MainWindowQuitAndCloseAllTests.cs` (new), with two live local sessions and one detached session in the daemon:
  - confirm=true: all three are killed, `shutdown` is received, and the window is closed;
  - confirm=false: nothing happens;
  - persistence off: the command is not registered (`CommandRegistry` lookup by title after `SetupCommandPalette()`).
- Test: Settings link (`SettingsWindow` headless): the link is present when the owner reports persistence active, and absent otherwise.
- [ ] **Steps:** failing tests → red → implement → green → commit `feat(mux): Quit and close all shells`.

### Task 18: Settings row copy for a default-on reader

**Files:**
- Modify: `src/Ntilde.App/SettingsWindow.axaml:~709-719`:
  - The label stays "Keep shells running when the window closes".
  - New description: "Your shells run in a background process (the multiplexer), so closing the window or a crash does not end them; Ntilde reattaches them when it starts. Turn this off to end shells when their window closes. SSH tabs keep running on the host only when their connection also turns on 'Keep remote sessions running'. Applies to new tabs."
  - ComboBox item text: "Keep running" / "Off".
- Modify: `src/Ntilde.McpServer/Tools/SettingsTools.cs:~62,131`. Only reword the description to match the Settings copy here. The prose and example that name the default value ("Default \"KeepOnClose\"") are written in Task 19's commit, since the default is whatever `TerminalSettings.DefaultSessionPersistence` will be after Task 19.
- Test: `tests/Ntilde.App.Tests/Core/SettingsWindowSessionPersistenceTests.cs` (or the existing settings layout test file): the description TextBlock text mentions "multiplexer" and "Turn this off". The McpServer drift guard still passes.
- [ ] **Steps:** failing test → red → implement → green → commit `docs(settings): describe session persistence for a default-on reader`.

### Task 19: Quiet restore after a reboot (R2), then the flip (R3)

**Files:**
- Create: `src/Ntilde.App/Shell/Mux/MuxRestoreExpectations.cs`:
  ```csharp
  /// Whether this launch should expect the previous session's daemon sessions to be gone: the session
  /// file was saved before the machine last booted, so a reboot ended them (R2).
  internal static class MuxRestoreExpectations
  {
      public static DateTime BootTimeUtc(Func<DateTime> utcNow, Func<long> tickCount64) => utcNow() - TimeSpan.FromMilliseconds(tickCount64());
      public static bool SessionsEndedByReboot(DateTime? sessionSavedUtc, DateTime bootTimeUtc) => sessionSavedUtc is { } saved && saved < bootTimeUtc;
  }
  ```
- Modify: `src/Ntilde.App/Shell/SessionManager.cs`: expose the loaded file's last-write time (UTC) with the restored session (`SessionRestoreInfo.SavedUtc` or similar), read once at startup restore.
- Modify: `src/Ntilde.App/MainWindow.axaml.cs` startup restore (`~:4116-4134`): when `SessionsEndedByReboot(...)`, set `pane.MuxQuietPreviousLost = true` on every restored mux pane.
- Modify: `src/Ntilde.App/Controls/TerminalPane.axaml.cs`:
  - Add `internal bool MuxQuietPreviousLost { get; set; }`.
  - On the `PreviousLost` outcome (banner + `MuxPreviousLostNoticeTitle` notice, `~:4237-4252`), skip both when it is set, and log one line `"[TerminalPane] previous session ended by a reboot; started a new shell"`.
- Then, **in its own commit:**
  - `TerminalSettings.DefaultSessionPersistence = SessionPersistenceMode.KeepOnClose`;
  - update the `SettingsTools.cs` prose and example to `"KeepOnClose"`;
  - in `SessionPersistenceSettingTests`, `A_settings_file_without_the_key_gets_the_default` now also asserts `"KeepOnClose"` explicitly;
  - `MainWindowMuxSharingTests.Mux_commands_are_not_registered_when_persistence_is_off` sets `"Off"` explicitly instead of relying on the default.
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxRestoreExpectationsTests.cs`: the boot math; saved before and after boot; a null saved time gives false.
- Test: `tests/Ntilde.App.Tests/Controls/MuxPaneTests.cs`: add PreviousLost with `MuxQuietPreviousLost` (no notice raised, no banner text in the buffer), and without it (both, as today).
- Test: `tests/Ntilde.App.Tests/Core/MainWindowMuxLifecycleTests.cs`, injecting the clock and boot time through an internal seam on the window. Add:
  - a restore whose session file predates boot raises no "previous sessions were lost" toast, while a fresh shell starts in each pane;
  - a file saved after boot, with the daemon gone, still toasts.
- [ ] **Step 1: Failing tests** for the quiet restore.
- [ ] **Step 2: Red.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Green.**
- [ ] **Step 5: Commit** `feat(mux): start fresh shells quietly after a reboot`.
- [ ] **Step 6: The flip.** Change the constant, the McpServer prose and example, and the two tests named above. Run the App.Tests main lane, the PlatformBoot lane and McpServer.Tests. Commit `feat(settings): keep local shells running by default`, with the body: "The maintainer decides in the PR; dropping this commit restores Off with every other Phase 5 change intact."

---

## Part D — §2 sessions survive app updates

**Measured (R9).** The run used Velopack 1.2.0 on Windows 11, a probe app packed with vpk 1.2.0, and a sandbox install. Evidence: `velopack-measurement.md`; it is summarised in spec §R9.
- The Windows apply logs `Checking for running processes in: <install root>` and then hard-kills (TerminateProcess) **every process whose image is anywhere under the install root**. That covers `current\` and any other subfolder.
- A copy of the same binary running from outside the root survived four applies with no heartbeat gap.
- The apply renames `current\` away. A process outside the root whose **working directory** is inside `current\` makes the rename fail ten times, and then the apply is abandoned: "Unable to start the update, because one or more running processes prevented it".
- On macOS and Linux the updaters contain no process-killing code (strings in `UpdateMac` / `UpdateNix`). There, a running daemon keeps its old image (inode) across the bundle or AppImage replacement.

Today the local daemon runs `%LOCALAPPDATA%\NtildeApp\current\Ntilde.exe mux serve`. Every shell's console host, the sideloaded `current\<arch>\OpenConsole.exe` (#310, `Ntilde.App.csproj:~452-512`), also runs from under the root. So without a copy, an update kills the daemon *and* every shell. Decision: on a Windows Velopack install, the daemon runs from a copy at `<app-data>\bin\<version>\`.

### Task 20: The daemon's build version on the wire

Neither the endpoint descriptor nor `hello` carries an app version (`MuxEndpointDescriptor`, `src/Ntilde.Mux.Contracts/MuxMessages.cs:~258-276`; `WelcomeResult` `:~51-55`). The new GUI must know when the daemon is "from the previous build".

**Files:**
- Modify: `src/Ntilde.Mux.Contracts/MuxMessages.cs`:
  - `MuxEndpointDescriptor.AppVersion` (`string?`, `[JsonIgnore(Condition = WhenWritingNull)]`);
  - `WelcomeResult.ServerVersion` (`string?`, same attribute).
- Modify: `src/Ntilde.Mux/MuxServerOptions.cs` (`public string? AppVersion { get; init; }`), `src/Ntilde.Mux/MuxDaemonHost.cs` (`CreateDescriptor` writes it), and `src/Ntilde.Mux/MuxServerConnection.cs` (`HandleHello` replies with it).
- Modify: `src/Ntilde.Mux/MuxClient.cs`: `public string? ServerVersion { get; }`, set from the welcome.
- Modify: the App serve path (`MuxCommand`/`MuxCliHost` → `MuxServeHost`) passes `AppVersionInfo` (`src/Ntilde.App/Shell/AppVersionInfo.cs`); the standalone `ntilde-mux` passes `MuxVersionInfo.Version`.
- Test: `tests/Ntilde.Mux.Tests/Contracts/MuxJsonTests.cs`:
  - a descriptor or welcome without a version serializes exactly as today (no key);
  - with one, it serializes as `appVersion` / `serverVersion`;
  - an old JSON without the key deserializes to null.
- Test: `tests/Ntilde.Mux.Tests/Client/…`: `MuxClient.ServerVersion` is the server's `AppVersion` against `MuxTestHost` with `AppVersion = "9.9.9"`, and null against a v1 server built without it.
- [ ] **Steps:** failing tests → red → implement → green (Mux.Tests, Architecture.Tests; build `src/Ntilde.Mux.Daemon`) → commit `feat(mux): the daemon reports its build version`.

### Task 21: On a Windows install, run the daemon from a copy outside the install root

**Files:**
- Create: `src/Ntilde.App/Shell/Mux/MuxDaemonImage.cs`:
  ```csharp
  /// Where the local daemon's executable runs from (R9). On a Windows Velopack install, Velopack's apply
  /// kills every process whose image is under the install root, so the daemon runs from a copy at
  /// <app-data>\bin\<version>\ - Ntilde.exe, the DLLs beside it (rusty_pty.dll, conpty.dll, ...) and the
  /// <arch>\OpenConsole.exe hosts conpty.dll starts - staged once per version. Elsewhere, and for dev
  /// builds, the running executable itself.
  internal static class MuxDaemonImage
  {
      /// The install root when exePath is <root>\current\<exe> and <root>\Update.exe exists; else null.
      public static string? VelopackInstallRoot(string exePath, Func<string, bool> fileExists);
      /// Stages the copy if needed and returns its executable; returns exePath when no copy is needed
      /// or staging failed (logged: the daemon then dies with the next update, as before Phase 5).
      public static string Resolve(string exePath, string appDataRoot, string version, IMuxImageFileSystem fs, Action<string> log);
      /// Deletes <app-data>\bin\<v> folders other than the current version's; a folder in use (a running
      /// older daemon) fails to delete and is kept. Best effort, off the UI thread.
      public static void PruneOldCopies(string appDataRoot, string currentVersion, IMuxImageFileSystem fs, Action<string> log);
  }
  ```
  Staging rules:
  - Copy into `<app-data>\bin\.<version>.<guid>.tmp\`:
    - the executable;
    - every `*.dll` in its directory;
    - every `<arch>\OpenConsole.exe` that exists (`arm64`, `x64`, `x86`).
  - Write a `.complete` file listing the copied files and their sizes, then `Directory.Move` to `<app-data>\bin\<version>\`.
  - A destination that exists with a valid `.complete` is reused (the sizes must match the current install's files; a mismatch means a re-pack under the same version, so stage again under `<version>-<n>`).
  - A concurrent stager losing the rename race reuses the winner's folder.
  - `IMuxImageFileSystem` is a thin seam over `File`/`Directory`, for tests.
- Modify: `src/Ntilde.Mux/Daemon/MuxDaemonSpawner.cs`: `ProcessMuxDaemonSpawner` takes an optional `Func<string, string>? imageResolver` (exe path in, exe path out). `Ntilde.Mux` gains no App reference: the App passes `p => MuxDaemonImage.Resolve(p, AppPaths.RootDirectory, AppVersionInfo.Version, …)` through `MuxDaemonLauncher.CreateDefault` / `MuxConnectionHost.CreateDefault` / `MuxCommand`'s `MuxCliHost`, so the GUI, `ntilde mux attach` and `ntilde.com` all spawn the same image.
- The daemon's working directory stays the user profile (`GetDaemonWorkingDirectory`). Add a test that it is never under the install root even when the profile directory is unavailable: fall back to the app-data root, never the exe's directory.
- Pruning runs once per GUI launch, after the local host connected, on the thread pool.
- `docs/CONFIG_STORAGE_CONTRACT.md`: add an inventory row for `bin\<version>\` (the Windows daemon image copies, safe to delete when no daemon runs).
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxDaemonImageTests.cs`, with a fake file system:
  - not a Velopack layout → `exePath` unchanged and no copy;
  - a Velopack layout → files copied, `.complete` written last, the returned path is `<app-data>\bin\<v>\Ntilde.exe`;
  - a second call reuses the copy without copying;
  - a partial copy without `.complete` is replaced;
  - a copy failure returns `exePath` and logs;
  - pruning deletes other versions and keeps the current one and one that throws on delete.
- Test: `tests/Ntilde.Mux.Tests/Daemon/ProcessMuxDaemonSpawnerTests.cs`: the resolver's result is the `FileName` of the start info.
- [ ] **Steps:** failing tests → red → implement → green → commit `feat(mux): on a Windows install the daemon runs from its own copy`.

### Task 22: Applying an update keeps a compatible daemon

Today `ApplyStagedUpdateAsync` (`MainWindow.axaml.cs:~10378-10444`) works like this:
- It probes the daemon and asks "N multiplexed session(s) will be closed by the update".
- It sends `shutdown`, waits 5 s, tears down, and calls Velopack's `ApplyUpdatesAndRestart`.
- `Program.ShouldAutoApplyUpdateOnStartup` (`Program.cs:~47-52`, `:155-171`) vetoes Velopack's startup auto-apply whenever a daemon answers a 200 ms probe.

The new build's protocol range is unknown to the old GUI. Ruling: the release puts it in the Velopack release notes as a marker line, and the old GUI reads it from the staged update. A missing marker means "compatible": Min has been 1 since Phase 0, and the new GUI still handles a mismatch at launch (Task 23).

**Files:**
- Modify: `.github/workflows/release.yml`: the release notes file passed to every `vpk pack` (`--releaseNotes`; add one if none is passed) ends with an HTML comment line `<!-- ntilde-mux-protocol: <min>-<max> -->`. The values come from the built `ntilde-mux --version --json` (`protocolMin` / `protocolMax`) in the job that has the binary. In jobs without it, read the two constants from `src/Ntilde.Mux.Contracts/MuxProtocol.cs` with a parse step (a tiny `scripts/ci/mux-protocol-range.sh` that greps `MinSupportedVersion = <n>;` / `MaxSupportedVersion = <n>;` and fails if either is missing; a test feeds it the real file).
- Modify: `src/Ntilde.App/Update/IUpdateService.cs` / `VelopackUpdateService.cs`: expose the staged release's notes (`UpdateInfo.TargetFullRelease.NotesMarkdown`) as `string? StagedReleaseNotes`.
- Create: `src/Ntilde.App/Update/MuxUpdateCompatibility.cs`:
  ```csharp
  internal static class MuxUpdateCompatibility
  {
      /// The range in the notes' marker line, or null when absent or malformed.
      public static (int Min, int Max)? ParseProtocolRange(string? releaseNotes);
      /// Whether the update keeps the daemon: the ranges overlap (a null new range counts as overlapping)
      /// and the daemon's image is outside the install root (a daemon inside it would be killed).
      public static bool KeepsDaemon((int Min, int Max) daemon, (int Min, int Max)? newBuild, string? daemonImagePath, string? installRoot);
  }
  ```
  The daemon's image path comes from the descriptor's `Pid`, via `Process.GetProcessById(pid).MainModule.FileName` (guarded; on failure, treat the daemon as inside the root → not kept, today's path). The install root comes from `MuxDaemonImage.VelopackInstallRoot`.
- Modify: `MainWindow.ApplyStagedUpdateAsync` / `PrepareMuxDaemonForUpdateAsync`:
  - `KeepsDaemon` → no confirmation, no `shutdown`; teardown detaches as on any close (sessions kept), then apply.
  - Otherwise, today's confirm + `shutdown`, with the message "{N} multiplexed session(s) will be closed by the update (the new version cannot keep them)." Save the session file **before** the shutdown, so it does not name ids that are about to die. Today the save happens after (survey S2-B6).
- Modify: `Program.ShouldAutoApplyUpdateOnStartup`: veto only when a live daemon's image is inside the install root. A daemon running from a copy no longer blocks startup auto-apply.
- Test: `tests/Ntilde.App.Tests/Update/MuxUpdateCompatibilityTests.cs`:
  - marker parsing: present, absent, malformed, several markers (first wins), whitespace;
  - `KeepsDaemon` truth table: overlap/no overlap/null range × inside/outside root/unknown path.
- Test: `tests/Ntilde.App.Tests/Update/UpdateClosesMuxTests.cs`, extended with the existing seams (`MuxProbeForUpdate`, `ConfirmSessionLossForUpdate`, `MuxReadDescriptorForUpdate`, `FakeApplyUpdateService`), plus new seams for the image path and the staged notes:
  - compatible + outside → apply called, no confirm, no shutdown sent;
  - incompatible → confirm shown, shutdown sent, session saved before the shutdown (assert order via a recording fake);
  - inside the root → today's path.
- Test: `tests/Ntilde.App.Tests/ProgramAutoApplyTests.cs` (or wherever `ShouldAutoApplyUpdateOnStartup` is tested; `grep -rn ShouldAutoApplyUpdateOnStartup tests`).
- Test: `scripts/tests/…` for `mux-protocol-range.sh` against the real `MuxProtocol.cs`.
- [ ] **Steps:** failing tests → red → implement → green → commit `feat(update): keep a compatible multiplexer running across an update`.

### Task 23: "Multiplexer is from the previous build" and "Restart multiplexer now"

**Files:**
- Modify: `src/Ntilde.App/Shell/Mux/MuxConnectionHost.cs`: raise `ServerVersionKnown(string? version)` once per connection (or expose `ServerVersion` with a `Connected` event, whichever fits the host's event style).
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`. When the local host connects and `ServerVersion` is non-null and differs from `AppVersionInfo.Version`, enqueue one notice per launch via `EnqueueNotice`:
  - title "Multiplexer";
  - message "The multiplexer is from the previous build ({old}); restart it when convenient — this closes its {N} shell(s).";
  - action **Restart multiplexer now** (a `PersistenceNoticeAction`, `src/Ntilde.App/Controls/PersistenceNoticeAction.cs`).

  The action:
  1. confirms with the count (`ShowConfirmationDialogAsync`);
  2. sends `shutdown` to the local daemon and waits up to 5 s;
  3. if it is still alive, kills it by pid, as `kill-server --force` does (`MuxCli.KillByPid` logic; extract a shared helper in `Ntilde.Mux` if needed);
  4. then calls `_muxHosts.Local.WarmUp()`, so a new daemon from this build starts.

  Open panes on the old daemon go through their existing "multiplexer disconnected" path, and Enter reconnects.
- The non-overlap case (`MuxUnavailableException(versionMismatch: true)`, `TerminalPane.axaml.cs:~3735-3743`): the existing hint text stays, and the same notice action is attached, so the user gets a button instead of a CLI command.
- Remote hosts: when a remote host's `ServerVersion` differs from the ntilde-mux version this app installs (`RemoteMuxStatusText` already compares versions; reuse its source), show one notice per host per launch: "ntilde-mux on {host} is from a previous version ({old}); restart it when convenient — this closes its {N} shell(s)", with action **Restart ntilde-mux on {host}**. The action sends `shutdown` over that host's `CurrentClient`; the next connect's proxy starts the new daemon.
  - Check the install/update flow (`RemoteMuxInstaller` and the "update ntilde-mux" notice action): it must replace the binary **without** shutting the running daemon down. If it shuts it down today, change it to keep the daemon and rely on this notice (that is the brief's "same rule for remote daemons"), and say so in the report.
- Test: `tests/Ntilde.App.Tests/Core/MainWindowMuxUpdateTests.cs` (new):
  - a `MuxTestHost` daemon with `AppVersion = "0.0.1"` gives exactly one notice with the action, even with three panes;
  - the same version gives no notice;
  - the action with confirm=true sends `shutdown`, then the host spawns or connects again (fake launcher records the spawn);
  - confirm=false sends nothing;
  - a version-mismatch fallback notice carries the action.
- Test: remote: `MainWindowMuxRemoteTests` with a fake remote daemon reporting an older version gives one notice per host; its action sends `shutdown` to that host only.
- Test: `RemoteMuxInstallerTests`: an update install leaves the running daemon alone (no `kill-server`/`shutdown` command among the executed remote commands).
- [ ] **Steps:** failing tests → red → implement → green → commit `feat(mux): offer to restart a multiplexer from a previous build`.

### Task 24: Update-survival evidence

The brief asks for a real Velopack update applied with a live daemon on Windows and on one Unix OS, with the shells intact afterwards, plus the no-overlap path.

**Files:**
- Modify: `src/Ntilde.App/Update/VelopackUpdateService.cs`: when the environment variable `NTILDE_UPDATE_SOURCE_DIR` names a directory, use `new SimpleFileSource(new DirectoryInfo(dir))` instead of the GitHub source. It is a test and verification hook, logged at startup when active. Document it in `docs/CONFIG_STORAGE_CONTRACT.md` (environment variables) and test it (`VelopackUpdateServiceTests`: the variable set → the source is a `SimpleFileSource` for that directory; unset → GitHub).
- Create: `scripts/mux-update-survival.ps1`. It is self-contained and runs on Windows:
  1. AOT-publish `src/Ntilde.App` as `0.12.0-survival.1` and `.2` (`scripts/build.ps1 publish … -p:Version=…`; local AOT recipe: memory `local-aot-and-linux-runs`, vswhere on PATH, quoted `-p:` args).
  2. `vpk pack` both with packId `NtildeSurvival` (never `NtildeApp`) into a feed directory, with the protocol marker in the notes.
  3. Install `.1` via `Setup.exe --silent --installto <sandbox>\install`.
  4. Start it with `NTILDE_APPDATA_ROOT=<sandbox>\data` and `NTILDE_UPDATE_SOURCE_DIR=<feed>`, plus a settings file with `SessionPersistence=KeepOnClose`.
  5. Spawn two sessions through the CLI: `<install>\current\Ntilde.exe mux` with `NTILDE_APPDATA_ROOT` set; `mux ls` shows them.
  6. Start a marker process in each: `mux attach` is interactive, so send input via a tiny client (`ntilde mux` has no `send`). Instead, start a session whose command writes a heartbeat file every second; pass the command through the mux spawn request, extending `ntilde mux` with a hidden `spawn-for-test <cmd>` verb only if no existing verb can start a session with a command (check `MuxCli` first).
  7. Trigger the apply: launch the GUI, which finds the staged update, and invoke the palette command through the agent host (`ntilde.spawn_session` is not it; use the MCP client in `tests/Ntilde.McpServer.Tests` as a library, or apply via `Update.exe apply`). Measure whichever is simplest and record which was used.
  8. Verify:
     - `current\sq.version` is `.2`;
     - the daemon pid is unchanged and its image path is under `<data>\bin\0.12.0-survival.1\`;
     - both heartbeat files kept advancing across the apply;
     - after restart, `mux ls` lists the same session ids.
  9. No-overlap path: pack `.3` with the marker `ntilde-mux-protocol: 3-3` and apply. Expected: confirm path → daemon shut down → after restart, the new GUI starts a fresh daemon. Record it.
  10. Uninstall the sandbox (`Update.exe --uninstall --silent`), remove the HKCU Uninstall key if left, and print a summary.
- Unix: run the same flow on Linux in Docker if Velopack's Linux AppImage can run there (FUSE: `--device /dev/fuse --cap-add SYS_ADMIN`, or `APPIMAGE_EXTRACT_AND_RUN=1`, which changes how `$APPIMAGE` resolves; note it). Otherwise hand the macOS run to the maintainer with the script's steps written for zsh (`scripts/mux-update-survival.sh`). Report exactly which OS runs were done by whom.
- [ ] **Steps:**
  1. Implement the hook and its test, then commit `feat(update): a local update source for verification runs`.
  2. Write the script and run it on Windows. Save its full output to the task report, to be pasted into the PR.
  3. Commit `test(update): an update-survival run against a sandboxed Velopack install`.

---

## Part E — §5 remote endpoints in the picker and `mux ls`

### Task 25: "Attach to session…" lists remote hosts

Today the picker is local-only. `AttachToMuxSessionAsync` (`MainWindow.axaml.cs:~5245`) uses `_muxHosts.Local`. `OfferMuxSessionsAsync` (`~:5274`) builds `openHere` from local panes and creates a *local shell* pane. `MuxSessionPicker.BuildRows` (`src/Ntilde.App/Shell/Mux/MuxSessionPicker.cs:33`) has no endpoint, and the picker seam returns `Guid?`. The factory needs no change for a shared remote attach: `MuxTerminalSessionFactory.CreateOn`'s `AttachShared` branch is endpoint-agnostic (`~:249-269`).

Caveat: a remote host exists only while a pane needs its endpoint. Detaching the last remote tab releases the host (`ReleaseUnneededRemoteMuxHosts` `~:7396`), so "connected hosts only" would not list the shell you just detached. Ruling: the picker lists connected hosts' sessions **and** one "Connect to <profile>…" row for each `PersistRemoteSessions` profile whose host is not connected. Choosing that row connects interactively (the user is waiting, so prompts are fine), lists the host's sessions, and reopens the picker. Afterwards `ScheduleRemoteMuxHostRelease` lets a host built only for the picker go away again.

**Files:**
- Modify: `src/Ntilde.App/Shell/Mux/MuxSessionPicker.cs`:
  - `MuxSessionPickerRow` gains `MuxEndpointId Endpoint` and `string HostDisplayName`.
  - New `MuxSessionPickerConnectRow(Guid ProfileId, string HostDisplayName)`.
  - `BuildRows(IEnumerable<MuxPickerHostListing> hosts, IReadOnlySet<(MuxEndpointId, Guid)> openHere)` with `record MuxPickerHostListing(MuxEndpointId Endpoint, string HostDisplayName, IReadOnlyList<SessionSummary>? Sessions, string? Error)`. Rows come grouped: local first, then remotes in `MuxConnectionHosts.All` order.
  - `Display` prefixes the host for remote rows: `"[user@host] title — command — cwd …"`.
  - An `Error` host yields one disabled informational row: `"[user@host] not reachable: <reason>"`.
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`:
  - Seam `PickMuxSession` becomes `Func<IReadOnlyList<MuxPickerItem>, Task<MuxPickerItem?>>`, where `MuxPickerItem` is the abstract base of the session row and the connect row. Update `BuildMuxSessionPickerWindow` and its tests to match.
  - Listing in `AttachToMuxSessionAsync`:
    - local as today;
    - for each remote host in `_muxHosts.All` with `CurrentClient is { } c`, `c.ListSessionsAsync` under `host.Policy.RpcTimeout`, in parallel;
    - connect rows for `_sshConnectionService.GetConnectionProfiles()` with `PersistRemoteSessions` and no connected host.
  - `openHere` and `FocusPaneShowingMuxSession` key on `(MuxEndpointId.Parse(p.MuxEndpoint), id)`. Orphan adoption stays local-only.
  - A chosen remote row creates an SSH-profile pane, not a shell pane:
    ```csharp
    TerminalProfile p = _sshConnectionService.GetConnectionProfile(profileId);   // fresh runtime copy, as AddTab does
    p.Command = OperatingSystem.IsWindows() ? "ssh.exe" : "ssh"; p.Arguments = "";
    var pane = new TerminalPane(p, _settings, SshDiagnosticsLevel.None)
    { MuxSessionIdToRestore = id, MuxAttachSharedToRestore = true, MuxEndpoint = MuxEndpointId.ForSsh(profileId).ToString() };
    AddTabWithPane(pane, title, select: true);
    ```
    Only offer endpoints whose profile still has `PersistRemoteSessions`; otherwise the pane would open plain SSH with a pending id (see Task 26).
  - A chosen connect row: `_muxHosts.GetOrCreate(MuxEndpointId.ForSsh(id))` → `GetClient(Policy.ConnectTimeout)` off the UI thread → list → reopen the picker with that host's rows → finally `ScheduleRemoteMuxHostRelease(endpoint)`.
  - `RemoteDetachedMessage` (`~:6630`) drops "Attach to session… lists local shells only": "Detached — Attach to session… reopens it".
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxSessionPickerTests.cs`: grouping and order, the host prefix, the error row, `openHere` keyed by endpoint (the same id on two endpoints is two rows).
- Test: `tests/Ntilde.App.Tests/Core/MainWindowMuxRemoteTests.cs`, using the local plus fake remote host harness at `~:92`. Add:
  - the picker receives local and remote rows, and a remote host whose `CurrentClient` is null contributes a connect row, not a listing;
  - choosing a remote row opens an SSH-profile pane whose `MuxEndpoint` is `ssh:<id>`, whose session is attached `Shared` to that id on the fake remote daemon, and which marks the tab shared;
  - choosing a remote id already shown focuses that pane;
  - choosing a connect row calls `GetClient` once, then offers that host's sessions.
- [ ] **Steps:** failing tests → red → implement → green (`MainWindowMux*`, `MuxSessionPicker*`) → commit `feat(mux): Attach to session lists remote hosts`.

### Task 26: Shared remote tabs keep their share through drops and never kill on close

From survey §5 A4:
- **Share flag lost on a drop.** The remote drop paths (`TerminalPane.axaml.cs:~4100-4161`: `EnterRemoteReconnecting`, `EnterRemoteWaitingForEnter`, `HandleRemoteMuxDisconnected`, `HandleRemoteAttachFailed`) set `_muxReattachId` but never `_muxReattachShared`, while the local path sets `_muxReattachShared = reattach && _muxSessionIsShare` (`~:4611`). The result:
  - a reconnected share comes back owned;
  - a share whose session exited or vanished spawns a fresh shell (PreviousLost) instead of taking `ShareEnded`.
- **Unconfirmed kill.** `KillMuxSessionOnClose` (`MainWindow.axaml.cs:~7332`) and `KillPendingRemoteMuxSessionOnClose` (`~:7411`) queue a kill for a remote session even when the link is down or the attach is pending. `DecidePaneCloseAsync` asks "Detach / Close" only for a connected session with interactive others, so closing a share while reconnecting kills another client's shell on the next connect.
- **Lost route.** A profile that lost `PersistRemoteSessions` between pick and spawn opens plain SSH with the share id pending, and its close kills the share.

**Files:**
- Modify: `src/Ntilde.App/Controls/TerminalPane.axaml.cs`: set `_muxReattachShared = _muxSessionIsShare` wherever the remote paths set `_muxReattachId`. When `MuxAttachSharedToRestore` is set but the request routes plain (not remote), drop the pending id (`MuxSessionIdToRestore = null`) with a log line.
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`, `DecidePaneCloseAsync` / `DisposeControlTree`: a pane that is a share (`MuxSessionIsShare`, or `MuxAttachSharedToRestore` with a pending id) whose sharing is not known right now (link down, reconnecting, or the attach still pending) closes with `PaneDisposition.Detach`. It never queues a kill. The same applies to local shares whose connection is gone (today's behaviour, now explicit).
- Test: `tests/Ntilde.App.Tests/Controls/MuxPaneRemoteTests.cs` (or the remote pane test file in use; `grep -ln "EnterRemoteReconnecting" tests`):
  - a shared remote pane dropped then `Reconnected` reattaches with `MuxAttachMode.Shared` and keeps `MuxSessionIsShare`;
  - a shared remote pane whose session is gone on reconnect takes the `ShareEnded` path (the pane closes, and no new session is spawned on the fake daemon).
- Test: `tests/Ntilde.App.Tests/Core/MainWindowMuxRemoteTests.cs`:
  - closing a shared remote tab while its host is reconnecting sends no `kill` to the daemon after reconnect, and the other client's session still runs;
  - closing an owned remote tab while reconnecting still kills (today's behaviour);
  - a share pick whose profile lost persistence opens plain SSH and closing it sends no kill.
- [ ] **Steps:** failing tests → red → implement → green → commit `fix(mux): a shared remote tab stays shared and never kills on close`.

### Task 27: `ntilde mux ls --all`

`MuxCli` (in `Ntilde.Mux`, no Platform reference) cannot reach SSH, and `ntilde-mux` must stay lean (`LayeringTests.Nothing_ntilde_mux_runs_references_an_OpenSSL_backed_assembly`). The CLI cannot see what a GUI connected. Ruling: `--all` lives in the App's `ntilde mux` adapter and connects on its own, **non-interactively** (keys, agent, saved vault password, an existing ControlMaster; it never prompts), to every profile with `PersistRemoteSessions`, in parallel with a 15 s per-host timeout. Each hello carries its own `ClientInstanceId`, so it never evicts a GUI connection.

**Files:**
- Modify: `src/Ntilde.App/Shell/Mux/MuxCommand.cs`: intercept `ls` when `--all` is present, before `MuxCli.Execute`:
  - print the local listing via `MuxCli` (unchanged), then one block per remote profile;
  - text: a `HOST` column (`this computer`, or `user@host`) in front of today's `ID  STATE  ATTACHED  SIZE  TITLE`;
  - an unreachable host prints `user@host  unreachable: <reason, control characters stripped>`;
  - `--json --all` prints `{"endpoints":[{"endpoint":"local","host":"this computer","sessions":[…]},{"endpoint":"ssh:<id>","host":"user@host","error":"…"}]}`. A new `MuxLsAllResult` is registered in a source-gen context in the App (`AppJsonContext` or a new `MuxCommandJsonContext`).
  - `ls --json` without `--all` is byte-for-byte unchanged.
- Create: `src/Ntilde.App/Shell/Mux/Remote/RemoteMuxLister.cs`: `internal static Task<IReadOnlyList<RemoteListing>> ListAsync(SshConnectionService svc, Func<SshProfile, RemoteMuxConnector> connectorFor, TimeSpan perHost, CancellationToken ct)`. Production `connectorFor` builds the connector from `RemoteMuxHostFactory.CreateTransport` and `new RemoteMuxInteractionHandler(user: null, …)`. Call `ConnectAsync(interactive: false, ct)`, then `ListSessionsAsync`, then dispose.
- Modify: `src/Ntilde.Mux/Cli/MuxCli.cs` usage text: `ls [--json] [--all]`. `--all` is accepted only by the App adapter; `ntilde-mux ls --all` prints "--all needs the ntilde app (it connects to your SSH profiles)" and exits 2. Update the tests that pin `UsageLines`.
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxCommandLsAllTests.cs`, with fake connectors over `MuxTestHost` daemons:
  - text layout with local, one remote and one unreachable host;
  - the JSON shape;
  - a host that hangs is cut at the timeout and the others still print;
  - no `PersistRemoteSessions` profiles: the local listing only, plus "No remote hosts keep sessions."
- Test: `tests/Ntilde.Mux.Tests/Cli/MuxCliTests.cs` (`ntilde-mux ls --all` exits 2 with the message).
- [ ] **Steps:** failing tests → red → implement → green → commit `feat(mux): ntilde mux ls --all lists remote hosts`.

---

## Part F — §6 SFTP and remote files on persisted remote tabs

Ruling R8: native persisted tabs get SFTP, the remote files sidebar, path autocomplete and palette transfers through their own non-interactive connections, as plain native tabs do. Port forwards and OpenSSH ControlMaster reuse are deferred, with reasons. OpenSSH persisted tabs get palette transfers (scp runs on its own connection).

### Task 28: Register native persisted tabs, with a per-host password scope

`ActiveSshSessionRegistry` (`src/Ntilde.App/Services/Ssh/ActiveSshSessionRegistry.cs`) stores `(SessionId, ProfileId, BackendKind)` plus per-server runtime passwords keyed `(sessionId, host, port, user)`. Its readers:
- the sidebar listing gate, `RemoteDirectoryBrowserService.TryCreateNativeListingConnection` (`~:89-172`), which requires `TryGetActiveNativeSession`;
- autocomplete;
- transfers, `SftpService.ExecuteNativeSftpTransfer` (`~:633`), which use the id for passwords only, via `NativeHopPasswordResolver.Resolve(baseOptions, registry, sessionId, savedTargetPassword)`.

A mux pane is never registered (`TerminalPane.axaml.cs:~3607-3608`: `if (session is not MuxClientSession)`). Typed passwords for a remote host live in `RemoteMuxInteractionHandler` per host, not in the registry.

**Files:**
- Modify: `ActiveSshSessionRegistry`:
  - `ActiveSshSessionDescriptor` gains an optional `Guid? PasswordScopeId`;
  - `TryGetRuntimePassword` lookups made through a descriptor use `PasswordScopeId ?? SessionId`;
  - new `UnregisterScope(Guid scopeId)` clears that scope's passwords.
- Modify: `src/Ntilde.App/Shell/Mux/Remote/RemoteMuxInteractionHandler.cs` and its owner:
  - each remote host gets a `Guid PasswordScopeId` (on `MuxConnectionHost` or the connector);
  - `Attempt` records each password answer's `request.Host/Port/User` with the secret (extend the `Answer` record);
  - `Succeeded()` writes the non-superseded *typed* (not vault) password answers into the registry under the host's scope via `SetRuntimePassword(scope, host, port, user, secret)`;
  - the host's dispose or release calls `UnregisterScope`.
  - Do **not** give the exec transport's `NativeSshPromptResponder` a session id: `SshInteractionService` would replay stored passwords and bypass the handler's refused-password and jump-hop rules. Keep R7: a jump-hop profile's handler remembers nothing, so it writes nothing.
- Modify: `src/Ntilde.App/Controls/TerminalPane.axaml.cs`:
  - register a `MuxClientSession` on a remote endpoint whose profile's backend is Native, as `(mux.Id, profileId, Native, PasswordScopeId: host scope)`; unregister on dispose and on detach;
  - `IsPersistentRemoteTab` notices at `~:1156` (sidebar) are removed for native profiles;
  - for OpenSSH profiles the sidebar entry stays hidden as on plain OpenSSH tabs (`IsRemoteFilesSidebarSupported` is profile-based).
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`, `OnPaneRequestRemoteFilesSidebarTransfer` (`~:5481`) and `InitiateSftpTransfer` (`~:8502`):
  - drop the persistent-tab refusal for native profiles;
  - for OpenSSH profiles allow palette transfers (scp `-B` on its own connection, as on plain OpenSSH tabs).
- Retargeted destination (spec §15, codex D2): SFTP rebuilds options from the stored profile. While the host's pinned destination differs from the stored profile's, sidebar and transfers on that tab are refused with the notice "Remote Files" / "Not available while this tab still runs on the host it was opened on — reopen the tab to use the new host". Add an `internal bool IsRetargeted` on `RemoteMuxConnector` (pinned ≠ current), surfaced through the host.
- Modify: `src/Ntilde.App/Views/Ssh/NewSshConnectionView.axaml` (`~:165-174`, the Reliability tab; the brief's §4 editor-row item): under the "Keep remote sessions running (ntilde-mux)" checkbox, add a wrapping hint (Opacity 0.7): "Persistent tabs do not run this connection's port forwards. With the OpenSSH backend they also have no Remote Files sidebar (transfers from the command palette work)." Add the same sentence to `RemoteMuxInstallDialog.cs` (`~:139`).
- Test: `tests/Ntilde.App.Tests/Ssh/ActiveSshSessionRegistryTests.cs`: scope lookup; `UnregisterScope` clears it; the session-only path is unchanged.
- Test: `tests/Ntilde.App.Tests/Shell/Mux/Remote/RemoteMuxInteractionHandlerTests.cs`:
  - a typed target password in a successful user attempt lands in the scope keyed by its host, port and user;
  - a refused attempt writes nothing;
  - a vault-filled password is not copied into the scope;
  - a jump-hop profile writes nothing.
- Test: `tests/Ntilde.App.Tests/Core/MainWindowMuxRemoteTests.cs`, or a pane test:
  - a native persisted pane registers with the host's scope;
  - the sidebar toggle opens, with no notice;
  - `RemoteDirectoryBrowserService` gets a listing connection (fake interop) carrying the typed password;
  - a retargeted host refuses with the new notice;
  - an OpenSSH persisted tab's palette transfer starts scp (fake process runner).
- Test: `NewSshConnectionViewLayoutTests`: the hint TextBlock exists and mentions "port forwards".
- [ ] **Steps:** failing tests → red → implement → green (App.Tests main lane, filter `FullyQualifiedName~Ssh|FullyQualifiedName~Mux|FullyQualifiedName~Sftp`; then the full lane) → commit `feat(mux): SFTP and remote files on native persistent tabs`.

---

## Part G — §7 release

### Task 29: One user manual chapter, README, docs, changelog, version

**Files:**
- Modify: `docs/USER_MANUAL.md`: replace the accreted §3.3 subsections (`~:110-238`) and the persistence notes elsewhere (`~:486-488` Remote Files, `~:522-541` `ntilde.com`) with one chapter, "Persistent sessions and the multiplexer", written for a default-on reader. Its sections:
  1. What runs: a background process keeps your shells.
  2. Closing the window, and the first-close question; Quit and close all shells.
  3. Reopening: what comes back, and after a reboot (fresh shells, no warning).
  4. Detach versus close; Attach to session…, including remote hosts and Connect to….
  5. Several windows and `ntilde mux attach`.
  6. Remote SSH tabs that keep running (`ntilde-mux`, install, `$XDG_DATA_HOME`, drops and reconnects, what works on a persistent tab: SFTP and Remote Files on native, transfers on OpenSSH, no port forwards).
  7. Updates: shells survive. When the multiplexer is from the previous build, "Restart multiplexer now"; when it is incompatible, what happens.
  8. Agents and windowless sessions.
  9. The CLI: `ntilde mux ls [--all] [--json]`, `attach`, `kill`, `kill-server [--force]`.
  10. `ntilde.com` on Windows. This includes the brief's §4 item: "If you capture its output (`ntilde | Out-Null`, `$x = ntilde`), the caller waits until the window closes; start it plainly or with `Start-Process ntilde`."
  11. Turning it off.

  Keep every anchor other docs link to: `grep -rn "USER_MANUAL.md#" docs src README.md`, and update links whose anchors change.
- Modify: `README.md`. Add a "Persistent sessions" bullet in "Why Ntilde?" (`~:27-42`) and a `### Persistent sessions` subsection after "Native SSH" in `## Features` (`~:262`). The one-line bullet: "Shells keep running when you close the window, reattach when you reopen, and SSH tabs survive network drops."
- Modify: `docs/ARCHITECTURE.md` §8.1/§8.2 (the default, `readScreen`, the update rule, the Windows daemon copy if Task 21 lands, the picker) and `docs/MODULE_OWNERSHIP.md` (new files from every task).
- Modify: `docs/superpowers/specs/2026-10-05-ntilde-mux-phase4.md`: mark the §14 items resolved, with links to Phase 5.
- Modify: `docs/superpowers/specs/2026-10-08-ntilde-mux-phase5.md` §R: any ruling added during execution.
- Create: `CHANGELOG.md`. There is none today; releases use GitHub's generated notes (`release.yml:124`). It gets an `## Unreleased` → `## 0.12.0` section with a user-facing multiplexer entry, not phase by phase, and states that earlier releases' notes are on GitHub Releases.
- Create: `docs/announcements/2026-10-<dd>-persistent-sessions.md`: the release notes draft (what it is, the default, how to turn it off, updates, remote tabs, agents, known limits).
- Modify: `Directory.Build.props`: `0.11.0` → `0.12.0` in `Version`, `AssemblyVersion` (`0.12.0.0`), `FileVersion` (`0.12.0.0`) and `InformationalVersion`, as in commit `cfd3cd6 release: 0.11.0`. Tagging is the maintainer's step.
- [ ] **Steps:**
  1. Write the docs.
  2. Run the doc link checker if the repo has one (`grep -n "markdown-link\|lychee\|linkcheck" .github/workflows/*.yml`); otherwise check every `USER_MANUAL.md#` anchor by hand.
  3. Commit `docs(mux): one persistent sessions chapter; README, changelog, release notes`.
  4. Commit `release: 0.12.0` with the version bump only.

### Task 30: Manual checklist on three OSes

**Files:**
- Create: `docs/superpowers/plans/2026-10-08-ntilde-mux-phase5-manual-checklist.md`. One numbered checklist that merges:
  - PR #489's 8 steps (`gh pr view 489 --json body`);
  - Phase 3's extensions (`docs/superpowers/specs/2026-09-29-ntilde-mux-phase3.md`, manual section);
  - Phase 4's Windows `ntilde.com` steps (`docs/superpowers/specs/2026-10-05-ntilde-mux-phase4.md`, manual section);
  - the remote drop/reconnect scenario (Docker sshd `novaterm-native-ssh-e2e:v5`, drop and restore with `docker network disconnect/connect`);
  - Phase 5's new steps: the first-close dialog, Quit and close all, reboot-quiet restore, update survival (Tasks 20-24's procedure), the picker with remote hosts, `ls --all`, SFTP on a persistent native tab, and an agent `list_sessions` with a windowless session.

  Each step has an expected result and a blank "Result / log" line per OS.
- [ ] **Steps:**
  1. Write the checklist.
  2. Run the Windows column. Use the dev build with `NTILDE_APPDATA_ROOT=%TEMP%\ntilde-smoke`. GUI steps are performed by the maintainer, because SendKeys automation is unreliable on this machine. CLI steps are scripted and their output pasted.
  3. Run the Linux column's CLI and daemon steps in Docker or WSL where possible.
  4. Hand the macOS column and the Linux GUI steps to the maintainer.
  5. Paste the logs into the final PR. Say plainly which cells were run by whom and which are still open.
  6. Commit `docs(mux): Phase 5 manual checklist`.

### Task 31: Final review and the `dev-mux` → `main` PR

- [ ] **Step 1: Run every suite** on the branch head: VT, Rendering, Architecture, Platform, McpServer, Mux, App main lane, App PlatformBoot lane, `cargo test` for rusty_ssh. Record the counts.
- [ ] **Step 2: Whole-branch review** on the most capable model, as SDD's final review. Make one fix dispatch, then one scoped re-review.
- [ ] **Step 3: Open the Phase 5 PR** into `dev-mux`. It is stacked on #510 until that merges. Its body has the sections by brief section, the §R rulings, the tests per §4 item, and the follow-ups.
- [ ] **Step 4: Merges into `dev-mux` and the `dev-mux` → `main` PR** happen on the maintainer's word. Prepare the `main` PR body: the user-facing summary, then links to #472, #474, #489, #499 and #504 (phases), #508 and #509 (hardening), #510 (sync) and the Phase 5 PR.
