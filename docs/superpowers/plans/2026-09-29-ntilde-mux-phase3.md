# ntilde mux Phase 3 (multi-client attach, text client, carry-overs) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** More than one client can attach to one daemon session, deliberately and safely:
- server-side attach modes (`Shared`, `IfUnattached`, `ReadOnly`);
- a GUI "Attach to session…" flow with a "shared with N" indicator, detach as distinct from close, and a close confirmation;
- a `ntilde mux attach` text client for foreign terminals;
- the PR #489 hardening carry-overs.

Everything stays behind `TerminalSettings.SessionPersistence`.

**Architecture:**
- **Protocol.** v2 is additive over v1. The new pieces are `AttachParams.Mode`, `SessionSummary.Cwd`, the notifications `sessionChanged` and `killed`, and the error `session_attached`. They are gated on the negotiated version.
- **Attach modes.** Exclusivity and read-only are decided inside the existing attach control item on the session's single parse thread.
- **Change notifications.** They are coalesced by the parse loop's own bounded wait. No timer or thread-pool work is involved.
- **The GUI** keeps one connection per window and adds the flows on the Phase 2 seams: factory, pane wiring, `ClosePaneAsync`, `DisposeControlTree`.
- **The text client** lives in `Ntilde.Mux/TextClient/`. It is a renderer over its own `TerminalBuffer`, with a dedicated render thread and a dedicated input thread, and does its console I/O behind `IConsoleSurface`.

**Tech Stack:** .NET 10, C#, Avalonia 12 (App only), xUnit v3, NativeAOT (App publish), `System.Text.Json` source generation, libc/kernel32 P/Invoke (`DllImport`, blittable signatures only).

**Spec:** `docs/superpowers/specs/2026-09-29-ntilde-mux-phase3.md`. Read it first; section numbers below refer to it. Also read the PR #489 description (`gh pr view 489 --repo benyblack/ntilde --json body -q .body`), `tests/Ntilde.Mux.Tests/Scenarios/MultiClientTests.cs` and `tests/Ntilde.Mux.Tests/Support/ClientPaneModel.cs`.

## Global Constraints

- **Branch.** Work on `feat/mux-phase3` (worktree `.worktrees/mux-phase3`), cut from `origin/dev-mux`. The PR targets `dev-mux`. Never touch `claude/ntilde-multiplexer-mbxx9b`, the top-level checkout, or another worktree. Never use bare `git stash`.
- **Building and testing.**
  - Build and test only through `scripts/build.ps1` / `scripts/build.sh`, one test project at a time.
  - `tests/Ntilde.App.Tests` runs with `--blame-hang-timeout 5m`, as two separate lanes, `Lane!=PlatformBoot` then `Lane=PlatformBoot`. Never run them concurrently. Log to a file.
- **NativeAOT.** `IsAotCompatible` and `TreatWarningsAsErrors` stay on. JSON is source-generated only, and there is no reflection.
- **Everything sits behind `SessionPersistence`.** With it Off, nothing changes and no existing test's assertions change.
- **No thread-pool work on the output path.** The text client's render thread is a dedicated thread.
- **Additive protocol changes only.** A v1 daemon keeps working with a v2 GUI.
- **`ReadOnly` is not a security boundary.**
- **`Ntilde.Mux` must not reference App/Avalonia.**
- **Commit trailer:** `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`.
- **Line endings are CRLF.** Run `scripts/build.ps1 format whitespace --no-restore` before each commit.
- **`TerminalPane` dispatch** uses `this.Dispatcher`, never `Dispatcher.UIThread` (#423).
- **Repo gotchas that apply here:**
  - This phase adds **no** `TerminalSettings` field. If one becomes necessary, it must also be added to the `TerminalPane.ApplySettings` whitelist and to `McpServer` `SettingsTools` (two gating drift-guard tests).
  - This phase adds **no** test project. A new one would need the `ci.yml` artifact list and the unit loop.
  - A plain `[Fact]` that touches `Application.Current` poisons the headless run. Every test that builds a `TerminalPane`, a `Window` or a `MainWindow` is an `[AvaloniaFact]`. Pure logic (picker rows, presentation, renderer, chord) stays plain `[Fact]`.
  - `FileShare.None` is not portable, and none is used.
  - No test here touches Skia, so nothing new needs the Rendering.Tests Linux gating.
- **`tests/Ntilde.Mux.Tests/Support/*` is compiled into App.Tests too** (`<Compile Include … Link="MuxSupport\…">` in `Ntilde.App.Tests.csproj`). A new support file that App.Tests uses gets a link line there (Task 18 adds one).

## Review Focus

1. **`IfUnattached` must be atomic on the parse thread, not the connection.** Two connections' attaches are queued while the parse thread is parked; exactly one wins. Pinned by Task 7 `AttachModeTests.IfUnattached_is_decided_on_the_parse_thread`.
2. **`sessionChanged` must never exceed one per session per interval, and its trailing flush must need no further activity.** The flush is driven by the parse loop's bounded wait. Look for any `Timer`, `Task.Delay`, `ThreadPool` or `Task.Run` in `HeadlessTerminalSession`: there must be none. Pinned by Task 8 `SessionEventsTests.A_burst_is_rate_bounded_and_the_trailing_notification_needs_no_further_activity`.
3. **A v2 GUI against a v1 daemon must never send a mode the daemon would silently ignore.** Pinned by Task 9 `ProtocolFallbackTests.A_v2_client_refuses_a_non_shared_mode_on_a_v1_server_before_sending`.
4. **The text client restores the console on every exit path:** detach, exit, kill, disconnect, attach failure, a console write that throws, and a signal. Pinned by Task 17 `TextClientTests.Console_modes_are_restored_on_every_exit_path`.
5. **With persistence Off, nothing is registered or changed.** Pinned by Task 14 `MainWindowMuxSharingTests.Mux_commands_are_not_registered_when_persistence_is_off` and the existing `MainWindowMuxLifecycleTests.Persistence_is_off_by_default`.
6. **Close versus detach.** Close kills, so the other client sees "ended from another window". Detach keeps the shell and drops the count. A pane whose shell already exited sends no kill. Pinned by Task 14 `Close_with_others_attached_prompts_and_each_choice_behaves` and Task 4 `A_close_of_an_exited_mux_pane_sends_no_kill`.

---

## File map

| File | Task | Responsibility |
|---|---|---|
| `src/Ntilde.App/Shell/Mux/MuxCommand.cs` | 1, 18 | `kill-server --force` pid fallback; `mux attach` |
| `src/Ntilde.App/Shell/Mux/MuxDaemonLauncher.cs`, `src/Ntilde.App/Controls/TerminalPane.axaml.cs` (hint text) | 1 | `kill-server --force` hint |
| `src/Ntilde.Mux/HeadlessTerminalSession.cs` | 2, 7, 8 | cap accounting; attach modes; session events; killed |
| `tests/Ntilde.Mux.Tests/Support/ScriptedTerminalSession.cs` | 2 | `SendInputEntered` |
| `src/Ntilde.App/Shell/Mux/MuxStartupProbe.cs` (new), `src/Ntilde.App/Program.cs` | 3, 18 | startup gate probe; attach console |
| `src/Ntilde.App/MainWindow.axaml.cs` | 3, 4, 12, 13, 14 | teardown guard; kill tracking; sharing chip; picker; detach/close |
| `src/Ntilde.App/Shell/Mux/MuxConnectionHost.cs` | 4 | tracked kills flushed on dispose |
| `src/Ntilde.App/Shell/Mux/MuxCommandMatch.cs` (new), `MuxTerminalSessionFactory.cs` | 5, 10 | command check; v2 restore; `AttachShared` |
| `src/Ntilde.Mux.Contracts/MuxProtocol.cs`, `MuxMessages.cs`, `MuxJsonContext.cs`, `MuxAttachMode.cs` (new) | 6 | v2 wire |
| `src/Ntilde.Mux/IMuxFrameSink.cs`, `MuxServer.cs`, `MuxServerConnection.cs`, `MuxServerOptions.cs`, `HeadlessSessionOptions.cs` | 7, 8 | modes, read-only enforcement, events |
| `src/Ntilde.Mux/MuxClient.cs`, `MuxClientSession.cs` | 9 | v2 client surfaces |
| `src/Ntilde.Pty/ITerminalSessionFactory.cs`, `src/Ntilde.App/Shell/Mux/PersistentSessionFactory.cs` | 10 | `AttachShared`, `AttachedElsewhere` |
| `src/Ntilde.App/Shell/Shortcuts/ShortcutDefinition.cs`, `ShortcutBindingResolver.cs`, `ShortcutCatalog.cs`, `src/Ntilde.App/SettingsWindow.axaml.cs` | 11 | unbound entries |
| `src/Ntilde.App/Controls/TerminalPane.axaml(.cs)`, `src/Ntilde.App/Shell/TabStatusPresentation.cs` | 10, 12 | notice; indicator; banner; focus grid |
| `src/Ntilde.App/Shell/Mux/MuxSessionPicker.cs` (new) | 13 | picker rows |
| `src/Ntilde.App/Shell/Mux/PaneDisposition.cs`, `SharedCloseChoice.cs` (new) | 14 | `Detach`; the close choice |
| `src/Ntilde.VT/Export/AnsiCellWriter.cs` (new) | 15 | SGR/cell serializer over `RenderCellSnapshot` |
| `src/Ntilde.Mux/TextClient/*` (new) | 16, 17, 18 | model, renderer, chord, client, console surfaces |
| `src/Ntilde.App/Shell/CliConsoleBindings.cs` | 18 | `PrepareInteractive` |
| `tests/Ntilde.Architecture.Tests/*` | 18 | TextClient rows, `mux attach` dispatch row |
| `tests/Ntilde.App.Tests/MuxDaemonSmokeTests.cs` | 19 | two shared clients on a real shell |
| `docs/USER_MANUAL.md`, `docs/ARCHITECTURE.md`, `docs/MODULE_OWNERSHIP.md` | 20 | docs |

Test-run command shapes used below (PowerShell):

```powershell
scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~<Class>"
scripts/build.ps1 test tests/Ntilde.VT.Tests --filter "FullyQualifiedName~<Class>"
scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~<Class>"
scripts/build.ps1 test tests/Ntilde.Architecture.Tests
```

**Task order is strict:** 1 → 2 → … → 21. Task 1 lands **before** the version bump (Task 6).

---

### Task 1: `kill-server --force` pid fallback (before the version bump)

**Files:**
- Modify: `src/Ntilde.App/Shell/Mux/MuxCommand.cs`
- Modify: `src/Ntilde.App/Shell/Mux/MuxDaemonLauncher.cs` (`KillServerHint`)
- Modify: `src/Ntilde.App/Controls/TerminalPane.axaml.cs` (`MuxVersionMismatchHint`)
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxCommandTests.cs`

**Interfaces:**
- Consumes: `MuxDiscovery.TryReadLiveDescriptor(string, out MuxEndpointDescriptor?)`, `MuxDiscovery.IsProcessAlive(int, string)`, `MuxDiscovery.DeleteDescriptorIfOwned(string, int)`, `MuxDaemonExit.WaitForExit(string, MuxEndpointDescriptor?, TimeSpan, int)`, and `MuxUnavailableException.VersionMismatch`, which `MuxDaemonLauncher.TryConnectExistingAsync` throws on `version_mismatch`. In tests: `MuxDaemonHost`, `MuxDaemonOptions { Pid, ProcessName }`, `ScriptedSessionFactory`, and the existing `StartLongRunningProcess()` helper in `MuxCommandTests`.
- Produces: `internal static int MuxCommand.KillByPid(string descriptorPath, bool force, TextWriter stdout, TextWriter stderr)`, and the usage line `ntilde mux kill-server [--force]`.

- [ ] **Step 1: Write the failing tests** (append to `MuxCommandTests`)

```csharp
    /// <summary>A daemon of another protocol version: the handshake fails, only the pid can stop it.</summary>
    private System.Diagnostics.Process StartForeignVersionDaemon()
    {
        System.Diagnostics.Process standIn = StartLongRunningProcess();
        var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions
        {
            MinProtocolVersion = 99,
            MaxProtocolVersion = 99,
            ForceConPtyFiltering = false,
        });
        _host = new MuxDaemonHost(server, new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = MuxDiscovery.GetDescriptorPath(_root),
            IdleExitAfter = TimeSpan.Zero,
            Pid = standIn.Id,
            ProcessName = standIn.ProcessName,
        });
        _host.Start();
        return standIn;
    }

    [Fact]
    public void Kill_server_against_another_protocol_version_names_the_pid_and_needs_force()
    {
        using System.Diagnostics.Process standIn = StartForeignVersionDaemon();
        try
        {
            var (code, output, err) = Run("mux", "kill-server");

            Assert.Equal(1, code);
            Assert.Contains($"pid {standIn.Id}", err);
            Assert.Contains("--force", err);
            Assert.DoesNotContain("terminated", output);
            Assert.False(standIn.HasExited);
        }
        finally
        {
            try { standIn.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void Kill_server_force_terminates_a_verified_daemon_of_another_version()
    {
        using System.Diagnostics.Process standIn = StartForeignVersionDaemon();
        try
        {
            var (code, output, err) = Run("mux", "kill-server", "--force");

            Assert.Equal(0, code);
            Assert.Contains($"pid {standIn.Id}", output);
            Assert.True(standIn.WaitForExit(10_000), "the daemon process was terminated");
            Assert.False(File.Exists(MuxDiscovery.GetDescriptorPath(_root)), "the stale descriptor is gone");
            Assert.Equal(string.Empty, err);
        }
        finally
        {
            try { standIn.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void The_pid_fallback_refuses_a_process_whose_name_does_not_match()
    {
        using System.Diagnostics.Process standIn = StartLongRunningProcess();
        try
        {
            string path = MuxDiscovery.GetDescriptorPath(_root);
            MuxDiscovery.WriteDescriptor(path, new MuxEndpointDescriptor
            {
                MinVersion = 99,
                MaxVersion = 99,
                Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
                Pid = standIn.Id,
                ProcessName = "not-the-daemon",   // a recycled pid
            });
            var o = new StringWriter();
            var e = new StringWriter();

            int code = MuxCommand.KillByPid(path, force: true, o, e);

            Assert.Equal(1, code);
            Assert.False(standIn.HasExited, "an unverified process is never killed");
        }
        finally
        {
            try { standIn.Kill(); } catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void Kill_server_rejects_unknown_options() => Assert.Equal(2, Run("mux", "kill-server", "--bogus").Code);
```

- [ ] **Step 2: Run them to see them fail**

`scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MuxCommandTests"`

Expected: a compile error (`MuxCommand.KillByPid` does not exist). With a stub `KillByPid` that returns 1, the three behavioural tests fail: the output says "mux: The running multiplexer (pid …) is a different version…", exit 1, no `--force` mention in the expected form, and the process survives.

- [ ] **Step 3: Implement.** In `MuxCommand.cs`, change the usage line and `KillServer`, and add `KillByPid`:

```csharp
    private const string Usage = """
        Usage:
          ntilde mux serve [--idle-exit-minutes N] [--foreground]
          ntilde mux ls [--json]
          ntilde mux kill <sessionId>
          ntilde mux kill-server [--force]
        """;
```

```csharp
    private static int KillServer(string[] args, TextWriter stdout, TextWriter stderr, string descriptorPath)
    {
        bool force = false;
        foreach (string arg in args.Skip(2))
        {
            if (arg != "--force") return Fail(stderr, Usage);
            force = true;
        }

        MuxDiscovery.TryReadDescriptor(descriptorPath, out MuxEndpointDescriptor? d);
        try
        {
            using MuxClient? client = Connect(descriptorPath, stderr);
            if (client is null) return 1;
            client.ShutdownServerAsync().GetAwaiter().GetResult();
        }
        catch (MuxUnavailableException ex) when (ex.VersionMismatch)
        {
            // It cannot be asked to stop: it does not speak our protocol (PR #489 follow-up).
            return KillByPid(descriptorPath, force, stdout, stderr);
        }

        // Wait for it to really be gone, so "kill-server && start" cannot race the old daemon.
        if (!MuxDaemonExit.WaitForExit(descriptorPath, d, TimeSpan.FromSeconds(5), Environment.ProcessId))
        {
            stderr.WriteLine("Multiplexer did not stop within 5 s.");
            return 1;
        }

        stdout.WriteLine("Multiplexer stopped.");
        return 0;
    }

    /// <summary>
    /// The version-mismatch fallback: terminate the daemon named by the descriptor, but only after
    /// verifying that its pid is alive under the recorded process name (a recycled pid is never
    /// killed), and only with <paramref name="force"/>. Never this process.
    /// </summary>
    internal static int KillByPid(string descriptorPath, bool force, TextWriter stdout, TextWriter stderr)
    {
        if (!MuxDiscovery.TryReadLiveDescriptor(descriptorPath, out MuxEndpointDescriptor? d))
        {
            stderr.WriteLine("The multiplexer speaks a different protocol version, and its process could not be verified (pid and process name); nothing was terminated.");
            return 1;
        }

        if (d.Pid == Environment.ProcessId)
        {
            stderr.WriteLine("Refusing to terminate this process.");
            return 1;
        }

        if (!force)
        {
            stderr.WriteLine($"The running multiplexer (pid {d.Pid}) speaks a different protocol version. Re-run with --force to terminate it (this ends its sessions).");
            return 1;
        }

        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(d.Pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            stderr.WriteLine($"Could not terminate pid {d.Pid}: {ex.Message}");
            return 1;
        }

        if (!MuxDaemonExit.WaitForExit(descriptorPath, d, TimeSpan.FromSeconds(5), Environment.ProcessId))
        {
            stderr.WriteLine("Multiplexer did not stop within 5 s.");
            return 1;
        }

        MuxDiscovery.DeleteDescriptorIfOwned(descriptorPath, d.Pid);
        stdout.WriteLine($"Multiplexer (pid {d.Pid}) terminated.");
        return 0;
    }
```

Update the two hints so they name the flag that now works:

```csharp
// MuxDaemonLauncher.cs
internal const string KillServerHint = "Run 'ntilde mux kill-server --force' to replace it (this closes its sessions).";
// TerminalPane.axaml.cs
internal const string MuxVersionMismatchHint = "[The running multiplexer is a different version — run 'ntilde mux kill-server --force' to replace it]";
```

- [ ] **Step 4: Run the tests again**

`scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MuxCommandTests|FullyQualifiedName~MuxTerminalSessionFactoryTests|FullyQualifiedName~MuxPaneTests"` → all PASS. `MuxPaneTests` and the factory test reference the hint constants, not their text.

- [ ] **Step 5: Commit.** Run `scripts/build.ps1 format whitespace --no-restore`, then:

```
git add src/Ntilde.App/Shell/Mux/MuxCommand.cs src/Ntilde.App/Shell/Mux/MuxDaemonLauncher.cs src/Ntilde.App/Controls/TerminalPane.axaml.cs tests/Ntilde.App.Tests/Shell/Mux/MuxCommandTests.cs
git commit -m "fix(mux): kill-server --force terminates a verified daemon of another protocol version

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Input cap charges a write until it returns

**Files:**
- Modify: `src/Ntilde.Mux/HeadlessTerminalSession.cs` (`InputLoop`)
- Modify: `tests/Ntilde.Mux.Tests/Support/ScriptedTerminalSession.cs` (`SendInputEntered`)
- Modify: `tests/Ntilde.Mux.Tests/Headless/HeadlessInputWriterTests.cs`

**Interfaces:**
- Consumes: `HeadlessTerminalSession.MaxQueuedInputBytesForTest`, `QueuedInputBytes`, and `ScriptedTerminalSession.SendInputGate`.
- Produces: `ScriptedTerminalSession.SendInputEntered` (a `ManualResetEventSlim`, set at the top of every `SendInput`).

- [ ] **Step 1: Add the test signal** to `ScriptedTerminalSession`:

```csharp
    /// <summary>Set on entry to every SendInput, before it waits on <see cref="SendInputGate"/>: the writer is inside a write.</summary>
    public ManualResetEventSlim SendInputEntered { get; } = new(false);

    public void SendInput(string input)
    {
        SendInputEntered.Set();
        if (ThrowOnSendInput) throw new InvalidOperationException("scripted SendInput failure");
        SendInputGate?.Wait();
        SentInput.Enqueue(input);
    }
```

- [ ] **Step 2: Write the failing test, and rewrite the one existing test that pinned the old accounting.** The existing `Input_beyond_the_byte_cap_is_dropped_not_queued_forever` waited for `QueuedInputBytes == 0` while the writer was blocked. That is exactly the bug. It is replaced by the version below. Spec §11 item 10 records this.

```csharp
    [Fact]
    public async Task Input_beyond_the_byte_cap_is_dropped_not_queued_forever()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using var gate = new ManualResetEventSlim(false);
        ScriptedTerminalSession fake = host.Fake(id);
        fake.SendInputGate = gate;
        HeadlessTerminalSession mux = host.Mux(id);
        mux.MaxQueuedInputBytesForTest = 1024;

        mux.SendInput("first");
        Assert.True(fake.SendInputEntered.Wait(TimeSpan.FromSeconds(10)), "the writer is inside the first write");
        Assert.Equal("first".Length * sizeof(char), mux.QueuedInputBytes);  // still charged while blocked
        mux.SendInput(new string('x', 2000));                                // over the cap: dropped
        Assert.Equal("first".Length * sizeof(char), mux.QueuedInputBytes);
        mux.SendInput("second");                                             // under the cap: queued
        Assert.Equal(("first".Length + "second".Length) * sizeof(char), mux.QueuedInputBytes);

        gate.Set();
        await TestWait.UntilAsync(() => string.Concat(fake.SentInput) == "firstsecond", "only the capped item was dropped");
        await TestWait.UntilAsync(() => mux.QueuedInputBytes == 0, "the cap is released once written");
    }

    [Fact]
    public async Task A_write_blocked_on_a_wedged_child_still_counts_against_the_cap()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using var gate = new ManualResetEventSlim(false);
        ScriptedTerminalSession fake = host.Fake(id);
        fake.SendInputGate = gate;
        HeadlessTerminalSession mux = host.Mux(id);
        mux.MaxQueuedInputBytesForTest = 20;                // exactly one 10-char string

        mux.SendInput("0123456789");
        Assert.True(fake.SendInputEntered.Wait(TimeSpan.FromSeconds(10)), "the writer took it and is blocked");
        mux.SendInput("a");                                  // the blocked write fills the cap: dropped

        gate.Set();
        await TestWait.UntilAsync(() => mux.QueuedInputBytes == 0, "the blocked write finished");
        Assert.Equal("0123456789", string.Concat(fake.SentInput));
    }
```

- [ ] **Step 3: Run the tests to see them fail**

`scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~HeadlessInputWriterTests"`

Expected:
- `A_write_blocked…` FAILS: `SentInput` is `"0123456789a"`, because the item was un-charged when taken.
- The rewritten cap test FAILS at the first `Assert.Equal`: 0 instead of 10.

- [ ] **Step 4: Implement.** In `HeadlessTerminalSession.InputLoop`, move the decrement into a `finally` after the write:

```csharp
    private void InputLoop()
    {
        try
        {
            foreach (string text in _input.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    _session.SendInput(text);
                    Volatile.Write(ref _inputDropLogged, 0);
                }
                catch (Exception ex)
                {
                    Log($"[Mux] session {Id}: SendInput failed: {ex.Message}");
                }
                finally
                {
                    // Charged until the write returns (PR #489 follow-up): a write blocked on a child
                    // that stopped reading is still input the child has not taken, and must count
                    // against the cap - un-charging it on take let one more cap's worth queue behind it.
                    Interlocked.Add(ref _queuedInputBytes, -(long)text.Length * sizeof(char));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose, a failed constructor or the terminal exit cancelled the token: the normal end.
        }
    }
```

Update the `QueuedInputBytes` doc comment to say "queued for the input writer or being written by it".

- [ ] **Step 5: Run the tests again.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~HeadlessInputWriterTests|FullyQualifiedName~HeadlessTerminalSessionTests"` → all PASS.

- [ ] **Step 6: Commit** (format first): `fix(mux): charge queued input until the write returns`, with the trailer.

---

### Task 3: Startup gate probe, and a guarded teardown before an update

**Files:**
- Create: `src/Ntilde.App/Shell/Mux/MuxStartupProbe.cs`
- Modify: `src/Ntilde.App/Program.cs` (the `liveDaemon` delegate)
- Modify: `src/Ntilde.App/MainWindow.axaml.cs` (`PerformAppTeardown` seam, `ApplyStagedUpdateAsync`)
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxStartupProbeTests.cs`
- Test: `tests/Ntilde.App.Tests/Update/UpdateClosesMuxTests.cs`

**Interfaces:**
- Consumes: `MuxDiscovery.TryReadLiveDescriptor`, `MuxDiscovery.DeleteDescriptorIfOwned`, `MuxDaemonLauncher.IsTrustedEndpoint(string, string, out string?)` (internal static), `MuxEndpointConnector.Connect(string, TimeSpan)`, and in tests `MuxDaemonHost` and `UpdateClosesMuxTests.RunToCompletion` / `StageUpdate` / `CreateWindow`.
- Produces:
  - `internal static bool MuxStartupProbe.IsDaemonLive(string descriptorPath, TimeSpan connectTimeout, Func<string, TimeSpan, Stream>? connect = null)`
  - `internal Action? MainWindow.TeardownFaultForTest { get; set; }`

- [ ] **Step 1: Write the failing probe tests**

```csharp
// tests/Ntilde.App.Tests/Shell/Mux/MuxStartupProbeTests.cs
using System.Diagnostics;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxStartupProbeTests : IDisposable
{
    // Short on purpose: the Unix socket path must fit sun_path (see MuxDiscovery.GetDefaultEndpoint).
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nmxp" + Guid.NewGuid().ToString("N")[..8]);
    private MuxDaemonHost? _host;

    public void Dispose()
    {
        _host?.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string DescriptorPath => MuxDiscovery.GetDescriptorPath(_root);

    /// <summary>A descriptor naming THIS live process (pid and name match) with nobody listening: a recycled pid.</summary>
    private void WriteRecycledPidDescriptor()
    {
        using Process self = Process.GetCurrentProcess();
        MuxDiscovery.WriteDescriptor(DescriptorPath, new MuxEndpointDescriptor
        {
            MinVersion = MuxProtocol.MinSupportedVersion,
            MaxVersion = MuxProtocol.MaxSupportedVersion,
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            Pid = self.Id,
            ProcessName = self.ProcessName,
        });
    }

    [Fact]
    public void No_descriptor_is_not_live() =>
        Assert.False(MuxStartupProbe.IsDaemonLive(DescriptorPath, TimeSpan.FromMilliseconds(200)));

    [Fact]
    public void A_recycled_pid_whose_endpoint_refuses_is_not_live_and_its_descriptor_is_deleted()
    {
        WriteRecycledPidDescriptor();

        Assert.False(MuxStartupProbe.IsDaemonLive(DescriptorPath, TimeSpan.FromMilliseconds(200)));
        Assert.False(File.Exists(DescriptorPath));
    }

    [Fact]
    public void A_connect_that_times_out_counts_as_refused()
    {
        WriteRecycledPidDescriptor();

        bool live = MuxStartupProbe.IsDaemonLive(DescriptorPath, TimeSpan.FromMilliseconds(200),
            (_, _) => throw new TimeoutException("scripted"));

        Assert.False(live);
        Assert.False(File.Exists(DescriptorPath));
    }

    [Fact]
    public void A_listening_daemon_is_live_and_its_descriptor_is_kept()
    {
        var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ForceConPtyFiltering = false });
        _host = new MuxDaemonHost(server, new MuxDaemonOptions
        {
            Endpoint = MuxDiscovery.GetDefaultEndpoint(_root),
            DescriptorPath = DescriptorPath,
            IdleExitAfter = TimeSpan.Zero,
        });
        _host.Start();

        Assert.True(MuxStartupProbe.IsDaemonLive(DescriptorPath, TimeSpan.FromSeconds(2)));
        Assert.True(File.Exists(DescriptorPath));
    }
}
```

- [ ] **Step 2: Write the failing teardown test** (append to `UpdateClosesMuxTests`, which already has `using System.Reflection;` if needed, else add it):

```csharp
    /// <summary>PR #489 follow-up: a teardown that throws must be reported, never an unobserved fault, and must not apply.</summary>
    [AvaloniaFact]
    public void A_teardown_that_throws_is_reported_and_the_update_is_not_applied()
    {
        MainWindow window = CreateWindow();
        FakeApplyUpdateService service = StageUpdate(window);
        window.MuxProbeForUpdate = _ => Task.FromResult<MuxClient?>(null);
        window.TeardownFaultForTest = () => throw new InvalidOperationException("scripted teardown failure");

        Task task = window.ApplyStagedUpdateAsync();
        PumpUntil(() => task.IsCompleted, "ApplyStagedUpdateAsync finished");

        Assert.True(task.IsCompletedSuccessfully, $"the apply path faulted: {task.Exception?.GetBaseException().Message}");
        Assert.Equal(0, service.ApplyCount);
        Assert.Equal("Update could not be applied", window.FindControl<Avalonia.Controls.TextBlock>("RecordingToastTitle")!.Text);

        // The window is still up; its later close must run the whole teardown again (it saves the session).
        window.TeardownFaultForTest = null;
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
        typeof(MainWindow).GetMethod("PerformAppTeardown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null);
        Assert.True(File.Exists(AppPaths.SessionFilePath));
    }
```

- [ ] **Step 3: Add the seam only, then run to see RED.** In `MainWindow.axaml.cs`:

```csharp
        /// <summary>Test seam: runs inside <see cref="PerformAppTeardown"/> right after its one-shot guard.</summary>
        internal Action? TeardownFaultForTest { get; set; }
```

and in `PerformAppTeardown`, right after `_teardownDone = true;`:

```csharp
            TeardownFaultForTest?.Invoke();
```

Create `MuxStartupProbe.cs` with a stub body `=> false;` so the probe tests compile.

Run:
- `scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MuxStartupProbeTests|FullyQualifiedName~UpdateClosesMuxTests"`

Expected:
- `A_listening_daemon_is_live…` FAILS (the stub returns false).
- The two deletion tests FAIL: the descriptor still exists.
- `A_teardown_that_throws…` FAILS: the task is faulted with the scripted exception.

- [ ] **Step 4: Implement the probe**

```csharp
// src/Ntilde.App/Shell/Mux/MuxStartupProbe.cs
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;

namespace Ntilde.Shell.Mux;

/// <summary>
/// Whether a multiplexer daemon is really listening, for the startup auto-apply gate
/// (PR #489 follow-up). A descriptor whose pid and process name check out is not enough - pids get
/// recycled - so the endpoint is probe-connected. A descriptor whose endpoint refuses is stale: it
/// is deleted (only if it still names that pid), and the gate no longer disables auto-update.
/// </summary>
internal static class MuxStartupProbe
{
    public static bool IsDaemonLive(string descriptorPath, TimeSpan connectTimeout, Func<string, TimeSpan, Stream>? connect = null)
    {
        if (!MuxDiscovery.TryReadLiveDescriptor(descriptorPath, out MuxEndpointDescriptor? d)) return false;

        // <root>/mux/mux-endpoint.json: only this root's endpoint is trusted (MuxDaemonLauncher).
        string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(descriptorPath))!)!;
        if (!MuxDaemonLauncher.IsTrustedEndpoint(d.Endpoint, root, out _)) return false;

        try
        {
            // Connected and closed without a hello: the daemon's reader sees EOF and forgets it.
            using Stream stream = (connect ?? MuxEndpointConnector.Connect)(d.Endpoint, connectTimeout);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            MuxDiscovery.DeleteDescriptorIfOwned(descriptorPath, d.Pid);
            return false;
        }
    }
}
```

In `Program.cs`, replace the `liveDaemon` lambda:

```csharp
            VelopackApp.Build()
                .SetAutoApplyOnStartup(ShouldAutoApplyUpdateOnStartup(
                    args,
                    static () => Ntilde.Shell.Mux.MuxStartupProbe.IsDaemonLive(
                        Ntilde.Mux.Contracts.MuxDiscovery.GetDescriptorPath(), TimeSpan.FromMilliseconds(200))))
                .Run();
```

Update the doc comment of `ShouldAutoApplyUpdateOnStartup`: "a daemon that answers a 200 ms probe-connect", not "a live pid".

- [ ] **Step 5: Implement the teardown guard.** In `ApplyStagedUpdateAsync`, replace the bare `PerformAppTeardown();` with:

```csharp
                try
                {
                    PerformAppTeardown();
                }
                catch (Exception ex)
                {
                    // Outside the apply try before (PR #489 follow-up): a throw here escaped this
                    // fire-and-forget method as an unobserved task fault. Nothing is applied; the
                    // window stays up and its own close must run the teardown again.
                    TerminalLogger.Log("Tearing down before the update failed: " + ex);
                    _teardownDone = false;
                    ShowRecordingToast(
                        "Update could not be applied",
                        "The update was downloaded but could not be applied. Close Ntilde and start it again to finish updating.",
                        null,
                        null,
                        autoHide: false);
                    return;
                }
```

- [ ] **Step 6: Run the tests again.** `scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MuxStartupProbeTests|FullyQualifiedName~UpdateClosesMuxTests|FullyQualifiedName~StartupAutoApplyTests"` → all PASS. `StartupAutoApplyTests` passes its own delegate, so it is unaffected.

- [ ] **Step 7: Commit** (format first): `fix(mux): probe-connect before the startup auto-apply gate; guard the pre-update teardown`, with the trailer.

---

### Task 4: The last tab's kill lands (tracked kills, flushed on dispose)

**Files:**
- Modify: `src/Ntilde.App/Shell/Mux/MuxConnectionHost.cs`
- Modify: `src/Ntilde.App/MainWindow.axaml.cs` (`KillMuxSessionOnClose` becomes an instance method)
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxConnectionHostTests.cs`
- Test: `tests/Ntilde.App.Tests/Core/MainWindowMuxLifecycleTests.cs`

**Interfaces:**
- Consumes: `MuxClientSession.KillAsync(CancellationToken)`, `MuxClientSession.IsConnected`, `MuxClientSession.IsProcessRunning`.
- Produces:
  - `public TimeSpan MuxConnectionHost.KillFlushTimeout { get; init; } = TimeSpan.FromSeconds(3)`
  - `public void MuxConnectionHost.TrackPendingKill(Task kill)`
  - `internal int MuxConnectionHost.PendingKillCountForTest`

- [ ] **Step 1: Write the failing host tests** (append to `MuxConnectionHostTests`)

```csharp
    [Fact]
    public void Dispose_waits_for_a_tracked_kill()
    {
        using var mux = new MuxTestHost();
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), "test", null)
        {
            KillFlushTimeout = TimeSpan.FromSeconds(10),
            DisposeFlushTimeout = TimeSpan.Zero,
        };
        Assert.NotNull(host.GetClient(TimeSpan.FromSeconds(5)));
        var kill = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.TrackPendingKill(kill.Task);

        Task dispose = Task.Run(host.Dispose, TestContext.Current.CancellationToken);

        Assert.False(dispose.Wait(300, TestContext.Current.CancellationToken), "Dispose returned before the kill was confirmed");
        kill.SetResult();
        Assert.True(dispose.Wait(5_000, TestContext.Current.CancellationToken), "Dispose returned once the kill was confirmed");
    }

    [Fact]
    public void Dispose_gives_up_on_an_unconfirmed_kill_after_the_timeout()
    {
        using var mux = new MuxTestHost();
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), "test", null)
        {
            KillFlushTimeout = TimeSpan.FromMilliseconds(200),
        };
        Assert.NotNull(host.GetClient(TimeSpan.FromSeconds(5)));
        host.TrackPendingKill(new TaskCompletionSource().Task); // never completes

        var sw = System.Diagnostics.Stopwatch.StartNew();
        host.Dispose();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"Dispose took {sw.Elapsed}");
    }
```

- [ ] **Step 2: Write the window tests** (append to `MainWindowMuxLifecycleTests`). First change the fixture's `CreateWindow` so the flush can be configured:

```csharp
    private MainWindow CreateWindow(TimeSpan? disposeFlush = null)
    {
        _host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct), "test", null)
        {
            DisposeFlushTimeout = disposeFlush ?? TimeSpan.FromSeconds(1),
        };
        var factory = new MuxTerminalSessionFactory(_host, new RecordingSessionFactory(new FakeTerminalSession()), null);
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
        });
        Assert.Same(_host, window.MuxHost);
        window.Show();
        PumpUntil(() => AllPanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the first pane attached");
        return window;
    }
```

```csharp
    /// <summary>
    /// PR #489 follow-up: with no ping flush at all, the kill for the last closed tab must still land,
    /// because the host now waits for the kill's own reply. (The deterministic RED for this change is
    /// MuxConnectionHostTests.Dispose_waits_for_a_tracked_kill; before the fix this test fails only
    /// when the fire-and-forget kill is still in the client's outbound queue at close, which is usual
    /// but not guaranteed.)
    /// </summary>
    [AvaloniaFact]
    public void Closing_last_tab_with_no_ping_flush_still_kills()
    {
        MainWindow window = CreateWindow(disposeFlush: TimeSpan.Zero);
        TerminalPane pane = AllPanes(window).Single();
        Guid id = ((MuxClientSession)pane.Session!).Id;
        var close = typeof(MainWindow).GetMethod("ClosePaneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var task = (Task<bool>)close.Invoke(window, [pane, true])!;

        PumpUntil(() => task.IsCompleted, "the close finished");
        Assert.DoesNotContain(id, _mux.Server.GetSessionIds());
    }

    [AvaloniaFact]
    public void A_close_of_an_exited_mux_pane_sends_no_kill()
    {
        MainWindow window = CreateWindow();
        TerminalPane pane = AllPanes(window).Single();
        var mux = (MuxClientSession)pane.Session!;
        // Non-zero: under the default "Graceful" ShellExitPolicy the pane stays (exit 0 would close
        // the last tab, and with it the window).
        _mux.Fake(mux.Id).Exit(3);
        PumpUntil(() => !mux.IsProcessRunning, "the pane saw the exit");

        typeof(MainWindow).GetMethod("KillMuxSessionOnClose", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [mux, PaneDisposition.EndSession]);

        Assert.Equal(0, _host!.PendingKillCountForTest);
    }
```

- [ ] **Step 3: Run the tests to see them fail**

`scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MuxConnectionHostTests|FullyQualifiedName~MainWindowMuxLifecycleTests"`

Expected: a compile error (`KillFlushTimeout`, `TrackPendingKill`, `PendingKillCountForTest` are missing). With stubs (an empty `TrackPendingKill`, and a count of 0), `Dispose_waits_for_a_tracked_kill` FAILS: Dispose returns at once.

- [ ] **Step 4: Implement the host side** in `MuxConnectionHost`:

```csharp
    private readonly List<Task> _pendingKills = new(); // guarded by _gate

    /// <summary>How long <see cref="Dispose"/> waits for the replies of tracked kills (closing the last tab).</summary>
    public TimeSpan KillFlushTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A kill a user close sent (its reply means it landed). <see cref="Dispose"/> waits for these
    /// first, so the last tab's kill cannot be dropped by the teardown right behind it (PR #489).
    /// </summary>
    public void TrackPendingKill(Task kill)
    {
        ArgumentNullException.ThrowIfNull(kill);
        lock (_gate)
        {
            _pendingKills.RemoveAll(t => t.IsCompleted);
            _pendingKills.Add(kill);
        }
    }

    internal int PendingKillCountForTest { get { lock (_gate) return _pendingKills.Count(t => !t.IsCompleted); } }
```

In `Dispose`, directly after `if (client is null) return;`:

```csharp
        Task[] kills;
        lock (_gate) kills = _pendingKills.Where(t => !t.IsCompleted).ToArray();
        if (kills.Length > 0 && client.IsConnected)
        {
            try
            {
                // Inside Task.Run like the ping below: no UI sync context is captured.
                if (!Task.Run(() => Task.WhenAll(kills), CancellationToken.None).Wait(KillFlushTimeout, CancellationToken.None))
                {
                    _log?.Invoke($"[Mux] {kills.Length} kill(s) not confirmed within {KillFlushTimeout.TotalSeconds:0.#} s; closing anyway");
                }
            }
            catch (AggregateException)
            {
                // A kill that failed was logged by whoever sent it; closing proceeds either way.
            }
        }
```

- [ ] **Step 5: Implement the window side.** Replace the static `KillMuxSessionOnClose` with an instance method. Its only caller, `DisposeControlTree`, is an instance method:

```csharp
        /// <summary>
        /// A user closed this pane: a mux shell must end. KillAsync (not the fire-and-forget Kill): its
        /// reply means the kill landed, and the host waits for it on dispose, so closing the last tab
        /// cannot drop it. Still enqueued synchronously on the UI thread (RequestAsync enqueues before
        /// its first await). A session that already exited or lost its connection is left alone.
        /// </summary>
        private void KillMuxSessionOnClose(ITerminalSession? session, Ntilde.Shell.Mux.PaneDisposition disposition)
        {
            if (disposition != Ntilde.Shell.Mux.PaneDisposition.EndSession || session is not Ntilde.Mux.MuxClientSession mux)
            {
                return;
            }

            if (!mux.IsConnected || !mux.IsProcessRunning) return;

            try
            {
                Task kill = mux.KillAsync();
                _muxHost?.TrackPendingKill(kill);
                _ = kill.ContinueWith(
                    t => TerminalLogger.Log($"[MainWindow] mux kill of {mux.Id} failed: {t.Exception?.GetBaseException().Message}"),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                TerminalLogger.Log($"[MainWindow] mux kill failed: {ex.Message}");
            }
        }
```

- [ ] **Step 6: Run the tests again.** The same filter → all PASS, including the existing `Closing_last_tab_kills_its_session_before_teardown` and `Window_close_detaches_and_the_session_keeps_running`.

- [ ] **Step 7: Commit** (format first): `fix(mux): closing a pane waits for its kill's reply before the connection closes`, with the trailer.

---

### Task 5: The reattach checks the command

**Files:**
- Create: `src/Ntilde.App/Shell/Mux/MuxCommandMatch.cs`
- Modify: `src/Ntilde.App/Shell/Mux/MuxTerminalSessionFactory.cs`
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxCommandMatchTests.cs`
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxTerminalSessionFactoryTests.cs`

**Interfaces:**
- Consumes: `SessionSummary.Command`, and in tests `MuxTerminalSessionFactoryTests.Build()` / `Local(Guid?)`.
- Produces: `internal static bool MuxCommandMatch.SameExecutable(string? daemonCommand, string? requestCommand)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Ntilde.App.Tests/Shell/Mux/MuxCommandMatchTests.cs
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxCommandMatchTests
{
    [Theory]
    [InlineData("pwsh", "pwsh")]
    [InlineData("pwsh.exe", "pwsh")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", "pwsh.exe")]
    [InlineData("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\"", "pwsh")]
    [InlineData("/bin/zsh", "zsh")]
    [InlineData("", "bash")]          // unknown daemon command: do not refuse a reattach over it
    [InlineData(null, "bash")]
    public void Same_executable(string? daemon, string request) => Assert.True(MuxCommandMatch.SameExecutable(daemon, request));

    [Theory]
    [InlineData("pwsh", "cmd.exe")]
    [InlineData("/bin/zsh", "/bin/bash")]
    public void Different_executable(string daemon, string request) => Assert.False(MuxCommandMatch.SameExecutable(daemon, request));

    [Fact]
    public void Case_is_ignored_only_on_Windows() =>
        Assert.Equal(OperatingSystem.IsWindows(), MuxCommandMatch.SameExecutable("PWSH.EXE", "pwsh"));
}
```

Append to `MuxTerminalSessionFactoryTests`:

```csharp
    /// <summary>PR #489 follow-up: a saved id whose daemon session runs another program is not reattached.</summary>
    [Fact]
    public void A_restore_whose_command_differs_spawns_fresh_and_leaves_the_session_alone()
    {
        var (mux, factory, _) = Build();
        using (mux) using (factory.Host)
        {
            Guid theirs = Task.Run(async () =>
            {
                MuxClient c = await mux.ConnectClientAsync();
                return await MuxTestHost.SpawnAsync(c);           // Command = "scripted"
            }).GetAwaiter().GetResult();

            PersistentSessionResult r = factory.CreatePersistent(Local(theirs) with { Command = "other-shell" });

            Assert.Equal(PersistentSessionOutcome.Spawned, r.Outcome);
            Assert.NotEqual(theirs, Assert.IsType<MuxClientSession>(r.Session).Id);
            Assert.Contains(theirs, mux.Server.GetSessionIds());
            Assert.False(mux.Fake(theirs).Disposed);
        }
    }
```

- [ ] **Step 2: Run the tests to see them fail**

`scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MuxCommandMatchTests|FullyQualifiedName~MuxTerminalSessionFactoryTests"`

Expected: a compile error (`MuxCommandMatch` is missing). With a stub that returns `true`, the factory test FAILS: the outcome is `Reattached`.

- [ ] **Step 3: Implement**

```csharp
// src/Ntilde.App/Shell/Mux/MuxCommandMatch.cs
namespace Ntilde.Shell.Mux;

/// <summary>
/// Whether a daemon session runs the program a restoring pane expects (PR #489 follow-up). Compared
/// by file name without extension, because the pane re-resolves its command on every launch (a path
/// may differ, the program should not). An empty daemon command (unknown) matches.
/// </summary>
internal static class MuxCommandMatch
{
    public static bool SameExecutable(string? daemonCommand, string? requestCommand)
    {
        if (string.IsNullOrWhiteSpace(daemonCommand) || string.IsNullOrWhiteSpace(requestCommand)) return true;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(ExecutableName(daemonCommand), ExecutableName(requestCommand), comparison);
    }

    private static string ExecutableName(string command)
    {
        // Both separators on every OS: a Windows path must not survive whole on Linux (Path.GetFileName would keep it).
        string file = command.Trim().Trim('"').Replace('\\', '/');
        int slash = file.LastIndexOf('/');
        if (slash >= 0) file = file[(slash + 1)..];
        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
    }
}
```

In `MuxTerminalSessionFactory.CreatePersistent`, inside `if (request.ExistingMuxSessionId is Guid existing)` and right after `match` is computed, add this before the existing checks:

```csharp
                if (match is { Running: true, Faulted: false } && !MuxCommandMatch.SameExecutable(match.Command, request.Command))
                {
                    // Not this pane's shell (a hand-edited or foreign session file): leave it running
                    // untouched and start what the pane asked for. Nothing was lost, so no banner.
                    _log?.Invoke($"[Mux] session {existing} runs '{match.Command}', not '{request.Command}'; starting a new shell instead of reattaching");
                    Guid other = Spawn(client, request);
                    return new(client.OpenSession(other, request.Command, request.Arguments), PersistentSessionOutcome.Spawned, Host.Endpoint, null);
                }
```

- [ ] **Step 4: Run the tests again.** The same filter → PASS. Also run `FullyQualifiedName~MainWindowMuxLifecycleTests|FullyQualifiedName~MuxPaneTests` → PASS: orphans and restores use `ShellHelper.ResolveExecutableOrDefault(s.Command)`, and those have the same file name.

- [ ] **Step 5: Commit** (format first): `fix(mux): a reattach whose daemon command differs starts a fresh shell`, with the trailer.

---
### Task 6: Protocol v2 contracts and the version bump

**Files:**
- Modify: `src/Ntilde.Mux.Contracts/MuxProtocol.cs`
- Create: `src/Ntilde.Mux.Contracts/MuxAttachMode.cs`
- Modify: `src/Ntilde.Mux.Contracts/MuxMessages.cs`
- Modify: `src/Ntilde.Mux.Contracts/MuxJsonContext.cs`
- Test: `tests/Ntilde.Mux.Tests/Contracts/MuxJsonTests.cs`
- Modify (input only): `tests/Ntilde.Mux.Tests/Server/MuxServerHandshakeTests.cs`

**Interfaces:**
- Consumes: none new.
- Produces:
  - `MuxProtocol.MaxSupportedVersion = 2`, `MuxProtocol.SessionEventsVersion = 2`
  - `MuxErrorCodes.SessionAttached = "session_attached"`
  - `MuxMethods.SessionChanged = "sessionChanged"`, `MuxMethods.Killed = "killed"`
  - `public enum MuxAttachMode { Shared, IfUnattached, ReadOnly }`
  - `public static class MuxAttachModes { const string Shared, IfUnattached, ReadOnly; string? ToWire(MuxAttachMode); bool TryParse(string?, out MuxAttachMode) }`
  - `AttachParams.Mode` (`string?`), `SessionSummary.Cwd` (`string?`), `SessionInfoResult.Title` / `.Cwd` (`string?`), `SessionInfoResult.AttachedClients` (`int?`)
  - `public sealed record SessionChangedNotification { Guid SessionId; int AttachedClients; string Title = ""; string? Cwd; }`
  - `public sealed record KilledNotification { Guid SessionId; string ByClientKind = ""; }`

- [ ] **Step 1: Write the failing tests** (append to `MuxJsonTests`)

```csharp
    [Fact]
    public void The_protocol_range_is_1_to_2()
    {
        Assert.Equal(1, MuxProtocol.MinSupportedVersion);
        Assert.Equal(2, MuxProtocol.MaxSupportedVersion);
        Assert.Equal(2, MuxProtocol.SessionEventsVersion);
    }

    [Fact]
    public void A_shared_attach_keeps_the_v1_wire_shape()
    {
        var p = new AttachParams
        {
            SessionId = new Guid("00000000-0000-0000-0000-000000000003"),
            MaxScrollbackRows = 10,
            Presentation = new MuxPresentation { Cols = 80, Rows = 24 },
            Mode = MuxAttachModes.ToWire(MuxAttachMode.Shared),
        };

        string json = System.Text.Json.JsonSerializer.Serialize(p, MuxJsonContext.Default.AttachParams);

        Assert.DoesNotContain("\"mode\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MuxAttachMode.IfUnattached, "ifUnattached")]
    [InlineData(MuxAttachMode.ReadOnly, "readOnly")]
    public void Non_shared_modes_travel_as_strings(MuxAttachMode mode, string wire)
    {
        var p = new AttachParams { SessionId = Guid.NewGuid(), Presentation = new MuxPresentation { Cols = 80, Rows = 24 }, Mode = MuxAttachModes.ToWire(mode) };

        string json = System.Text.Json.JsonSerializer.Serialize(p, MuxJsonContext.Default.AttachParams);
        AttachParams back = System.Text.Json.JsonSerializer.Deserialize(json, MuxJsonContext.Default.AttachParams)!;

        Assert.Contains($"\"mode\":\"{wire}\"", json, StringComparison.Ordinal);
        Assert.True(MuxAttachModes.TryParse(back.Mode, out MuxAttachMode parsed));
        Assert.Equal(mode, parsed);
    }

    [Theory]
    [InlineData(null, true, MuxAttachMode.Shared)]
    [InlineData("shared", true, MuxAttachMode.Shared)]
    [InlineData("ifUnattached", true, MuxAttachMode.IfUnattached)]
    [InlineData("readOnly", true, MuxAttachMode.ReadOnly)]
    [InlineData("ReadOnly", false, MuxAttachMode.Shared)]   // exact, like every other wire string
    [InlineData("bogus", false, MuxAttachMode.Shared)]
    public void Attach_modes_parse_exactly(string? wire, bool ok, MuxAttachMode expected)
    {
        Assert.Equal(ok, MuxAttachModes.TryParse(wire, out MuxAttachMode mode));
        Assert.Equal(expected, mode);
    }

    [Fact]
    public void The_session_changed_notification_round_trips_with_camel_case_params()
    {
        var n = new SessionChangedNotification { SessionId = Guid.NewGuid(), AttachedClients = 3, Title = "vim", Cwd = "/tmp" };
        System.Text.Json.JsonElement e = MuxFrames.ToElement(n, MuxJsonContext.Default.SessionChangedNotification);

        Assert.Contains("\"attachedClients\":3", e.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(n, MuxFrames.ParseParams(e, MuxJsonContext.Default.SessionChangedNotification));
    }

    [Fact]
    public void The_killed_notification_round_trips_with_camel_case_params()
    {
        var n = new KilledNotification { SessionId = Guid.NewGuid(), ByClientKind = "ntilde-cli" };
        System.Text.Json.JsonElement e = MuxFrames.ToElement(n, MuxJsonContext.Default.KilledNotification);

        Assert.Contains("\"byClientKind\":\"ntilde-cli\"", e.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(n, MuxFrames.ParseParams(e, MuxJsonContext.Default.KilledNotification));
    }

    [Fact]
    public void A_v1_summary_and_session_info_parse_with_the_new_fields_absent()
    {
        SessionSummary s = System.Text.Json.JsonSerializer.Deserialize(
            "{\"sessionId\":\"00000000-0000-0000-0000-000000000004\",\"title\":\"t\",\"command\":\"pwsh\",\"cols\":80,\"rows\":24,\"running\":true,\"attachedClients\":1,\"faulted\":false}",
            MuxJsonContext.Default.SessionSummary)!;
        SessionInfoResult i = System.Text.Json.JsonSerializer.Deserialize(
            "{\"running\":true,\"hasActiveChildProcesses\":false}", MuxJsonContext.Default.SessionInfoResult)!;

        Assert.Null(s.Cwd);
        Assert.Null(i.Title);
        Assert.Null(i.Cwd);
        Assert.Null(i.AttachedClients);
    }
```

- [ ] **Step 2: Run them to see them fail.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~MuxJsonTests"` → compile errors: none of the new members exist.

- [ ] **Step 3: Implement the contracts.** In `MuxProtocol.cs`:

```csharp
    public const int MinSupportedVersion = 1;
    public const int MaxSupportedVersion = 2;

    /// <summary>
    /// The first version with attach modes, <c>sessionChanged</c>, <c>killed</c> and
    /// <c>session_attached</c> (Phase 3 spec §2). Every v2 behaviour checks this one constant.
    /// </summary>
    public const int SessionEventsVersion = 2;
```

In `MuxErrorCodes`, add `public const string SessionAttached = "session_attached";`. In `MuxMethods`, add:

```csharp
    /// <summary>Notification (server → v2 client): attached count, title or cwd changed; coalesced (Phase 3 spec §4).</summary>
    public const string SessionChanged = "sessionChanged";

    /// <summary>Notification (server → v2 client): another client killed the session; precedes its <see cref="Exited"/>.</summary>
    public const string Killed = "killed";
```

Create `MuxAttachMode.cs`:

```csharp
namespace Ntilde.Mux.Contracts;

/// <summary>How an attach treats other clients (Phase 3 spec §3).</summary>
public enum MuxAttachMode
{
    /// <summary>Today's behaviour: attach whatever else is attached; this client's size wins.</summary>
    Shared = 0,

    /// <summary>Fail with <see cref="MuxErrorCodes.SessionAttached"/> if another interactive client is attached; decided on the session's parse thread.</summary>
    IfUnattached = 1,

    /// <summary>Receive the stream, but the server drops this client's input and resizes. A convenience, not a security boundary.</summary>
    ReadOnly = 2,
}

/// <summary>The wire strings of <see cref="MuxAttachMode"/>. <see cref="MuxAttachMode.Shared"/> travels as null, so a shared attach is the v1 shape.</summary>
public static class MuxAttachModes
{
    public const string Shared = "shared";
    public const string IfUnattached = "ifUnattached";
    public const string ReadOnly = "readOnly";

    public static string? ToWire(MuxAttachMode mode) => mode switch
    {
        MuxAttachMode.Shared => null,
        MuxAttachMode.IfUnattached => IfUnattached,
        MuxAttachMode.ReadOnly => ReadOnly,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown attach mode."),
    };

    /// <summary>Exact (ordinal) match; null means shared. False for anything else.</summary>
    public static bool TryParse(string? wire, out MuxAttachMode mode)
    {
        switch (wire)
        {
            case null:
            case Shared:
                mode = MuxAttachMode.Shared;
                return true;
            case IfUnattached:
                mode = MuxAttachMode.IfUnattached;
                return true;
            case ReadOnly:
                mode = MuxAttachMode.ReadOnly;
                return true;
            default:
                mode = MuxAttachMode.Shared;
                return false;
        }
    }
}
```

In `MuxMessages.cs`, add the members. Existing ones are unchanged:

```csharp
public sealed record SessionSummary
{
    // ... existing members ...

    /// <summary>The last OSC 7 directory the mux parser saw; null when none (or a v1 daemon).</summary>
    public string? Cwd { get; init; }
}

public sealed record AttachParams
{
    // ... existing members ...

    /// <summary>
    /// <see cref="MuxAttachModes"/> wire string; null (absent) = shared, the v1 shape. A string, not a
    /// JSON enum: an unknown value then gets a request-level error rather than a malformed-params close.
    /// </summary>
    public string? Mode { get; init; }
}

public sealed record SessionInfoResult
{
    // ... existing members ...
    public string? Title { get; init; }
    public string? Cwd { get; init; }
    public int? AttachedClients { get; init; }
}

/// <summary>Params of <see cref="MuxMethods.SessionChanged"/>: the session's facts after the change.</summary>
public sealed record SessionChangedNotification
{
    public Guid SessionId { get; init; }
    public int AttachedClients { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Cwd { get; init; }
}

/// <summary>Params of <see cref="MuxMethods.Killed"/>. <see cref="ByClientKind"/> is the killer's hello <c>clientKind</c>.</summary>
public sealed record KilledNotification
{
    public Guid SessionId { get; init; }
    public string ByClientKind { get; init; } = string.Empty;
}
```

In `MuxJsonContext.cs`, add `[JsonSerializable(typeof(SessionChangedNotification))]` and `[JsonSerializable(typeof(KilledNotification))]`.

- [ ] **Step 4: Fix the one handshake test whose input the bump invalidates.** In `MuxServerHandshakeTests.Disjoint_ranges_are_refused_and_the_connection_closed`, the hello offered `2..3`. That overlaps the new default `1..2`. Change the input only:

```csharp
        raw.Request(MuxMethods.Hello, new HelloParams { MinVersion = 3, MaxVersion = 4 }, MuxJsonContext.Default.HelloParams);
```

The assertion (`version_mismatch`, connection closed) is unchanged. Spec §11 item 10 records this.

- [ ] **Step 5: Run the whole Mux suite.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests` → all PASS. A v2 server and a v2 client now negotiate 2, and no v2 behaviour exists yet. Then run `scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~Mux"` → PASS.

- [ ] **Step 6: Commit** (format first): `feat(mux): protocol v2 contracts (attach modes, sessionChanged, killed, session_attached)`, with the trailer.

---

### Task 7: The server decides attach modes on the parse thread and enforces read-only

**Files:**
- Modify: `src/Ntilde.Mux/IMuxFrameSink.cs`
- Modify: `src/Ntilde.Mux/HeadlessTerminalSession.cs`
- Modify: `src/Ntilde.Mux/MuxServerConnection.cs`
- Test: `tests/Ntilde.Mux.Tests/Server/AttachModeTests.cs`

**Interfaces:**
- Consumes: `MuxAttachModes.TryParse`, `MuxErrorCodes.SessionAttached`, `MuxProtocol.SessionEventsVersion`. In tests: `MuxTestHost.ConnectRaw()`, `RawMuxConnection.HelloAsync(min, max)`, `RawMuxConnection.Request/Send/ReadAsync`, `HeadlessTerminalSession.InvokeAsync`, `QueuedControlCount`, `QueuedInputBytes`, and `ScriptedTerminalSession.SentInput` / `Resizes`.
- Produces:
  - `bool IMuxFrameSink.WantsSessionEvents => false;` (default interface member)
  - `public bool MuxServerConnection.WantsSessionEvents`, `public string MuxServerConnection.ClientKind`
  - `internal void HeadlessTerminalSession.PostAttach(IMuxFrameSink sink, long requestId, int maxScrollbackRows, MuxPresentation presentation, int maxSnapshotBytes, MuxAttachMode mode = MuxAttachMode.Shared)`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Ntilde.Mux.Tests/Server/AttachModeTests.cs
using System.Collections.Concurrent;
using System.Text;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Server;

/// <summary>Phase 3 spec §3: attach modes are decided inside the attach item on the session's parse thread.</summary>
public sealed class AttachModeTests
{
    private static async Task<RawMuxConnection> ConnectV2Async(MuxTestHost host)
    {
        RawMuxConnection raw = host.ConnectRaw();
        WelcomeResult welcome = await raw.HelloAsync(min: 1, max: 2);
        Assert.Equal(2, welcome.Version);
        return raw;
    }

    private static long SendAttach(RawMuxConnection raw, Guid id, string? mode, MuxPresentation? presentation = null) =>
        raw.Request(MuxMethods.Attach, new AttachParams
        {
            SessionId = id,
            MaxScrollbackRows = 0,
            Presentation = presentation ?? MuxTestHost.DefaultPresentation,
            Mode = mode,
        }, MuxJsonContext.Default.AttachParams);

    /// <summary>"snapshot", "ok", or the error code of the reply to <paramref name="requestId"/>; skips stream frames and notifications.</summary>
    private static async Task<string> ReadOutcomeAsync(RawMuxConnection raw, long requestId)
    {
        while (true)
        {
            using MuxInboundFrame frame = await raw.ReadAsync() ?? throw new EndOfStreamException("The server closed the connection.");
            if (frame.Kind == MuxFrameKind.Snapshot)
            {
                Assert.True(MuxFrames.TryParseSnapshot(frame.Payload, out long rid, out _, out _, out _));
                if (rid == requestId) return "snapshot";
            }
            else if (frame.Kind == MuxFrameKind.Response)
            {
                MuxResponse r = MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxResponse);
                if (r.Id == requestId) return r.Error?.Code ?? "ok";
            }
        }
    }

    [Fact]
    public async Task IfUnattached_is_decided_on_the_parse_thread()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        RawMuxConnection a = await ConnectV2Async(host);
        RawMuxConnection b = await ConnectV2Async(host);
        HeadlessTerminalSession mux = host.Mux(id);
        using var gate = new ManualResetEventSlim(false);

        Task<int> parked = mux.InvokeAsync(() => { gate.Wait(TimeSpan.FromSeconds(30)); return 0; });
        await TestWait.UntilAsync(() => mux.QueuedControlCount == 0, "the parse thread took the parking item");
        long ra = SendAttach(a, id, MuxAttachModes.IfUnattached);
        long rb = SendAttach(b, id, MuxAttachModes.IfUnattached);
        await TestWait.UntilAsync(() => mux.QueuedControlCount == 2, "both attaches wait behind the parked parse thread");
        gate.Set();
        await parked;

        string[] outcomes = [await ReadOutcomeAsync(a, ra), await ReadOutcomeAsync(b, rb)];
        Assert.Single(outcomes, o => o == "snapshot");
        Assert.Single(outcomes, o => o == MuxErrorCodes.SessionAttached);
        Assert.Equal(1, mux.AttachedClients);
    }

    [Fact]
    public async Task IfUnattached_races_have_exactly_one_winner()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        for (int i = 0; i < 50; i++)
        {
            Guid id = await MuxTestHost.SpawnAsync(spawner);
            RawMuxConnection a = await ConnectV2Async(host);
            RawMuxConnection b = await ConnectV2Async(host);
            long ra = 0, rb = 0;
            Parallel.Invoke(
                () => ra = SendAttach(a, id, MuxAttachModes.IfUnattached),
                () => rb = SendAttach(b, id, MuxAttachModes.IfUnattached));

            string[] outcomes = [await ReadOutcomeAsync(a, ra), await ReadOutcomeAsync(b, rb)];
            Assert.Single(outcomes, o => o == "snapshot");
            Assert.Single(outcomes, o => o == MuxErrorCodes.SessionAttached);
            a.Dispose();
            b.Dispose();
        }
    }

    [Fact]
    public async Task A_read_only_observer_does_not_block_IfUnattached_and_the_holder_may_reattach()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        RawMuxConnection observer = await ConnectV2Async(host);
        RawMuxConnection gui = await ConnectV2Async(host);
        RawMuxConnection other = await ConnectV2Async(host);

        Assert.Equal("snapshot", await ReadOutcomeAsync(observer, SendAttach(observer, id, MuxAttachModes.ReadOnly)));
        Assert.Equal("snapshot", await ReadOutcomeAsync(gui, SendAttach(gui, id, MuxAttachModes.IfUnattached)));
        Assert.Equal("snapshot", await ReadOutcomeAsync(gui, SendAttach(gui, id, MuxAttachModes.IfUnattached)));  // re-attach by the holder
        Assert.Equal(MuxErrorCodes.SessionAttached, await ReadOutcomeAsync(other, SendAttach(other, id, MuxAttachModes.IfUnattached)));
        Assert.Equal(2, host.Mux(id).AttachedClients);
    }

    [Fact]
    public async Task ReadOnly_attach_does_not_resize_and_its_input_and_resize_are_dropped()
    {
        var log = new ConcurrentQueue<string>();
        using var host = new MuxTestHost(new MuxServerOptions { ForceConPtyFiltering = false, Log = log.Enqueue });
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);                        // 80x24
        RawMuxConnection ro = await ConnectV2Async(host);

        long attach = SendAttach(ro, id, MuxAttachModes.ReadOnly, MuxTestHost.DefaultPresentation with { Cols = 100, Rows = 30 });
        Assert.Equal("snapshot", await ReadOutcomeAsync(ro, attach));
        Assert.Equal((80, 24), (host.Mux(id).Cols, host.Mux(id).Rows));      // a read-only attach does not resize

        ro.Send(MuxFrames.Input(id, Encoding.UTF8.GetBytes("typed")));
        ro.Send(MuxFrames.Input(id, Encoding.UTF8.GetBytes("again")));
        long resize = ro.Request(MuxMethods.Resize, new ResizeParams { SessionId = id, Cols = 90, Rows = 20 }, MuxJsonContext.Default.ResizeParams);
        Assert.Equal("ok", await ReadOutcomeAsync(ro, resize));               // answered, never an error
        await host.Mux(id).InvokeAsync(() => 0);                              // any posted resize has run
        await TestWait.UntilAsync(() => host.Mux(id).QueuedInputBytes == 0, "the input writer is idle");

        Assert.Empty(host.Fake(id).SentInput);
        Assert.Empty(host.Fake(id).Resizes);
        Assert.Equal((80, 24), (host.Mux(id).Cols, host.Mux(id).Rows));
        Assert.Single(log, l => l.Contains("read-only", StringComparison.Ordinal) && l.Contains("input", StringComparison.Ordinal));
        Assert.Single(log, l => l.Contains("read-only", StringComparison.Ordinal) && l.Contains("resize", StringComparison.Ordinal));

        // An interactive attach on the same connection lifts it.
        Assert.Equal("snapshot", await ReadOutcomeAsync(ro, SendAttach(ro, id, null)));
        ro.Send(MuxFrames.Input(id, Encoding.UTF8.GetBytes("now")));
        await TestWait.UntilAsync(() => host.Fake(id).SentInput.Contains("now"), "input flows after an interactive attach");
    }

    [Fact]
    public async Task An_unknown_mode_is_a_request_error_and_the_connection_stays_open()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        RawMuxConnection raw = await ConnectV2Async(host);

        Assert.Equal(MuxErrorCodes.ProtocolError, await ReadOutcomeAsync(raw, SendAttach(raw, id, "bogus")));
        long ping = raw.Request(MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);
        Assert.Equal("ok", await ReadOutcomeAsync(raw, ping));
        Assert.Equal(0, host.Mux(id).AttachedClients);
    }
}
```

- [ ] **Step 2: Run them to see them fail.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~AttachModeTests"`. The server ignores `mode` today, so:
  - both `IfUnattached` tests get two snapshots;
  - the read-only test sees a resize to 100x30 and receives the input;
  - the unknown-mode test gets a snapshot, not `protocol_error`.

- [ ] **Step 3: Implement the sink flag.** In `IMuxFrameSink.cs`:

```csharp
    /// <summary>
    /// True when this peer negotiated protocol v2 or later and so understands <c>sessionChanged</c> and
    /// <c>killed</c> (Phase 3 spec §2). Default false: a v1 peer - and the test recorder - never gets them.
    /// </summary>
    bool WantsSessionEvents => false;
```

- [ ] **Step 4: Implement the mode decision in `HeadlessTerminalSession`.**

Add the field:

```csharp
    private readonly HashSet<IMuxFrameSink> _readOnlySinks = new(); // parse thread only: subscribers attached ReadOnly
```

Replace `PostAttach` so the mode reaches the item:

```csharp
    internal void PostAttach(IMuxFrameSink sink, long requestId, int maxScrollbackRows, MuxPresentation presentation, int maxSnapshotBytes, MuxAttachMode mode = MuxAttachMode.Shared)
    {
        var item = WorkItem.ForAction(
            () => ExecuteAttach(sink, requestId, maxScrollbackRows, presentation, maxSnapshotBytes, mode),
            onDropped: () => ReplySessionExited(sink, requestId));
        if (!TryEnqueue(_control, item)) item.OnDropped!();
    }
```

In `ExecuteAttach`, change the signature to take `MuxAttachMode mode`. After the `IsFaulted` check, insert:

```csharp
        // Exclusivity is decided here, inside the one attach item on the one parse thread: every
        // attach to this session from any connection runs serially on this thread, so nothing can
        // subscribe between this check and the subscription below (Phase 3 spec §3).
        if (mode == MuxAttachMode.IfUnattached && HasOtherInteractiveSubscriber(sink))
        {
            Reply(sink, requestId, MuxErrorCodes.SessionAttached, $"Session {Id} is attached to another client.");
            return;
        }

        bool readOnly = mode == MuxAttachMode.ReadOnly;
```

Wrap the existing `ApplyPresentation` / `ApplyResize` block in `if (!readOnly) { … }`, so a read-only attach neither resizes nor changes the presentation. Its snapshot is at the session's current size.

At the end, replace the subscribe lines:

```csharp
        if (!_subscribers.Contains(sink)) _subscribers.Add(sink);
        if (readOnly) _readOnlySinks.Add(sink);
        else _readOnlySinks.Remove(sink);
        PublishAttachedCount();
        if (IsExited) Offer(sink, ExitedFrame());
```

In the `!accepted` branch, add `_readOnlySinks.Remove(sink);` after `_subscribers.Remove(sink)`.

Add the helper:

```csharp
    /// <summary>Parse thread only. A read-only observer does not count: it must not make a GUI abandon its own shell (spec §3).</summary>
    private bool HasOtherInteractiveSubscriber(IMuxFrameSink sink)
    {
        foreach (IMuxFrameSink s in _subscribers)
        {
            if (!ReferenceEquals(s, sink) && !_readOnlySinks.Contains(s)) return true;
        }

        return false;
    }
```

Keep `_readOnlySinks` in step with `_subscribers` wherever a subscriber leaves:
- `PostDetach`: `if (_subscribers.Remove(sink)) { _readOnlySinks.Remove(sink); PublishAttachedCount(); }`
- `Broadcast`: `IMuxFrameSink dropped = _subscribers[i]; _subscribers.RemoveAt(i); _readOnlySinks.Remove(dropped);`
- `EnterFaulted` and `ProcessExit(terminal)`: `_readOnlySinks.Clear();` next to `_subscribers.Clear();`

- [ ] **Step 5: Implement enforcement in `MuxServerConnection`.**

Add the fields and properties:

```csharp
    private readonly HashSet<Guid> _readOnly = new();                          // reader thread only
    private readonly HashSet<(Guid Session, string What)> _readOnlyDropLogged = new(); // reader thread only
    private string _clientKind = string.Empty;

    public string ClientKind => Volatile.Read(ref _clientKind);

    /// <summary>A v2 peer understands sessionChanged and killed (spec §2); a v1 peer never sees them.</summary>
    public bool WantsSessionEvents => ProtocolVersion >= MuxProtocol.SessionEventsVersion;
```

In `HandleHello`, before `Volatile.Write(ref _version, chosen);`:

```csharp
        Volatile.Write(ref _clientKind, Clip(p.ClientKind ?? string.Empty));
```

In `Dispatch`, in the `MuxFrameKind.Input` case, before `session.SendInput`:

```csharp
                if (_readOnly.Contains(id))
                {
                    NoteReadOnlyDrop(id, "input");
                    break;
                }
```

In `HandleRequest`:

- In the `Resize` case, right after parsing `p`:

  ```csharp
                        if (_readOnly.Contains(p.SessionId))
                        {
                            NoteReadOnlyDrop(p.SessionId, "resize");
                            ReplyEmpty(request);
                            break;
                        }
  ```

- In the `Detach` case, inside the `!superseded` path, add `_readOnly.Remove(p.SessionId);`.
- In the `Kill` case, add `_readOnly.Remove(p.SessionId);`.

In `HandleAttach`, parse the mode first, record it, and pass it on:

```csharp
        AttachParams p = Params(request, MuxJsonContext.Default.AttachParams);
        if (!MuxAttachModes.TryParse(p.Mode, out MuxAttachMode mode))
        {
            throw new MuxRequestException(MuxErrorCodes.ProtocolError, $"Unknown attach mode '{Clip(p.Mode ?? string.Empty)}'.");
        }

        _server.RequireGeometry(p.Presentation.Cols, p.Presentation.Rows);
        HeadlessTerminalSession session = Session(p.SessionId);
        int rows = Math.Clamp(p.MaxScrollbackRows, 0, _server.Options.MaxAttachScrollbackRows);
        _attached.Add(p.SessionId);
        _latestAttach[p.SessionId] = request.Id;

        // A second, independent layer to the session's own (which skips geometry for this sink): this
        // connection's input and resizes for the session are dropped while it is attached read-only.
        // Not a security boundary: any same-user process can open another, interactive connection.
        if (mode == MuxAttachMode.ReadOnly) _readOnly.Add(p.SessionId);
        else _readOnly.Remove(p.SessionId);

        session.PostAttach(this, request.Id, rows, p.Presentation, _server.Options.MaxSnapshotBytes, mode);
```

Add the helper:

```csharp
    /// <summary>Reader thread. Once per (session, kind), so a client typing into a read-only view does not flood the log.</summary>
    private void NoteReadOnlyDrop(Guid sessionId, string what)
    {
        if (_readOnlyDropLogged.Add((sessionId, what)))
        {
            SafeLog($"[MuxServer] connection {ConnectionId} ({ClientKind}) is read-only on session {sessionId}: dropping its {what} (logged once)");
        }
    }
```

- [ ] **Step 6: Run the tests again.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests` → all PASS: the new class, and every existing suite unchanged.

- [ ] **Step 7: Commit** (format first): `feat(mux): attach modes decided on the parse thread; read-only enforced per connection`, with the trailer.

---

### Task 8: Session events: coalesced `sessionChanged`, cwd, and `killed`

**Files:**
- Modify: `src/Ntilde.Mux/HeadlessSessionOptions.cs`, `src/Ntilde.Mux/MuxServerOptions.cs`
- Modify: `src/Ntilde.Mux/HeadlessTerminalSession.cs`
- Modify: `src/Ntilde.Mux/MuxServer.cs`, `src/Ntilde.Mux/MuxServerConnection.cs`
- Modify: `tests/Ntilde.Mux.Tests/Support/RecordingFrameSink.cs`
- Test: `tests/Ntilde.Mux.Tests/Headless/SessionEventsTests.cs`
- Test: `tests/Ntilde.Mux.Tests/Server/MuxServerRequestTests.cs`

**Interfaces:**
- Consumes: `IMuxFrameSink.WantsSessionEvents` (Task 7), `SessionChangedNotification`, `KilledNotification`, `MuxMethods.SessionChanged` / `Killed` (Task 6), and `AnsiParser.OnWorkingDirectoryChanged`.
- Produces:
  - `HeadlessSessionOptions.SessionChangedInterval` and `MuxServerOptions.SessionChangedInterval` (both `TimeSpan`, default 100 ms)
  - `public string? HeadlessTerminalSession.Cwd`
  - `internal void HeadlessTerminalSession.Kill(IMuxFrameSink? by, string? byClientKind)`
  - `internal void MuxServer.Kill(Guid id, IMuxFrameSink? by = null, string? byClientKind = null)`
  - `SessionSummary.Cwd` and `SessionInfoResult.Title` / `.Cwd` / `.AttachedClients`, now filled
  - `RecordingFrameSink.WantsSessionEvents` (settable), `RecordingFrameSink.SessionChanges`, `RecordingFrameSink.SessionChangeTimesMs`

- [ ] **Step 1: Extend the test sink.** In `RecordingFrameSink`, add a timestamp list and the flag. Nothing existing changes, and the flag defaults to false:

```csharp
    private readonly List<long> _times = new();

    /// <summary>Opt in to sessionChanged/killed, like a v2 connection. Default false, so exact-sequence assertions elsewhere are unchanged.</summary>
    public bool WantsSessionEvents { get; set; }

    public bool TryEnqueue(MuxOutboundFrame frame)
    {
        if (!Accept) return false;
        byte[] payload = frame.Bytes[MuxProtocol.FrameHeaderBytes..].ToArray();
        lock (_gate)
        {
            _frames.Add(new RecordedFrame(frame.Kind, payload));
            _times.Add(Environment.TickCount64);
        }

        return true;
    }

    public IReadOnlyList<SessionChangedNotification> SessionChanges =>
        Frames.Where(f => f.IsNotification(MuxMethods.SessionChanged))
              .Select(f => MuxFrames.ParseParams(MuxFrames.ParseJson(f.Payload, MuxJsonContext.Default.MuxNotification).Params, MuxJsonContext.Default.SessionChangedNotification))
              .ToArray();

    public IReadOnlyList<long> SessionChangeTimesMs
    {
        get
        {
            lock (_gate)
            {
                return _frames.Select((f, i) => (f, i)).Where(x => x.f.IsNotification(MuxMethods.SessionChanged)).Select(x => _times[x.i]).ToArray();
            }
        }
    }
```

In `RecordedFrame`, add a helper and two cases to `Describe`, placed before the `ExitedNotification` fallback:

```csharp
    public bool IsNotification(string method) =>
        Kind == MuxFrameKind.Notification && MuxFrames.ParseJson(Payload, MuxJsonContext.Default.MuxNotification).Method == method;

    // in Describe(), case MuxFrameKind.Notification:
                if (n.Method == MuxMethods.SessionChanged)
                {
                    SessionChangedNotification sc = MuxFrames.ParseParams(n.Params, MuxJsonContext.Default.SessionChangedNotification);
                    return string.Create(CultureInfo.InvariantCulture, $"SessionChanged:{sc.AttachedClients}");
                }

                if (n.Method == MuxMethods.Killed)
                {
                    return "Killed:" + MuxFrames.ParseParams(n.Params, MuxJsonContext.Default.KilledNotification).ByClientKind;
                }
```

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/Ntilde.Mux.Tests/Headless/SessionEventsTests.cs
using System.Diagnostics;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;

namespace Ntilde.Mux.Tests.Headless;

/// <summary>Phase 3 spec §4, §5.</summary>
public sealed class SessionEventsTests
{
    private static readonly MuxPresentation P = new() { Cols = 80, Rows = 24, CellWidthPx = 10, CellHeightPx = 20 };

    private static (HeadlessTerminalSession Mux, ScriptedTerminalSession Fake) NewSession(TimeSpan interval)
    {
        var fake = new ScriptedTerminalSession(new TerminalSessionRequest("scripted", string.Empty, string.Empty, 80, 24, null, false, null));
        var mux = new HeadlessTerminalSession(Guid.NewGuid(), fake, new HeadlessSessionOptions
        {
            Command = "scripted",
            Title = "t",
            ForceConPtyFiltering = false,
            SessionChangedInterval = interval,
        });
        return (mux, fake);
    }

    [Fact]
    public async Task An_attach_is_announced_to_v2_sinks_only()
    {
        (HeadlessTerminalSession mux, _) = NewSession(TimeSpan.FromMilliseconds(100));
        using (mux)
        {
            var v2 = new RecordingFrameSink { WantsSessionEvents = true };
            var v1 = new RecordingFrameSink();
            mux.PostAttach(v2, 1, 0, P, MuxProtocol.MaxFrameBytes);
            mux.PostAttach(v1, 2, 0, P, MuxProtocol.MaxFrameBytes);

            await TestWait.UntilAsync(() => v2.SessionChanges.Count > 0 && v2.SessionChanges[^1].AttachedClients == 2, "the v2 sink hears the final count");
            Assert.Equal("t", v2.SessionChanges[^1].Title);
            Assert.DoesNotContain(v1.Described, d => d.StartsWith("SessionChanged", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_burst_is_rate_bounded_and_the_trailing_notification_needs_no_further_activity()
    {
        TimeSpan interval = TimeSpan.FromMilliseconds(200);
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession(interval);
        using (mux)
        {
            var sink = new RecordingFrameSink { WantsSessionEvents = true };
            var sw = Stopwatch.StartNew();
            mux.PostAttach(sink, 1, 0, P, MuxProtocol.MaxFrameBytes);
            for (int i = 0; i < 20; i++) fake.Emit($"\x1b]2;title-{i}\x07");
            await mux.FlushAsync();

            // Nothing else happens from here on: the last title must still arrive, flushed by the
            // parse loop's own bounded wait (no timer, no thread pool).
            await TestWait.UntilAsync(() => sink.SessionChanges.Count > 0 && sink.SessionChanges[^1].Title == "title-19",
                "the trailing notification carries the last title", TimeSpan.FromSeconds(5));
            long elapsedMs = sw.ElapsedMilliseconds;

            int allowed = 1 + (int)Math.Ceiling(elapsedMs / interval.TotalMilliseconds);
            Assert.InRange(sink.SessionChanges.Count, 1, allowed);
            IReadOnlyList<long> times = sink.SessionChangeTimesMs;
            for (int i = 1; i < times.Count; i++)
            {
                // TickCount64 resolution is ~15 ms on Windows: allow that much jitter, never more.
                Assert.True(times[i] - times[i - 1] >= interval.TotalMilliseconds - 20, $"notifications {i - 1} and {i} were {times[i] - times[i - 1]} ms apart");
            }

            int settled = sink.SessionChanges.Count;
            await Task.Delay(interval * 3, TestContext.Current.CancellationToken);
            Assert.Equal(settled, sink.SessionChanges.Count); // no change, no notification
        }
    }

    [Fact]
    public async Task Title_and_cwd_come_from_the_mux_parser()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession(TimeSpan.FromMilliseconds(50));
        using (mux)
        {
            var sink = new RecordingFrameSink { WantsSessionEvents = true };
            mux.PostAttach(sink, 1, 0, P, MuxProtocol.MaxFrameBytes);
            fake.Emit("\x1b]2;build\x07\x1b]7;file://localhost/tmp/work\x07");

            await TestWait.UntilAsync(() => sink.SessionChanges.Count > 0 && sink.SessionChanges[^1].Cwd is not null, "the cwd was announced");
            SessionChangedNotification last = sink.SessionChanges[^1];
            Assert.Equal("build", last.Title);
            Assert.EndsWith("work", last.Cwd, StringComparison.Ordinal);
            Assert.Equal("build", mux.Title);
            Assert.EndsWith("work", mux.Cwd, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Kill_sends_killed_before_exited_to_other_v2_sinks_only()
    {
        (HeadlessTerminalSession mux, _) = NewSession(TimeSpan.FromMilliseconds(50));
        using (mux)
        {
            var killer = new RecordingFrameSink { WantsSessionEvents = true };
            var other = new RecordingFrameSink { WantsSessionEvents = true };
            var v1 = new RecordingFrameSink();
            mux.PostAttach(killer, 1, 0, P, MuxProtocol.MaxFrameBytes);
            mux.PostAttach(other, 2, 0, P, MuxProtocol.MaxFrameBytes);
            mux.PostAttach(v1, 3, 0, P, MuxProtocol.MaxFrameBytes);
            await mux.InvokeAsync(() => 0);

            mux.Kill(killer, "ntilde-cli");

            await TestWait.UntilAsync(() => other.Described.Contains("Exited:-1"), "the other sink saw the exit");
            string[] ending = other.Described.Where(d => d.StartsWith("Killed", StringComparison.Ordinal) || d.StartsWith("Exited", StringComparison.Ordinal)).ToArray();
            Assert.Equal(["Killed:ntilde-cli", "Exited:-1"], ending);
            Assert.DoesNotContain(killer.Described, d => d.StartsWith("Killed", StringComparison.Ordinal));
            Assert.Contains("Exited:-1", killer.Described);
            Assert.DoesNotContain(v1.Described, d => d.StartsWith("Killed", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_natural_exit_sends_no_killed()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession(TimeSpan.FromMilliseconds(50));
        using (mux)
        {
            var sink = new RecordingFrameSink { WantsSessionEvents = true };
            mux.PostAttach(sink, 1, 0, P, MuxProtocol.MaxFrameBytes);
            fake.Exit(3);

            await TestWait.UntilAsync(() => sink.Described.Contains("Exited:3"), "the exit was announced");
            Assert.DoesNotContain(sink.Described, d => d.StartsWith("Killed", StringComparison.Ordinal));
        }
    }
}
```

Append to `MuxServerRequestTests`:

```csharp
    [Fact]
    public async Task List_and_session_info_carry_title_cwd_and_the_attached_count()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        host.Fake(id).Emit("\x1b]2;edit\x07\x1b]7;file://localhost/srv/app\x07");
        await host.SettleAsync(id, client);

        SessionSummary s = Assert.Single(await client.ListSessionsAsync(TestContext.Current.CancellationToken));
        SessionInfoResult info = await pane.Session.RefreshSessionInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal("edit", s.Title);
        Assert.EndsWith("app", s.Cwd, StringComparison.Ordinal);
        Assert.Equal(("edit", 1), (info.Title, info.AttachedClients));
        Assert.EndsWith("app", info.Cwd, StringComparison.Ordinal);
    }
```

- [ ] **Step 3: Run them to see them fail.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~SessionEventsTests|FullyQualifiedName~MuxServerRequestTests"` → a compile error first (`SessionChangedInterval`, `Cwd`, `Kill(sink, kind)`). With empty stubs, every event test times out, and the request test sees a null `Cwd`.

- [ ] **Step 4: Implement the options.** In `HeadlessSessionOptions`:

```csharp
    /// <summary>At most one sessionChanged per this interval; later changes collapse into one trailing notification (spec §4).</summary>
    public TimeSpan SessionChangedInterval { get; init; } = TimeSpan.FromMilliseconds(100);
```

In `MuxServerOptions`, add the same property, and in `MuxServer.Validate` add

```csharp
        if (o.SessionChangedInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), o.SessionChangedInterval, "SessionChangedInterval cannot be negative.");
```

Pass it through in `MuxServer.Spawn`'s `HeadlessSessionOptions` initialiser (`SessionChangedInterval = Options.SessionChangedInterval,`).

- [ ] **Step 5: Implement the session events in `HeadlessTerminalSession`.**

Add the fields:

```csharp
    private string? _cwd;
    private readonly long _sessionChangedIntervalMs;
    private bool _sessionChangePending;                         // parse thread only
    private long _lastSessionChangeSentMs = long.MinValue / 2;  // parse thread only
    private int _lastPublishedAttached;                         // parse thread only
    private IMuxFrameSink? _killedBy;
    private string _killedByKind = string.Empty;
    private int _killRequested;
```

In the constructor, replace the `OnTitleChanged` line and add the cwd and interval:

```csharp
        _sessionChangedIntervalMs = (long)Math.Max(0, options.SessionChangedInterval.TotalMilliseconds);
        _parser.OnTitleChanged = title =>
        {
            Volatile.Write(ref _title, title);
            MarkSessionChanged();
        };
        _parser.OnWorkingDirectoryChanged = cwd =>
        {
            Volatile.Write(ref _cwd, cwd);
            MarkSessionChanged();
        };
```

Add the property: `public string? Cwd => Volatile.Read(ref _cwd);`

Replace `PublishAttachedCount`, and add the change plumbing:

```csharp
    /// <summary>Parse thread only. A changed count is a session change (spec §4).</summary>
    private void PublishAttachedCount()
    {
        int count = _subscribers.Count;
        Volatile.Write(ref _attached, count);
        if (count != _lastPublishedAttached)
        {
            _lastPublishedAttached = count;
            MarkSessionChanged();
        }
    }

    /// <summary>Parse thread only (every caller runs inside an item: attach/detach, the parser's OSC callbacks).</summary>
    private void MarkSessionChanged() => _sessionChangePending = true;

    /// <summary>Milliseconds until a pending change may be sent; 0 = now.</summary>
    private int SessionChangeWaitMs()
    {
        long due = _lastSessionChangeSentMs + _sessionChangedIntervalMs - Environment.TickCount64;
        return due <= 0 ? 0 : (int)Math.Min(due, int.MaxValue);
    }

    /// <summary>Parse thread only. Sends the session's current facts to every v2 subscriber.</summary>
    private void FlushSessionChanged()
    {
        _sessionChangePending = false;
        _lastSessionChangeSentMs = Environment.TickCount64;
        if (IsFaulted || !_subscribers.Exists(static s => s.WantsSessionEvents)) return;

        MuxOutboundFrame frame;
        try
        {
            frame = MuxFrames.Notification(new MuxNotification
            {
                Method = MuxMethods.SessionChanged,
                Params = MuxFrames.ToElement(new SessionChangedNotification
                {
                    SessionId = Id,
                    AttachedClients = _subscribers.Count,
                    Title = Title,
                    Cwd = Cwd,
                }, MuxJsonContext.Default.SessionChangedNotification),
            });
        }
        catch (Exception ex)
        {
            Log($"[Mux] session {Id}: building sessionChanged failed: {ex.Message}");
            return;
        }

        BroadcastToSessionEventSinks(frame, except: null);
    }

    /// <summary>Parse thread only. Like <see cref="Broadcast"/>, but only to v2 sinks, optionally skipping one.</summary>
    private void BroadcastToSessionEventSinks(MuxOutboundFrame frame, IMuxFrameSink? except)
    {
        try
        {
            for (int i = _subscribers.Count - 1; i >= 0; i--)
            {
                IMuxFrameSink sink = _subscribers[i];
                if (ReferenceEquals(sink, except) || !sink.WantsSessionEvents) continue;
                if (!sink.TryEnqueue(frame))
                {
                    _subscribers.RemoveAt(i);
                    _readOnlySinks.Remove(sink);
                    PublishAttachedCount();
                }
            }
        }
        finally
        {
            frame.Release();
        }
    }
```

Replace the body of `ParseLoop`'s `while (true)`:

```csharp
            while (true)
            {
                WorkItem item;
                if (_sessionChangePending)
                {
                    // The delayed flush: while a change is pending, wait at most until it is due.
                    // An idle session wakes once, at the deadline - no timer, no thread pool (spec §4).
                    int wait = SessionChangeWaitMs();
                    if (wait == 0 || BlockingCollection<WorkItem>.TryTakeFromAny(_queues, out item, wait, _cts.Token) < 0)
                    {
                        FlushSessionChanged();
                        continue;
                    }
                }
                else
                {
                    BlockingCollection<WorkItem>.TakeFromAny(_queues, out item, _cts.Token);
                }

                Execute(item);
                if (item.Kind == WorkKind.Exit && item.Terminal) break;
                if (_sessionChangePending && SessionChangeWaitMs() == 0) FlushSessionChanged();
            }
```

`TryTakeFromAny` is only evaluated when `wait > 0`, because of `||` short-circuiting, so `item` is assigned whenever `Execute` runs.

Replace `Kill`:

```csharp
    /// <summary>Ends the session (the shutdown path; no client is named, so no <c>killed</c> is sent).</summary>
    public void Kill() => Kill(by: null, byClientKind: null);

    /// <summary>
    /// A client killed it: other v2 subscribers get <c>killed</c> before <c>exited</c> (spec §5). The
    /// killer is recorded BEFORE the child is disposed, because the child's own OnExit may be the
    /// exit item that reaches the parse thread first.
    /// </summary>
    internal void Kill(IMuxFrameSink? by, string? byClientKind)
    {
        if (byClientKind is not null)
        {
            Volatile.Write(ref _killedBy, by);
            Volatile.Write(ref _killedByKind, byClientKind);
            Volatile.Write(ref _killRequested, 1);
        }

        try { _session.Dispose(); }
        catch (Exception ex) { Log($"[Mux] session {Id}: disposing the child failed: {ex.Message}"); }
        TryEnqueue(_data, WorkItem.ForExit(null, terminal: true));
    }
```

In `ProcessExit`, replace `if (_subscribers.Count > 0) Broadcast(ExitedFrame());` with:

```csharp
            if (_subscribers.Count > 0)
            {
                if (Volatile.Read(ref _killRequested) != 0) SendKilled();
                Broadcast(ExitedFrame());
            }
```

Add `SendKilled`:

```csharp
    private void SendKilled()
    {
        MuxOutboundFrame frame;
        try
        {
            frame = MuxFrames.Notification(new MuxNotification
            {
                Method = MuxMethods.Killed,
                Params = MuxFrames.ToElement(new KilledNotification { SessionId = Id, ByClientKind = Volatile.Read(ref _killedByKind) }, MuxJsonContext.Default.KilledNotification),
            });
        }
        catch (Exception ex)
        {
            Log($"[Mux] session {Id}: building killed failed: {ex.Message}");
            return;
        }

        BroadcastToSessionEventSinks(frame, except: Volatile.Read(ref _killedBy));
    }
```

- [ ] **Step 6: Wire the server.**
- In `MuxServer`, change `internal void Kill(Guid id)` to `internal void Kill(Guid id, IMuxFrameSink? by = null, string? byClientKind = null)`, and call `session.Kill(by, byClientKind);`.
- In `MuxServer.ListSessions`, add `Cwd = s.Cwd,`.
- In `MuxServerConnection`'s `Kill` case, call `_server.Kill(p.SessionId, this, ClientKind);`.
- In its `SessionInfo` case, add `Title = s.Title, Cwd = s.Cwd, AttachedClients = s.AttachedClients,` to the result.

- [ ] **Step 7: Run the tests again.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests` → all PASS. In particular, every exact-sequence assertion in `HeadlessTerminalSessionTests` is unchanged: its sinks do not opt in.

- [ ] **Step 8: Check the Review Focus constraint by reading the diff.** `Grep "Timer|Task.Delay|ThreadPool|Task.Run" src/Ntilde.Mux/HeadlessTerminalSession.cs` → no matches.

- [ ] **Step 9: Commit** (format first): `feat(mux): coalesced sessionChanged driven by the parse loop, cwd tracking, killed before exited`, with the trailer.

---

### Task 9: Client v2 surfaces and the v1/v2 fallbacks

**Files:**
- Modify: `src/Ntilde.Mux/MuxClient.cs`
- Modify: `src/Ntilde.Mux/MuxClientSession.cs`
- Test: `tests/Ntilde.Mux.Tests/Client/ProtocolFallbackTests.cs`
- Test: `tests/Ntilde.Mux.Tests/Client/SessionEventsClientTests.cs`

**Interfaces:**
- Consumes: Tasks 6–8, `MuxTestHost.AttachPaneAsync`, `SettleAsync`, and `ClientPaneModel`.
- Produces:
  - `public MuxClientSession MuxClient.OpenSession(Guid sessionId, string shellCommand = "", string? shellArguments = null, MuxAttachMode attachMode = MuxAttachMode.Shared)`
  - `MuxClientSession`:
    - `AttachMode` (`MuxAttachMode`)
    - `Task<long> AttachAsync(MuxAttachMode mode, int maxScrollbackRows, MuxPresentation presentation, CancellationToken cancellationToken = default)`; the existing `AttachAsync(int, MuxPresentation, CancellationToken)` now uses `AttachMode`
    - `bool SupportsSessionEvents`, `int? AttachedClients`, `string? Title`, `string? Cwd`, `bool WasKilledElsewhere`
    - `event Action? SessionChanged`, `event Action<string>? KilledElsewhere` (both on the delivery thread)
    - `Task<int?> RefreshSharingAsync(CancellationToken cancellationToken = default)`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Ntilde.Mux.Tests/Client/ProtocolFallbackTests.cs
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Client;

/// <summary>Phase 3 spec §2.1: the fallback matrix.</summary>
public sealed class ProtocolFallbackTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static MuxServerOptions V1Server => new() { MaxProtocolVersion = 1, ForceConPtyFiltering = false };

    [Fact]
    public async Task A_v2_client_refuses_a_non_shared_mode_on_a_v1_server_before_sending()
    {
        using var host = new MuxTestHost(V1Server);
        MuxClient client = await host.ConnectClientAsync();
        Assert.Equal(1, client.ProtocolVersion);
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession session = client.OpenSession(id, "scripted", null, MuxAttachMode.IfUnattached);

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => session.AttachAsync(0, MuxTestHost.DefaultPresentation, Ct));

        Assert.Equal(MuxErrorCodes.VersionMismatch, ex.Code);
        await client.PingAsync(Ct);
        await host.Mux(id).InvokeAsync(() => 0);
        Assert.Equal(0, host.Mux(id).AttachedClients);   // nothing reached the daemon
        Assert.False(session.IsAttached);
    }

    [Fact]
    public async Task A_v2_client_on_a_v1_server_shares_and_gets_no_session_events()
    {
        using var host = new MuxTestHost(V1Server);
        MuxClient c1 = await host.ConnectClientAsync();
        MuxClient c2 = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c1);
        ClientPaneModel p1 = await MuxTestHost.AttachPaneAsync(c1, id);
        int changes = 0;
        p1.Session.SessionChanged += () => Interlocked.Increment(ref changes);
        await MuxTestHost.AttachPaneAsync(c2, id);
        await host.SettleAsync(id, c1, c2);

        Assert.False(p1.Session.SupportsSessionEvents);
        Assert.Null(p1.Session.AttachedClients);
        Assert.Equal(0, Volatile.Read(ref changes));
        Assert.Equal(2, await p1.Session.RefreshSharingAsync(Ct));   // listSessions has the count in v1 too

        await c2.KillAsync(id, Ct);
        await TestWait.UntilAsync(() => !p1.Session.IsProcessRunning, "c1 saw the exit");
        Assert.False(p1.Session.WasKilledElsewhere);                  // v1: a plain exit
    }

    [Fact]
    public async Task A_v1_client_on_a_v2_server_is_sent_no_new_notifications()
    {
        using var host = new MuxTestHost();
        MuxClient v2 = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(v2);
        RawMuxConnection v1 = host.ConnectRaw();
        Assert.Equal(1, (await v1.HelloAsync(min: 1, max: 1)).Version);
        long attach = v1.Request(MuxMethods.Attach, new AttachParams { SessionId = id, Presentation = MuxTestHost.DefaultPresentation }, MuxJsonContext.Default.AttachParams);

        ClientPaneModel neu = await MuxTestHost.AttachPaneAsync(v2, id);
        await TestWait.UntilAsync(() => neu.Session.AttachedClients == 2, "the v2 client heard the count, so the flush happened");
        long ping = v1.Request(MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);

        // Every frame the v1 connection got up to its pong, in order: none may be a notification.
        bool pong = false;
        while (!pong)
        {
            using MuxInboundFrame frame = await v1.ReadAsync() ?? throw new EndOfStreamException();
            Assert.NotEqual(MuxFrameKind.Notification, frame.Kind);
            if (frame.Kind == MuxFrameKind.Response)
            {
                pong = MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxResponse).Id == ping;
            }
        }

        Assert.True(attach > 0);
    }
}
```

```csharp
// tests/Ntilde.Mux.Tests/Client/SessionEventsClientTests.cs
using System.Collections.Concurrent;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Client;

public sealed class SessionEventsClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SessionChanged_reaches_every_attached_v2_client()
    {
        using var host = new MuxTestHost();
        MuxClient c1 = await host.ConnectClientAsync();
        MuxClient c2 = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c1);
        ClientPaneModel p1 = await MuxTestHost.AttachPaneAsync(c1, id);
        ClientPaneModel p2 = await MuxTestHost.AttachPaneAsync(c2, id);

        await TestWait.UntilAsync(() => p1.Session.AttachedClients == 2 && p2.Session.AttachedClients == 2, "both hear the count");
        Assert.True(p1.Session.SupportsSessionEvents);

        p2.Session.Dispose();
        await TestWait.UntilAsync(() => p1.Session.AttachedClients == 1, "the detach is announced to the one left");
    }

    [Fact]
    public async Task KilledElsewhere_precedes_OnExit_and_the_killer_is_not_told()
    {
        using var host = new MuxTestHost();
        MuxClient c1 = await host.ConnectClientAsync();
        MuxClient killer = await host.ConnectClientAsync(new MuxClientOptions { ClientKind = "killer-kind" });
        Guid id = await MuxTestHost.SpawnAsync(c1);
        ClientPaneModel p1 = await MuxTestHost.AttachPaneAsync(c1, id);
        ClientPaneModel pk = await MuxTestHost.AttachPaneAsync(killer, id);
        var events = new ConcurrentQueue<string>();
        p1.Session.KilledElsewhere += kind => events.Enqueue("killed:" + kind);
        p1.Session.OnExit += code => events.Enqueue("exit:" + code);
        int killerTold = 0;
        pk.Session.KilledElsewhere += _ => Interlocked.Increment(ref killerTold);

        await killer.KillAsync(id, Ct);

        await TestWait.UntilAsync(() => events.Count == 2, "killed and exit arrived");
        Assert.Equal(["killed:killer-kind", "exit:-1"], events.ToArray());
        Assert.True(p1.Session.WasKilledElsewhere);
        Assert.Equal(0, Volatile.Read(ref killerTold));
    }

    [Fact]
    public async Task IfUnattached_races_between_clients_have_exactly_one_winner()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        MuxClient a = await host.ConnectClientAsync();
        MuxClient b = await host.ConnectClientAsync();
        for (int i = 0; i < 30; i++)
        {
            Guid id = await MuxTestHost.SpawnAsync(spawner);
            MuxClientSession sa = a.OpenSession(id, "scripted", null, MuxAttachMode.IfUnattached);
            MuxClientSession sb = b.OpenSession(id, "scripted", null, MuxAttachMode.IfUnattached);

            async Task<string> Attach(MuxClientSession s)
            {
                try
                {
                    await s.AttachAsync(0, MuxTestHost.DefaultPresentation, Ct);
                    return "attached";
                }
                catch (MuxProtocolException ex)
                {
                    return ex.Code;
                }
            }

            string[] outcomes = await Task.WhenAll(Attach(sa), Attach(sb));
            Assert.Single(outcomes, o => o == "attached");
            Assert.Single(outcomes, o => o == MuxErrorCodes.SessionAttached);
            sa.Dispose();
            sb.Dispose();
        }
    }

    [Fact]
    public async Task A_read_only_session_attaches_without_resizing()
    {
        using var host = new MuxTestHost();
        MuxClient c = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c);                  // 80x24
        MuxClientSession ro = c.OpenSession(id, "scripted", null, MuxAttachMode.ReadOnly);
        var model = new ClientPaneModel(ro);

        await ro.AttachAsync(0, MuxTestHost.DefaultPresentation with { Cols = 120, Rows = 40 }, Ct);

        Assert.Equal(MuxAttachMode.ReadOnly, ro.AttachMode);
        Assert.Equal((80, 24), (host.Mux(id).Cols, host.Mux(id).Rows));
        Assert.Equal((80, 24), (model.Buffer.Cols, model.Buffer.Rows));
    }
}
```

- [ ] **Step 2: Run them to see them fail.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~ProtocolFallbackTests|FullyQualifiedName~SessionEventsClientTests"` → a compile error (the new members are missing).

- [ ] **Step 3: Implement `MuxClient`.**

Change `OpenSession`:

```csharp
    public MuxClientSession OpenSession(Guid sessionId, string shellCommand = "", string? shellArguments = null, MuxAttachMode attachMode = MuxAttachMode.Shared)
    {
        var session = new MuxClientSession(this, sessionId, shellCommand, shellArguments, attachMode);
        if (!_sessions.TryAdd(sessionId, session))
        {
            throw new InvalidOperationException($"Session {sessionId} is already open on this client; dispose it first.");
        }

        return session;
    }
```

Change the internal `AttachAsync` signature to take the mode. It refuses early on v1, before anything is registered or sent, and puts the mode on the wire:

```csharp
    internal async Task<long> AttachAsync(MuxClientSession session, MuxAttachMode mode, int maxScrollbackRows, MuxPresentation presentation, CancellationToken cancellationToken)
    {
        // A v1 daemon ignores Mode and would silently share (spec §2.1): refuse here, before sending.
        if (mode != MuxAttachMode.Shared && ProtocolVersion < MuxProtocol.SessionEventsVersion)
        {
            throw new MuxProtocolException(MuxErrorCodes.VersionMismatch,
                $"Attach mode {mode} needs multiplexer protocol {MuxProtocol.SessionEventsVersion}; the running multiplexer speaks {ProtocolVersion}.");
        }

        long id = Interlocked.Increment(ref _nextId);
        JsonElement p = MuxFrames.ToElement(
            new AttachParams { SessionId = session.Id, MaxScrollbackRows = maxScrollbackRows, Presentation = presentation, Mode = MuxAttachModes.ToWire(mode) },
            MuxJsonContext.Default.AttachParams);
        // ... the rest of the method is unchanged ...
```

In `OnNotification`, add two branches before the "Unknown notifications are ignored" comment:

```csharp
        else if (notification.Method == MuxMethods.SessionChanged)
        {
            SessionChangedNotification changed = MuxFrames.ParseParams(notification.Params, MuxJsonContext.Default.SessionChangedNotification);
            if (_sessions.TryGetValue(changed.SessionId, out MuxClientSession? session)) session.DeliverSessionChanged(changed);
        }
        else if (notification.Method == MuxMethods.Killed)
        {
            KilledNotification killed = MuxFrames.ParseParams(notification.Params, MuxJsonContext.Default.KilledNotification);
            if (_sessions.TryGetValue(killed.SessionId, out MuxClientSession? session)) session.DeliverKilled(killed.ByClientKind);
        }
```

- [ ] **Step 4: Implement `MuxClientSession`.**

Change the constructor and add the state:

```csharp
    private int _attachedClients = -1; // -1 = unknown (v1, or nothing heard yet)
    private string? _title;
    private string? _cwd;
    private int _killedElsewhere;

    internal MuxClientSession(MuxClient client, Guid sessionId, string shellCommand, string? shellArguments, MuxAttachMode attachMode = MuxAttachMode.Shared)
    {
        _client = client;
        Id = sessionId;
        ShellCommand = shellCommand;
        ShellArguments = shellArguments;
        AttachMode = attachMode;
    }

    /// <summary>The mode <see cref="AttachAsync(int, MuxPresentation, CancellationToken)"/> uses (chosen by whoever opened the session).</summary>
    public MuxAttachMode AttachMode { get; }

    /// <summary>True when the connection negotiated v2: <see cref="SessionChanged"/> and <see cref="KilledElsewhere"/> can fire.</summary>
    public bool SupportsSessionEvents => _client.ProtocolVersion >= MuxProtocol.SessionEventsVersion;

    /// <summary>Clients attached to the session (this one included), from the last sessionChanged or <see cref="RefreshSharingAsync"/>; null = unknown.</summary>
    public int? AttachedClients { get { int n = Volatile.Read(ref _attachedClients); return n < 0 ? null : n; } }

    public string? Title => Volatile.Read(ref _title);
    public string? Cwd => Volatile.Read(ref _cwd);

    /// <summary>Set, before <see cref="OnExit"/> fires, when another client killed the session (v2).</summary>
    public bool WasKilledElsewhere => Volatile.Read(ref _killedElsewhere) != 0;

    /// <summary>Delivery thread: <see cref="AttachedClients"/>, <see cref="Title"/> or <see cref="Cwd"/> changed.</summary>
    public event Action? SessionChanged;

    /// <summary>Delivery thread, before <see cref="OnExit"/>: another client killed the session; the argument is its client kind.</summary>
    public event Action<string>? KilledElsewhere;
```

Replace `AttachAsync` with the mode-aware pair:

```csharp
    /// <summary>Attaches with <see cref="AttachMode"/>. Returns the snapshot's <c>StreamSeq</c>; <see cref="SnapshotReceived"/> has fired by then.</summary>
    public Task<long> AttachAsync(int maxScrollbackRows, MuxPresentation presentation, CancellationToken cancellationToken = default) =>
        AttachAsync(AttachMode, maxScrollbackRows, presentation, cancellationToken);

    /// <summary>
    /// Attaches in <paramref name="mode"/>. <see cref="MuxAttachMode.IfUnattached"/> throws
    /// <see cref="MuxProtocolException"/> with <see cref="MuxErrorCodes.SessionAttached"/> when another
    /// interactive client holds the session. Any non-shared mode against a v1 daemon throws
    /// <see cref="MuxErrorCodes.VersionMismatch"/> without sending anything.
    /// </summary>
    public Task<long> AttachAsync(MuxAttachMode mode, int maxScrollbackRows, MuxPresentation presentation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _presentation = presentation;
        return _client.AttachAsync(this, mode, maxScrollbackRows, presentation, cancellationToken);
    }
```

Add the refresh and the deliveries:

```csharp
    /// <summary>
    /// Reads the attached count (and title, cwd) from <c>listSessions</c>, which a v1 daemon answers too:
    /// the close confirmation's source of truth. Returns null when the session is no longer listed.
    /// </summary>
    public async Task<int?> RefreshSharingAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SessionSummary> sessions = await _client.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        SessionSummary? me = sessions.FirstOrDefault(s => s.SessionId == Id);
        if (me is null) return null;
        Volatile.Write(ref _attachedClients, me.AttachedClients);
        Volatile.Write(ref _title, me.Title);
        Volatile.Write(ref _cwd, me.Cwd);
        return me.AttachedClients;
    }

    internal void DeliverSessionChanged(SessionChangedNotification changed)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Volatile.Write(ref _attachedClients, Math.Max(0, changed.AttachedClients));
        Volatile.Write(ref _title, changed.Title);
        Volatile.Write(ref _cwd, changed.Cwd);
        SessionChanged?.Invoke();
    }

    internal void DeliverKilled(string byClientKind)
    {
        if (Interlocked.Exchange(ref _killedElsewhere, 1) == 0) KilledElsewhere?.Invoke(byClientKind);
    }
```

Extend the class `<remarks>` list of delivery-thread events with `SessionChanged` and `KilledElsewhere`.

- [ ] **Step 5: Run the tests again.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests` → all PASS. `scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~Mux"` → PASS. App uses `OpenSession(id, cmd, args)`, whose trailing optional parameter defaults to `Shared`.

- [ ] **Step 6: Commit** (format first): `feat(mux): client attach modes, SessionChanged/KilledElsewhere and v1 fallbacks`, with the trailer.

---

### Task 10: Factory and restore: `AttachShared`, exclusive restore, `AttachedElsewhere`

**Files:**
- Modify: `src/Ntilde.Pty/ITerminalSessionFactory.cs` (`TerminalSessionRequest.AttachShared`)
- Modify: `src/Ntilde.App/Shell/Mux/PersistentSessionFactory.cs` (`AttachedElsewhere`)
- Modify: `src/Ntilde.App/Shell/Mux/MuxTerminalSessionFactory.cs`
- Modify: `src/Ntilde.App/Controls/TerminalPane.axaml.cs`
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxTerminalSessionFactoryTests.cs`
- Test: `tests/Ntilde.App.Tests/Controls/MuxPaneRestoreTests.cs`

**Interfaces:**
- Consumes: `MuxClient.OpenSession(…, MuxAttachMode)`, `MuxClient.ProtocolVersion`, `MuxProtocol.SessionEventsVersion`, `MuxErrorCodes.SessionAttached` (Tasks 6 and 9), and `MuxCommandMatch` (Task 5). In tests: `MuxTestHost`, `ClientPaneModel`, `RecordingSessionFactory`, `FakeTerminalSession`, and `PaneSpawnTestHelpers.DisableShellIntegration`.
- Produces:
  - `TerminalSessionRequest(…, Guid? ExistingMuxSessionId = null, bool AttachShared = false)`
  - `PersistentSessionOutcome.AttachedElsewhere`
  - `internal bool TerminalPane.MuxAttachSharedToRestore { get; set; }`
  - `internal const string TerminalPane.MuxAttachedElsewhereNoticeTitle = "Previous shell in use"`
  - `internal const string TerminalPane.MuxAttachedElsewhereBanner = "[Your previous shell is open in another window — started a new shell]"`

- [ ] **Step 1: Write the failing factory tests.** Make `Build` accept server options (existing callers are unchanged):

```csharp
    private static (MuxTestHost Mux, MuxTerminalSessionFactory Factory, RecordingSessionFactory Fallback) Build(MuxServerOptions? serverOptions = null)
    {
        var mux = new MuxTestHost(serverOptions);
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), "test-endpoint", null);
        var fallback = new RecordingSessionFactory(new FakeTerminalSession());
        return (mux, new MuxTerminalSessionFactory(host, fallback, null), fallback);
    }

    private static ClientPaneModel AttachOtherClient(MuxTestHost mux) => Task.Run(async () =>
    {
        MuxClient c = await mux.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c);
        return await MuxTestHost.AttachPaneAsync(c, id);
    }).GetAwaiter().GetResult();
```

Change the existing `A_session_attached_by_another_client_is_not_taken_over`. It now pins the **v1** path, and the outcome is the new `AttachedElsewhere` (spec §7.1, §11 item 10):

```csharp
    /// <summary>
    /// Against a v1 daemon the factory keeps Phase 2's client-side check: a session another client is
    /// attached to stays with it, and this instance gets a fresh shell - now reported as AttachedElsewhere.
    /// </summary>
    [Fact]
    public void A_session_attached_by_another_client_is_not_taken_over()
    {
        var (mux, factory, _) = Build(new MuxServerOptions { MaxProtocolVersion = 1, ForceConPtyFiltering = false });
        using (mux) using (factory.Host)
        {
            ClientPaneModel other = AttachOtherClient(mux);
            Guid theirs = other.Session.Id;

            PersistentSessionResult r = factory.CreatePersistent(Local(theirs));

            Assert.Equal(PersistentSessionOutcome.AttachedElsewhere, r.Outcome);
            var mine = Assert.IsType<MuxClientSession>(r.Session);
            Assert.NotEqual(theirs, mine.Id);
            Assert.Contains(theirs, mux.Server.GetSessionIds());
            Assert.Contains(mine.Id, mux.Server.GetSessionIds());
            Assert.True(other.Session.IsAttached, "the other client's session is untouched");
            Assert.False(mux.Fake(theirs).Disposed);
        }
    }
```

Add the new tests:

```csharp
    [Fact]
    public void On_v2_a_restore_opens_IfUnattached_and_the_attach_itself_refuses()
    {
        var (mux, factory, _) = Build();
        using (mux) using (factory.Host)
        {
            ClientPaneModel other = AttachOtherClient(mux);
            Guid theirs = other.Session.Id;

            PersistentSessionResult r = factory.CreatePersistent(Local(theirs));

            Assert.Equal(PersistentSessionOutcome.Reattached, r.Outcome);   // no client-side count check on v2
            var mine = Assert.IsType<MuxClientSession>(r.Session);
            Assert.Equal(theirs, mine.Id);
            Assert.Equal(MuxAttachMode.IfUnattached, mine.AttachMode);
            var ex = Assert.Throws<MuxProtocolException>(() =>
                Task.Run(() => mine.AttachAsync(0, MuxTestHost.DefaultPresentation)).GetAwaiter().GetResult());
            Assert.Equal(MuxErrorCodes.SessionAttached, ex.Code);
            Assert.True(other.Session.IsAttached);
        }
    }

    [Fact]
    public void AttachShared_joins_a_session_another_client_holds()
    {
        var (mux, factory, _) = Build();
        using (mux) using (factory.Host)
        {
            ClientPaneModel other = AttachOtherClient(mux);
            Guid theirs = other.Session.Id;

            PersistentSessionResult r = factory.CreatePersistent(Local(theirs) with { AttachShared = true });

            Assert.Equal(PersistentSessionOutcome.Reattached, r.Outcome);
            var mine = Assert.IsType<MuxClientSession>(r.Session);
            Assert.Equal((theirs, MuxAttachMode.Shared), (mine.Id, mine.AttachMode));
            Task.Run(() => mine.AttachAsync(0, MuxTestHost.DefaultPresentation)).GetAwaiter().GetResult();
            TestWait.UntilAsync(() => mux.Mux(theirs).AttachedClients == 2, "both attached").GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void AttachShared_to_an_exited_session_still_attaches()
    {
        var (mux, factory, _) = Build();
        using (mux) using (factory.Host)
        {
            ClientPaneModel other = AttachOtherClient(mux);   // keeps the exited session from being reaped
            Guid theirs = other.Session.Id;
            mux.Fake(theirs).Exit(4);
            TestWait.UntilAsync(() => mux.Mux(theirs).IsExited, "the mux saw the exit").GetAwaiter().GetResult();

            PersistentSessionResult r = factory.CreatePersistent(Local(theirs) with { AttachShared = true });

            Assert.Equal(PersistentSessionOutcome.Reattached, r.Outcome);
            Assert.Equal(theirs, Assert.IsType<MuxClientSession>(r.Session).Id);
        }
    }

    [Fact]
    public void AttachShared_to_a_missing_session_spawns_fresh_and_says_the_previous_one_is_lost()
    {
        var (mux, factory, _) = Build();
        using (mux) using (factory.Host)
        {
            PersistentSessionResult r = factory.CreatePersistent(Local(Guid.NewGuid()) with { AttachShared = true });

            Assert.Equal(PersistentSessionOutcome.PreviousLost, r.Outcome);
            Assert.Contains(Assert.IsType<MuxClientSession>(r.Session).Id, mux.Server.GetSessionIds());
        }
    }
```

- [ ] **Step 2: Write the failing pane tests**

```csharp
// tests/Ntilde.App.Tests/Controls/MuxPaneRestoreTests.cs
using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Controls;

/// <summary>Phase 3 spec §7.1, §7.2: restore is exclusive by protocol; a deliberate share joins.</summary>
public sealed class MuxPaneRestoreTests : IDisposable
{
    private readonly MuxTestHost _mux = new();
    private readonly MuxConnectionHost _host;
    private readonly MuxTerminalSessionFactory _factory;
    private TerminalPane? _pane;
    private Avalonia.Controls.Window? _window;

    public MuxPaneRestoreTests()
    {
        _host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct), "test", null);
        _factory = new MuxTerminalSessionFactory(_host, new RecordingSessionFactory(new FakeTerminalSession()), null);
    }

    public void Dispose()
    {
        if (_window is not null)
        {
            _window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        _pane?.Dispose();
        _host.Dispose();
        _mux.Dispose();
    }

    private static void PumpUntil(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"Timed out waiting until {because}.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    private ClientPaneModel AttachOtherClient() => Task.Run(async () =>
    {
        MuxClient c = await _mux.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c);
        return await MuxTestHost.AttachPaneAsync(c, id);
    }).GetAwaiter().GetResult();

    /// <summary>Hosts a pane that reopens <paramref name="id"/>: an unhosted TermView has a 0x0 grid and would not spawn.</summary>
    private void ShowRestoringPane(Guid id, bool shared, List<(string Title, string Message)> notices)
    {
        _pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(_pane);
        _pane.SessionFactory = _factory;
        _pane.MuxSessionIdToRestore = id;
        _pane.MuxAttachSharedToRestore = shared;
        _pane.PersistenceNotice += (_, title, message) => notices.Add((title, message));
        _window = new Avalonia.Controls.Window { Content = _pane, Width = 900, Height = 500 };
        _window.Show();
    }

    [AvaloniaFact]
    public void A_restore_that_loses_the_attach_race_starts_a_fresh_shell_and_says_so()
    {
        ClientPaneModel other = AttachOtherClient();
        Guid theirs = other.Session.Id;
        var notices = new List<(string Title, string Message)>();

        ShowRestoringPane(theirs, shared: false, notices);

        PumpUntil(() => _pane!.Session is MuxClientSession { IsAttached: true } m && m.Id != theirs, "the pane attached a fresh shell");
        PumpUntil(() => notices.Count > 0, "the notice was raised");
        Assert.Equal((TerminalPane.MuxAttachedElsewhereNoticeTitle, TerminalPane.MuxAttachedElsewhereBanner), Assert.Single(notices));
        Assert.True(other.Session.IsAttached);
        Assert.Equal(1, _mux.Mux(theirs).AttachedClients);
    }

    [AvaloniaFact]
    public void A_pane_asked_to_share_attaches_alongside_the_other_client()
    {
        ClientPaneModel other = AttachOtherClient();
        Guid theirs = other.Session.Id;
        var notices = new List<(string Title, string Message)>();

        ShowRestoringPane(theirs, shared: true, notices);

        PumpUntil(() => _pane!.Session is MuxClientSession { IsAttached: true } m && m.Id == theirs, "the pane attached the shared session");
        PumpUntil(() => _mux.Mux(theirs).AttachedClients == 2, "both clients are attached");
        Assert.Empty(notices);
        Assert.False(_pane!.MuxAttachSharedToRestore, "consumed once");
    }
}
```

- [ ] **Step 3: Run them to see them fail.** `scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MuxTerminalSessionFactoryTests|FullyQualifiedName~MuxPaneRestoreTests"` → compile errors: `AttachShared`, `AttachedElsewhere`, `MuxAttachSharedToRestore` and the notice constants are missing.

- [ ] **Step 4: Implement the request and the outcome.** In `ITerminalSessionFactory.cs`, add the trailing parameter and its doc:

```csharp
    /// <param name="AttachShared">
    /// With <paramref name="ExistingMuxSessionId"/>: the user chose to share this session ("Attach to
    /// session…"), so attach whatever else is attached to it. False (a restore) attaches only if no
    /// other interactive client holds it. Ignored by factories that do not multiplex.
    /// </param>
    public sealed record TerminalSessionRequest(
        string Command,
        string Arguments,
        string StartingDirectory,
        int Cols,
        int Rows,
        IReadOnlyDictionary<string, string>? EnvironmentOverrides,
        bool SkipPowerShellPostLaunchInit,
        SshSessionDescriptor? Ssh,
        Guid? ExistingMuxSessionId = null,
        bool AttachShared = false);
```

In `PersistentSessionFactory.cs`:

```csharp
    /// <summary>
    /// A restore's session is held by another client (another window or instance): a fresh shell was
    /// started, and the pane says so. On v2 the pane produces this after the attach itself refused
    /// (session_attached); on v1 the factory's listSessions check does.
    /// </summary>
    AttachedElsewhere,
```

- [ ] **Step 5: Implement the factory.** Replace the body of `if (request.ExistingMuxSessionId is Guid existing) { … }` in `MuxTerminalSessionFactory.CreatePersistent`:

```csharp
            if (request.ExistingMuxSessionId is Guid existing)
            {
                IReadOnlyList<SessionSummary> sessions = Rpc(ct => client.ListSessionsAsync(ct));
                SessionSummary? match = sessions.FirstOrDefault(s => s.SessionId == existing);

                if (request.AttachShared)
                {
                    // A deliberate share: join whatever else is attached, running or exited (an exited
                    // session shows its last screen and exit). Gone or faulted: a fresh shell.
                    if (match is { Faulted: false })
                    {
                        return new(client.OpenSession(existing, request.Command, request.Arguments, MuxAttachMode.Shared), PersistentSessionOutcome.Reattached, Host.Endpoint, null);
                    }

                    Guid replacement = Spawn(client, request);
                    return new(client.OpenSession(replacement, request.Command, request.Arguments), PersistentSessionOutcome.PreviousLost, Host.Endpoint, null);
                }

                if (match is { Running: true, Faulted: false } && !MuxCommandMatch.SameExecutable(match.Command, request.Command))
                {
                    _log?.Invoke($"[Mux] session {existing} runs '{match.Command}', not '{request.Command}'; starting a new shell instead of reattaching");
                    Guid other = Spawn(client, request);
                    return new(client.OpenSession(other, request.Command, request.Arguments), PersistentSessionOutcome.Spawned, Host.Endpoint, null);
                }

                if (match is { Running: true, Faulted: false })
                {
                    if (client.ProtocolVersion >= MuxProtocol.SessionEventsVersion)
                    {
                        // Exclusive by protocol: the attach itself refuses (session_attached) if another
                        // interactive client holds it, decided on the daemon's parse thread (spec §3).
                        // No client-side count check - two instances could both pass one.
                        return new(client.OpenSession(existing, request.Command, request.Arguments, MuxAttachMode.IfUnattached), PersistentSessionOutcome.Reattached, Host.Endpoint, null);
                    }

                    if (match.AttachedClients == 0)
                    {
                        return new(client.OpenSession(existing, request.Command, request.Arguments), PersistentSessionOutcome.Reattached, Host.Endpoint, null);
                    }

                    // v1: the Phase 2 check. Another client is showing it; leave it there.
                    _log?.Invoke($"[Mux] session {existing} is attached elsewhere; starting a new shell instead of taking it over");
                    Guid own = Spawn(client, request);
                    return new(client.OpenSession(own, request.Command, request.Arguments), PersistentSessionOutcome.AttachedElsewhere, Host.Endpoint, null);
                }

                // A faulted session is still running in the daemon: nothing will ever reopen it, so
                // end it now rather than leak its shell until the daemon exits.
                if (match is { Running: true, Faulted: true }) KillQuietly(client, existing);

                Guid fresh = Spawn(client, request);
                return new(client.OpenSession(fresh, request.Command, request.Arguments), PersistentSessionOutcome.PreviousLost, Host.Endpoint, null);
            }
```

(Task 5's command check moves into this block unchanged.)

- [ ] **Step 6: Implement the pane side** in `TerminalPane.axaml.cs`.

Add the constants next to the other mux banners:

```csharp
        internal const string MuxAttachedElsewhereBanner = "[Your previous shell is open in another window — started a new shell]";
        internal const string MuxAttachedElsewhereNoticeTitle = "Previous shell in use";
```

Add the state:

```csharp
        /// <summary>Set with <see cref="MuxSessionIdToRestore"/> by "Attach to session…": join it shared (consumed once).</summary>
        internal bool MuxAttachSharedToRestore { get; set; }

        private bool _muxAttachedElsewhereNotice; // UI thread: raise the notice once the replacement shell attached
        private bool _muxSessionIsShare;          // UI thread: the current session was a deliberate share
        private bool _muxReattachShared;          // UI thread: the lost session was a share, reattach it shared

        /// <summary>
        /// Whether the next spawn joins its session shared: a pending "Attach to session…" id, or the
        /// reattach of a share whose connection dropped. Consumed with the id.
        /// </summary>
        private bool TakeMuxAttachShared(bool isSsh)
        {
            if (isSsh) return false;
            bool shared = _muxReattachId is not null
                ? _muxReattachShared
                : MuxSessionIdToRestore is not null && MuxAttachSharedToRestore;
            MuxAttachSharedToRestore = false;
            _muxReattachShared = false;
            return shared;
        }
```

In `InitializeSessionCore`, compute the flag **before** the request (it must see the id before `TakeMuxSessionIdToRestore` clears it) and pass it:

```csharp
                bool isSsh = profile != null && profile.Type == ConnectionType.SSH;
                bool attachShared = TakeMuxAttachShared(isSsh);
                var request = new TerminalSessionRequest(
                    // ... unchanged arguments ...
                    ExistingMuxSessionId: TakeMuxSessionIdToRestore(isSsh),
                    AttachShared: attachShared);
```

In `CreateLocalSession`, after `PersistentSessionResult result = persistent.CreatePersistent(request);`, add `_muxSessionIsShare = request.AttachShared;`. Then extend the outcome chain:

```csharp
            else if (result.Outcome == PersistentSessionOutcome.AttachedElsewhere)
            {
                // v1: the factory's check found it attached elsewhere; said once the new shell attached.
                _muxAttachedElsewhereNotice = true;
            }
```

In `HandleMuxConnectionLost`, next to `_muxReattachId = …`, add `_muxReattachShared = reattach && _muxSessionIsShare;`.

In `AttachMuxAsync`, raise the notice on success and handle the refusal before the generic catch:

```csharp
            try
            {
                await mux.AttachAsync(scrollback, presentation).ConfigureAwait(false);
                this.Dispatcher.Post(() =>
                {
                    if (!IsCurrentMux(mux)) return;
                    if (previousLost)
                    {
                        TerminalLogger.Log($"[TerminalPane] previous multiplexer session was lost; started {mux.Id}");
                        PersistenceNotice?.Invoke(this, MuxPreviousLostNoticeTitle, MuxPreviousLostBanner);
                    }

                    if (_muxAttachedElsewhereNotice)
                    {
                        _muxAttachedElsewhereNotice = false;
                        PersistenceNotice?.Invoke(this, MuxAttachedElsewhereNoticeTitle, MuxAttachedElsewhereBanner);
                    }

                    PersistentSessionAttached?.Invoke(this);
                });
            }
            catch (MuxProtocolException ex) when (ex.Code == MuxErrorCodes.SessionAttached)
            {
                // The restore lost to another window, atomically on the daemon (spec §7.1): start fresh.
                TerminalLogger.Log($"[TerminalPane] session {mux.Id} is attached in another window; starting a new shell");
                this.Dispatcher.Post(() => RestartAfterAttachedElsewhere(mux));
            }
            catch (Exception ex)
            {
                // ... unchanged ...
            }
```

```csharp
        /// <summary>UI thread. Drops the refused session (a detach) and spawns a fresh shell; the notice follows its attach.</summary>
        private void RestartAfterAttachedElsewhere(MuxClientSession source)
        {
            if (!IsCurrentMux(source)) return;
            _muxReattachId = null;
            MuxSessionIdToRestore = null;
            _muxAttachedElsewhereNotice = true;
            Reconnect();
        }
```

Add `using Ntilde.Mux.Contracts;` to `TerminalPane.axaml.cs` if it is not already there. It is needed for `MuxErrorCodes`, and `MuxAttachMode` is not used here.

- [ ] **Step 7: Run the tests again.** Run the same filter, plus `FullyQualifiedName~MuxPaneTests|FullyQualifiedName~MainWindowMuxLifecycleTests|FullyQualifiedName~PaneSessionFactoryTests` → PASS.

- [ ] **Step 8: Commit** (format first): `feat(mux): exclusive restore by protocol, AttachShared, AttachedElsewhere notice`, with the trailer.

---
### Task 11: Unbound shortcut entries (`attach_session`, `detach_pane`)

**Files:**
- Modify: `src/Ntilde.App/Shell/Shortcuts/ShortcutDefinition.cs`
- Modify: `src/Ntilde.App/Shell/Shortcuts/ShortcutBindingResolver.cs`
- Modify: `src/Ntilde.App/Shell/Shortcuts/ShortcutCatalogEntry.cs`, `ShortcutCatalog.cs`
- Modify: `src/Ntilde.App/SettingsWindow.axaml.cs`
- Test: `tests/Ntilde.App.Tests/Core/ShortcutCatalogTests.cs`, `ShortcutBindingResolverTests.cs`, `SettingsWindowShortcutFilteringTests.cs`

**Interfaces:**
- Consumes: `SessionPersistenceMode.IsKeepOnClose(string?)`.
- Produces:
  - `ShortcutDefinition` accepts an empty default, and exposes `bool IsUnbound`.
  - `ShortcutCatalogEntry(…, string DefaultBinding, bool RequiresSessionPersistence = false)`
  - catalog entries `attach_session` ("Session: Attach to Session…") and `detach_pane` ("Pane: Detach"), both with default `""` and `RequiresSessionPersistence: true`
  - `internal static IReadOnlyList<ShortcutCatalogEntry> SettingsWindow.FilterShortcutCatalogEntries(string query, bool sessionPersistence = true)`
  - `internal static string SettingsWindow.DescribeDefaultBinding(string binding)`

- [ ] **Step 1: Write the failing tests**

```csharp
// ShortcutCatalogTests.cs (append)
    [Fact]
    public void The_mux_entries_exist_with_no_default_chord()
    {
        ShortcutCatalogEntry attach = Assert.Single(ShortcutCatalog.GetEntries(), e => e.CommandId == "attach_session");
        ShortcutCatalogEntry detach = Assert.Single(ShortcutCatalog.GetEntries(), e => e.CommandId == "detach_pane");

        Assert.Equal(("Session: Attach to Session…", "", ShortcutScope.App, true), (attach.Title, attach.DefaultBinding, attach.Scope, attach.RequiresSessionPersistence));
        Assert.Equal(("Pane: Detach", "", ShortcutScope.Pane, true), (detach.Title, detach.DefaultBinding, detach.Scope, detach.RequiresSessionPersistence));
        Assert.All(ShortcutCatalog.GetDefinitions().Where(d => d.CommandId is "attach_session" or "detach_pane"), d => Assert.True(d.IsUnbound));
    }
```

```csharp
// ShortcutBindingResolverTests.cs (append)
    [Fact]
    public void Unbound_entries_never_conflict_with_each_other()
    {
        ShortcutDefinition[] definitions =
        [
            new("attach_session", ShortcutScope.App, ""),
            new("detach_pane", ShortcutScope.Pane, ""),
            new("settings", ShortcutScope.App, "Ctrl+,"),
        ];

        ShortcutBindingResolution resolution = ShortcutBindingResolver.Resolve(definitions, null);

        Assert.True(resolution.IsValid);
        Assert.DoesNotContain(resolution.Bindings, b => b.CommandId is "attach_session" or "detach_pane");
    }

    [Fact]
    public void An_unbound_entry_can_be_bound_and_then_conflicts_like_any_other()
    {
        ShortcutDefinition[] definitions = [new("attach_session", ShortcutScope.App, ""), new("settings", ShortcutScope.App, "Ctrl+,")];

        ShortcutBindingResolution bound = ShortcutBindingResolver.Resolve(definitions,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["attach_session"] = "Ctrl+Alt+A" });
        ShortcutBindingResolution clash = ShortcutBindingResolver.Resolve(definitions,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["attach_session"] = "Ctrl+," });
        ShortcutBindingResolution junk = ShortcutBindingResolver.Resolve(definitions,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["attach_session"] = "Ctrl+LaunchMissiles" });

        Assert.Equal("Ctrl+Alt+A", Assert.Single(bound.Bindings, b => b.CommandId == "attach_session").Binding);
        Assert.False(clash.IsValid);
        Assert.True(junk.IsValid);                                        // an invalid override leaves it unbound
        Assert.DoesNotContain(junk.Bindings, b => b.CommandId == "attach_session");
    }
```

```csharp
// SettingsWindowShortcutFilteringTests.cs (append)
    [Fact]
    public void Mux_entries_are_listed_only_with_session_persistence_on()
    {
        Assert.Contains(SettingsWindow.FilterShortcutCatalogEntries("", sessionPersistence: true), e => e.CommandId == "attach_session");
        Assert.DoesNotContain(SettingsWindow.FilterShortcutCatalogEntries("", sessionPersistence: false), e => e.CommandId is "attach_session" or "detach_pane");
    }

    [Fact]
    public void An_unbound_default_reads_none()
    {
        Assert.Equal("Default none", SettingsWindow.DescribeDefaultBinding(""));
        Assert.Equal("Default Ctrl+,", SettingsWindow.DescribeDefaultBinding("Ctrl+,"));
    }
```

- [ ] **Step 2: Run them to see them fail.** `scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~ShortcutCatalogTests|FullyQualifiedName~ShortcutBindingResolverTests|FullyQualifiedName~SettingsWindowShortcutFilteringTests"` → compile errors: `IsUnbound`, `RequiresSessionPersistence`, the new filter parameter and `DescribeDefaultBinding` are missing.

- [ ] **Step 3: Implement.** In `ShortcutDefinition`, allow an empty default:

```csharp
    public ShortcutDefinition(string commandId, ShortcutScope scope, string defaultBinding)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            throw new ArgumentException("Command id cannot be empty.", nameof(commandId));
        }

        ArgumentNullException.ThrowIfNull(defaultBinding);
        CommandId = commandId;
        Scope = scope;
        DefaultBinding = defaultBinding.Trim();
    }

    /// <summary>No default chord: reachable from the palette until the user binds one (Phase 3 spec §7.6).</summary>
    public bool IsUnbound => DefaultBinding.Length == 0;
```

In `ShortcutBindingResolver.Resolve`, skip bindings that are still empty, and let an invalid override of an unbound entry leave it unbound:

```csharp
            if (string.IsNullOrWhiteSpace(binding))
            {
                continue; // unbound: no chord, nothing to normalise or collide with
            }

            string normalizedBinding;
            try
            {
                normalizedBinding = ShortcutNormalizer.Normalize(binding);
            }
            catch (ArgumentException) when (!string.Equals(binding, definition.DefaultBinding, StringComparison.Ordinal))
            {
                if (definition.IsUnbound) continue;
                normalizedBinding = ShortcutNormalizer.Normalize(definition.DefaultBinding);
            }
```

In `ShortcutCatalogEntry`:

```csharp
public sealed record ShortcutCatalogEntry(
    string CommandId,
    string Title,
    string Category,
    ShortcutScope Scope,
    string DefaultBinding,
    bool RequiresSessionPersistence = false);
```

In `ShortcutCatalog.Entries`, after `close_pane`, add:

```csharp
        // Phase 3 multiplexer commands (spec §7.2, §7.4): palette-first, no default chord. Listed in
        // Settings only while session persistence is on, which is also the only time MainWindow
        // registers or dispatches them.
        new("attach_session", "Session: Attach to Session…", "General", ShortcutScope.App, "", RequiresSessionPersistence: true),
        new("detach_pane", "Pane: Detach", "General", ShortcutScope.Pane, "", RequiresSessionPersistence: true),
```

In `SettingsWindow.axaml.cs`, change the filter and the row label:

```csharp
        internal static IReadOnlyList<ShortcutCatalogEntry> FilterShortcutCatalogEntries(string query, bool sessionPersistence = true)
        {
            IEnumerable<ShortcutCatalogEntry> entries = ShortcutCatalog.GetEntries();
            if (!sessionPersistence)
            {
                // With persistence off nothing mux-related exists: no command, no dispatch, no row.
                entries = entries.Where(entry => !entry.RequiresSessionPersistence);
            }

            // ... the existing query filter and ordering, unchanged ...
        }

        internal static string DescribeDefaultBinding(string binding) =>
            string.IsNullOrWhiteSpace(binding) ? "Default none" : $"Default {binding}";
```

Make these two call-site changes:
- `RebuildShortcutRows` (the method that calls `FilterShortcutCatalogEntries(query)`) now calls `FilterShortcutCatalogEntries(query, Ntilde.Shell.Mux.SessionPersistenceMode.IsKeepOnClose(_settings.SessionPersistence))`.
- The row description becomes `Text = $"{entry.Category} · {FormatScopeLabel(entry.Scope)} · {DescribeDefaultBinding(entry.DefaultBinding)}"`.

- [ ] **Step 4: Run the tests again.** Run the same filter, plus `FullyQualifiedName~TitleBar|FullyQualifiedName~AssistShortcutBinding` → PASS.

- [ ] **Step 5: Commit** (format first): `feat(shortcuts): unbound catalog entries; attach_session and detach_pane`, with the trailer.

---

### Task 12: The pane: shared indicator, killed-elsewhere banner, focus re-requests the grid

**Files:**
- Modify: `src/Ntilde.App/Controls/TerminalPane.axaml` (overlay badge)
- Modify: `src/Ntilde.App/Controls/TerminalPane.axaml.cs`
- Modify: `src/Ntilde.App/Shell/TabStatusPresentation.cs`
- Test: `tests/Ntilde.App.Tests/Controls/MuxPaneSharingTests.cs`
- Test: `tests/Ntilde.App.Tests/TabStatusPresentationTests.cs`

**Interfaces:**
- Consumes: `MuxClientSession.SessionChanged`, `AttachedClients`, `WasKilledElsewhere` (Task 9), `TermView.Cols/Rows`, and `IsCurrentMux`. In tests: `MuxTestHost`, `ClientPaneModel`, and `MuxTestText.VisibleText`.
- Produces:
  - `TerminalPane`:
    - `int MuxOtherClients` (UI thread)
    - `event Action<TerminalPane>? MuxSharingChanged`
    - `void ApplyMuxSharing(int? attachedClients)`
    - `void ReassertMuxGrid()`
    - `const string MuxKilledElsewhereBanner = "[Shell ended from another window]"`
    - the x:Named controls `MuxSharedIndicator` and `MuxSharedText`
  - `TabMarkerSet(…, bool Shared = false)`
  - `TabStatusPresentation.ResolveTabMarkers(…, bool isShared = false)`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Ntilde.App.Tests/Controls/MuxPaneSharingTests.cs
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Shell.Mux;

namespace Ntilde.Tests.Controls;

/// <summary>Phase 3 spec §7.3, §7.5.</summary>
public sealed class MuxPaneSharingTests : IDisposable
{
    private readonly MuxTestHost _mux = new();
    private readonly MuxConnectionHost _host;
    private readonly MuxTerminalSessionFactory _factory;
    private TerminalPane? _pane;
    private TextBox? _decoy;
    private Window? _window;

    public MuxPaneSharingTests()
    {
        _host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct), "test", null);
        _factory = new MuxTerminalSessionFactory(_host, new RecordingSessionFactory(new FakeTerminalSession()), null);
    }

    public void Dispose()
    {
        if (_window is not null)
        {
            _window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        _pane?.Dispose();
        _host.Dispose();
        _mux.Dispose();
    }

    private static void PumpUntil(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"Timed out waiting until {because}.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    /// <summary>A hosted pane (it spawns on first layout) beside a focusable decoy, so focus can move away and back.</summary>
    private MuxClientSession StartHostedPane()
    {
        _pane = new TerminalPane();
        PaneSpawnTestHelpers.DisableShellIntegration(_pane);
        _pane.SessionFactory = _factory;
        _decoy = new TextBox();
        var root = new DockPanel();
        DockPanel.SetDock(_decoy, Avalonia.Controls.Dock.Top);
        root.Children.Add(_decoy);
        root.Children.Add(_pane);
        _window = new Window { Content = root, Width = 900, Height = 500 };
        _window.Show();
        PumpUntil(() => _pane.Session is MuxClientSession { IsAttached: true }, "the hosted pane attached");
        return (MuxClientSession)_pane.Session!;
    }

    private ClientPaneModel AttachOther(Guid id, Ntilde.Mux.Contracts.MuxPresentation? presentation = null) => Task.Run(async () =>
    {
        MuxClient c = await _mux.ConnectClientAsync();
        return await MuxTestHost.AttachPaneAsync(c, id, presentation);
    }).GetAwaiter().GetResult();

    [AvaloniaFact]
    public void The_indicator_follows_SessionChanged()
    {
        MuxClientSession s = StartHostedPane();
        Assert.False(_pane!.MuxSharedIndicator.IsVisible);
        int raised = 0;
        _pane.MuxSharingChanged += _ => raised++;

        ClientPaneModel other = AttachOther(s.Id);
        PumpUntil(() => _pane.MuxSharedIndicator.IsVisible, "the indicator appeared");
        Assert.Equal("shared with 1", _pane.MuxSharedText.Text);
        Assert.Equal(1, _pane.MuxOtherClients);

        other.Session.Dispose();
        PumpUntil(() => !_pane.MuxSharedIndicator.IsVisible, "the indicator went away");
        Assert.Equal(0, _pane.MuxOtherClients);
        Assert.Equal(2, raised);
    }

    [AvaloniaFact]
    public void The_exit_banner_says_the_shell_ended_elsewhere_after_a_foreign_kill()
    {
        MuxClientSession s = StartHostedPane();
        MuxClient killer = Task.Run(() => _mux.ConnectClientAsync()).GetAwaiter().GetResult();

        Task.Run(() => killer.KillAsync(s.Id)).GetAwaiter().GetResult();
        PumpUntil(() => !s.IsProcessRunning, "the pane saw the exit");
        Assert.True(s.WasKilledElsewhere);
        _pane!.WriteLocalExitBanner(-1);

        PumpUntil(() => MuxTestText.VisibleText(_pane.Buffer!).Contains(TerminalPane.MuxKilledElsewhereBanner, StringComparison.Ordinal), "the banner is on screen");
        Assert.DoesNotContain("[Shell exited]", MuxTestText.VisibleText(_pane.Buffer!), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Gaining_focus_takes_the_grid_back_from_another_client()
    {
        MuxClientSession s = StartHostedPane();
        (int cols, int rows) = (_pane!.TermView.Cols, _pane.TermView.Rows);
        PumpUntil(() => _mux.Mux(s.Id).Cols == cols && _mux.Mux(s.Id).Rows == rows, "the session has the pane's grid");
        _decoy!.Focus();
        Dispatcher.UIThread.RunJobs();

        AttachOther(s.Id, MuxTestHost.DefaultPresentation with { Cols = cols - 10, Rows = rows - 3 }); // latest resize wins
        PumpUntil(() => _pane.Buffer!.Cols == cols - 10, "the pane follows the other client (letterboxed)");

        _pane.TermView.Focus();
        PumpUntil(() => _mux.Mux(s.Id).Cols == cols && _mux.Mux(s.Id).Rows == rows, "focus re-requested the pane's own grid");
    }
}
```

```csharp
// TabStatusPresentationTests.cs (append inside the class)
        [Fact]
        public void ResolveTabMarkers_Shared_IsIndependentOfTheOthers()
        {
            Assert.Equal(new TabMarkerSet(Bell: true, Activity: false, AgentWrote: false, AgentWatched: false, Shared: true),
                TabStatusPresentation.ResolveTabMarkers(hasBell: true, hasActivity: false, AgentAttentionTier.Idle, rollupPolicy: "WritesOnly", isShared: true));
            Assert.False(TabStatusPresentation.ResolveTabMarkers(hasBell: false, hasActivity: false, AgentAttentionTier.Idle, rollupPolicy: null).Shared);
        }
```

- [ ] **Step 2: Run them to see them fail.** `scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MuxPaneSharingTests|FullyQualifiedName~TabStatusPresentationTests"` → compile errors: `MuxSharedIndicator`, `MuxOtherClients`, `MuxKilledElsewhereBanner` and `Shared` are missing.

- [ ] **Step 3: Add the overlay.** In `TerminalPane.axaml`, add this beside `AgentOutputToggle`, in the same parent grid, `Grid.Row="0"`:

```xml
                    <!-- Phase 3 "shared with N" (spec §7.3). An overlay, deliberately not a StatusBar
                         segment: the status bar sits in the Auto row, so showing it shrinks the
                         terminal, sends a resize, and "latest resize wins" would take the shared grid
                         from the window the user is typing in. Visibility is owned by
                         TerminalPane.ApplyMuxSharing; the look is the agent segment's. -->
                    <Border x:Name="MuxSharedIndicator"
                            Grid.Row="0"
                            ZIndex="60"
                            IsVisible="False"
                            IsHitTestVisible="False"
                            HorizontalAlignment="Right"
                            VerticalAlignment="Bottom"
                            Margin="0,0,18,8"
                            Padding="6,2"
                            CornerRadius="3"
                            Background="#CC1A1A1A">
                        <StackPanel Orientation="Horizontal" Spacing="5">
                            <Ellipse Width="6" Height="6" Fill="#4FB0D4" VerticalAlignment="Center"/>
                            <TextBlock x:Name="MuxSharedText" Foreground="#AAAAAA" FontSize="10" VerticalAlignment="Center"/>
                        </StackPanel>
                    </Border>
```

- [ ] **Step 4: Implement the pane.** In `TerminalPane.axaml.cs`:

```csharp
        internal const string MuxKilledElsewhereBanner = "[Shell ended from another window]";

        /// <summary>UI thread: how many OTHER clients are attached to this pane's mux session (0 = not shared, or unknown).</summary>
        internal int MuxOtherClients { get; private set; }

        /// <summary>Raised on the UI thread when <see cref="MuxOtherClients"/> changes (MainWindow marks the tab).</summary>
        internal event Action<TerminalPane>? MuxSharingChanged;

        /// <summary>UI thread. Null (v1 daemon, disconnected, replaced session) hides the badge.</summary>
        internal void ApplyMuxSharing(int? attachedClients)
        {
            int others = attachedClients is int n ? Math.Max(0, n - 1) : 0;
            MuxSharedIndicator.IsVisible = others > 0;
            MuxSharedText.Text = others > 0 ? $"shared with {others}" : string.Empty;
            if (others == MuxOtherClients) return;
            MuxOtherClients = others;
            MuxSharingChanged?.Invoke(this);
        }

        /// <summary>
        /// UI thread. Latest resize wins (spec §7.5): when another client has resized the shared
        /// session, this view letterboxes; when it regains focus it asks for its own grid again, so the
        /// window the user is typing in wins. A no-op when the grids already agree.
        /// </summary>
        internal void ReassertMuxGrid()
        {
            if (Session is not MuxClientSession { IsAttached: true } mux || Buffer is not { } buffer) return;
            int cols = TermView.Cols, rows = TermView.Rows;
            if (cols <= 0 || rows <= 0) return;
            if (buffer.Cols == cols && buffer.Rows == rows) return;
            mux.Resize(cols, rows);
        }
```

Wire it up:
- In `WireMuxSession`, first line: `ApplyMuxSharing(null);`. After the other subscriptions:

  ```csharp
            // Delivery thread; marshal. The attach itself changes the count, so a v2 daemon announces
            // the initial sharing right after the snapshot.
            mux.SessionChanged += () => this.Dispatcher.Post(() => { if (IsCurrentMux(mux)) ApplyMuxSharing(mux.AttachedClients); });
  ```

- In `WireStreamOrderedSession`: `if (Session is MuxClientSession mux) WireMuxSession(mux, muxPreviousLost); else ApplyMuxSharing(null);`
- In `HandleMuxConnectionLost`, after the guard: `ApplyMuxSharing(null);`
- Replace the focus wiring `TermView.GotFocus += (s, e) => UpdateFocusVisuals(true);` with:

  ```csharp
            TermView.GotFocus += (s, e) =>
            {
                UpdateFocusVisuals(true);
                ReassertMuxGrid();
            };
  ```

- `WriteLocalExitBanner` gets its killed-elsewhere branch first:

  ```csharp
        internal void WriteLocalExitBanner(int code)
        {
            if (Session is MuxClientSession { WasKilledElsewhere: true })
            {
                // Another window (or `ntilde mux kill`) ended it (spec §7.5): say so, not "exited".
                WriteBanner($"\r\n{MuxKilledElsewhereBanner}\r\n[Press Enter to restart]\r\n");
                return;
            }

            // ... unchanged ...
        }
  ```

- [ ] **Step 5: Implement the marker.** In `TabStatusPresentation.cs`:

```csharp
    /// <param name="Shared">A pane in this tab is attached to a mux session another client also shows (Phase 3).</param>
    internal readonly record struct TabMarkerSet(bool Bell, bool Activity, bool AgentWrote, bool AgentWatched, bool Shared = false);
```

```csharp
        internal static TabMarkerSet ResolveTabMarkers(
            bool hasBell,
            bool hasActivity,
            AgentHost.AgentAttentionTier agentTier,
            string? rollupPolicy,
            bool isShared = false)
        {
            return new TabMarkerSet(
                Bell: hasBell,
                Activity: hasActivity && !hasBell,
                AgentWrote: agentTier == AgentHost.AgentAttentionTier.Wrote,
                AgentWatched: agentTier == AgentHost.AgentAttentionTier.Watched
                              && string.Equals(rollupPolicy, "All", System.StringComparison.Ordinal),
                Shared: isShared);
        }
```

The dot (`ResolveTabDot`) is unchanged: sharing is a marker, not an attention state.

- [ ] **Step 6: Run the tests again.** Run the same filter, plus `FullyQualifiedName~MuxPaneTests|FullyQualifiedName~TerminalPane` → PASS.

- [ ] **Step 7: Commit** (format first): `feat(mux): pane shared indicator, killed-elsewhere banner, focus re-requests the grid`, with the trailer.

---

### Task 13: "Attach to session…": picker, shared tab, tab marker

**Files:**
- Create: `src/Ntilde.App/Shell/Mux/MuxSessionPicker.cs`
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxSessionPickerTests.cs`
- Test: `tests/Ntilde.App.Tests/Core/MainWindowMuxSharingTests.cs` (new)

**Interfaces:**
- Consumes: `TerminalPane.MuxAttachSharedToRestore` (Task 10), `TerminalPane.MuxOtherClients` / `MuxSharingChanged` (Task 12), `TabStatusPresentation.ResolveTabMarkers(…, isShared)` (Task 12), `MuxConnectionHost.GetClient`, `AddTabWithPane`, `ShellHelper.ResolveExecutableOrDefault`, `ResolveOwningTabForPane`, `EnumeratePanes`, `GetLayoutRootForTab`, and `QueueTabVisualRefresh`.
- Produces:
  - `internal sealed record MuxSessionPickerRow(Guid SessionId, string Title, string Command, string? Cwd, int Cols, int Rows, int AttachedClients, bool Running, int? ExitCode, bool OpenHere)` with `State` and `Display`
  - `internal static IReadOnlyList<MuxSessionPickerRow> MuxSessionPicker.BuildRows(IEnumerable<SessionSummary> sessions, IReadOnlySet<Guid> openHere)`
  - `MainWindow`:
    - `Func<IReadOnlyList<MuxSessionPickerRow>, Task<Guid?>> PickMuxSession` (internal)
    - `internal Task AttachToMuxSessionAsync()`
    - `internal const string SharedGlyph = "⧉"`

- [ ] **Step 1: Write the failing picker tests**

```csharp
// tests/Ntilde.App.Tests/Shell/Mux/MuxSessionPickerTests.cs
using Ntilde.Mux.Contracts;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxSessionPickerTests
{
    private static SessionSummary S(string title, bool running = true, bool faulted = false, int attached = 0, string? cwd = null, int? exit = null) =>
        new() { SessionId = Guid.NewGuid(), Title = title, Command = "pwsh", Cols = 120, Rows = 40, Running = running, Faulted = faulted, AttachedClients = attached, Cwd = cwd, ExitCode = exit };

    [Fact]
    public void Running_sessions_come_first_faulted_ones_are_left_out_and_open_here_is_marked()
    {
        SessionSummary exited = S("zeta", running: false, exit: 2);
        SessionSummary b = S("beta", attached: 1, cwd: "/srv");
        SessionSummary a = S("alpha");
        SessionSummary broken = S("broken", faulted: true);

        IReadOnlyList<MuxSessionPickerRow> rows = MuxSessionPicker.BuildRows([exited, b, a, broken], new HashSet<Guid> { b.SessionId });

        Assert.Equal(["alpha", "beta", "zeta"], rows.Select(r => r.Title).ToArray());
        Assert.True(rows[1].OpenHere);
        Assert.Equal("exited 2", rows[2].State);
        Assert.Contains("/srv", rows[1].Display, StringComparison.Ordinal);
        Assert.Contains("120x40", rows[1].Display, StringComparison.Ordinal);
        Assert.Contains("1 attached", rows[1].Display, StringComparison.Ordinal);
        Assert.Contains("open here", rows[1].Display, StringComparison.Ordinal);
        Assert.Contains("detached", rows[0].Display, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Write the failing window tests.** This is the new file. Tasks 14 and 19 append to it.

```csharp
// tests/Ntilde.App.Tests/Core/MainWindowMuxSharingTests.cs
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory
using Ntilde.Tests.Shell.Mux;
using Ntilde.VT.Tests.StateTransfer;

namespace Ntilde.Tests.Core;

/// <summary>
/// Phase 3 spec §7: a window attaches shared to a session another client (another instance) holds.
/// The window's panes spawn into an in-memory MuxServer; the "other instance" is a second MuxClient.
/// </summary>
public sealed class MainWindowMuxSharingTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private readonly MuxTestHost _mux = new();
    private MuxConnectionHost? _host;

    public MainWindowMuxSharingTests()
    {
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
    }

    public void Dispose()
    {
        TestMainWindowFactory.DisposeCreatedWindows();
        _host?.Dispose();
        _mux.Dispose();
    }

    private MainWindow CreateWindow()
    {
        _host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(_mux.Listener.Connect(), null, ct), "test", null);
        var factory = new MuxTerminalSessionFactory(_host, new RecordingSessionFactory(new FakeTerminalSession()), null);
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
        });
        window.Show();
        PumpUntil(() => AllPanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true }), "the first pane attached");
        return window;
    }

    private static IReadOnlyList<TerminalPane> AllPanes(MainWindow w) => w.AllPanesForTest();

    private static TerminalSettings Settings(MainWindow window) =>
        (TerminalSettings)typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    private static void PumpUntil(Func<bool> condition, string because, int ms = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > ms) Assert.Fail($"Timed out: {because}");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    /// <summary>Another ntilde instance: its own connection, a session it spawned and shows.</summary>
    private (MuxClient Client, ClientPaneModel Pane) OtherInstance() => Task.Run(async () =>
    {
        MuxClient c = await _mux.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c);
        return (c, await MuxTestHost.AttachPaneAsync(c, id));
    }).GetAwaiter().GetResult();

    /// <summary>Runs "Attach to session…", choosing <paramref name="id"/>, and returns the pane that attached it.</summary>
    private static TerminalPane AttachShared(MainWindow window, Guid id)
    {
        window.PickMuxSession = rows =>
        {
            Assert.Contains(rows, r => r.SessionId == id);
            return Task.FromResult<Guid?>(id);
        };
        Task command = window.AttachToMuxSessionAsync();
        PumpUntil(() => command.IsCompleted, "the attach command finished");
        PumpUntil(() => AllPanes(window).Any(p => p.Session is MuxClientSession { IsAttached: true } m && m.Id == id), "the shared tab attached");
        return AllPanes(window).Single(p => p.Session is MuxClientSession m && m.Id == id);
    }

    private void Settle(Guid id, params MuxClient[] clients)
    {
        Task.Run(() => _mux.SettleAsync(id, clients)).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private void AssertBothEqualTheMux(Guid id, TerminalPane mine, ClientPaneModel theirs, string because)
    {
        HeadlessTerminalSession m = _mux.Mux(id);
        TerminalStateAssert.AssertEquivalent($"[mine: {because}]", m.Buffer, m.Parser, mine.Buffer!, mine.Parser!);
        _mux.AssertPaneMatchesMux(id, theirs, $"theirs: {because}");
    }

    [AvaloniaFact]
    public void Attach_to_session_opens_a_shared_tab_and_both_clients_equal_the_mux()
    {
        MainWindow window = CreateWindow();
        (MuxClient other, ClientPaneModel theirs) = OtherInstance();
        Guid id = theirs.Session.Id;

        TerminalPane mine = AttachShared(window, id);
        MuxClient mineClient = _host!.CurrentClient!;
        PumpUntil(() => _mux.Mux(id).AttachedClients == 2, "both clients attached");

        _mux.Fake(id).Emit("shared\r\n\x1b[1mbold\x1b[0m line\r\n");
        Settle(id, mineClient, other);
        AssertBothEqualTheMux(id, mine, theirs, "after output");

        ((MuxClientSession)mine.Session!).Resize(70, 20);
        Settle(id, mineClient, other);
        AssertBothEqualTheMux(id, mine, theirs, "after a resize from this window");

        theirs.Session.Resize(90, 25);
        Settle(id, mineClient, other);
        AssertBothEqualTheMux(id, mine, theirs, "after a resize from the other instance");
    }

    [AvaloniaFact]
    public void A_shared_tab_is_marked_in_the_tab_strip()
    {
        MainWindow window = CreateWindow();
        (_, ClientPaneModel theirs) = OtherInstance();

        TerminalPane mine = AttachShared(window, theirs.Session.Id);

        PumpUntil(() => mine.MuxOtherClients == 1, "the pane knows it is shared");
        TabItem tab = window.FindControl<TabControl>("Tabs")!.Items.OfType<TabItem>().Single(t => ReferenceEquals(t.Content, mine));
        PumpUntil(() => (ToolTip.GetTip((Control)tab.Header!) as string)?.Contains(MainWindow.SharedGlyph, StringComparison.Ordinal) == true,
            "the tab label carries the shared marker");
    }

    [AvaloniaFact]
    public void Choosing_a_session_open_here_focuses_its_tab_instead_of_duplicating_it()
    {
        MainWindow window = CreateWindow();
        TerminalPane own = AllPanes(window).Single();
        Guid id = ((MuxClientSession)own.Session!).Id;
        IReadOnlyList<MuxSessionPickerRow>? offered = null;
        window.PickMuxSession = rows =>
        {
            offered = rows;
            return Task.FromResult<Guid?>(id);
        };

        Task command = window.AttachToMuxSessionAsync();
        PumpUntil(() => command.IsCompleted, "the attach command finished");

        Assert.True(Assert.Single(offered!, r => r.SessionId == id).OpenHere);
        Assert.Same(own, Assert.Single(AllPanes(window)));
    }
}
```

- [ ] **Step 3: Run them to see them fail.** `scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MuxSessionPickerTests|FullyQualifiedName~MainWindowMuxSharingTests"` → compile errors: `MuxSessionPicker`, `PickMuxSession`, `AttachToMuxSessionAsync` and `SharedGlyph` are missing.

- [ ] **Step 4: Implement the rows**

```csharp
// src/Ntilde.App/Shell/Mux/MuxSessionPicker.cs
using System.Globalization;
using Ntilde.Mux.Contracts;

namespace Ntilde.Shell.Mux;

/// <summary>One line of the "Attach to session…" picker (spec §7.2).</summary>
internal sealed record MuxSessionPickerRow(
    Guid SessionId, string Title, string Command, string? Cwd, int Cols, int Rows,
    int AttachedClients, bool Running, int? ExitCode, bool OpenHere)
{
    public string State => Running ? "running" : $"exited {ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}";

    public string Display
    {
        get
        {
            string title = string.IsNullOrWhiteSpace(Title) ? Command : Title;
            string where = string.IsNullOrEmpty(Cwd) ? string.Empty : "  " + Cwd;
            string attached = AttachedClients switch
            {
                0 => "detached",
                _ => string.Create(CultureInfo.InvariantCulture, $"{AttachedClients} attached"),
            };
            string here = OpenHere ? "  ·  open here" : string.Empty;
            return string.Create(CultureInfo.InvariantCulture, $"{title}  —  {Command}{where}  ·  {Cols}x{Rows}  ·  {attached}  ·  {State}{here}");
        }
    }
}

internal static class MuxSessionPicker
{
    /// <summary>Running first, then by title; faulted sessions cannot be attached and are left out.</summary>
    public static IReadOnlyList<MuxSessionPickerRow> BuildRows(IEnumerable<SessionSummary> sessions, IReadOnlySet<Guid> openHere) =>
        sessions
            .Where(s => !s.Faulted)
            .OrderByDescending(s => s.Running)
            .ThenBy(s => string.IsNullOrWhiteSpace(s.Title) ? s.Command : s.Title, StringComparer.OrdinalIgnoreCase)
            .Select(s => new MuxSessionPickerRow(s.SessionId, s.Title, s.Command, s.Cwd, s.Cols, s.Rows, s.AttachedClients, s.Running, s.ExitCode, openHere.Contains(s.SessionId)))
            .ToList();
}
```

- [ ] **Step 5: Implement the window side** in `MainWindow.axaml.cs`.

Add the seam, assigned in the constructor next to `ConfirmSessionLossForUpdate = …;`:

```csharp
        /// <summary>Shows the picker; null = cancelled. A seam so tests choose without a modal.</summary>
        internal Func<IReadOnlyList<Ntilde.Shell.Mux.MuxSessionPickerRow>, Task<Guid?>> PickMuxSession { get; set; }
        // in the constructor:
        PickMuxSession = ShowMuxSessionPickerAsync;
```

Add the command:

```csharp
        /// <summary>
        /// "Attach to session…" (spec §7.2). Lists the daemon's sessions off the UI thread, lets the
        /// user pick one, and opens it in a new tab attached shared. A session this window already
        /// shows is focused instead: one connection cannot hold two views of one session.
        /// </summary>
        internal async Task AttachToMuxSessionAsync()
        {
            if (_muxHost is not { } host) return;
            IReadOnlyList<Ntilde.Mux.Contracts.SessionSummary>? sessions = await Task.Run(async () =>
            {
                Ntilde.Mux.MuxClient? client = host.GetClient(TimeSpan.FromSeconds(5));
                if (client is null) return null;
                try
                {
                    return await client.ListSessionsAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is Ntilde.Mux.Contracts.MuxProtocolException or IOException or TimeoutException)
                {
                    AppLogger.Log($"[MainWindow] listing mux sessions failed: {ex.Message}");
                    return null;
                }
            });
            if (_teardownDone) return;
            if (sessions is null)
            {
                ShowRecordingToast("Attach to session", "The multiplexer is not reachable.", null, null, autoHide: true);
                return;
            }

            var openHere = new HashSet<Guid>();
            foreach (TerminalPane p in AllPanes())
            {
                if (p.Session is Ntilde.Mux.MuxClientSession m) openHere.Add(m.Id);
            }

            IReadOnlyList<Ntilde.Shell.Mux.MuxSessionPickerRow> rows = Ntilde.Shell.Mux.MuxSessionPicker.BuildRows(sessions, openHere);
            if (rows.Count == 0)
            {
                ShowRecordingToast("Attach to session", "No sessions are running in the multiplexer.", null, null, autoHide: true);
                return;
            }

            Guid? chosen = await PickMuxSession(rows);
            if (chosen is not Guid id || _teardownDone) return;
            if (rows.First(r => r.SessionId == id).OpenHere)
            {
                FocusPaneShowingMuxSession(id);
                return;
            }

            Ntilde.Mux.Contracts.SessionSummary summary = sessions.First(s => s.SessionId == id);
            var pane = new TerminalPane(ShellHelper.ResolveExecutableOrDefault(summary.Command), summary.Arguments ?? string.Empty, _settings)
            {
                MuxSessionIdToRestore = id,
                MuxAttachSharedToRestore = true,
            };
            AddTabWithPane(pane, string.IsNullOrWhiteSpace(summary.Title) ? summary.Command : summary.Title, select: true);
        }

        private void FocusPaneShowingMuxSession(Guid id)
        {
            TerminalPane? pane = AllPanes().FirstOrDefault(p => p.Session is Ntilde.Mux.MuxClientSession m && m.Id == id);
            if (pane is null) return;
            if (ResolveOwningTabForPane(pane) is { } tab && this.FindControl<TabControl>("Tabs") is { } tabs) tabs.SelectedItem = tab;
            UpdateActivePane(pane);
            FocusPaneTerminal(pane, defer: true);
        }

        private async Task<Guid?> ShowMuxSessionPickerAsync(IReadOnlyList<Ntilde.Shell.Mux.MuxSessionPickerRow> rows)
        {
            Guid? chosen = null;
            var dialog = CreateThemedDialogWindow("Attach to Session", 640, 360, canResize: true);
            var list = new ListBox { ItemsSource = rows.Select(r => r.Display).ToList(), SelectedIndex = 0, MaxHeight = 240 };
            var attach = new Button { Content = "Attach", Width = 92 };
            var cancel = new Button { Content = "Cancel", Width = 92 };
            void Accept()
            {
                if (list.SelectedIndex < 0) return;
                chosen = rows[list.SelectedIndex].SessionId;
                dialog.Close();
            }

            attach.Click += (_, _) => Accept();
            list.DoubleTapped += (_, _) => Accept();
            cancel.Click += (_, _) => dialog.Close();
            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Attach to a running session. It opens in a new tab and stays shared with its other windows.",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        list,
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancel, attach },
                        },
                    },
                },
            };

            await dialog.ShowDialog(this);
            return chosen;
        }
```

Register the palette command in `SetupCommandPalette`, after "Pane: Reconnect":

```csharp
            if (_muxHost is not null)
            {
                CommandRegistry.Register("Session: Attach to Session…", "Session", () => _ = AttachToMuxSessionAsync(), GetEffectiveShortcutBinding("attach_session", ""), "attach_session");
            }
```

Dispatch the key in the tunnel `KeyDown` chain, next to `close_pane`. It is inert until the user binds a chord:

```csharp
                if (_muxHost is not null && IsShortcut(e, "attach_session", ""))
                {
                    RecordCommandUsage("attach_session");
                    _ = AttachToMuxSessionAsync();
                    e.Handled = true;
                    return;
                }
```

Mark shared tabs:
- `TabRuntimeState` gets `public bool IsShared { get; set; }`.
- Add the glyph and brush beside the agent ones:

  ```csharp
        internal const string SharedGlyph = "⧉";  // two joined squares: one shell, several windows
        private static readonly IBrush TabSharedChipBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0x4F, 0xB0, 0xD4));
  ```

- In `WirePane`, add `pane.MuxSharingChanged -= OnPaneMuxSharingChanged;` and then `pane.MuxSharingChanged += OnPaneMuxSharingChanged;`. In `UnwirePane`, add the `-=`.
- Add the handler:

  ```csharp
        private void OnPaneMuxSharingChanged(TerminalPane pane)
        {
            TabItem? tab = ResolveOwningTabForPane(pane);
            if (tab is null) return;
            TabRuntimeState state = GetOrCreateTabState(tab);
            bool shared = EnumeratePanes(GetLayoutRootForTab(tab)).Any(p => p.MuxOtherClients > 0);
            if (state.IsShared == shared) return;
            state.IsShared = shared;
            QueueTabVisualRefresh(tab);
        }
  ```

- In the vertical header factory, add `var sharedChip = Chip("TabSharedChip", SharedGlyph, TabSharedChipBrush);` and `chipsColumn.Children.Add(sharedChip);` after the agent chips.
- In `UpdateVerticalTabExtras`, pass `state.IsShared` as `isShared` to `ResolveTabMarkers`, and add `SetChipVisibility(tab, "TabSharedChip", markers.Shared);`.
- In `GetAttentionMarkerSuffix`, before `return suffix;`, add `if (state.IsShared) suffix += " " + SharedGlyph;`.
- In `GetAttentionAnnouncementSuffix`, return `attention + agent + (state.IsShared ? " shared" : string.Empty);`.

- [ ] **Step 6: Run the tests again.** The same filter → PASS. Also run `FullyQualifiedName~MainWindowMuxLifecycleTests|FullyQualifiedName~TabStatus|FullyQualifiedName~CompiledBinding` → PASS.

- [ ] **Step 7: Commit** (format first): `feat(mux): Attach to session… picker opens a shared tab; shared tab marker`, with the trailer.

---

### Task 14: Detach versus close

**Files:**
- Modify: `src/Ntilde.App/Shell/Mux/PaneDisposition.cs`
- Create: `src/Ntilde.App/Shell/Mux/SharedCloseChoice.cs`
- Modify: `src/Ntilde.App/MainWindow.axaml.cs`
- Test: `tests/Ntilde.App.Tests/Core/MainWindowMuxSharingTests.cs`

**Interfaces:**
- Consumes: `MuxClientSession.RefreshSharingAsync` (Task 9), `KillMuxSessionOnClose` (Task 4), and `AttachToMuxSessionAsync` (Task 13).
- Produces:
  - `PaneDisposition.Detach`
  - `internal enum SharedCloseChoice { Cancel, Close, Detach }`
  - `MainWindow`:
    - `Func<int, Task<SharedCloseChoice>> ConfirmSharedClose` (internal)
    - `internal Task<bool> DetachPaneAsync(TerminalPane pane)`
    - `private Task<SharedCloseChoice> DecidePaneCloseAsync(TerminalPane pane)`
    - `private Task<bool> ClosePaneCoreAsync(TerminalPane pane, bool skipConfirm, PaneDisposition requested)`
    - `private Task<bool> CloseTabCoreAsync(TabItem tab, bool skipProcessChecks, PaneDisposition requested)`
    - `private void CloseTabCore(TabItem ti, PaneDisposition disposition, IReadOnlySet<TerminalPane>? detach)`
    - `DisposeControlTree(Control, PaneDisposition = EndSession, IReadOnlySet<TerminalPane>? detach = null)`

`ClosePaneAsync(TerminalPane, bool)`, `CloseTabAsync(TabItem, bool)` and `CloseTab(TabItem)` keep their exact signatures. Existing tests find `ClosePaneAsync` by reflection with two arguments, so an overload or an extra optional parameter would break them.

- [ ] **Step 1: Write the failing tests** (append to `MainWindowMuxSharingTests`)

```csharp
    [AvaloniaFact]
    public void Detach_pane_keeps_the_shell_running_and_the_count_drops()
    {
        MainWindow window = CreateWindow();
        (_, ClientPaneModel theirs) = OtherInstance();
        Guid id = theirs.Session.Id;
        TerminalPane mine = AttachShared(window, id);
        PumpUntil(() => _mux.Mux(id).AttachedClients == 2, "both attached");

        Task<bool> detach = window.DetachPaneAsync(mine);
        PumpUntil(() => detach.IsCompleted, "the detach finished");

        Assert.True(detach.Result);
        PumpUntil(() => _mux.Mux(id).AttachedClients == 1, "the daemon saw the detach");
        Assert.Contains(id, _mux.Server.GetSessionIds());
        Assert.False(_mux.Mux(id).IsExited);
        Assert.DoesNotContain(mine, AllPanes(window));
        Assert.Equal("Shell detached", window.FindControl<TextBlock>("RecordingToastTitle")!.Text);
        PumpUntil(() => theirs.Session.AttachedClients == 1, "the other instance heard it");
    }

    [AvaloniaTheory]
    [InlineData("Cancel")]
    [InlineData("Detach")]
    [InlineData("Close")]
    public void Close_with_others_attached_prompts_and_each_choice_behaves(string choiceName)
    {
        SharedCloseChoice choice = Enum.Parse<SharedCloseChoice>(choiceName);
        MainWindow window = CreateWindow();
        (_, ClientPaneModel theirs) = OtherInstance();
        Guid id = theirs.Session.Id;
        TerminalPane mine = AttachShared(window, id);
        PumpUntil(() => _mux.Mux(id).AttachedClients == 2, "both attached");
        int asked = -1;
        window.ConfirmSharedClose = others =>
        {
            asked = others;
            return Task.FromResult(choice);
        };
        var close = typeof(MainWindow).GetMethod("ClosePaneAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var task = (Task<bool>)close.Invoke(window, [mine, false])!;
        PumpUntil(() => task.IsCompleted, "the close finished");

        Assert.Equal(1, asked);
        switch (choice)
        {
            case SharedCloseChoice.Cancel:
                Assert.False(task.Result);
                Assert.Contains(mine, AllPanes(window));
                Assert.Equal(2, _mux.Mux(id).AttachedClients);
                break;
            case SharedCloseChoice.Detach:
                Assert.True(task.Result);
                PumpUntil(() => _mux.Mux(id).AttachedClients == 1, "the daemon saw the detach");
                Assert.False(_mux.Mux(id).IsExited);
                break;
            case SharedCloseChoice.Close:
                Assert.True(task.Result);
                PumpUntil(() => !_mux.Server.GetSessionIds().Contains(id), "the shell was killed");
                PumpUntil(() => theirs.Session.WasKilledElsewhere, "the other instance was told who ended it");
                break;
        }
    }

    [AvaloniaFact]
    public void A_lone_pane_is_decided_without_the_shared_prompt()
    {
        MainWindow window = CreateWindow();
        Settings(window).PaneClosePolicy = "Force";
        TerminalPane own = AllPanes(window).Single();
        int asked = -1;
        window.ConfirmSharedClose = others =>
        {
            asked = others;
            return Task.FromResult(SharedCloseChoice.Cancel);
        };
        var decide = typeof(MainWindow).GetMethod("DecidePaneCloseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var task = (Task<SharedCloseChoice>)decide.Invoke(window, [own])!;
        PumpUntil(() => task.IsCompleted, "the decision finished");

        Assert.Equal(SharedCloseChoice.Close, task.Result);
        Assert.Equal(-1, asked);
    }

    [AvaloniaFact]
    public void Killed_elsewhere_shows_the_ended_from_another_window_banner()
    {
        MainWindow window = CreateWindow();
        Settings(window).ShellExitPolicy = "Graceful";
        (MuxClient other, ClientPaneModel theirs) = OtherInstance();
        Guid id = theirs.Session.Id;
        TerminalPane mine = AttachShared(window, id);

        Task.Run(() => other.KillAsync(id)).GetAwaiter().GetResult();

        PumpUntil(() => MuxTestText.VisibleText(mine.Buffer!).Contains(TerminalPane.MuxKilledElsewhereBanner, StringComparison.Ordinal), "the banner is shown");
        Assert.Contains(mine, AllPanes(window)); // Graceful and a kill's exit code -1: the pane stays
    }

    [AvaloniaFact]
    public void Mux_commands_are_registered_with_persistence_on()
    {
        MainWindow window = CreateWindow();

        typeof(MainWindow).GetMethod("SetupCommandPalette", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);

        Assert.Contains(CommandRegistry.GetCommands(), c => c.Id == "attach_session");
        Assert.Contains(CommandRegistry.GetCommands(), c => c.Id == "detach_pane");
    }

    [AvaloniaFact]
    public void Mux_commands_are_not_registered_when_persistence_is_off()
    {
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
        });
        Assert.Null(window.MuxHost);

        typeof(MainWindow).GetMethod("SetupCommandPalette", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);

        Assert.DoesNotContain(CommandRegistry.GetCommands(), c => c.Id is "attach_session" or "detach_pane");
    }
```

- [ ] **Step 2: Run them to see them fail.** `scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MainWindowMuxSharingTests"` → compile errors: `SharedCloseChoice`, `DetachPaneAsync`, `ConfirmSharedClose` and `DecidePaneCloseAsync` are missing. With stubs (a detach that closes with `EndSession`, and no prompt), the detach and choice tests fail: the session is killed, and `asked` stays -1.

- [ ] **Step 3: Implement the enums**

```csharp
// src/Ntilde.App/Shell/Mux/PaneDisposition.cs
namespace Ntilde.Shell.Mux;

/// <summary>
/// What disposing a pane's control tree means for its session (Phase 2 spec §9, Phase 3 §7.4). Only
/// a persistent (mux) session is affected: a normal session ends either way.
/// </summary>
internal enum PaneDisposition
{
    /// <summary>The user closed the pane: the shell ends, even one living in the daemon.</summary>
    EndSession,

    /// <summary>"Pane: Detach" or the shared-close prompt's Detach: only the view goes, the daemon keeps the shell.</summary>
    Detach,
}
```

```csharp
// src/Ntilde.App/Shell/Mux/SharedCloseChoice.cs
namespace Ntilde.Shell.Mux;

/// <summary>A pane-close decision: the shared-close prompt's three buttons, and the plain close/keep answer.</summary>
internal enum SharedCloseChoice
{
    Cancel,
    Close,
    Detach,
}
```

- [ ] **Step 4: Implement the decision, and route closes through a disposition.** In `MainWindow.axaml.cs`:

```csharp
        /// <summary>The shared-close prompt (spec §7.4). A seam so tests answer without a modal.</summary>
        internal Func<int, Task<Ntilde.Shell.Mux.SharedCloseChoice>> ConfirmSharedClose { get; set; }
        // in the constructor, next to PickMuxSession:
        ConfirmSharedClose = ShowSharedCloseDialogAsync;
```

```csharp
        /// <summary>
        /// Close, Detach or Cancel for one pane. A mux pane whose shell other clients also show asks the
        /// three-way question first; the count comes from listSessions (bounded, works on v1 too).
        /// Otherwise the Phase 2 decision (<see cref="ShouldClosePaneAsync"/>) stands.
        /// </summary>
        private async Task<Ntilde.Shell.Mux.SharedCloseChoice> DecidePaneCloseAsync(TerminalPane pane)
        {
            if (pane.Session is Ntilde.Mux.MuxClientSession { IsConnected: true, IsAttached: true } mux)
            {
                int? attached;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    attached = await mux.RefreshSharingAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or Ntilde.Mux.Contracts.MuxProtocolException or ObjectDisposedException or IOException)
                {
                    attached = mux.AttachedClients; // stale is acceptable
                }

                if (attached is int n && n > 1)
                {
                    try
                    {
                        return await ConfirmSharedClose(n - 1);
                    }
                    catch (Exception ex)
                    {
                        // A question that could not be asked is never read as "close it for everyone".
                        AppLogger.Log($"[MainWindow] the shared-close prompt failed: {ex.Message}");
                        return Ntilde.Shell.Mux.SharedCloseChoice.Cancel;
                    }
                }
            }

            return await ShouldClosePaneAsync(pane) ? Ntilde.Shell.Mux.SharedCloseChoice.Close : Ntilde.Shell.Mux.SharedCloseChoice.Cancel;
        }

        private async Task<Ntilde.Shell.Mux.SharedCloseChoice> ShowSharedCloseDialogAsync(int others)
        {
            var choice = Ntilde.Shell.Mux.SharedCloseChoice.Cancel;
            var dialog = CreateThemedDialogWindow("Close Shared Shell", 480, 200, canResize: false);
            string who = others == 1 ? "1 other window is" : $"{others} other windows are";
            var cancel = new Button { Content = "Cancel", Width = 92 };
            var detach = new Button { Content = "Detach", Width = 92 };
            var close = new Button { Content = "Close (ends it)", Width = 130 };
            cancel.Click += (_, _) => { choice = Ntilde.Shell.Mux.SharedCloseChoice.Cancel; dialog.Close(); };
            detach.Click += (_, _) => { choice = Ntilde.Shell.Mux.SharedCloseChoice.Detach; dialog.Close(); };
            close.Click += (_, _) => { choice = Ntilde.Shell.Mux.SharedCloseChoice.Close; dialog.Close(); };
            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = $"{who} attached to this shell.", FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = "Close ends the shell for every window. Detach closes only this pane and keeps the shell running.", TextWrapping = TextWrapping.Wrap },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancel, detach, close },
                        },
                    },
                },
            };

            await dialog.ShowDialog(this);
            return choice;
        }
```

Turn `ClosePaneAsync` into a thin forwarder, and give its body a disposition:

```csharp
        private Task<bool> ClosePaneAsync(TerminalPane pane, bool skipConfirm = false) =>
            ClosePaneCoreAsync(pane, skipConfirm, Ntilde.Shell.Mux.PaneDisposition.EndSession);

        private async Task<bool> ClosePaneCoreAsync(TerminalPane pane, bool skipConfirm, Ntilde.Shell.Mux.PaneDisposition requested)
        {
            // ... the existing body, with these three changes:
            //
            // (1) replace
            //         if (!skipConfirm && !await ShouldClosePaneAsync(paneToClose)) { FocusPaneTerminal(paneToClose, defer: true); return false; }
            //     with
            Ntilde.Shell.Mux.PaneDisposition disposition = requested;
            if (!skipConfirm)
            {
                Ntilde.Shell.Mux.SharedCloseChoice choice = await DecidePaneCloseAsync(paneToClose);
                if (choice == Ntilde.Shell.Mux.SharedCloseChoice.Cancel)
                {
                    FocusPaneTerminal(paneToClose, defer: true);
                    return false;
                }

                if (choice == Ntilde.Shell.Mux.SharedCloseChoice.Detach) disposition = Ntilde.Shell.Mux.PaneDisposition.Detach;
            }
            //
            // (2) in the split branch: DisposeControlTree(paneToClose, disposition);
            //
            // (3) the fallback: return await CloseTabCoreAsync(paneTab, skipProcessChecks: true, disposition);
        }
```

Do the same for `CloseTabAsync`, and route its per-pane decisions through `DecidePaneCloseAsync`:

```csharp
        private Task<bool> CloseTabAsync(TabItem tab, bool skipProcessChecks = false) =>
            CloseTabCoreAsync(tab, skipProcessChecks, Ntilde.Shell.Mux.PaneDisposition.EndSession);

        private async Task<bool> CloseTabCoreAsync(TabItem tab, bool skipProcessChecks, Ntilde.Shell.Mux.PaneDisposition requested)
        {
            if (_closeTabInProgress) return false;
            _closeTabInProgress = true;

            try
            {
                var tabState = GetOrCreateTabState(tab);
                if (tabState.IsProtected) return false;
                if (_paneZoomStateByTab.ContainsKey(tab)) ExitPaneZoom(tab, publishEvent: true);

                var layoutRoot = GetLayoutRootForTab(tab);
                var detach = new HashSet<TerminalPane>();
                if (!skipProcessChecks && layoutRoot != null)
                {
                    foreach (var pane in EnumeratePanes(layoutRoot))
                    {
                        Ntilde.Shell.Mux.SharedCloseChoice choice = await DecidePaneCloseAsync(pane);
                        if (choice == Ntilde.Shell.Mux.SharedCloseChoice.Cancel)
                        {
                            UpdateActivePane(pane);
                            FocusPaneTerminal(pane, defer: true);
                            return false;
                        }

                        if (choice == Ntilde.Shell.Mux.SharedCloseChoice.Detach) detach.Add(pane);
                    }
                }

                PublishPaneEvent(tab, ResolvePaneForTab(tab), PaneAuditEventKind.Close, "tab");
                CloseTabCore(tab, requested, detach);
                return true;
            }
            finally
            {
                _closeTabInProgress = false;
            }
        }
```

`CloseTab(TabItem ti)` becomes `CloseTabCore(ti, Ntilde.Shell.Mux.PaneDisposition.EndSession, null);`. The existing body moves into `CloseTabCore(TabItem ti, Ntilde.Shell.Mux.PaneDisposition disposition, IReadOnlySet<TerminalPane>? detach)`, and its dispose line becomes `if (ti.Content is Control content) DisposeControlTree(content, disposition, detach);`.

`DisposeControlTree` takes the per-pane override:

```csharp
        private void DisposeControlTree(Control control, Ntilde.Shell.Mux.PaneDisposition disposition = Ntilde.Shell.Mux.PaneDisposition.EndSession, IReadOnlySet<TerminalPane>? detach = null)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => DisposeControlTree(control, disposition, detach));
                return;
            }

            if (control is TerminalPane pane)
            {
                UnwirePane(pane);
                var session = pane.DetachFromUiThread();
                Ntilde.Shell.Mux.PaneDisposition effective = detach?.Contains(pane) == true ? Ntilde.Shell.Mux.PaneDisposition.Detach : disposition;
                KillMuxSessionOnClose(session, effective); // Detach: no kill; the Dispose below detaches
                // ... the existing Task.Run(session.Dispose) block, unchanged ...
            }
            else if (control is Panel panel) { foreach (var child in panel.Children) if (child is Control c) DisposeControlTree(c, disposition, detach); }
            else if (control is ContentPresenter cp && cp.Content is Control childContent) DisposeControlTree(childContent, disposition, detach);
        }
```

Add the detach command:

```csharp
        private void DetachActivePane()
        {
            if (_currentPane is { } pane) _ = DetachPaneAsync(pane);
        }

        /// <summary>"Pane: Detach" (spec §7.4): the pane closes, the shell keeps running in the daemon.</summary>
        internal async Task<bool> DetachPaneAsync(TerminalPane pane)
        {
            if (pane.Session is not Ntilde.Mux.MuxClientSession { IsConnected: true })
            {
                ShowRecordingToast("Pane: Detach", "Only a persistent shell can be detached.", null, null, autoHide: true);
                return false;
            }

            bool closed = await ClosePaneCoreAsync(pane, skipConfirm: true, Ntilde.Shell.Mux.PaneDisposition.Detach);
            if (closed && !_teardownDone)
            {
                ShowRecordingToast("Shell detached", "Shell kept running — Attach to session… to get it back", null, null, autoHide: true);
            }

            return closed;
        }
```

In `SetupCommandPalette`, inside the `_muxHost is not null` block from Task 13, add:

```csharp
                CommandRegistry.Register("Pane: Detach", "View", () => DetachActivePane(), GetEffectiveShortcutBinding("detach_pane", ""), "detach_pane");
```

In the `KeyDown` chain, next to `attach_session`:

```csharp
                if (_muxHost is not null && IsShortcut(e, "detach_pane", ""))
                {
                    RecordCommandUsage("detach_pane");
                    DetachActivePane();
                    e.Handled = true;
                    return;
                }
```

In the window's activation wiring (next to `this.Activated += (s, e) => FocusCurrentTerminal(defer: true);`), add the second half of spec §7.5:

```csharp
            this.Activated += (_, _) => _currentPane?.ReassertMuxGrid();
```

Update the `DisposeControlTree` `<param>` doc: "EndSession for a close; Detach for Pane: Detach and the shared prompt's Detach".

- [ ] **Step 5: Run the tests again.** The same filter → PASS. Then the whole mux-adjacent set: `scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~Mux|FullyQualifiedName~ShellExit|FullyQualifiedName~ClosePane|FullyQualifiedName~DeadPane"` → PASS, with no change in existing assertions.

- [ ] **Step 6: Commit** (format first): `feat(mux): detach pane and a Close/Detach/Cancel prompt for shared shells`, with the trailer.

---
### Task 15: `AnsiCellWriter` in `Ntilde.VT`

**Files:**
- Create: `src/Ntilde.VT/Export/AnsiCellWriter.cs`
- Test: `tests/Ntilde.VT.Tests/Export/AnsiCellWriterTests.cs`

**Interfaces:**
- Consumes: `TerminalBuffer.CaptureRenderSnapshot(RenderSnapshotRequest, out long)`, `RenderCellSnapshot`, `TermColor`.
- Produces:
  - `public static int AnsiCellWriter.AppendRow(StringBuilder sb, ReadOnlySpan<RenderCellSnapshot> cells, int maxCols)`
  - `public static void AnsiCellWriter.AppendSgr(StringBuilder sb, in RenderCellSnapshot cell)`
  - `public static bool AnsiCellWriter.SameStyle(in RenderCellSnapshot a, in RenderCellSnapshot b)`
  - `public const string AnsiCellWriter.Reset = "\x1b[0m"`

`TerminalExporter.ExportToAnsi` is **not** touched (spec §6.3).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Ntilde.VT.Tests/Export/AnsiCellWriterTests.cs
using System.Text;
using Ntilde.VT;
using Ntilde.VT.Export;

namespace Ntilde.VT.Tests.Export;

public sealed class AnsiCellWriterTests
{
    private static TerminalBuffer Parse(string text, int cols = 20, int rows = 3)
    {
        var buffer = new TerminalBuffer(cols, rows);
        new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null }.Process(text);
        return buffer;
    }

    private static string WriteRow(TerminalBuffer buffer, int row, int maxCols)
    {
        using TerminalRenderSnapshot snap = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = buffer.Rows, ViewportCols = buffer.Cols }, out _);
        var sb = new StringBuilder();
        AnsiCellWriter.AppendRow(sb, snap.RowsData.Array![row].Cells, maxCols);
        return sb.ToString();
    }

    [Fact]
    public void A_written_row_reproduces_text_and_attributes_when_replayed()
    {
        TerminalBuffer source = Parse("\x1b[1;31mAB\x1b[0m \x1b[38;2;10;20;30;48;5;200mC\x1b[0m \x1b[3;4;7mD\x1b[0m 界X");

        TerminalBuffer replay = Parse(WriteRow(source, 0, source.Cols));

        for (int c = 0; c < 12; c++)
        {
            TerminalCell a = source.GetCell(c, 0), b = replay.GetCell(c, 0);
            Assert.True(source.GetGrapheme(c, 0) == replay.GetGrapheme(c, 0), $"grapheme at {c}");
            Assert.True((a.IsBold, a.IsItalic, a.IsUnderline, a.IsInverse, a.IsWide, a.IsWideContinuation)
                     == (b.IsBold, b.IsItalic, b.IsUnderline, b.IsInverse, b.IsWide, b.IsWideContinuation), $"attributes at {c}");
            Assert.True((a.IsDefaultForeground, a.IsPaletteForeground, a.Fg, a.IsDefaultBackground, a.IsPaletteBackground, a.Bg)
                     == (b.IsDefaultForeground, b.IsPaletteForeground, b.Fg, b.IsDefaultBackground, b.IsPaletteBackground, b.Bg), $"colours at {c}");
        }
    }

    [Fact]
    public void A_style_change_is_a_full_reset_form_and_the_row_ends_plain()
    {
        string row = WriteRow(Parse("\x1b[1mA\x1b[0mB"), 0, 2);

        Assert.StartsWith("\x1b[0;1mA\x1b[0mB", row, StringComparison.Ordinal);
        Assert.EndsWith(AnsiCellWriter.Reset, row, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wide_cell_cut_by_the_clip_edge_is_a_space_and_the_column_count_is_exact()
    {
        TerminalBuffer source = Parse("abcd界", cols: 10, rows: 1);   // 界 occupies columns 4 and 5
        using TerminalRenderSnapshot snap = source.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = 1, ViewportCols = 10 }, out _);
        var sb = new StringBuilder();

        int written = AnsiCellWriter.AppendRow(sb, snap.RowsData.Array![0].Cells, maxCols: 5);

        Assert.Equal(5, written);
        Assert.DoesNotContain("界", sb.ToString(), StringComparison.Ordinal);
        TerminalBuffer replay = Parse(sb.ToString(), cols: 10, rows: 1);
        Assert.Equal("abcd", TerminalExporter.GetVisibleRowTexts(replay)[0]);
        Assert.Equal(" ", replay.GetGrapheme(4, 0));
    }

    [Theory]
    [InlineData("\x1b[32mx", "\x1b[0;32m")]
    [InlineData("\x1b[92mx", "\x1b[0;92m")]
    [InlineData("\x1b[38;5;123mx", "\x1b[0;38;5;123m")]
    [InlineData("\x1b[44mx", "\x1b[0;44m")]
    [InlineData("\x1b[2;9mx", "\x1b[0;2;9m")]
    public void Sgr_forms(string input, string expectedPrefix) =>
        Assert.StartsWith(expectedPrefix, WriteRow(Parse(input), 0, 1), StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run them to see them fail.** `scripts/build.ps1 test tests/Ntilde.VT.Tests --filter "FullyQualifiedName~AnsiCellWriterTests"` → compile error: `AnsiCellWriter` is missing.

- [ ] **Step 3: Implement**

```csharp
// src/Ntilde.VT/Export/AnsiCellWriter.cs
using System;
using System.Text;

namespace Ntilde.VT.Export
{
    /// <summary>
    /// Serialises <see cref="RenderCellSnapshot"/> rows back to ANSI text, for a renderer that paints a
    /// terminal into another terminal (the mux text client, Phase 3 spec §6.3). Pure text generation:
    /// no I/O, no cursor movement, no erase - the caller positions each row.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="TerminalExporter.ExportToAnsi"/>, whose exact output (a
    /// newline-joined, trimmed whole-screen dump over TerminalCells) the "Export Snapshot (ANSI)"
    /// command relies on. A style change is written as a full <c>SGR 0;…</c>, never a delta: a
    /// few more bytes, no state to get wrong.
    /// </remarks>
    public static class AnsiCellWriter
    {
        public const string Reset = "\x1b[0m";

        /// <summary>
        /// Appends columns [0, <paramref name="maxCols"/>) of one row and returns how many columns it
        /// wrote. Continuation halves are skipped (the wide cell before them covers them); a wide cell
        /// whose second half would fall at or past <paramref name="maxCols"/>, and an orphan
        /// continuation, are written as a space so the column count stays exact. Ends with SGR 0.
        /// </summary>
        public static int AppendRow(StringBuilder sb, ReadOnlySpan<RenderCellSnapshot> cells, int maxCols)
        {
            ArgumentNullException.ThrowIfNull(sb);
            int limit = Math.Min(maxCols, cells.Length);
            bool haveStyle = false;
            RenderCellSnapshot current = default;
            int col = 0;
            while (col < limit)
            {
                RenderCellSnapshot cell = cells[col];
                bool wide = cell.IsWide && !cell.IsWideContinuation;
                string text;
                int width;
                if (cell.IsWideContinuation || (wide && col + 1 >= limit))
                {
                    text = " ";
                    width = 1;
                }
                else
                {
                    text = cell.Text ?? (cell.Character == '\0' ? " " : cell.Character.ToString());
                    width = wide ? 2 : 1;
                }

                if (!haveStyle || !SameStyle(current, cell))
                {
                    AppendSgr(sb, cell);
                    current = cell;
                    haveStyle = true;
                }

                sb.Append(text);
                col += width;
            }

            if (col > 0) sb.Append(Reset);
            return col;
        }

        /// <summary>The cell's complete style as one <c>ESC [ 0 ; … m</c>.</summary>
        public static void AppendSgr(StringBuilder sb, in RenderCellSnapshot cell)
        {
            ArgumentNullException.ThrowIfNull(sb);
            sb.Append("\x1b[0");
            if (cell.IsBold) sb.Append(";1");
            if (cell.IsFaint) sb.Append(";2");
            if (cell.IsItalic) sb.Append(";3");
            if (cell.IsUnderline) sb.Append(";4");
            if (cell.IsBlink) sb.Append(";5");
            if (cell.IsInverse) sb.Append(";7");
            if (cell.IsHidden) sb.Append(";8");
            if (cell.IsStrikethrough) sb.Append(";9");
            if (!cell.IsDefaultForeground) AppendColor(sb, cell.FgIndex, cell.Foreground, foreground: true);
            if (!cell.IsDefaultBackground) AppendColor(sb, cell.BgIndex, cell.Background, foreground: false);
            sb.Append('m');
        }

        public static bool SameStyle(in RenderCellSnapshot a, in RenderCellSnapshot b) =>
            a.IsBold == b.IsBold && a.IsFaint == b.IsFaint && a.IsItalic == b.IsItalic && a.IsUnderline == b.IsUnderline
            && a.IsBlink == b.IsBlink && a.IsInverse == b.IsInverse && a.IsHidden == b.IsHidden && a.IsStrikethrough == b.IsStrikethrough
            && a.IsDefaultForeground == b.IsDefaultForeground && a.IsDefaultBackground == b.IsDefaultBackground
            && a.FgIndex == b.FgIndex && a.BgIndex == b.BgIndex
            && (a.IsDefaultForeground || a.FgIndex >= 0 || a.Foreground == b.Foreground)
            && (a.IsDefaultBackground || a.BgIndex >= 0 || a.Background == b.Background);

        private static void AppendColor(StringBuilder sb, short index, TermColor rgb, bool foreground)
        {
            if (index >= 0 && index < 8)
            {
                sb.Append(';').Append((foreground ? 30 : 40) + index);
            }
            else if (index >= 8 && index < 16)
            {
                sb.Append(';').Append((foreground ? 90 : 100) + index - 8);
            }
            else if (index >= 16)
            {
                sb.Append(foreground ? ";38;5;" : ";48;5;").Append(index);
            }
            else
            {
                sb.Append(foreground ? ";38;2;" : ";48;2;").Append(rgb.R).Append(';').Append(rgb.G).Append(';').Append(rgb.B);
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests again.** `scripts/build.ps1 test tests/Ntilde.VT.Tests` → all PASS, including the existing `TerminalExporter` tests, which are unchanged.

- [ ] **Step 5: Commit** (format first): `feat(vt): AnsiCellWriter serialises render-snapshot rows to positioned ANSI`, with the trailer.

---

### Task 16: Text client model and renderer

**Files:**
- Create: `src/Ntilde.Mux/TextClient/TextClientModel.cs`
- Create: `src/Ntilde.Mux/TextClient/TextClientRenderer.cs`
- Test: `tests/Ntilde.Mux.Tests/TextClient/TextClientRendererTests.cs`
- Test: `tests/Ntilde.Mux.Tests/TextClient/TextClientModelTests.cs`

**Interfaces:**
- Consumes: `AnsiCellWriter` (Task 15), `TerminalBuffer.CaptureRenderSnapshot`, `TerminalBuffer.Modes`, `TerminalBuffer.IsAltScreenActive`, `TerminalStateTransfer.Restore`, and `MuxClientSession.SnapshotReceived` / `OnOutputReceived` / `StreamResize` / `ForceConPtyFiltering`.
- Produces:
  - `public sealed class TextClientModel(MuxClientSession session)`, with `Session`, `Buffer`, `Parser` and `event Action? Changed` (raised on the delivery thread)
  - `public sealed class TextClientRenderer(TerminalBuffer buffer)`, with:
    - `bool ReadOnly { get; init; }`
    - `string Render(int consoleCols, int consoleRows)`
    - `void Invalidate()`
    - `const string EnterSequence = "\x1b[?1049h\x1b[H\x1b[2J"`
    - `const string LeaveSequence = "\x1b[0m\x1b[?25h\x1b[?1l\x1b[?2004l\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l\x1b[?1049l"`

- [ ] **Step 1: Write the failing renderer tests.** The "outer terminal" is our own VT buffer and parser, fed with the renderer's output. It is the independent judge of "equals an ANSI dump of the snapshot".

```csharp
// tests/Ntilde.Mux.Tests/TextClient/TextClientRendererTests.cs
using System.Diagnostics;
using System.Text;
using Ntilde.Mux.TextClient;
using Ntilde.VT;
using Ntilde.VT.Export;

namespace Ntilde.Mux.Tests.TextClient;

public sealed class TextClientRendererTests
{
    private sealed class Session
    {
        public Session(int cols, int rows)
        {
            Buffer = new TerminalBuffer(cols, rows);
            Parser = new AnsiParser(Buffer, forceConPtyFiltering: false) { ImageDecoder = null };
        }

        public TerminalBuffer Buffer { get; }
        public AnsiParser Parser { get; }
        public void Feed(string text) => Parser.Process(text);
    }

    /// <summary>An independent terminal fed with everything the renderer wrote.</summary>
    private static TerminalBuffer Outer(string output, int cols, int rows)
    {
        var outer = new TerminalBuffer(cols, rows);
        new AnsiParser(outer, forceConPtyFiltering: false) { ImageDecoder = null }.Process(output);
        return outer;
    }

    private static string[] Rows(TerminalBuffer b) => TerminalExporter.GetVisibleRowTexts(b);

    [Fact]
    public void The_first_frame_reproduces_the_screen()
    {
        var s = new Session(80, 24);
        s.Feed("first line\r\n\x1b[1;31mred bold\x1b[0m and plain\r\n\x1b[38;2;1;2;3mtrue\x1b[0m");
        var renderer = new TextClientRenderer(s.Buffer);

        TerminalBuffer outer = Outer(renderer.Render(80, 24), 80, 24);

        Assert.Equal(Rows(s.Buffer), Rows(outer));
        Assert.True(outer.GetCell(0, 1).IsBold);
        Assert.True(outer.GetCell(0, 1).IsPaletteForeground);
        Assert.Equal(1u, outer.GetCell(0, 1).Fg);
    }

    [Fact]
    public void Only_changed_rows_are_repainted()
    {
        var s = new Session(80, 24);
        s.Feed("first line\r\nsecond line");
        var renderer = new TextClientRenderer(s.Buffer);
        string frame1 = renderer.Render(80, 24);

        s.Feed("\x1b[6;1Hsixth");
        string frame2 = renderer.Render(80, 24);

        Assert.DoesNotContain("\x1b[2J", frame2, StringComparison.Ordinal);
        Assert.Contains("\x1b[6;1H", frame2, StringComparison.Ordinal);
        Assert.DoesNotContain("first line", frame2, StringComparison.Ordinal);
        Assert.Equal(Rows(s.Buffer), Rows(Outer(frame1 + frame2, 80, 24)));
        Assert.Equal(string.Empty, renderer.Render(80, 24)); // nothing changed, nothing written
    }

    [Fact]
    public void An_inner_alt_screen_switch_repaints_fully_without_nesting_1049()
    {
        var s = new Session(80, 24);
        s.Feed("shell prompt $ ");
        var renderer = new TextClientRenderer(s.Buffer);
        string frames = renderer.Render(80, 24);

        s.Feed("\x1b[?1049h\x1b[Hvim screen");
        string toAlt = renderer.Render(80, 24);
        frames += toAlt;
        Assert.Contains("\x1b[2J", toAlt, StringComparison.Ordinal);
        Assert.DoesNotContain("1049", toAlt, StringComparison.Ordinal);
        Assert.Equal(Rows(s.Buffer), Rows(Outer(frames, 80, 24)));

        s.Feed("\x1b[?1049l");
        string back = renderer.Render(80, 24);
        frames += back;
        Assert.DoesNotContain("1049", back, StringComparison.Ordinal);
        Assert.Contains("shell prompt $", Rows(Outer(frames, 80, 24))[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_larger_session_is_clipped_with_a_status_line()
    {
        var s = new Session(120, 40);
        var text = new StringBuilder();
        for (int i = 0; i < 40; i++) text.Append("line ").Append(i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).Append(i < 39 ? "\r\n" : "");
        s.Feed(text.ToString());
        s.Feed("\x1b[11;1H");                                  // cursor on row 10: the window stays at the top
        var renderer = new TextClientRenderer(s.Buffer);

        TerminalBuffer outer = Outer(renderer.Render(80, 24), 80, 24);

        Assert.Equal("line 00", Rows(outer)[0]);
        Assert.Equal("line 22", Rows(outer)[22]);
        Assert.StartsWith("session is 120x40, this terminal is 80x24", Rows(outer)[23], StringComparison.Ordinal);
    }

    [Fact]
    public void The_clip_window_follows_the_cursor()
    {
        var s = new Session(80, 40);
        var text = new StringBuilder();
        for (int i = 0; i < 40; i++) text.Append("row ").Append(i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).Append(i < 39 ? "\r\n" : "");
        s.Feed(text.ToString());
        s.Feed("\x1b[36;1H");                                  // cursor row 35; 23 visible rows → top = 13
        var renderer = new TextClientRenderer(s.Buffer);

        TerminalBuffer outer = Outer(renderer.Render(80, 24), 80, 24);

        Assert.Equal("row 13", Rows(outer)[0]);
        Assert.Equal("row 35", Rows(outer)[22]);
    }

    [Fact]
    public void Read_only_always_shows_its_status_line()
    {
        var s = new Session(80, 24);
        var renderer = new TextClientRenderer(s.Buffer) { ReadOnly = true };

        TerminalBuffer outer = Outer(renderer.Render(80, 24), 80, 24);

        Assert.StartsWith("read-only", Rows(outer)[23], StringComparison.Ordinal);
    }

    [Fact]
    public void Modes_follow_the_buffer_and_are_emitted_only_on_change()
    {
        var s = new Session(80, 24);
        var renderer = new TextClientRenderer(s.Buffer);
        renderer.Render(80, 24);

        s.Feed("\x1b[?1h\x1b[?2004h\x1b[?25l");
        string on = renderer.Render(80, 24);
        s.Feed("x");
        string again = renderer.Render(80, 24);

        Assert.Contains("\x1b[?1h", on, StringComparison.Ordinal);
        Assert.Contains("\x1b[?2004h", on, StringComparison.Ordinal);
        Assert.Contains("\x1b[?25l", on, StringComparison.Ordinal);
        Assert.DoesNotContain("\x1b[?1h", again, StringComparison.Ordinal);
        Assert.DoesNotContain("\x1b[?2004h", again, StringComparison.Ordinal);
    }

    [Fact]
    public void Mouse_modes_are_mirrored_only_while_unclipped()
    {
        var s = new Session(80, 24);
        s.Feed("\x1b[?1000h\x1b[?1006h");
        var renderer = new TextClientRenderer(s.Buffer);

        string fits = renderer.Render(80, 24);
        string clipped = renderer.Render(60, 20);

        Assert.Contains("\x1b[?1000h", fits, StringComparison.Ordinal);
        Assert.Contains("\x1b[?1006h", fits, StringComparison.Ordinal);
        Assert.Contains("\x1b[?1000l", clipped, StringComparison.Ordinal);
        Assert.Contains("\x1b[?1006l", clipped, StringComparison.Ordinal);
    }

    [Fact]
    public void Repaint_cost_for_a_redraw_storm_is_measured()
    {
        var s = new Session(200, 60);
        var renderer = new TextClientRenderer(s.Buffer);
        var rng = new Random(42);
        renderer.Render(200, 60);
        var sw = new Stopwatch();
        const int frames = 100;
        for (int f = 0; f < frames; f++)
        {
            var screen = new StringBuilder("\x1b[H");
            for (int r = 0; r < 60; r++)
            {
                screen.Append("\x1b[3").Append(rng.Next(8)).Append('m');
                for (int c = 0; c < 200; c++) screen.Append((char)('a' + rng.Next(26)));
                if (r < 59) screen.Append("\r\n");
            }

            s.Feed(screen.ToString());
            sw.Start();
            renderer.Render(200, 60);
            sw.Stop();
        }

        double avgMs = sw.Elapsed.TotalMilliseconds / frames;
        TestContext.Current.TestOutputHelper?.WriteLine($"[mux-phase3] text client full-screen repaint (200x60): {avgMs:F2} ms/frame");
        Assert.True(avgMs < 50, $"a full-screen repaint took {avgMs:F2} ms"); // a generous ceiling; the number is for the PR
    }
}
```

```csharp
// tests/Ntilde.Mux.Tests/TextClient/TextClientModelTests.cs
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.TextClient;

namespace Ntilde.Mux.Tests.TextClient;

public sealed class TextClientModelTests
{
    [Fact]
    public async Task The_model_equals_the_mux_after_snapshot_output_and_resize()
    {
        using var host = new MuxTestHost();
        MuxClient c = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c);
        host.Fake(id).Emit("before attach\r\n\x1b[1mbold\x1b[0m\r\n");
        MuxClientSession session = c.OpenSession(id, "scripted");
        var model = new TextClientModel(session);
        int changes = 0;
        model.Changed += () => Interlocked.Increment(ref changes);

        await session.AttachAsync(0, MuxTestHost.DefaultPresentation, TestContext.Current.CancellationToken);
        host.Fake(id).Emit("after attach\r\n\x1b[c");               // a device query: the model must never answer it
        host.Mux(id).PostResize(100, 30, null);
        await host.SettleAsync(id, c);

        Ntilde.VT.Tests.StateTransfer.TerminalStateAssert.AssertEquivalent("[text client model]",
            host.Mux(id).Buffer, host.Mux(id).Parser, model.Buffer, model.Parser);
        Assert.True(Volatile.Read(ref changes) >= 3);
        await TestWait.UntilAsync(() => !host.Fake(id).SentInput.IsEmpty, "the mux answered the query");
        Assert.Single(host.Fake(id).SentInput);                     // once: the mux's answer, never the model's
    }
}
```

- [ ] **Step 2: Run them to see them fail.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~TextClient"` → compile errors: the types are missing.

- [ ] **Step 3: Implement the model**

```csharp
// src/Ntilde.Mux/TextClient/TextClientModel.cs
using Ntilde.VT;

namespace Ntilde.Mux.TextClient;

/// <summary>
/// The text client's own copy of a session (Phase 1's ClientPaneModel, generalised; spec §6.1):
/// restore on snapshot, parse output, resize in stream - all on the client's delivery thread. The
/// parser's replies are discarded: the mux has already answered every device query.
/// </summary>
public sealed class TextClientModel
{
    public TextClientModel(MuxClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        Buffer = new TerminalBuffer(80, 24);
        Parser = new AnsiParser(Buffer, session.ForceConPtyFiltering)
        {
            ImageDecoder = null,
            AllowNativeKittyGraphics = false,
        };
        Parser.OnResponse = static _ => { };
        session.SnapshotReceived += OnSnapshot;
        session.OnOutputReceived += OnOutput;
        session.StreamResize += OnResize;
    }

    public MuxClientSession Session { get; }
    public TerminalBuffer Buffer { get; }
    public AnsiParser Parser { get; }

    /// <summary>Raised on the delivery thread after every change to <see cref="Buffer"/>; the render thread's wake-up.</summary>
    public event Action? Changed;

    private void OnSnapshot(TerminalStateSnapshot snapshot)
    {
        TerminalStateTransfer.Restore(Buffer, Parser, snapshot);
        Changed?.Invoke();
    }

    private void OnOutput(string text)
    {
        Parser.Process(text);
        Changed?.Invoke();
    }

    private void OnResize(int cols, int rows)
    {
        Buffer.Resize(cols, rows);
        Changed?.Invoke();
    }
}
```

- [ ] **Step 4: Implement the renderer**

```csharp
// src/Ntilde.Mux/TextClient/TextClientRenderer.cs
using System.Globalization;
using System.Text;
using Ntilde.VT;
using Ntilde.VT.Export;

namespace Ntilde.Mux.TextClient;

/// <summary>
/// Turns the model's buffer into output for a foreign terminal (Phase 3 spec §6.2). No I/O: the
/// caller writes what <see cref="Render"/> returns. The renderer is its buffer's only
/// CaptureRenderSnapshot consumer, so the snapshot's dirty spans describe exactly what changed since
/// the previous frame.
/// </summary>
public sealed class TextClientRenderer
{
    /// <summary>Written once on attach: the outer terminal's alternate screen, cleared.</summary>
    public const string EnterSequence = "\x1b[?1049h\x1b[H\x1b[2J";

    /// <summary>Written on every exit path: plain SGR, cursor shown, modes off, back to the outer main screen.</summary>
    public const string LeaveSequence = "\x1b[0m\x1b[?25h\x1b[?1l\x1b[?2004l\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l\x1b[?1049l";

    private readonly TerminalBuffer _buffer;
    private bool _haveFrame;
    private int _lastConsoleCols, _lastConsoleRows, _lastSessionCols, _lastSessionRows, _lastTop;
    private bool _lastAltScreen;
    private string? _lastStatus;
    private (int Row, int Col) _lastCursor = (-1, -1);
    private bool? _appCursor, _bracketed, _cursorVisible, _mouse1000, _mouse1002, _mouse1003, _mouse1006;

    public TextClientRenderer(TerminalBuffer buffer) => _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));

    /// <summary>Read-only attach: a permanent status line says so.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The next <see cref="Render"/> repaints everything (console resized, or anything else invalidated the outer screen).</summary>
    public void Invalidate() => _haveFrame = false;

    /// <summary>The bytes that bring the outer terminal up to date; empty when nothing changed.</summary>
    public string Render(int consoleCols, int consoleRows)
    {
        consoleCols = Math.Max(1, consoleCols);
        consoleRows = Math.Max(1, consoleRows);
        using TerminalRenderSnapshot snap = _buffer.CaptureRenderSnapshot(
            new RenderSnapshotRequest { ViewportRows = _buffer.Rows, ViewportCols = _buffer.Cols }, out _);
        int sessionRows = snap.ViewportRows;
        int sessionCols = snap.ViewportCols;
        string? status = BuildStatus(sessionCols, sessionRows, consoleCols, consoleRows);
        int visibleRows = Math.Max(1, consoleRows - (status is null ? 0 : 1));
        int visibleCols = consoleCols;
        int top = Math.Clamp(snap.CursorRow - visibleRows + 1, 0, Math.Max(0, sessionRows - visibleRows));
        bool alt = _buffer.IsAltScreenActive;

        bool full = !_haveFrame
            || consoleCols != _lastConsoleCols || consoleRows != _lastConsoleRows
            || sessionCols != _lastSessionCols || sessionRows != _lastSessionRows
            || top != _lastTop || alt != _lastAltScreen || !string.Equals(status, _lastStatus, StringComparison.Ordinal);

        var sb = new StringBuilder();
        bool painted = false;
        if (full)
        {
            sb.Append("\x1b[?25l\x1b[H\x1b[0m\x1b[2J");
            _cursorVisible = false;
            int rows = Math.Min(visibleRows, sessionRows - top);
            for (int r = 0; r < rows; r++) AppendRow(sb, snap, top + r, r, visibleCols, clearFirst: false);
            if (status is not null)
            {
                sb.Append(Cup(consoleRows, 1)).Append("\x1b[0;7m").Append(status.Length <= consoleCols ? status : status[..consoleCols]).Append(AnsiCellWriter.Reset);
            }

            painted = true;
        }
        else if (snap.DirtySpans.Array is { } spans)
        {
            var dirtyRows = new SortedSet<int>();
            for (int i = 0; i < snap.DirtySpans.Length; i++)
            {
                int row = spans[i].Row;
                if (row >= top && row < top + visibleRows) dirtyRows.Add(row);
            }

            foreach (int row in dirtyRows) AppendRow(sb, snap, row, row - top, visibleCols, clearFirst: true);
            painted = dirtyRows.Count > 0;
        }

        AppendModes(sb, unclipped: top == 0 && sessionCols <= visibleCols && sessionRows <= visibleRows);

        int cursorRow = snap.CursorRow - top;
        int cursorCol = Math.Min(snap.CursorCol, visibleCols - 1);
        bool cursorShown = _buffer.Modes.IsCursorVisible && cursorRow >= 0 && cursorRow < visibleRows && snap.CursorCol < visibleCols;
        if (cursorShown && (painted || (cursorRow, cursorCol) != _lastCursor)) sb.Append(Cup(cursorRow + 1, cursorCol + 1));
        _lastCursor = cursorShown ? (cursorRow, cursorCol) : (-1, -1);
        if (_cursorVisible != cursorShown)
        {
            sb.Append(cursorShown ? "\x1b[?25h" : "\x1b[?25l");
            _cursorVisible = cursorShown;
        }

        _haveFrame = true;
        (_lastConsoleCols, _lastConsoleRows, _lastSessionCols, _lastSessionRows, _lastTop) = (consoleCols, consoleRows, sessionCols, sessionRows, top);
        _lastAltScreen = alt;
        _lastStatus = status;
        return sb.ToString();
    }

    private string? BuildStatus(int sessionCols, int sessionRows, int consoleCols, int consoleRows)
    {
        bool tooBig = sessionCols > consoleCols || sessionRows > consoleRows - (ReadOnly ? 1 : 0);
        if (!tooBig && !ReadOnly) return null;
        string size = string.Create(CultureInfo.InvariantCulture,
            $"session is {sessionCols}x{sessionRows}, this terminal is {consoleCols}x{consoleRows} — resize to fit");
        if (!ReadOnly) return size;
        return tooBig ? "read-only · " + size : "read-only · Ctrl+\\ d to detach";
    }

    /// <summary>
    /// One session row at one screen row. An incremental repaint erases the line FIRST (<c>CSI 2K</c>):
    /// an <c>EL</c> after the text would erase the last cell when the row fills to the pending-wrap column.
    /// </summary>
    private static void AppendRow(StringBuilder sb, in TerminalRenderSnapshot snap, int sessionRow, int screenRow, int visibleCols, bool clearFirst)
    {
        sb.Append(Cup(screenRow + 1, 1));
        if (clearFirst) sb.Append("\x1b[0m\x1b[2K");
        RenderRowSnapshot row = snap.RowsData.Array![sessionRow];
        if (row.Cells is { Length: > 0 } cells) AnsiCellWriter.AppendRow(sb, cells, Math.Min(visibleCols, row.Cols));
    }

    /// <summary>Only on change against what was last EMITTED (unknown at first, so the first frame states each one).</summary>
    private void AppendModes(StringBuilder sb, bool unclipped)
    {
        ModeState m = _buffer.Modes;
        Toggle(sb, ref _appCursor, m.IsApplicationCursorKeys, 1);
        Toggle(sb, ref _bracketed, m.IsBracketedPasteMode, 2004);
        // Mirrored only while the window is unclipped: the outer terminal's mouse reports then carry
        // the inner coordinates and go through as ordinary input (spec §6.6).
        Toggle(sb, ref _mouse1000, unclipped && m.MouseModeX10, 1000);
        Toggle(sb, ref _mouse1002, unclipped && m.MouseModeButtonEvent, 1002);
        Toggle(sb, ref _mouse1003, unclipped && m.MouseModeAnyEvent, 1003);
        Toggle(sb, ref _mouse1006, unclipped && m.MouseModeSGR, 1006);
    }

    private static void Toggle(StringBuilder sb, ref bool? last, bool want, int mode)
    {
        if (last == want) return;
        sb.Append("\x1b[?").Append(mode.ToString(CultureInfo.InvariantCulture)).Append(want ? 'h' : 'l');
        last = want;
    }

    private static string Cup(int row, int col) => string.Create(CultureInfo.InvariantCulture, $"\x1b[{row};{col}H");
}
```

- [ ] **Step 5: Run the tests again.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~TextClient"` → PASS. Record the repaint number from the test output for the PR (report item 5). Then `scripts/build.ps1 build -c Release src/Ntilde.Mux` → 0 warnings.

- [ ] **Step 6: Commit** (format first): `feat(mux): text client model and dirty-row renderer`, with the trailer.

---

### Task 17: Text client session loop: threads, detach chord, exit codes, restore on every path

**Files:**
- Create: `src/Ntilde.Mux/TextClient/IConsoleSurface.cs` (the interface, and `ConsoleUnavailableException`)
- Create: `src/Ntilde.Mux/TextClient/DetachChord.cs`
- Create: `src/Ntilde.Mux/TextClient/TextClientSession.cs` (with `TextClientOptions` and `TextClientExit`)
- Create: `tests/Ntilde.Mux.Tests/Support/FakeConsoleSurface.cs`
- Test: `tests/Ntilde.Mux.Tests/TextClient/DetachChordTests.cs`
- Test: `tests/Ntilde.Mux.Tests/TextClient/TextClientTests.cs`

**Interfaces:**
- Consumes: `TextClientModel` and `TextClientRenderer` (Task 16), and `MuxClientSession.AttachAsync(MuxAttachMode, …)`, `OnExit`, `KilledElsewhere`, `Faulted`, `Disconnected` (Task 9).
- Produces:
  - `public interface IConsoleSurface : IDisposable { (int Cols, int Rows) Size { get; } void EnterRawMode(); void RestoreMode(); void Write(string text); int Read(char[] buffer); event Action? Resized; }`
  - `public sealed class ConsoleUnavailableException : Exception`
  - `public sealed class DetachChord { const char Prefix = '\u001c'; bool Feed(ReadOnlySpan<char> input, StringBuilder passThrough); }`
  - `public sealed class TextClientOptions { bool ReadOnly; TimeSpan RenderInterval = 16 ms; bool HandleSignals; }`
  - `public enum TextClientExit { Detached, SessionExited, Faulted, Disconnected, AttachFailed, Error }`
  - `public sealed class TextClientSession : IDisposable`, with:
    - the constructor `TextClientSession(MuxClient client, Guid sessionId, IConsoleSurface console, TextClientOptions? options = null)`
    - `int Run(TextWriter stderr)`
    - `void RequestStop(TextClientExit reason, string? detail = null)`
    - `TextClientExit? ExitReason`

- [ ] **Step 1: Add the interface** (needed by the fake):

```csharp
// src/Ntilde.Mux/TextClient/IConsoleSurface.cs
namespace Ntilde.Mux.TextClient;

/// <summary>The terminal the text client draws on (spec §6.1). Real ones: Windows and Unix surfaces; tests: a fake.</summary>
public interface IConsoleSurface : IDisposable
{
    /// <summary>The visible grid, cols x rows.</summary>
    (int Cols, int Rows) Size { get; }

    /// <summary>Raw input (no line editing, echo or signal keys) and VT output.</summary>
    void EnterRawMode();

    /// <summary>Back to the modes found before <see cref="EnterRawMode"/>. Idempotent; safe from any thread.</summary>
    void RestoreMode();

    void Write(string text);

    /// <summary>Blocks for input; returns the chars read, 0 when input is closed.</summary>
    int Read(char[] buffer);

    /// <summary>The grid changed (SIGWINCH, or a poll on Windows). Any thread.</summary>
    event Action? Resized;
}

/// <summary>Stdin/stdout is not an interactive terminal (a pipe, a redirect, no console).</summary>
public sealed class ConsoleUnavailableException : Exception
{
    public ConsoleUnavailableException() : this("An interactive terminal is required.") { }
    public ConsoleUnavailableException(string message) : base(message) { }
    public ConsoleUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
```

- [ ] **Step 2: Add the fake console**

```csharp
// tests/Ntilde.Mux.Tests/Support/FakeConsoleSurface.cs
using System.Collections.Concurrent;
using System.Text;
using Ntilde.Mux.TextClient;
using Ntilde.VT;
using Ntilde.VT.Export;

namespace Ntilde.Mux.Tests.Support;

/// <summary>A scripted terminal: records output, feeds typed input, and tracks raw mode for the restore assertions.</summary>
internal sealed class FakeConsoleSurface : IConsoleSurface
{
    private readonly object _gate = new();
    private readonly StringBuilder _output = new();
    private readonly BlockingCollection<string> _input = new();
    private string _pending = string.Empty;
    private int _writes;

    public FakeConsoleSurface(int cols = 80, int rows = 24) => Size = (cols, rows);

    public (int Cols, int Rows) Size { get; private set; }
    public bool IsRaw { get { lock (_gate) return _raw; } }
    public int EnterRawCount { get { lock (_gate) return _enterRaw; } }
    public int RestoreCount { get { lock (_gate) return _restore; } }

    /// <summary>1-based: that <see cref="Write"/> call throws <see cref="IOException"/> (0 = never).</summary>
    public int ThrowOnWriteNumber { get; set; }

    public string Output { get { lock (_gate) return _output.ToString(); } }

    public event Action? Resized;

    private bool _raw;
    private int _enterRaw;
    private int _restore;

    public void EnterRawMode()
    {
        lock (_gate)
        {
            _raw = true;
            _enterRaw++;
        }
    }

    public void RestoreMode()
    {
        lock (_gate)
        {
            if (_raw) _restore++;
            _raw = false;
        }
    }

    public void Write(string text)
    {
        lock (_gate)
        {
            _writes++;
            if (ThrowOnWriteNumber > 0 && _writes == ThrowOnWriteNumber) throw new IOException("scripted console failure");
            _output.Append(text);
        }
    }

    public int Read(char[] buffer)
    {
        if (_pending.Length == 0)
        {
            try
            {
                if (!_input.TryTake(out string? next, Timeout.Infinite)) return 0;
                _pending = next;
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
        }

        int n = Math.Min(buffer.Length, _pending.Length);
        _pending.CopyTo(0, buffer, 0, n);
        _pending = _pending[n..];
        return n;
    }

    public void Type(string text) => _input.Add(text);

    public void Resize(int cols, int rows)
    {
        Size = (cols, rows);
        Resized?.Invoke();
    }

    /// <summary>The screen an outer terminal would show after everything written so far.</summary>
    public string ScreenText()
    {
        var outer = new TerminalBuffer(Size.Cols, Size.Rows);
        new AnsiParser(outer, forceConPtyFiltering: false) { ImageDecoder = null }.Process(Output);
        return string.Join('\n', TerminalExporter.GetVisibleRowTexts(outer));
    }

    public void Dispose() => _input.CompleteAdding();
}
```

- [ ] **Step 3: Write the failing tests**

```csharp
// tests/Ntilde.Mux.Tests/TextClient/DetachChordTests.cs
using System.Text;
using Ntilde.Mux.TextClient;

namespace Ntilde.Mux.Tests.TextClient;

public sealed class DetachChordTests
{
    [Theory]
    [InlineData("abc", "abc", false)]
    [InlineData("ab\u001cd", "ab", true)]
    [InlineData("\u001cD", "", true)]
    [InlineData("\u001c\u001c", "\u001c", false)]      // Ctrl+\ Ctrl+\ sends one literal Ctrl+\
    [InlineData("\u001cx", "\u001cx", false)]          // anything else: nothing is lost
    [InlineData("\u001cdls", "", true)]                // input after the chord is not sent
    public void Feed(string input, string expectedPassThrough, bool expectedDetach)
    {
        var chord = new DetachChord();
        var pass = new StringBuilder();

        bool detach = chord.Feed(input, pass);

        Assert.Equal(expectedDetach, detach);
        Assert.Equal(expectedPassThrough, pass.ToString());
    }

    [Fact]
    public void A_chord_split_across_reads_still_detaches()
    {
        var chord = new DetachChord();
        var pass = new StringBuilder();

        Assert.False(chord.Feed("x\u001c", pass));
        Assert.True(chord.Feed("d", pass));
        Assert.Equal("x", pass.ToString());
    }
}
```

```csharp
// tests/Ntilde.Mux.Tests/TextClient/TextClientTests.cs
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.TextClient;

namespace Ntilde.Mux.Tests.TextClient;

/// <summary>Phase 3 spec §6.4, §6.5: the text client end to end over an in-memory daemon and a fake console.</summary>
public sealed class TextClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<int> RunAsync(TextClientSession client) => Task.Run(() => client.Run(TextWriter.Null), Ct);

    [Fact]
    public async Task Attach_paints_the_session_and_the_chord_detaches_with_exit_0()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        host.Fake(id).Emit("hello from the shell\r\n");
        MuxClient attacher = await host.ConnectClientAsync(new MuxClientOptions { ClientKind = "ntilde-attach" });
        using var console = new FakeConsoleSurface(80, 24);
        using var client = new TextClientSession(attacher, id, console);

        Task<int> run = RunAsync(client);
        await TestWait.UntilAsync(() => console.ScreenText().Contains("hello from the shell", StringComparison.Ordinal), "the session was painted");
        Assert.True(console.IsRaw);
        console.Type("\u001c");
        console.Type("d");

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(console.IsRaw);
        Assert.StartsWith(TextClientRenderer.EnterSequence, console.Output, StringComparison.Ordinal);
        Assert.Contains(TextClientRenderer.LeaveSequence, console.Output, StringComparison.Ordinal);
        Assert.EndsWith($"[detached from {id}]\r\n", console.Output, StringComparison.Ordinal);
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 0, "the daemon saw the detach");
        Assert.False(host.Mux(id).IsExited);
    }

    [Fact]
    public async Task Typed_input_reaches_the_session_and_two_prefixes_send_one_literal()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        MuxClient attacher = await host.ConnectClientAsync();
        using var console = new FakeConsoleSurface();
        using var client = new TextClientSession(attacher, id, console);
        Task<int> run = RunAsync(client);
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 1, "attached");

        console.Type("ls\r");
        console.Type("\u001c\u001c");

        await TestWait.UntilAsync(() => string.Concat(host.Fake(id).SentInput) == "ls\r\u001c", "the input arrived, the chord prefix once");
        console.Type("\u001cd");
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task A_session_exit_returns_1_and_prints_the_code()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        MuxClient attacher = await host.ConnectClientAsync();
        using var console = new FakeConsoleSurface();
        using var client = new TextClientSession(attacher, id, console);
        Task<int> run = RunAsync(client);
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 1, "attached");

        host.Fake(id).Exit(7);

        Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.EndsWith("[session exited with code 7]\r\n", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Killed_elsewhere_returns_1_and_says_so()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        MuxClient attacher = await host.ConnectClientAsync();
        using var console = new FakeConsoleSurface();
        using var client = new TextClientSession(attacher, id, console);
        Task<int> run = RunAsync(client);
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 1, "attached");

        await spawner.KillAsync(id, Ct);

        Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.EndsWith("[session ended from another window]\r\n", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_only_sends_no_input_requests_no_resize_and_says_so()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);            // 80x24
        MuxClient attacher = await host.ConnectClientAsync();
        using var console = new FakeConsoleSurface(100, 30);
        using var client = new TextClientSession(attacher, id, console, new TextClientOptions { ReadOnly = true });
        Task<int> run = RunAsync(client);
        await TestWait.UntilAsync(() => console.ScreenText().Contains("read-only", StringComparison.Ordinal), "the status line says read-only");

        console.Type("typed");
        console.Resize(120, 40);
        await TestWait.UntilAsync(() => console.ScreenText().Contains("read-only", StringComparison.Ordinal), "repainted after the resize");
        await attacher.PingAsync(Ct);
        await host.Mux(id).InvokeAsync(() => 0);
        await TestWait.UntilAsync(() => host.Mux(id).QueuedInputBytes == 0, "the input writer is idle");

        Assert.Empty(host.Fake(id).SentInput);
        Assert.Equal((80, 24), (host.Mux(id).Cols, host.Mux(id).Rows));
        console.Type("\u001cd");
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task A_console_resize_requests_the_new_grid()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        MuxClient attacher = await host.ConnectClientAsync();
        using var console = new FakeConsoleSurface(80, 24);
        using var client = new TextClientSession(attacher, id, console);
        Task<int> run = RunAsync(client);
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 1, "attached");

        console.Resize(100, 30);

        await TestWait.UntilAsync(() => (host.Mux(id).Cols, host.Mux(id).Rows) == (100, 30), "the session took the console's grid");
        console.Type("\u001cd");
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Theory]
    [InlineData("detach")]
    [InlineData("exit")]
    [InlineData("killed")]
    [InlineData("disconnect")]
    [InlineData("attach-failed")]
    [InlineData("write-throws")]
    [InlineData("signal")]
    public async Task Console_modes_are_restored_on_every_exit_path(string path)
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(spawner);
        MuxClient attacher = await host.ConnectClientAsync();
        using var console = new FakeConsoleSurface(80, 24);
        if (path == "write-throws") console.ThrowOnWriteNumber = 2;   // #1 is the enter sequence, #2 the first frame
        Guid target = path == "attach-failed" ? Guid.NewGuid() : id;
        using var client = new TextClientSession(attacher, target, console);

        Task<int> run = RunAsync(client);
        if (path is not ("attach-failed" or "write-throws"))
        {
            await TestWait.UntilAsync(() => console.IsRaw && host.Mux(id).AttachedClients == 1, "attached");
        }

        switch (path)
        {
            case "detach": console.Type("\u001cd"); break;
            case "exit": host.Fake(id).Exit(0); break;
            case "killed": await spawner.KillAsync(id, Ct); break;
            case "disconnect": attacher.Dispose(); break;
            case "signal": client.RequestStop(TextClientExit.Detached, "signal"); break;
        }

        int code = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.False(console.IsRaw, $"{path}: the console was left raw");
        Assert.Equal(console.EnterRawCount, console.RestoreCount);
        if (console.Output.Contains("\x1b[?1049h", StringComparison.Ordinal))
        {
            Assert.Contains("\x1b[?1049l", console.Output, StringComparison.Ordinal);
        }

        Assert.Equal(path switch { "detach" or "signal" => 0, "exit" or "killed" => 1, _ => 2 }, code);
    }
}
```

(The type is `TextClientSession`, not `TextClient`: a type named like its own namespace, `Ntilde.Mux.TextClient`, would bind to the namespace from any code under `Ntilde.Mux.*`, the test projects included.)

- [ ] **Step 4: Run them to see them fail.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~DetachChordTests|FullyQualifiedName~TextClientTests"` → compile errors: `DetachChord`, `TextClientSession`, `TextClientOptions` and `TextClientExit` are missing.

- [ ] **Step 5: Implement the chord**

```csharp
// src/Ntilde.Mux/TextClient/DetachChord.cs
using System.Text;

namespace Ntilde.Mux.TextClient;

/// <summary>
/// Ctrl+\ then d detaches; Ctrl+\ Ctrl+\ sends one literal Ctrl+\; Ctrl+\ then anything else sends
/// both, so nothing typed is lost (spec §6.4). The state survives across reads.
/// </summary>
public sealed class DetachChord
{
    public const char Prefix = '\u001c';

    private bool _afterPrefix;

    /// <summary>Appends what should reach the session; true when the chord completed (input after it is discarded).</summary>
    public bool Feed(ReadOnlySpan<char> input, StringBuilder passThrough)
    {
        ArgumentNullException.ThrowIfNull(passThrough);
        foreach (char c in input)
        {
            if (_afterPrefix)
            {
                _afterPrefix = false;
                if (c is 'd' or 'D') return true;
                passThrough.Append(Prefix);
                if (c != Prefix) passThrough.Append(c);
                continue;
            }

            if (c == Prefix)
            {
                _afterPrefix = true;
                continue;
            }

            passThrough.Append(c);
        }

        return false;
    }
}
```

- [ ] **Step 6: Implement the client**

```csharp
// src/Ntilde.Mux/TextClient/TextClientSession.cs
using System.Runtime.InteropServices;
using System.Text;
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.TextClient;

public sealed class TextClientOptions
{
    /// <summary>Attach <see cref="MuxAttachMode.ReadOnly"/>: no input, no resize requests, a status line says so.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>At most one frame per interval (~60 Hz).</summary>
    public TimeSpan RenderInterval { get; init; } = TimeSpan.FromMilliseconds(16);

    /// <summary>The CLI registers SIGINT/SIGTERM/SIGHUP/SIGQUIT and a ProcessExit backstop; tests do not.</summary>
    public bool HandleSignals { get; init; }
}

public enum TextClientExit
{
    Detached,
    SessionExited,
    Faulted,
    Disconnected,
    AttachFailed,
    Error,
}

/// <summary>
/// <c>ntilde mux attach</c> (spec §6): renders a mux session into a foreign terminal from its own
/// buffer - never relaying the raw stream, whose device queries the outer terminal would answer.
/// Threads: the client's delivery thread parses; one dedicated render thread paints; one dedicated
/// input thread reads keys. No thread-pool work on the output path. <see cref="Run"/> restores the
/// console on every path.
/// </summary>
public sealed class TextClientSession : IDisposable
{
    private static readonly PosixSignal[] StopSignals = [PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGQUIT];

    private readonly MuxClient _client;
    private readonly Guid _sessionId;
    private readonly IConsoleSurface _console;
    private readonly TextClientOptions _options;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _done = new(false);
    private readonly object _gate = new();
    private TextClientExit? _exit;
    private string? _detail;
    private int _exitCode;
    private string? _killedBy;
    private volatile bool _stopping;

    public TextClientSession(MuxClient client, Guid sessionId, IConsoleSurface console, TextClientOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _sessionId = sessionId;
        _options = options ?? new TextClientOptions();
    }

    public TextClientExit? ExitReason { get { lock (_gate) return _exit; } }

    /// <summary>Any thread. The first reason wins.</summary>
    public void RequestStop(TextClientExit reason, string? detail = null)
    {
        lock (_gate)
        {
            if (_exit is not null) return;
            _exit = reason;
            _detail = detail;
        }

        try { _done.Set(); }
        catch (ObjectDisposedException) { /* Run has returned */ }
        Wake();
    }

    /// <summary>Blocks until detach, exit or failure. Returns 0 detached, 1 the session ended, 2 a connection or usage failure.</summary>
    public int Run(TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        MuxAttachMode mode = _options.ReadOnly ? MuxAttachMode.ReadOnly : MuxAttachMode.Shared;
        MuxClientSession? session = null;
        Thread? renderThread = null;
        bool entered = false;
        var registrations = new List<IDisposable>();
        try
        {
            session = _client.OpenSession(_sessionId, string.Empty, null, mode);
            var model = new TextClientModel(session);
            var renderer = new TextClientRenderer(model.Buffer) { ReadOnly = _options.ReadOnly };
            model.Changed += Wake;
            session.KilledElsewhere += kind => Volatile.Write(ref _killedBy, kind);
            session.OnExit += code =>
            {
                Volatile.Write(ref _exitCode, code);
                RequestStop(TextClientExit.SessionExited);
            };
            session.Faulted += message => RequestStop(TextClientExit.Faulted, message);
            session.Disconnected += reason => RequestStop(TextClientExit.Disconnected, reason);
            _console.Resized += Wake;
            if (_options.HandleSignals) RegisterSignals(registrations);

            _console.EnterRawMode();
            _console.Write(TextClientRenderer.EnterSequence);
            entered = true;

            (int cols, int rows) = _console.Size;
            try
            {
                // The outer terminal sends legacy keys and the mux answers queries: no kitty keyboard.
                session.AttachAsync(mode, 0, new MuxPresentation { Cols = Math.Max(1, cols), Rows = Math.Max(1, rows), KittyKeyboardEnabled = false })
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is MuxProtocolException or IOException or TimeoutException or ObjectDisposedException)
            {
                RequestStop(TextClientExit.AttachFailed, ex.Message);
            }

            if (ExitReason is null)
            {
                MuxClientSession attached = session;
                renderThread = new Thread(() => RenderLoop(attached, renderer)) { IsBackground = true, Name = "MuxAttachRender" };
                var inputThread = new Thread(() => InputLoop(attached)) { IsBackground = true, Name = "MuxAttachInput" };
                Wake(); // the first frame
                renderThread.Start();
                inputThread.Start();
            }

            _done.Wait();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            RequestStop(TextClientExit.Error, ex.Message);
        }
        finally
        {
            _stopping = true;
            Wake();
            renderThread?.Join(TimeSpan.FromSeconds(2)); // the only writer until now
            _console.Resized -= Wake;
            if (entered) TryWrite(TextClientRenderer.LeaveSequence);
            _console.RestoreMode();
            foreach (IDisposable registration in registrations) registration.Dispose();

            // A session that ended needs no detach; everything else leaves it running in the daemon.
            if (session is not null && ExitReason is not TextClientExit.SessionExited) session.Dispose();
        }

        return Report(stderr);
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Dispose();
        _done.Dispose();
    }

    private void Wake()
    {
        try { _wake.Set(); }
        catch (ObjectDisposedException) { /* a late delivery after Dispose */ }
    }

    private void RenderLoop(MuxClientSession session, TextClientRenderer renderer)
    {
        try
        {
            long intervalMs = (long)_options.RenderInterval.TotalMilliseconds;
            long lastRenderMs = long.MinValue / 2;
            (int Cols, int Rows) lastSize = _console.Size;
            while (!_stopping)
            {
                _wake.WaitOne();
                if (_stopping) break;
                long wait = lastRenderMs + intervalMs - Environment.TickCount64;
                if (wait > 0 && _done.Wait((int)wait)) break; // throttle, but never outlive a stop

                (int Cols, int Rows) size = _console.Size;
                if (size != lastSize)
                {
                    lastSize = size;
                    if (!_options.ReadOnly) session.Resize(size.Cols, size.Rows);
                    renderer.Invalidate();
                }

                string output = renderer.Render(size.Cols, size.Rows);
                lastRenderMs = Environment.TickCount64;
                if (output.Length > 0) _console.Write(output);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or UnauthorizedAccessException)
        {
            RequestStop(TextClientExit.Error, ex.Message);
        }
    }

    private void InputLoop(MuxClientSession session)
    {
        var chord = new DetachChord();
        var pass = new StringBuilder();
        char[] buffer = new char[1024];
        try
        {
            while (!_stopping)
            {
                int n = _console.Read(buffer);
                if (n <= 0)
                {
                    RequestStop(TextClientExit.Detached, "input closed");
                    return;
                }

                pass.Clear();
                bool detach = chord.Feed(buffer.AsSpan(0, n), pass);
                if (pass.Length > 0 && !_options.ReadOnly) session.SendInput(pass.ToString());
                if (detach)
                {
                    RequestStop(TextClientExit.Detached);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            RequestStop(TextClientExit.Error, ex.Message);
        }
    }

    private void RegisterSignals(List<IDisposable> registrations)
    {
        // The guard keeps the platform analyzer (CA1416) satisfied: PosixSignalRegistration does not exist there.
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsBrowser()) return;
        foreach (PosixSignal signal in StopSignals)
        {
            try
            {
                // Cancel: do not let the runtime terminate us before the finally restores the console.
                registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    RequestStop(TextClientExit.Detached, "signal");
                }));
            }
            catch (PlatformNotSupportedException)
            {
                // Not every signal exists everywhere; the others still restore.
            }
        }

        AppDomain.CurrentDomain.ProcessExit += RestoreOnProcessExit;
        registrations.Add(new Unsubscribe(() => AppDomain.CurrentDomain.ProcessExit -= RestoreOnProcessExit));
    }

    private void RestoreOnProcessExit(object? sender, EventArgs e) => _console.RestoreMode();

    private void TryWrite(string text)
    {
        try { _console.Write(text); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { /* the console is gone */ }
    }

    private int Report(TextWriter stderr)
    {
        TextClientExit reason;
        string? detail;
        lock (_gate)
        {
            reason = _exit ?? TextClientExit.Detached;
            detail = _detail;
        }

        switch (reason)
        {
            case TextClientExit.Detached:
                TryWrite($"[detached from {_sessionId}]\r\n");
                return 0;
            case TextClientExit.SessionExited:
                TryWrite(Volatile.Read(ref _killedBy) is not null
                    ? "[session ended from another window]\r\n"
                    : $"[session exited with code {Volatile.Read(ref _exitCode)}]\r\n");
                return 1;
            case TextClientExit.Faulted:
                TryWrite("[session failed in the multiplexer]\r\n");
                return 1;
            case TextClientExit.Disconnected:
                TryWrite("[connection to the multiplexer lost]\r\n");
                return 2;
            case TextClientExit.AttachFailed:
                stderr.WriteLine($"mux: attach failed: {detail}");
                return 2;
            default:
                stderr.WriteLine($"mux: {detail}");
                return 2;
        }
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
```

- [ ] **Step 7: Run the tests again.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~TextClient|FullyQualifiedName~DetachChord"` → PASS. Then the whole suite, `scripts/build.ps1 test tests/Ntilde.Mux.Tests` → PASS, and `scripts/build.ps1 build -c Release src/Ntilde.Mux` → 0 warnings.

- [ ] **Step 8: Commit** (format first): `feat(mux): text client loop with detach chord, exit codes and console restore on every path`, with the trailer.

---

### Task 18: Console surfaces, `ntilde mux attach`, architecture rows

**Files:**
- Create: `src/Ntilde.Mux/TextClient/UnixConsoleSurface.cs`
- Create: `src/Ntilde.Mux/TextClient/WindowsConsoleSurface.cs`
- Create: `src/Ntilde.Mux/TextClient/ConsoleSurfaces.cs`
- Modify: `src/Ntilde.App/Shell/Mux/MuxCommand.cs`
- Modify: `src/Ntilde.App/Shell/CliConsoleBindings.cs`
- Modify: `src/Ntilde.App/Program.cs`
- Modify: `tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj` (link `FakeConsoleSurface.cs`)
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxCommandTests.cs`
- Test: `tests/Ntilde.Mux.Tests/TextClient/ConsoleSurfacesTests.cs`
- Test: `tests/Ntilde.Architecture.Tests/CliCommandDispatchTests.cs`, `LayeringTests.cs`, `NamespaceAlignmentTests.cs`

**Interfaces:**
- Consumes: `TextClientSession`, `IConsoleSurface` and `ConsoleUnavailableException` (Task 17), `MuxDaemonLauncher(string, IMuxDaemonSpawner, MuxClientOptions?, Action<string>?)`, and `MuxCommand.IsReportableFailure`.
- Produces:
  - `public static IConsoleSurface ConsoleSurfaces.Create()`
  - `public static bool MuxCommand.IsAttach(string[] args)`
  - `internal static Func<IConsoleSurface>? MuxCommand.ConsoleFactoryForTest { get; set; }`
  - `internal static bool MuxCommand.TryResolveSession(string target, IReadOnlyList<SessionSummary> sessions, out Guid id, out string? why)`
  - `public static void CliConsoleBindings.PrepareInteractive()`
  - usage line `ntilde mux attach <sessionId|prefix> [--read-only]`
  - CLI verbs connect with `ClientKind = "ntilde-cli"`, and `attach` with `"ntilde-attach"`

- [ ] **Step 1: Link the fake into App.Tests.** In `Ntilde.App.Tests.csproj`'s mux-support `ItemGroup`, add:

```xml
    <Compile Include="..\Ntilde.Mux.Tests\Support\FakeConsoleSurface.cs" Link="MuxSupport\FakeConsoleSurface.cs" />
```

- [ ] **Step 2: Write the failing tests**

```csharp
// MuxCommandTests.cs (append; add `using Ntilde.Mux.TextClient;` at the top)
    [Fact]
    public void Attach_is_recognised()
    {
        Assert.True(MuxCommand.IsAttach(["mux", "attach", "abcd"]));
        Assert.False(MuxCommand.IsAttach(["mux", "ls"]));
        Assert.False(MuxCommand.IsAttach(["backup"]));
    }

    [Theory]
    [InlineData("mux", "attach")]
    [InlineData("mux", "attach", "abcd", "efgh")]
    [InlineData("mux", "attach", "--bogus", "abcd")]
    public void Bad_attach_arguments_are_exit_2(params string[] args) => Assert.Equal(2, Run(args).Code);

    [Fact]
    public void Attach_without_a_daemon_is_exit_2()
    {
        var (code, _, err) = Run("mux", "attach", Guid.NewGuid().ToString());
        Assert.Equal(2, code);
        Assert.Contains("No multiplexer is running", err);
    }

    [Fact]
    public async Task Attach_to_an_unknown_session_is_exit_2()
    {
        await StartDaemonWithOneSessionAsync();
        var (code, _, err) = Run("mux", "attach", Guid.NewGuid().ToString());
        Assert.Equal(2, code);
        Assert.Contains("No session", err);
    }

    [Fact]
    public async Task Attach_by_prefix_renders_and_detaches_with_the_chord()
    {
        Guid id = await StartDaemonWithOneSessionAsync();
        using var console = new FakeConsoleSurface(80, 24);
        MuxCommand.ConsoleFactoryForTest = () => console;
        try
        {
            Task<(int Code, string Out, string Err)> run = Task.Run(() => Run("mux", "attach", id.ToString("N")[..8]), Ct);
            await TestWait.UntilAsync(() => console.IsRaw, "the text client took the console");
            console.Type("\u001cd");

            var (code, _, err) = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(0, code);
            Assert.Equal(string.Empty, err);
            Assert.Contains($"[detached from {id}]", console.Output, StringComparison.Ordinal);
            Assert.False(console.IsRaw);
        }
        finally
        {
            MuxCommand.ConsoleFactoryForTest = null;
        }
    }

    [Fact]
    public void A_session_prefix_must_be_unique_and_at_least_4_characters()
    {
        var a = new SessionSummary { SessionId = new Guid("abcd0000-0000-0000-0000-000000000001") };
        var b = new SessionSummary { SessionId = new Guid("abcd0000-0000-0000-0000-000000000002") };

        Assert.True(MuxCommand.TryResolveSession("abcd0000000000000000000000000001", [a, b], out Guid exact, out _));
        Assert.Equal(a.SessionId, exact);
        Assert.False(MuxCommand.TryResolveSession("abcd", [a, b], out _, out string? ambiguous));
        Assert.Contains("matches 2", ambiguous);
        Assert.False(MuxCommand.TryResolveSession("abc", [a, b], out _, out string? tooShort));
        Assert.Contains("at least 4", tooShort);
        Assert.True(MuxCommand.TryResolveSession("abcd0000-0000-0000-0000-000000000002", [a, b], out Guid full, out _));
        Assert.Equal(b.SessionId, full);
    }
```

```csharp
// tests/Ntilde.Mux.Tests/TextClient/ConsoleSurfacesTests.cs
using Ntilde.Mux.TextClient;

namespace Ntilde.Mux.Tests.TextClient;

public sealed class ConsoleSurfacesTests
{
    [Fact]
    public void Without_a_terminal_the_surface_refuses_instead_of_corrupting_the_pipe()
    {
        // Test runners redirect stdin; locally from a terminal it may be a TTY, so this is conditional.
        Assert.SkipUnless(Console.IsInputRedirected && !OperatingSystem.IsWindows(), "stdin is a terminal here (or Windows, whose surface opens CONIN$ directly)");

        Assert.Throws<ConsoleUnavailableException>(() => ConsoleSurfaces.Create().Dispose());
    }
}
```

```csharp
// CliCommandDispatchTests.cs (append)
    /// <summary>Phase 3: `mux attach` is interactive - both entry points must give it a real console (spec §6.7).</summary>
    [Fact]
    public void Mux_attach_gets_an_interactive_console_from_the_App_entry_point()
    {
        Type mux = App.GetType("Ntilde.Shell.Mux.MuxCommand", throwOnError: true)!;
        MethodInfo isAttach = mux.GetMethod("IsAttach", CommandMemberFlags, StringArrayParameter)!;
        Assert.True((bool)isAttach.Invoke(null, [new[] { "mux", "attach", "abcd" }])!);

        string program = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ntilde.App/Program.cs"));
        Assert.Contains("MuxCommand.IsAttach(", program, StringComparison.Ordinal);
        Assert.Contains("CliConsoleBindings.PrepareInteractive(", program, StringComparison.Ordinal);

        string cli = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ntilde.Cli/Program.cs"));
        Assert.Contains("MuxCommand.IsSupportedCliMode(", cli, StringComparison.Ordinal);
    }
```

```csharp
// LayeringTests.cs (append)
    [Fact]
    public void The_text_client_stays_console_only()
    {
        var result = Types.InAssembly(Mux)
            .That().ResideInNamespace("Ntilde.Mux.TextClient")
            .Should()
            .NotHaveDependencyOnAny("Avalonia", "SkiaSharp", "Ntilde.Platform", "Ntilde.Rendering", "Ntilde.Shell", "Ntilde.Controls")
            .GetResult();

        Assert.True(result.IsSuccessful, $"TextClient must stay console-only. Offenders: {Join(result.FailingTypeNames)}");
    }
```

```csharp
// NamespaceAlignmentTests.cs (append)
    [Fact]
    public void Text_client_types_reside_in_the_TextClient_namespace()
    {
        var result = Types.InAssembly(LoadByName("Ntilde.Mux"))
            .That().HaveNameEndingWith("ConsoleSurface")
            .Or().HaveNameStartingWith("TextClient")
            .Or().HaveName("DetachChord")
            .Should()
            .ResideInNamespace("Ntilde.Mux.TextClient")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Text client types belong in Ntilde.Mux.TextClient. Offenders: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
```

- [ ] **Step 3: Run them to see them fail.**
  - `scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MuxCommandTests"`
  - `scripts/build.ps1 test tests/Ntilde.Architecture.Tests`
  - `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~ConsoleSurfacesTests"`

  Expected: compile errors (`IsAttach`, `ConsoleFactoryForTest`, `TryResolveSession` and `ConsoleSurfaces` are missing).

- [ ] **Step 4: Implement the Unix surface**

```csharp
// src/Ntilde.Mux/TextClient/UnixConsoleSurface.cs
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Ntilde.Mux.TextClient;

/// <summary>
/// The terminal on fds 0/1 (spec §6.1). Raw mode through libc on an OPAQUE termios buffer
/// (tcgetattr → cfmakeraw → tcsetattr): struct termios differs between Linux (60 bytes) and macOS
/// (72), and cfmakeraw knows its own platform's layout. Input and output use read(0)/write(1)
/// directly: .NET's Console input on Unix runs its own line editing. The size comes from
/// Console.WindowWidth/Height (the runtime's non-variadic shim): ioctl is variadic and not safe to
/// P/Invoke on macOS arm64.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("freebsd")]
public sealed class UnixConsoleSurface : IConsoleSurface
{
    private const int TermiosBytes = 256; // larger than any platform's struct termios
    private const int TcsaNow = 0;
    private const int EINTR = 4;          // the same on Linux, macOS and FreeBSD

    private readonly byte[] _saved = new byte[TermiosBytes];
    private readonly byte[] _readBytes = new byte[1024];
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly object _modeGate = new();
    private readonly PosixSignalRegistration _winch;
    private bool _raw;

    public UnixConsoleSurface()
    {
        if (isatty(0) != 1 || isatty(1) != 1)
        {
            throw new ConsoleUnavailableException("mux attach needs an interactive terminal: stdin and stdout must be a TTY.");
        }

        _winch = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, context =>
        {
            context.Cancel = true;
            Resized?.Invoke();
        });
    }

    public event Action? Resized;

    public (int Cols, int Rows) Size
    {
        get
        {
            try
            {
                return (Math.Max(1, Console.WindowWidth), Math.Max(1, Console.WindowHeight));
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
            {
                return (80, 24);
            }
        }
    }

    public void EnterRawMode()
    {
        lock (_modeGate)
        {
            if (_raw) return;
            if (tcgetattr(0, _saved) != 0) throw new IOException($"tcgetattr failed (errno {Marshal.GetLastPInvokeError()}).");
            byte[] raw = (byte[])_saved.Clone();
            cfmakeraw(raw);
            if (tcsetattr(0, TcsaNow, raw) != 0) throw new IOException($"tcsetattr failed (errno {Marshal.GetLastPInvokeError()}).");
            _raw = true;
        }
    }

    public void RestoreMode()
    {
        lock (_modeGate)
        {
            if (!_raw) return;
            _ = tcsetattr(0, TcsaNow, _saved);
            _raw = false;
        }
    }

    public void Write(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int offset = 0;
        while (offset < bytes.Length)
        {
            nint written = write(1, ref bytes[offset], (nuint)(bytes.Length - offset));
            if (written < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == EINTR) continue;
                throw new IOException($"write to the terminal failed (errno {errno}).");
            }

            offset += (int)written;
        }
    }

    public int Read(char[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        int max = Math.Min(_readBytes.Length, buffer.Length); // UTF-8 never decodes to more chars than bytes
        while (true)
        {
            nint n = read(0, ref _readBytes[0], (nuint)max);
            if (n < 0)
            {
                if (Marshal.GetLastPInvokeError() == EINTR) continue;
                return 0;
            }

            if (n == 0) return 0;
            int chars = _decoder.GetChars(_readBytes, 0, (int)n, buffer, 0, flush: false);
            if (chars > 0) return chars; // a split UTF-8 sequence completes on the next read
        }
    }

    public void Dispose()
    {
        RestoreMode();
        _winch.Dispose();
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int isatty(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, byte[] termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int optionalActions, byte[] termios);

    [DllImport("libc")]
    private static extern void cfmakeraw(byte[] termios);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, ref byte buffer, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, ref byte buffer, nuint count);
}
```

- [ ] **Step 5: Implement the Windows surface**

```csharp
// src/Ntilde.Mux/TextClient/WindowsConsoleSurface.cs
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ntilde.Mux.TextClient;

/// <summary>
/// The process's console (spec §6.1, §6.7). Opens CONIN$/CONOUT$ itself, so it works in the WinExe
/// after AttachConsole/AllocConsole, where the std handles are not set. Windows has no SIGWINCH: a
/// dedicated background thread polls the size every 200 ms.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsConsoleSurface : IConsoleSurface
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    private const uint EnableProcessedInput = 0x1, EnableLineInput = 0x2, EnableEchoInput = 0x4, EnableWindowInput = 0x8, EnableMouseInput = 0x10, EnableVirtualTerminalInput = 0x200;
    private const uint EnableProcessedOutput = 0x1, EnableVirtualTerminalProcessing = 0x4, DisableNewlineAutoReturn = 0x8;

    private readonly nint _in;
    private readonly nint _out;
    private readonly object _modeGate = new();
    private readonly Thread _sizePoll;
    private uint _savedIn;
    private uint _savedOut;
    private bool _raw;
    private volatile bool _disposed;

    public WindowsConsoleSurface()
    {
        _in = CreateFileW("CONIN$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        _out = CreateFileW("CONOUT$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (_in == -1 || _out == -1 || !GetConsoleMode(_in, out _) || !GetConsoleMode(_out, out _))
        {
            CloseHandles();
            throw new ConsoleUnavailableException("mux attach needs an interactive console.");
        }

        _sizePoll = new Thread(PollSize) { IsBackground = true, Name = "MuxAttachSizePoll" };
        _sizePoll.Start();
    }

    public event Action? Resized;

    public (int Cols, int Rows) Size =>
        GetConsoleScreenBufferInfo(_out, out ConsoleScreenBufferInfo info)
            ? (Math.Max(1, info.Window.Right - info.Window.Left + 1), Math.Max(1, info.Window.Bottom - info.Window.Top + 1))
            : (80, 24);

    public void EnterRawMode()
    {
        lock (_modeGate)
        {
            if (_raw) return;
            if (!GetConsoleMode(_in, out _savedIn) || !GetConsoleMode(_out, out _savedOut)) throw new IOException("GetConsoleMode failed.");
            uint input = (_savedIn & ~(EnableLineInput | EnableEchoInput | EnableProcessedInput | EnableWindowInput | EnableMouseInput)) | EnableVirtualTerminalInput;
            uint output = _savedOut | EnableProcessedOutput | EnableVirtualTerminalProcessing | DisableNewlineAutoReturn;
            if (!SetConsoleMode(_in, input) || !SetConsoleMode(_out, output))
            {
                _ = SetConsoleMode(_in, _savedIn);
                _ = SetConsoleMode(_out, _savedOut);
                throw new IOException($"SetConsoleMode failed ({Marshal.GetLastPInvokeError()}); this console may not support VT sequences.");
            }

            _raw = true;
        }
    }

    public void RestoreMode()
    {
        lock (_modeGate)
        {
            if (!_raw) return;
            _ = SetConsoleMode(_in, _savedIn);
            _ = SetConsoleMode(_out, _savedOut);
            _raw = false;
        }
    }

    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int offset = 0;
        while (offset < text.Length)
        {
            ReadOnlySpan<char> rest = text.AsSpan(offset);
            if (!WriteConsoleW(_out, ref MemoryMarshal.GetReference(rest), (uint)rest.Length, out uint written, 0) || written == 0)
            {
                throw new IOException($"WriteConsoleW failed ({Marshal.GetLastPInvokeError()}).");
            }

            offset += (int)written;
        }
    }

    public int Read(char[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length == 0) return 0;
        return ReadConsoleW(_in, ref buffer[0], (uint)buffer.Length, out uint read, 0) ? (int)read : 0;
    }

    public void Dispose()
    {
        _disposed = true;
        RestoreMode();
        CloseHandles();
    }

    private void PollSize()
    {
        (int Cols, int Rows) last = Size;
        while (!_disposed)
        {
            Thread.Sleep(200);
            if (_disposed) return;
            (int Cols, int Rows) now = Size;
            if (now == last) continue;
            last = now;
            Resized?.Invoke();
        }
    }

    private void CloseHandles()
    {
        if (_in != -1 && _in != 0) _ = CloseHandle(_in);
        if (_out != -1 && _out != 0) _ = CloseHandle(_out);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleScreenBufferInfo
    {
        public Coord Size;
        public Coord CursorPosition;
        public ushort Attributes;
        public SmallRect Window;
        public Coord MaximumWindowSize;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreateFileW(string fileName, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(nint handle, uint mode);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteConsoleW(nint handle, ref char buffer, uint count, out uint written, nint reserved);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleW(nint handle, ref char buffer, uint count, out uint read, nint control);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleScreenBufferInfo(nint handle, out ConsoleScreenBufferInfo info);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
```

```csharp
// src/Ntilde.Mux/TextClient/ConsoleSurfaces.cs
namespace Ntilde.Mux.TextClient;

public static class ConsoleSurfaces
{
    /// <summary>The real terminal. Throws <see cref="ConsoleUnavailableException"/> when there is none.</summary>
    public static IConsoleSurface Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsConsoleSurface();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()) return new UnixConsoleSurface();
        throw new ConsoleUnavailableException("mux attach is not supported on this platform.");
    }
}
```

If the Release build of `Ntilde.Mux` reports a platform-compatibility warning (CA1416) or an interop analyzer warning (SYSLIB1054 is informational and does not fail the build), fix it at the source. Use the `OperatingSystem.Is…` guard shown, or the `[SupportedOSPlatform]` attributes. Never suppress it globally.

- [ ] **Step 6: Implement the CLI.** In `MuxCommand.cs`:

Update the usage:

```csharp
    private const string Usage = """
        Usage:
          ntilde mux serve [--idle-exit-minutes N] [--foreground]
          ntilde mux ls [--json]
          ntilde mux kill <sessionId>
          ntilde mux kill-server [--force]
          ntilde mux attach <sessionId|prefix> [--read-only]
        """;
```

Add the recogniser and the seam:

```csharp
    public static bool IsAttach(string[] args) =>
        IsSupportedCliMode(args) && args.Length > 1 && string.Equals(args[1], "attach", StringComparison.OrdinalIgnoreCase);

    /// <summary>Test seam: the console `mux attach` draws on. Null = the real terminal (ConsoleSurfaces.Create).</summary>
    internal static Func<Ntilde.Mux.TextClient.IConsoleSurface>? ConsoleFactoryForTest { get; set; }
```

Add `"attach" => Attach(args, stderr, descriptorPath),` to the verb switch.

In `Connect`, name the client kind: `new MuxDaemonLauncher(descriptorPath, new NoSpawn(), new MuxClientOptions { ClientKind = "ntilde-cli" })`.

Add the verb:

```csharp
    /// <summary>
    /// The text client (spec §6). Exit codes: 0 detached, 1 the session ended, 2 usage or any
    /// connection problem (including "no multiplexer running", unlike the other verbs).
    /// </summary>
    private static int Attach(string[] args, TextWriter stderr, string descriptorPath)
    {
        string? target = null;
        bool readOnly = false;
        foreach (string arg in args.Skip(2))
        {
            if (arg == "--read-only") readOnly = true;
            else if (target is null && !arg.StartsWith('-')) target = arg;
            else return Fail(stderr, Usage);
        }

        if (target is null) return Fail(stderr, Usage);

        MuxClient? client;
        try
        {
            var launcher = new MuxDaemonLauncher(descriptorPath, new NoSpawn(), new MuxClientOptions { ClientKind = "ntilde-attach" });
            client = launcher.TryConnectExistingAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (IsReportableFailure(ex))
        {
            stderr.WriteLine($"mux: {ex.Message}");
            return 2;
        }

        if (client is null)
        {
            stderr.WriteLine("No multiplexer is running.");
            return 2;
        }

        using (client)
        {
            Guid id;
            try
            {
                if (!TryResolveSession(target, client.ListSessionsAsync().GetAwaiter().GetResult(), out id, out string? why))
                {
                    stderr.WriteLine($"mux: {why}");
                    return 2;
                }
            }
            catch (Exception ex) when (IsReportableFailure(ex))
            {
                stderr.WriteLine($"mux: {ex.Message}");
                return 2;
            }

            if (readOnly && client.ProtocolVersion < MuxProtocol.SessionEventsVersion)
            {
                stderr.WriteLine("mux: the running multiplexer is too old for --read-only; run 'ntilde mux kill-server' to replace it.");
                return 2;
            }

            Ntilde.Mux.TextClient.IConsoleSurface console;
            try
            {
                console = (ConsoleFactoryForTest ?? Ntilde.Mux.TextClient.ConsoleSurfaces.Create)();
            }
            catch (Ntilde.Mux.TextClient.ConsoleUnavailableException ex)
            {
                stderr.WriteLine($"mux: {ex.Message}");
                return 2;
            }

            using (console)
            using (var textClient = new Ntilde.Mux.TextClientSession(client, id, console,
                new Ntilde.Mux.TextClient.TextClientOptions { ReadOnly = readOnly, HandleSignals = ConsoleFactoryForTest is null }))
            {
                return textClient.Run(stderr);
            }
        }
    }

    /// <summary>A full id, or a unique prefix of at least 4 hex characters (dashes ignored).</summary>
    internal static bool TryResolveSession(string target, IReadOnlyList<SessionSummary> sessions, out Guid id, out string? why)
    {
        if (Guid.TryParse(target, out id))
        {
            Guid wanted = id;
            if (sessions.Any(s => s.SessionId == wanted))
            {
                why = null;
                return true;
            }

            why = $"No session {id}.";
            return false;
        }

        string prefix = target.Replace("-", string.Empty, StringComparison.Ordinal);
        id = Guid.Empty;
        if (prefix.Length < 4)
        {
            why = "A session id prefix needs at least 4 characters.";
            return false;
        }

        SessionSummary[] matches = sessions.Where(s => s.SessionId.ToString("N").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1)
        {
            id = matches[0].SessionId;
            why = null;
            return true;
        }

        why = matches.Length == 0 ? $"No session starts with '{target}'." : $"'{target}' matches {matches.Length} sessions; give more of the id.";
        return false;
    }
```

In `CliConsoleBindings.cs`, add:

```csharp
    /// <summary>
    /// `mux attach` (spec §6.7): a console to draw on - the parent's when it has one, otherwise (a
    /// launch from Explorer) a new one. The text client opens CONIN$/CONOUT$ itself.
    /// </summary>
    public static void PrepareInteractive()
    {
        if (OperatingSystem.IsWindows())
        {
            const int AttachParentProcess = -1;
            if (!AttachConsole(AttachParentProcess)) _ = AllocConsole();
        }

        RebindOutputStream(Console.OpenStandardOutput, Console.SetOut);
        RebindOutputStream(Console.OpenStandardError, Console.SetError);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
```

In `Program.cs`, replace the mux dispatch's console line:

```csharp
            if (Ntilde.Shell.Mux.MuxCommand.IsSupportedCliMode(args))
            {
                // serve is a daemon: it must not attach to the launching console (it detaches from it).
                // attach is interactive: it needs a real console, allocated if the parent has none.
                if (Ntilde.Shell.Mux.MuxCommand.IsAttach(args)) CliConsoleBindings.PrepareInteractive();
                else if (!Ntilde.Shell.Mux.MuxCommand.IsServe(args)) CliConsoleBindings.Prepare();
                Environment.ExitCode = Ntilde.Shell.Mux.MuxCommand.Execute(args, Console.Out, Console.Error);
                return;
            }
```

`src/Ntilde.Cli/Program.cs` is unchanged. It is a console executable and already dispatches `MuxCommand`, which the architecture row pins.

- [ ] **Step 7: Run the tests again**
  - `scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MuxCommandTests"` → PASS.
  - `scripts/build.ps1 test tests/Ntilde.Architecture.Tests` → PASS.
  - `scripts/build.ps1 test tests/Ntilde.Mux.Tests` → PASS. The `ConsoleSurfacesTests` row is skipped on Windows.
  - `scripts/build.ps1 build -c Release src/Ntilde.Mux` → 0 warnings.

- [ ] **Step 8: A manual console check.** Record it in the PR; it is not automated.
  1. On Windows, from `cmd`, run `cmd /c <bin>\Ntilde.exe mux attach <prefix>` against a running daemon. The screen is painted, and Ctrl+\ d returns to the prompt with the console intact: typing echoes, and Enter works.
  2. From `<bin>\Ntilde.Cli.exe mux attach <prefix>` in PowerShell, the same.
  3. On Linux or macOS, from bash, `./Ntilde mux attach <prefix>`, then `stty -a` after detaching shows `icanon echo`.

- [ ] **Step 9: Commit** (format first): `feat(mux): ntilde mux attach with Windows and Unix console surfaces`, with the trailer.

---
### Task 19: PtySmoke: two clients share a real shell on a real daemon

**Files:**
- Modify: `tests/Ntilde.App.Tests/MuxDaemonSmokeTests.cs`

**Interfaces:**
- Consumes: the existing `Spawner()`, `IsAlive`, `ScreenText`, `TestAppDataRoot`, `MuxDaemonLauncher.EnsureConnectedAsync`, `ClientPaneModel`, and `MuxClient.OpenSession(…, MuxAttachMode)` (Task 9).
- Produces: `MuxDaemonSmokeTests.Two_clients_attached_shared_to_a_real_shell_see_the_same_marker` (`[Trait("Category", "PtySmoke")]` via the class).

- [ ] **Step 1: Write the test** (append to the class). Add `private const string SharedMarker = "mux-shared-marker";` and a marker-parameterised counter next to the existing one:

```csharp
    private static int CountMarker(ClientPaneModel pane, string marker) => ScreenText(pane).Split(marker).Length - 1;

    [Fact]
    public async Task Two_clients_attached_shared_to_a_real_shell_see_the_same_marker()
    {
        using var root = new TestAppDataRoot();
        string descriptorPath = MuxDiscovery.GetDescriptorPath(root.RootPath);
        var presentation = MuxTestHost.DefaultPresentation with { Cols = 100, Rows = 30 };
        var launcher = new MuxDaemonLauncher(descriptorPath, Spawner()) { SpawnTimeout = TimeSpan.FromSeconds(30) };
        int? daemonPid = null, shellPid = null;
        MuxClient? first = null, second = null;
        try
        {
            first = await launcher.EnsureConnectedAsync(Ct);
            second = await launcher.EnsureConnectedAsync(Ct);
            Assert.NotSame(first, second);
            Assert.Equal(2, first.ProtocolVersion);
            Assert.True(MuxDiscovery.TryReadLiveDescriptor(descriptorPath, out MuxEndpointDescriptor? d));
            daemonPid = d.Pid;

            string shell = ShellHelper.GetDefaultShell();
            Guid id = await first.SpawnAsync(new SpawnParams
            {
                Command = shell,
                Cols = 100,
                Rows = 30,
                SkipPowerShellPostLaunchInit = true,
                Title = "shared-smoke",
            }, Ct);
            var a = new ClientPaneModel(first.OpenSession(id, shell));
            await a.Session.AttachAsync(1000, presentation, Ct);
            var b = new ClientPaneModel(second.OpenSession(id, shell, null, MuxAttachMode.Shared));
            await b.Session.AttachAsync(1000, presentation, Ct);
            shellPid = (await a.Session.RefreshSessionInfoAsync(Ct)).Pid;

            await TestWait.UntilAsync(() => a.Session.AttachedClients == 2 && b.Session.AttachedClients == 2,
                "both clients heard that they share the shell", TimeSpan.FromSeconds(15));
            await TestWait.UntilAsync(() => ScreenText(a).Length > 0, "the shell drew a prompt", TimeSpan.FromSeconds(30));
            await Task.Delay(500, Ct);
            a.Session.SendInput($"echo {SharedMarker}\r");

            // The typed command and its output: the marker appears twice, on both screens.
            await TestWait.UntilAsync(() => CountMarker(a, SharedMarker) >= 2, "client A sees the marker", TimeSpan.FromSeconds(30));
            await TestWait.UntilAsync(() => CountMarker(b, SharedMarker) >= 2, "client B sees the same marker", TimeSpan.FromSeconds(30));

            // A restore-style attach is refused while both hold it: a restore would not duplicate this shell.
            using (MuxClient third = await launcher.EnsureConnectedAsync(Ct))
            {
                using MuxClientSession c = third.OpenSession(id, shell, null, MuxAttachMode.IfUnattached);
                var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => c.AttachAsync(0, presentation, Ct));
                Assert.Equal(MuxErrorCodes.SessionAttached, ex.Code);
            }

            await second.ShutdownServerAsync(Ct);
            using Process daemon = Process.GetProcessById(daemonPid.Value);
            Assert.True(daemon.WaitForExit(15_000), "the daemon exited");
            if (shellPid is int sp) await TestWait.UntilAsync(() => !IsAlive(sp), "the shell is gone", TimeSpan.FromSeconds(15));
        }
        finally
        {
            first?.Dispose();
            second?.Dispose();
            if (daemonPid is int dp && IsAlive(dp)) { try { using var p = Process.GetProcessById(dp); p.Kill(entireProcessTree: true); } catch (Exception) { } }
            if (shellPid is int sp && IsAlive(sp)) { try { using var p = Process.GetProcessById(sp); p.Kill(); } catch (Exception) { } }
        }
    }
```

- [ ] **Step 2: Run it.** Record the processes present first: `Get-Process Ntilde -ErrorAction SilentlyContinue`. Then:

`scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~MuxDaemonSmokeTests"` → both tests PASS.

This test needs every earlier task. RED before Task 9 is a compile error (no `MuxAttachMode` overload). Afterwards, compare `Get-Process Ntilde` with the earlier list: no daemon survived.

- [ ] **Step 3: Commit** (format first): `test(mux): real-daemon PtySmoke with two clients sharing one shell`, with the trailer.

---

### Task 20: Docs

**Files:**
- Modify: `docs/USER_MANUAL.md` (§3.3)
- Modify: `docs/ARCHITECTURE.md` (§8.1; the Ntilde.Mux row of the dependency table; the test-project table row)
- Modify: `docs/MODULE_OWNERSHIP.md` (the Ntilde.Mux and Ntilde.VT sections)

**Interfaces:**
- Consumes: the shipped behaviour of Tasks 1–19.
- Produces: user- and maintainer-facing documentation.

- [ ] **Step 1: `docs/USER_MANUAL.md` §3.3.** Add these subsections after the existing ones, and keep the existing text:
  - **Sharing a shell between windows.**
    - "Session: Attach to Session…" in the command palette. It has no default shortcut, and you can bind one under Settings → Shortcuts; the entry appears only while the setting is on.
    - The picker's columns: title, command, folder, size, attached count, and running or exited.
    - The chosen shell opens in a new tab. It works from a second ntilde window or instance.
    - A shell already open in this window is focused rather than opened twice.
    - The "shared with N" badge and the ⧉ tab marker.
  - **Detach versus close.**
    - "Pane: Detach" leaves the shell running. Get it back with "Attach to session…"; it also reappears as a background tab at the next launch.
    - Closing a pane ends its shell.
    - When other windows are attached, closing asks: Close (ends it for everyone) / Detach / Cancel.
    - A shell ended from another window shows `[Shell ended from another window]`.
  - **Size.** The latest resize wins. A window that is not in control letterboxes, and it takes the size back when you focus it.
  - **`ntilde mux attach <id|prefix> [--read-only]`.**
    - It uses the id or a unique prefix of at least 4 characters from `ntilde mux ls`.
    - The detach chord is **Ctrl+\ then d**. **Ctrl+\ Ctrl+\** sends a literal Ctrl+\.
    - Exit codes: 0 detached, 1 the session ended (the code is printed), 2 usage or connection error.
    - A status line appears when your terminal is smaller than the session.
    - `--read-only` shows the session without sending input or resizing. **It is a convenience, not a security boundary**: anyone who can run programs as you can attach normally.
    - On Windows, run it from `cmd`, `cmd /c ntilde mux attach <id>`, or `Start-Process -Wait -NoNewWindow ntilde 'mux','attach','<id>'`. A plain PowerShell prompt does not wait for the program and competes for the keyboard. `Ntilde.Cli.exe`, where installed, works anywhere.
  - **`ntilde mux kill-server --force`.** It replaces a daemon of another version. It terminates the verified daemon process, and its shells.
  - **Limits.** A v1 daemon (from before this version) keeps working for spawn, attach, detach and kill, but shows no sharing indicator, and `--read-only` needs a new daemon.

- [ ] **Step 2: `docs/ARCHITECTURE.md` §8.1.** Add:
  - **Protocol v2.** It is additive, with a negotiated range of 1..2. The items: `AttachParams.Mode` (shared / ifUnattached / readOnly), `SessionSummary.Cwd`, `sessionChanged` (coalesced to one per session per 100 ms by the parse loop's bounded wait), `killed` (before `exited`, never to the killer), and `session_attached`. Include the fallback matrix from spec §2.1.
  - **Attach modes.** `IfUnattached` is decided inside the attach item on the session's single parse thread, and read-only observers do not count. Read-only is enforced both by the session (no geometry) and by the connection (input and resize dropped). It is not a security boundary.
  - **The text client** (`Ntilde.Mux.TextClient`). Cover:
    - `TextClientModel` holds its own buffer and parser;
    - `TextClientRenderer` does dirty-row repaint over `CaptureRenderSnapshot`, with `AnsiCellWriter` in `Ntilde.VT.Export`;
    - the dedicated render and input threads;
    - `IConsoleSurface` and its Windows / Unix implementations;
    - it never relays raw bytes, because the outer terminal would answer the device queries the mux already answered;
    - the outer alternate screen is entered once;
    - how `mux attach` gets a console in the WinExe (`CliConsoleBindings.PrepareInteractive`).
  - Extend the process diagram with a `ntilde mux attach` box beside the GUI, connecting to the same endpoint.

- [ ] **Step 3: `docs/MODULE_OWNERSHIP.md`.**
  - **Ntilde.Mux:** the namespace line becomes `Ntilde.Mux` (+ `.Transport`, `.TextClient`). Add owned-invariant bullets:
    - attach modes are decided on the parse thread;
    - `sessionChanged` coalescing lives in the parse loop, with no timer;
    - the text client never relays raw output;
    - console modes are restored on every exit path.
  - **Ntilde.VT:** add `Export/AnsiCellWriter.cs` ("render-snapshot rows to positioned ANSI; no I/O").
  - **Ntilde.App `Shell/Mux/`:** add `MuxSessionPicker`, `MuxStartupProbe`, `MuxCommandMatch` and `SharedCloseChoice`.

- [ ] **Step 4: Commit:** `docs(mux): Phase 3 sharing, detach, mux attach, protocol v2`, with the trailer.

---

### Task 21: Final verification, the AOT check, and the manual checklist

**Files:** none changed, except to fix what this task finds (each fix is its own commit, with a test).

**Interfaces:** none.

- [ ] **Step 1: Full verification.** Run on Windows, one project per invocation, never concurrently. Record pass/fail/skip counts for the PR.

```powershell
scripts/build.ps1 format whitespace --no-restore --verify-no-changes
scripts/build.ps1 build -c Release src/Ntilde.Mux
scripts/build.ps1 build -c Release src/Ntilde.Mux.Contracts
scripts/build.ps1 test tests/Ntilde.Mux.Tests
scripts/build.ps1 test tests/Ntilde.VT.Tests
scripts/build.ps1 test tests/Ntilde.Architecture.Tests
scripts/build.ps1 test tests/Ntilde.McpServer.Tests
scripts/build.ps1 test tests/Ntilde.Platform.Tests
scripts/build.ps1 test tests/Ntilde.Rendering.Tests
scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "Lane!=PlatformBoot" *> $env:TEMP\apptests-main.log
scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "Lane=PlatformBoot" *> $env:TEMP\apptests-boot.log
```

The two Release builds must show 0 warnings. Read the App.Tests logs with the Grep tool, not a shell `grep` with backslashes (the rtk rewrite turns `\[FAIL\]` into a character class). Check the `N executed` line, so a hang-truncated run is visible. The `McpServer` drift guards must still pass: no `TerminalSettings` field was added.

- [ ] **Step 2: The AOT publish, with the text client compiled in**

```powershell
scripts/build.ps1 publish src/Ntilde.App -c Release -r win-x64 *> $env:TEMP\aot-publish.log
```

Then use the Grep tool on `$env:TEMP\aot-publish.log` for the pattern `IL2026|IL3050`. It must find no matches. If the native link step fails on `vswhere`, put the VS Installer folder on `PATH` (a machine setup issue, as in PR #489). Then run the published `Ntilde.exe mux ls` against a running daemon to prove the AOT binary serves the verbs, and run `cmd /c <publish>\Ntilde.exe mux attach <prefix>` once to prove the P/Invoke surface works under AOT. Detach with Ctrl+\ d.

- [ ] **Step 3: The redraw-storm number (report item 5).** Take the `[mux-phase3] text client full-screen repaint` line from the Task 16 test output (`scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~Repaint_cost" --logger "console;verbosity=detailed"`). Then measure a real one:
  1. `ntilde mux attach <id>` into a session running `vim`.
  2. Hold PageDown on a large file for 5 s.
  3. Note that the render thread keeps up. The client stays responsive and the final screen matches the GUI.

- [ ] **Step 4: Manual checklist, run on Windows 11.** GUI automation is unreliable there, so the user runs these steps. Paste the log into the PR. Setting: **Keep shells running when the window closes** on.

PR #489's eight steps:
1. Open two shells. Run `Start-Sleep 1000` in one and `vim` in the other. Close the window.
2. From `cmd`, `ntilde mux ls` lists 2 running sessions with 0 attached. Exactly one `mux serve` process exists (Task Manager → Details), and the shells are its children (Process Explorer).
3. Relaunch. The same shells come back, with the same screen (vim redrawn) and scrollback.
4. Kill the GUI in Task Manager. The shells survive, and relaunching reattaches them.
5. Kill the daemon (Task Manager → the `mux serve` process). The panes show `[Multiplexer disconnected] [Press Enter to reconnect]`. Enter starts a new daemon and shows the toast "[Previous session was lost — started a new shell]".
6. Close a pane. Its shell ends, and `ntilde mux ls` no longer lists it.
7. `ntilde mux kill-server` ends everything.
8. Launch a second Ntilde window (a second instance). It does not attach the first window's visible shells, and its own restore shows no "lost" toast.

The four extensions (brief, "Report back" item 6):

9. **The same shell in two windows.**
   1. In window A, open a shell.
   2. In window B (a second instance), run "Session: Attach to Session…" from the palette. The picker lists A's shell with "1 attached". Choose it.
   3. B gets a new tab showing the same screen. Both panes show the "shared with 1" badge, and both tabs show ⧉ in vertical-tab mode.
   4. Type `echo from-A` in A. It appears in B at once.
   5. Type in B. It appears in A.
   6. Resize B's window: A letterboxes. Click into A: A takes the size back.
10. **Detach in one window, and the other keeps it.**
    1. In B, run "Pane: Detach". The toast reads "Shell detached — Shell kept running — Attach to session… to get it back".
    2. A's badge disappears. A's shell is still usable.
    3. `ntilde mux ls` shows the session with 1 attached.
11. **Close in one window, with the prompt.**
    1. Attach B again.
    2. In A, close the pane (Ctrl+Shift+W). The prompt reads "1 other window is attached to this shell", with Cancel / Detach / Close (ends it).
    3. Cancel: nothing changes.
    4. Close again and choose Close: A's pane closes, and B's pane shows `[Shell ended from another window]`.
12. **`ntilde mux attach` from a terminal.**
    1. From `cmd`, run `ntilde mux ls`, then `ntilde mux attach <first 8 chars of an id>`. The session's screen is painted in cmd, including colours and the cursor. The GUI pane shows "shared with 1".
    2. Type `dir` and Enter: it runs in both.
    3. Press **Ctrl+\ then d**. You get `[detached from <id>]`, cmd's prompt is back and typing echoes normally, and the exit code (`echo %ERRORLEVEL%`) is 0.
    4. Repeat with `--read-only`. The status line reads "read-only". Typing does nothing in the session, and detaching works.

- [ ] **Step 5: Final review of the branch diff against the spec's Review Focus list** (plan header). Then write the PR description:
  - files by deliverable;
  - the test tables per project, with both App.Tests lanes;
  - the AOT result;
  - the protocol delta and the fallback matrix (spec §2);
  - how exclusivity is enforced, and the test that proves it (spec §3, Task 7);
  - the text client's approach and the measured repaint cost;
  - the manual log;
  - spec §11 (differences) and spec §12 (open questions), plus Phase 4 questions: the remote daemon over an SSH exec channel, `--stdio`, and the install flow, including the Windows console launcher.

  End the PR description with the Claude Code line. Push, and open the PR against `dev-mux` only when the user asks.

---

## Execution notes

- Tasks 1–5 are independent carry-overs, and each is small. Task 1 must precede Task 6.
- Tasks 6 → 7 → 8 → 9 are strictly ordered (contracts → server modes → server events → client).
- Task 10 needs 5 and 9. Tasks 11–14 need 10. Task 13 needs 12, and 14 needs 13, because the detach tests open their second tab through the picker.
- Tasks 15 → 16 → 17 → 18 are ordered. Task 15 has no mux dependency and may run earlier.
- After every task, re-run at minimum the task's filter, the whole `tests/Ntilde.Mux.Tests` (Tasks 2 and 6–9, 16–17), or `FullyQualifiedName~Mux` in App.Tests (Tasks 1, 3–5, 10–14, 18), plus `tests/Ntilde.Architecture.Tests` whenever a project or namespace changes (Tasks 15 and 18).
- Shared main checkout: other sessions commit into `D:\projects\nova2`. Work only in this worktree, and check `git rev-parse --abbrev-ref HEAD` shows `feat/mux-phase3` before each commit.
