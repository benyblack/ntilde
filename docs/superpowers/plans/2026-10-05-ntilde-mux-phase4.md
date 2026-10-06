# ntilde multiplexer Phase 4 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** SSH tabs that survive network drops. The daemon runs on the remote host and is reached
through an SSH exec channel. This plan also delivers the ten PR #499 carry-overs and the Windows
`ntilde.com` launcher.

**Architecture:**
- **Daemon core and CLI** move from `Ntilde.App/Shell/Mux` into `Ntilde.Mux` (`Daemon/`, `Cli/`), with
  paths injected.
- **`ntilde-mux`**, a new single-file AOT exe (rusty_pty linked statically), hosts `serve` and
  `proxy --stdio`.
- **The GUI** keeps one `MuxConnectionHost` per endpoint (`local` or `ssh:<profileId>`). A remote host
  connects through an OpenSSH or native exec transport, then `StdioMuxTransport`, then `MuxClient`,
  and it pings the daemon and reconnects.
- **`ntilde.com`** is a separate tiny AOT console exe.

**Tech Stack:** .NET 10 (C#, NativeAOT, Avalonia 12 for UI), Rust (`rusty_pty`, `rusty_ssh` with
russh 0.59), xunit v3, GitHub Actions, Docker (sshd E2E), Velopack 1.2.0.

**Spec:** `docs/superpowers/specs/2026-10-05-ntilde-mux-phase4.md`. Read it before any task: section
numbers below (§n) refer to it.

## Global Constraints

- **Build and test** only through `scripts/build.ps1` (Windows, PowerShell tool) or `scripts/build.sh`.
  Never call raw `dotnet build`/`dotnet test`. Pass one project per call.
- **App.Tests** always runs as two separate invocations, each with `--blame-hang-timeout 5m` and never
  concurrently: `--filter "Lane!=PlatformBoot"` and `--filter "Lane=PlatformBoot"`. For a slice, add
  `&FullyQualifiedName~X` to the first filter.
- **Worktree:** `D:\projects\nova2\.worktrees\mux-phase4`, branch `feat/mux-phase4`, off
  `origin/dev-mux`.
  - Run `git rev-parse --abbrev-ref HEAD` before each commit; it must print `feat/mux-phase4`.
  - Never touch the parent checkout `D:\projects\nova2` or the branch `claude/ntilde-multiplexer-mbxx9b`.
  - Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **Layering:**
  - `Ntilde.Mux` references only Pty, VT, Replay and Mux.Contracts, and nothing from App or Avalonia.
  - `Ntilde.Mux.Daemon` references only `Ntilde.Mux`.
  - `Ntilde.Launcher` has no project or package references.
  - `Ntilde.Platform` references only Pty.
- **Protocol:** additive only, and `MuxProtocol` stays Min 1 / Max 2. JSON contexts stay
  source-generated with `WhenWritingNull`.
- **Threading:** no thread-pool work on the output path. Reconnect and liveness run on one
  `System.Threading.Timer` per host, never on a delivery thread. The proxy pumps on dedicated threads.
- **Remote endpoint:** reachable only through SSH. There is never a TCP listener, and the remote UDS
  is 0600 in a 0700 directory, as locally.
- **The proxy is a dumb byte pump.** Protocol logic stays in `MuxClient`/`MuxServer`.
- **Settings:** no new `TerminalSettings` field. The per-profile flag is
  `SshMuxOptions.PersistRemoteSessions`. With it off, or `SessionPersistence` off, nothing changes.
- **AOT:** IL2026/IL3050 are errors in every published exe. The remote binary is one file per RID:
  `linux-x64`, `linux-arm64`, `osx-arm64`. Its glibc floor is 2.35.
- **Install path** on the remote: `~/.local/share/ntilde/bin/ntilde-mux`, always invoked by absolute
  path or through `sh -c 'exec "$HOME/…/ntilde-mux" …'`, never through PATH.
- **Constants** (not settings): reconnect budget 10 min; backoff 1, 2, 4, 8, 16, 30 s with ±20%
  jitter; liveness ping every 15 s with a 10 s timeout; preamble scan cap 64 KiB; preamble line
  `NTILDE-MUX-PROXY 1 <pid>\n`.
- **Banner and notice texts** are verbatim from the spec, §7.4 and §7.5.
- **Tests** are deterministic: fake clocks and schedulers, no sleeps for correctness. A flaky test
  fixed by raising a timeout is not fixed.

## Review Focus

1. **A remote tab closed while its link is down.** The user meant to end that shell. The kill must be
   queued on the endpoint's host and delivered after the next reconnect, within the budget. It must not
   be dropped, which would orphan a shell nobody can adopt. Test in Task 20:
   `Kill_requested_while_disconnected_is_sent_after_reconnect`.
2. **The link comes back but the reattach fails** (attach RPC timeout, `session_attached` from an old
   daemon's zombie twin). The pane must re-enter the loop or show the Enter banner. It must never be
   left with no session and no banner. Test in Task 21:
   `Reattach_failure_after_reconnect_shows_the_enter_banner`.
3. **The login shell is fish, tcsh or nushell.** The connect, probe and install commands must parse in
   any shell: a bare safe absolute path, or `sh -c '<script without single quotes>'`. Test in Task 18:
   `Remote_command_is_shell_agnostic`, a theory that asserts the shape for recorded and default paths
   and rejects unsafe recorded paths.
4. **Several panes of one profile restoring at startup.** They must cause exactly one SSH connection,
   so one set of prompts. Test in Task 21: `Restoring_three_panes_of_one_profile_starts_one_exec`.
5. **The app is closed while a remote connect or prompt is pending.** Teardown must not hang: host
   dispose cancels the attempt, and the transport kills `ssh` or closes the native handle. Test in
   Task 20: `Dispose_during_a_blocked_remote_connect_returns_promptly`, using a fake transport that
   blocks until cancelled.

---

## Wave A — carry-overs (spec §4). Land these first.

### Task 1: Descriptor start time guards pid recycling (carry-over 1)

**Files:**
- Modify: `src/Ntilde.Mux.Contracts/MuxMessages.cs` (`MuxEndpointDescriptor`), `src/Ntilde.Mux.Contracts/MuxDiscovery.cs` (`IsProcessAlive`, `TryReadLiveDescriptor`)
- Modify: `src/Ntilde.Mux/MuxDaemonOptions.cs`, `src/Ntilde.Mux/MuxDaemonHost.cs` (`CreateDescriptor`)
- Modify: `src/Ntilde.App/Shell/Mux/MuxCommand.cs` (`KillByPid`)
- Test: `tests/Ntilde.Mux.Tests/Contracts/MuxDiscoveryTests.cs`, `tests/Ntilde.App.Tests/Shell/Mux/MuxCommandTests.cs`

**Interfaces:**
- Produces:
  - `MuxEndpointDescriptor.StartTime` (`long?`, UTC ticks, `[JsonIgnore(Condition = WhenWritingNull)]`).
  - `MuxDiscovery.IsProcessAlive(int pid, string processName, long? startTimeUtcTicks = null)`.
  - `public static bool MuxDiscovery.StartTimeMatches(Process process, long? startTimeUtcTicks)`:
    true when the ticks are null, or when `|process.StartTime.ToUniversalTime().Ticks - ticks| < TimeSpan.TicksPerSecond`.
    If reading `StartTime` throws `InvalidOperationException`, `Win32Exception` or
    `NotSupportedException`, it returns true, the conservative answer for a check that only refuses.
  - `MuxDaemonOptions.StartTimeUtcTicks` (`long?`). Its default is
    `Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks`, guarded like the above.

- [ ] **Step 1: Write the failing tests.** In `MuxDiscoveryTests`:
  ```csharp
  [Fact]
  public void A_descriptor_whose_start_time_differs_is_not_alive()
  {
      using Process self = Process.GetCurrentProcess();
      long real = self.StartTime.ToUniversalTime().Ticks;
      Assert.True(MuxDiscovery.IsProcessAlive(self.Id, self.ProcessName, real));
      Assert.True(MuxDiscovery.IsProcessAlive(self.Id, self.ProcessName, null));
      Assert.False(MuxDiscovery.IsProcessAlive(self.Id, self.ProcessName, real + TimeSpan.FromSeconds(5).Ticks));
  }

  [Fact]
  public void Descriptor_round_trips_start_time_and_omits_it_when_null() { /* serialize via MuxJsonContext; null → no "StartTime" key */ }
  ```
  Then extend the existing KillByPid test group in `MuxCommandTests`:
  `Force_refuses_a_pid_whose_start_time_changed`.
  1. Start a long-lived child: `ping -n 30 127.0.0.1` on Windows, `sleep 30` elsewhere.
  2. Write a descriptor naming its pid and real process name, with `StartTime = real + 5 s`.
  3. Call `KillByPid(path, force: true, …)`.
  4. Expect exit 1, stderr containing `is no longer the multiplexer`, and the child still running.
     Kill it in `finally`.

  Also add `Daemon_descriptor_records_the_start_time` to `tests/Ntilde.Mux.Tests/Server/MuxDaemonHostTests.cs`.
- [ ] **Step 2: Run the tests and verify they fail.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~MuxDiscoveryTests|FullyQualifiedName~MuxDaemonHostTests"`. Expected: a compile error (the `IsProcessAlive` overload and `StartTime` are missing).
- [ ] **Step 3: Implement.**
  - **Discovery:** `TryReadLiveDescriptor` passes `descriptor.StartTime`.
  - **KillByPid:** after the existing name check, and immediately before `process.Kill(...)`:
    ```csharp
    if (!MuxDiscovery.StartTimeMatches(process, d.StartTime))
    {
        stderr.WriteLine($"pid {d.Pid} is no longer the multiplexer; nothing was terminated.");
        return 1;
    }
    ```
  - **Host:** `MuxDaemonHost.CreateDescriptor` sets `StartTime = _options.StartTimeUtcTicks`.
- [ ] **Step 4: Run the tests and verify they pass.** Run the same Mux.Tests filter, then
  `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Lane!=PlatformBoot&FullyQualifiedName~MuxCommandTests" --blame-hang-timeout 5m`.
- [ ] **Step 5: Commit.** `fix(mux): record the daemon start time so kill-server --force cannot hit a recycled pid`

### Task 2: The startup probe deletes a descriptor only on a genuine refusal (carry-over 2)

**Files:**
- Modify: `src/Ntilde.App/Shell/Mux/MuxStartupProbe.cs`
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxStartupProbeTests.cs`

**Interfaces:**
- Produces `internal static bool MuxStartupProbe.IsGenuineRefusal(Exception ex, string endpoint, bool isWindows, Func<string, bool> fileExists)`:
  - Windows: true iff `ex` is a `FileNotFoundException`.
  - Elsewhere: true iff `ex` (or its inner exception) is a `SocketException` with
    `SocketErrorCode == SocketError.ConnectionRefused`, or `!fileExists(endpoint)`.

- [ ] **Step 1: Write the failing tests.**
  - Theory `Only_a_refusal_or_a_missing_socket_deletes_the_descriptor`. Rows:
    - `ConnectionRefused` → deleted;
    - `AccessDenied` → kept;
    - `TryAgain` → kept;
    - `AddressFamilyNotSupported` → kept;
    - a plain `IOException` while the socket file exists → kept;
    - a plain `IOException` while the file is missing → deleted.
  - Drive `IsDaemonLive` with the existing `connect` seam: throw
    `new IOException("x", new SocketException((int)code))`, and a `fileExists` seam (add an optional
    parameter `Func<string,bool>? fileExists = null`). Assert the return value and whether the
    descriptor file still exists.
  - Run the Unix rows on every OS by passing `isWindows: false` to the pure function. The
    end-to-end `IsDaemonLive` rows are gated with `Assert.SkipWhen(OperatingSystem.IsWindows(), …)`.
- [ ] **Step 2: Run the tests and verify they fail.** `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Lane!=PlatformBoot&FullyQualifiedName~MuxStartupProbeTests" --blame-hang-timeout 5m`
- [ ] **Step 3: Implement.** Replace the `IOException or SocketException` catch with:
  ```csharp
  catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
  {
      if (!IsGenuineRefusal(ex, d.Endpoint, OperatingSystem.IsWindows(), fileExists ?? File.Exists)) return true;
      MuxDiscovery.DeleteDescriptorIfOwned(descriptorPath, d.Pid);
      return false;
  }
  ```
  Update the class remarks: everything that is not a refusal counts as live.
- [ ] **Step 4: Run the tests and verify they pass.** Run the same filter.
- [ ] **Step 5: Commit.** `fix(mux): keep a live daemon's descriptor unless the probe was genuinely refused`

### Task 3: Descriptor repair cannot clobber a newer descriptor (carry-over 7)

**Files:**
- Modify: `src/Ntilde.Mux/MuxDaemonHost.cs` (`EnsureDescriptor`, ~L136-171), `src/Ntilde.Mux.Contracts/MuxDiscovery.cs` (add `TryWriteDescriptorIfAbsent`)
- Test: `tests/Ntilde.Mux.Tests/Server/MuxDaemonHostTests.cs`

**Interfaces:**
- Produces:
  - `public static bool MuxDiscovery.TryWriteDescriptorIfAbsent(string path, MuxEndpointDescriptor d)`:
    a temp file, then `File.Move(temp, path, overwrite: false)`. It returns false and deletes the
    temp file on `IOException` when the target exists.
  - `public static bool MuxDiscovery.TryReplaceDescriptorIfUnchanged(string path, string expectedContent, MuxEndpointDescriptor d)`:
    re-reads the file, using the same share flags as `TryReadDescriptor`. If the text equals
    `expectedContent`, it writes through the existing atomic replace and returns true; otherwise it
    returns false.
  - Read `EnsureDescriptor` first. Keep its logging, and capture the raw text it judged so it can be
    passed as `expectedContent`.

- [ ] **Step 1: Write the failing tests.** `Repair_does_not_overwrite_a_descriptor_written_meanwhile`.
  - **Missing case:** call the repair entry point (the `Tick`/`EnsureDescriptor` path that the existing
    tests reach through `MuxDaemonHost` seams) with a hook that writes a foreign descriptor (another
    pid) between the existence check and the write. Assert the foreign content survives.
  - **Dead-pid case:** the hook changes the file between the read and the replace. Assert the change
    survives.
  - If the host has no seam, add `internal Action? BeforeDescriptorWriteForTest`, called between the
    decision and the write.
- [ ] **Step 2: Run the tests and verify they fail.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~MuxDaemonHostTests"`
- [ ] **Step 3: Implement** with the two new discovery helpers. Log `[Mux] descriptor written by another daemon meanwhile; leaving it` when a write is skipped.
- [ ] **Step 4: Run the tests and verify they pass.** Run the same filter.
- [ ] **Step 5: Commit.** `fix(mux): descriptor repair uses create-new and compare-before-replace`

### Task 4: Text client autowrap, and Unix `EAGAIN` (carry-overs 4 and 6)

**Files:**
- Modify: `src/Ntilde.Mux/TextClient/TextClientRenderer.cs` (~L27-30), `src/Ntilde.Mux/TextClient/UnixConsoleSurface.cs` (~L108-126 and the P/Invokes)
- Test: `tests/Ntilde.Mux.Tests/TextClient/TextClientRendererTests.cs`, `tests/Ntilde.Mux.Tests/TextClient/UnixConsoleSurfaceTests.cs` (new)

**Interfaces:**
- Produces `internal static int UnixConsoleSurface.ReadFd(int fd, byte[] buffer, int count)`. It is the
  extracted read loop:
  - It retries on `EINTR`.
  - On `EAGAIN`/`EWOULDBLOCK` it calls `poll`, then retries.
  - It returns ≤ 0 only on EOF or another error.
  - Errno values: `EAGAIN` is 11 on Linux and 35 on macOS; `EWOULDBLOCK` equals `EAGAIN` on both.
    Read it with `Marshal.GetLastPInvokeError()` on a `SetLastError = true` import.
  - New P/Invokes: `[DllImport("libc", SetLastError = true)] private static extern int poll(ref PollFd fds, nuint nfds, int timeout);`
    and `[StructLayout(LayoutKind.Sequential)] private struct PollFd { public int Fd; public short Events; public short REvents; }`
    with `POLLIN = 1`.

- [ ] **Step 1: Write the failing tests.**
  - `Enter_turns_autowrap_off_and_leave_turns_it_on`: `TextClientRenderer.EnterSequence` contains
    `"\x1b[?7l"`, and `LeaveSequence` contains `"\x1b[?7h"` and ends with `"\x1b[?1049l"`.
  - `Eagain_waits_instead_of_closing_input`, gated `Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux: the test sets O_NONBLOCK through fcntl, which is variadic")`:
    1. Create a pipe with P/Invokes in the test: `pipe(int[2])`, plus `fcntl(fd, F_SETFL, O_NONBLOCK)`
       with `F_SETFL = 4` and `O_NONBLOCK = 0x800`.
    2. Start `ReadFd(readEnd, buf, 16)` on a thread.
    3. Wait 200 ms, assert it has not returned, then write `"hi"` to the write end.
    4. Join for up to 5 s. Assert it returns 2 and the bytes are `"hi"`.
- [ ] **Step 2: Run the tests and verify they fail.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~TextClientRendererTests|FullyQualifiedName~UnixConsoleSurfaceTests"`. The Unix test skips on Windows: also run it in the Docker Linux run of Task 12 Step 7, or rely on CI ubuntu.
- [ ] **Step 3: Implement.**
  - Add `?7l` to `EnterSequence` (after `?1049h`) and `?7h` to `LeaveSequence` (before `?1049l`).
  - Move the read loop into `ReadFd` and call it from `Read`.
  - Add a comment for §2 decision 8: why `poll` and not `fcntl`.
- [ ] **Step 4: Run the tests and verify they pass.** Run the same filter, plus the whole `TextClient` folder:
  `--filter "FullyQualifiedName~Ntilde.Mux.Tests.TextClient"`. Existing exact-sequence assertions may
  need the new codes; update them and say so in the commit body.
- [ ] **Step 5: Commit.** `fix(mux): text client turns outer autowrap off; EAGAIN on stdin waits instead of detaching`

### Task 5: Kitty keyboard is the AND over interactive clients; `InteractiveClients` on the wire (carry-overs 5 and 9, server side)

**Files:**
- Modify: `src/Ntilde.Mux.Contracts/MuxMessages.cs` (`SessionChangedNotification`, `SessionInfoResult`)
- Modify: `src/Ntilde.Mux/HeadlessTerminalSession.cs` (`ApplyPresentation` ~L843, `ExecuteAttachCore` ~L729, `ApplyPendingResize` ~L349, `PublishAttachedCount` ~L976, `FlushSessionChanged` ~L1004, the detach and drop paths), `src/Ntilde.Mux/MuxServerConnection.cs` (sessionInfo fill ~L493)
- Test: `tests/Ntilde.Mux.Tests/Headless/HeadlessKittyPresentationTests.cs` (new), `tests/Ntilde.Mux.Tests/Headless/SessionEventsTests.cs`

**Interfaces:**
- Produces:
  - `SessionChangedNotification.InteractiveClients` and `SessionInfoResult.InteractiveClients` (`int?`, written only when non-null).
  - `HeadlessTerminalSession.KittyKeyboardEnabled` (an internal getter for tests: the parser's current
    flag, read through `InvokeAsync`).

- [ ] **Step 1: Write the failing tests.**
  - `Kitty_is_the_and_over_interactive_clients`:
    1. Sink A attaches Shared with `KittyKeyboardEnabled = true`. Assert true.
    2. Sink B attaches Shared with `false`. Assert false.
    3. B detaches. Assert true.
    4. A read-only sink C attaches with `false`. Assert still true.
    5. A resize from A carrying `false`. Assert false.
  - `With_no_interactive_client_the_last_value_stays`.
  - In `SessionEventsTests`:
    - `ReadOnly_to_shared_reattach_is_a_change`: one v2 sink attaches ReadOnly, then re-attaches Shared.
      Expect a `sessionChanged` with `AttachedClients == 1` and `InteractiveClients == 1`, after the
      first one carried `InteractiveClients == 0`.
    - `SessionInfo_carries_InteractiveClients`.
  - Use the existing `RecordingFrameSink` with `WantsSessionEvents = true`, and the fake clock or
    interval seams the file already uses.
- [ ] **Step 2: Run the tests and verify they fail.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~HeadlessKittyPresentationTests|FullyQualifiedName~SessionEventsTests"`
- [ ] **Step 3: Implement.**
  - **Parse-thread state:** `Dictionary<IMuxFrameSink, bool> _kittyBySink` (parse thread only).
  - **Recording:** `ApplyPresentation(sink, presentation)` records `_kittyBySink[sink] = presentation.KittyKeyboardEnabled` (interactive sinks only), then `RecomputeKitty()`:
    ```csharp
    private void RecomputeKitty()
    {
        bool any = false, all = true;
        foreach (IMuxFrameSink s in _subscribers)
        {
            if (_readOnlySinks.Contains(s) || !_kittyBySink.TryGetValue(s, out bool k)) continue;
            any = true; all &= k;
        }
        if (any) _parser.KittyKeyboardEnabled = all; // none interactive: keep the last value
    }
    ```
  - **Cleanup:** every path that removes a subscriber (detach, drop, fault, exit) removes it from
    `_kittyBySink` and calls `RecomputeKitty()`.
  - **Change detection:** `PublishAttachedCount` compares `(attached, interactive)` with the last
    published pair.
  - **Payloads:** `FlushSessionChanged` writes `InteractiveClients = _interactive`, and `sessionInfo`
    fills it from `InteractiveClients`.
  - Keep every existing exact-sequence test green. A v1 sink never gets `sessionChanged`, so nothing
    changes there.
- [ ] **Step 4: Run the tests and verify they pass.** Run the whole project: `scripts/build.ps1 test tests/Ntilde.Mux.Tests`.
- [ ] **Step 5: Commit.** `fix(mux): kitty keyboard follows every interactive client; publish InteractiveClients`

### Task 6: GUI counts interactive peers only (carry-over 9, client side)

**Files:**
- Modify: `src/Ntilde.Mux/MuxClientSession.cs` (`DeliverSessionChanged`, `RefreshSharingAsync`, `RefreshSessionInfoAsync`)
- Modify: `src/Ntilde.App/Controls/TerminalPane.axaml.cs` (`ApplyMuxSharing` ~L3744 and its callers), `src/Ntilde.App/MainWindow.axaml.cs` (`DecidePaneCloseAsync` ~L6021, the skipConfirm Leave decision: grep `PaneDisposition.Leave`)
- Test: `tests/Ntilde.Mux.Tests/Client/MuxClientSessionSharingTests.cs` (new or existing), `tests/Ntilde.App.Tests/Controls/MuxPaneSharingTests.cs`, `tests/Ntilde.App.Tests/Core/MainWindowMuxSharingTests.cs`

**Interfaces:**
- Produces:
  - `MuxClientSession.InteractiveClients` (`int?`).
  - `MuxClientSession.InteractiveOthers` (`int?`): `InteractiveClients - 1` when known, else
    `AttachedClients - 1`, floored at 0, and null when both are unknown.
  - `TerminalPane.ApplyMuxSharing(int? interactiveOthers)`, so callers pass the others count.
    `MuxOtherClients` keeps its name and meaning: interactive others.

- [ ] **Step 1: Write the failing tests.**
  - Client: a `sessionChanged` with `AttachedClients = 2, InteractiveClients = 1` gives
    `InteractiveOthers == 0`. Without `InteractiveClients` it gives 1, the fallback.
  - Pane (`MuxPaneSharingTests.A_read_only_peer_does_not_show_the_badge`): with a fake mux session
    raising `SessionChanged` for (2, 1), the badge stays hidden.
  - MainWindow:
    - `Closing_a_pane_watched_read_only_does_not_prompt`: the listSessions summary has
      `AttachedClients 2, InteractiveClients 1`, and the close goes straight to `EndSession`.
    - `Agent_close_with_only_a_read_only_peer_kills`: skipConfirm gives `EndSession`, not `Leave`.
  - Use the existing harnesses in those files.
- [ ] **Step 2: Run the tests and verify they fail.** Run Mux.Tests with the client filter, then App.Tests `Lane!=PlatformBoot&(FullyQualifiedName~MuxPaneSharingTests|FullyQualifiedName~MainWindowMuxSharingTests)`.
- [ ] **Step 3: Implement.**
  - Feed `InteractiveClients` from the notification, from `RefreshSessionInfoAsync`, and from
    `RefreshSharingAsync`. For `RefreshSharingAsync`, v2 uses `summary.InteractiveClients` (absent
    means 0); v1 leaves it null.
  - Replace `AttachedClients - 1` / `> 1` in the pane and the close decision with `InteractiveOthers`.
- [ ] **Step 4: Run the tests and verify they pass.** Run both filters, plus App.Tests `Lane!=PlatformBoot&FullyQualifiedName~Mux`.
- [ ] **Step 5: Commit.** `fix(mux): badge and close decisions count interactive peers, not read-only observers`

### Task 7: Shared tabs restore shared; WirePane unsubscribe; dialog keys (carry-overs 3, 8 and 10)

**Files:**
- Modify: `src/Ntilde.Pty/SessionModels.cs` (`PaneNode.MuxShared`), `src/Ntilde.App/Shell/SessionManager.cs` (`WriteMuxIds` ~L463, `ApplyRestoredMuxId` ~L484), `src/Ntilde.App/Controls/TerminalPane.axaml.cs` (expose `internal bool MuxSessionIsShare => _muxSessionIsShare;`)
- Modify: `src/Ntilde.App/MainWindow.axaml.cs` (`WirePane` ~L4690; `ShowMuxSessionPickerAsync` ~L4970; `ShowSharedCloseDialogAsync` ~L6062)
- Test: `tests/Ntilde.App.Tests/Shell/SessionManagerMuxTests.cs` (or wherever the WriteMuxIds tests live), `tests/Ntilde.App.Tests/Core/MainWindowMuxSharingTests.cs`, `tests/Ntilde.App.Tests/Core/MainWindowMuxLifecycleTests.cs`

**Interfaces:**
- Produces:
  - `PaneNode.MuxShared` (`bool`, `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]`).
  - `internal static Window MainWindow.BuildMuxSessionPickerWindow(...)` and
    `BuildSharedCloseWindow(int others)`, extracted so headless tests can show them and press keys.
    Both return the window plus a `Task<T>` result.

- [ ] **Step 1: Write the failing tests.**
  - `Shared_panes_save_and_restore_shared`. Capture a tab whose pane has `MuxSessionIsShare = true`;
    the node has `MuxShared = true`. Restore it; the pane's `MuxAttachSharedToRestore == true`. Then,
    with a fake factory, the restored pane's request has `AttachShared = true`, and there is no
    `AttachedElsewhere` notice.
  - `WirePane_twice_closes_an_adoption_loss_once`. Wire the same pane twice (via
    `ApplySessionPersistenceSetting` off→on, or by calling `WirePane` directly if it is internal),
    raise `MuxAdoptionLost`, and assert `ClosePaneAsync` ran once. Use the existing
    close-counting seam, or count tab removals.
  - `[AvaloniaFact]` `Picker_Enter_attaches_and_Escape_cancels` and
    `Shared_close_Enter_detaches_and_Escape_cancels`. Show the extracted windows headlessly
    (`Show()` + `Dispatcher.UIThread.RunJobs()`), send `KeyDown` Enter or Escape through
    `window.KeyPressQwerty` / `RaiseEvent(new KeyEventArgs …)` as the repo's headless tests do, and
    assert the result.
- [ ] **Step 2: Run the tests and verify they fail.** App.Tests `Lane!=PlatformBoot&(FullyQualifiedName~SessionManager|FullyQualifiedName~MainWindowMux)`.
- [ ] **Step 3: Implement.**
  - `WriteMuxIds` sets `leaf.MuxShared = pane.MuxSessionIsShare` whenever it writes an id. For a
    pending id, it uses `pane.MuxAttachSharedToRestore`.
  - `ApplyRestoredMuxId` sets `pane.MuxAttachSharedToRestore = node.MuxShared`.
  - `WithoutMuxIds` clears `MuxShared`.
  - `WirePane` adds `pane.MuxAdoptionLost -= OnPaneMuxAdoptionLost;` to its unsubscribe block.
  - Picker: `Attach` gets `IsDefault = true`, `Cancel` gets `IsCancel = true`.
  - Shared-close: `Detach` gets `IsDefault = true`, `Cancel` gets `IsCancel = true`, and nothing is
    default on Close. Give Cancel initial focus, as at L8690.
- [ ] **Step 4: Run the tests and verify they pass.** Run the same filter.
- [ ] **Step 5: Commit.** `fix(mux): shared tabs restore shared; WirePane unsubscribes MuxAdoptionLost; dialogs take Enter/Escape`

---

## Wave B — move the daemon core and the CLI into Ntilde.Mux (spec §6)

### Task 8: `Ntilde.Mux.Daemon`: launcher, spawner, exit wait, probe, serve host, paths

**Files:**
- Move (`git mv`, then fix the namespace to `Ntilde.Mux.Daemon` and make the types `public`):
  - `src/Ntilde.App/Shell/Mux/MuxDaemonLauncher.cs` → `src/Ntilde.Mux/Daemon/MuxDaemonLauncher.cs`, with `MuxUnavailableException`;
  - `MuxDaemonSpawner.cs` → `Daemon/MuxDaemonSpawner.cs`;
  - `MuxDaemonExit.cs` → `Daemon/MuxDaemonExit.cs`;
  - `MuxStartupProbe.cs` → `Daemon/MuxStartupProbe.cs`.
- Move and rename: `src/Ntilde.App/Shell/Mux/MuxDaemonProcess.cs` → `src/Ntilde.Mux/Daemon/MuxServeHost.cs`; `MuxServeOptions` moves with it.
- Create:
  - `src/Ntilde.Mux/Daemon/MuxPaths.cs`;
  - `src/Ntilde.Mux/Daemon/MuxLogFile.cs`, copied from `src/Ntilde.App/Shell/RotatingFileLogWriter.cs`; the App copy stays;
  - `src/Ntilde.Mux/Daemon/LocalShellSessionFactory.cs`.
- Modify every App caller: `src/Ntilde.App/Program.cs`, `MainWindow.axaml.cs`, `MuxConnectionHost.cs`, `MuxTerminalSessionFactory.cs`, `MuxCommand.cs` (serve now calls `MuxServeHost.Run`).
- Move tests:
  - `tests/Ntilde.App.Tests/Shell/Mux/MuxDaemonLauncherTests.cs` and `MuxStartupProbeTests.cs` → `tests/Ntilde.Mux.Tests/Daemon/`;
  - from `MuxReview2Tests.cs`, the movable rows (TryStart, lock-held exit 3, `$APPIMAGE`) → `tests/Ntilde.Mux.Tests/Daemon/MuxDaemonReviewTests.cs`;
  - `tests/Ntilde.App.Tests/Core/RotatingFileLogWriterTests.cs` is copied as `tests/Ntilde.Mux.Tests/Daemon/MuxLogFileTests.cs`.
- Test (new): `tests/Ntilde.Mux.Tests/Daemon/LocalShellSessionFactoryTests.cs`, `tests/Ntilde.Mux.Tests/Daemon/MuxPathsTests.cs`.

**Interfaces:**
- Produces:
  ```csharp
  namespace Ntilde.Mux.Daemon;
  public sealed record MuxPaths(string Root)
  {
      public static MuxPaths Default() => new(MuxDiscovery.GetRootDirectory());
      public string DescriptorPath => MuxDiscovery.GetDescriptorPath(Root);
      public string Endpoint => MuxDiscovery.GetDefaultEndpoint(Root);
      public string LogDirectory { get; init; } = Path.Combine(Root, "logs");
  }
  public sealed record MuxServeOptions(TimeSpan IdleExitAfter, bool Foreground);
  public static partial class MuxServeHost
  {
      public const int LockHeldExitCode = 3;
      public static int Run(MuxServeOptions options, MuxPaths paths, ITerminalSessionFactory sessionFactory, TextWriter stderr);
  }
  public sealed class LocalShellSessionFactory : ITerminalSessionFactory   // Ntilde.Pty.ITerminalSessionFactory
  {
      public static readonly LocalShellSessionFactory Instance = new();
      public ITerminalSession Create(TerminalSessionRequest request);
      internal static (string Command, string Arguments) ResolveShell(string command, string arguments, Func<string> defaultShell);
      internal static string ResolveWorkingDirectory(string? requested, string home, Func<string, bool> directoryExists);
  }
  public interface IMuxDaemonSpawner { void Spawn(); int? LastSpawnExitCode => null; }
  public sealed partial class ProcessMuxDaemonSpawner : IMuxDaemonSpawner
  {
      public static ProcessMuxDaemonSpawner CreateDefault(IReadOnlyList<string> serveArguments);  // GUI: ["mux","serve"]
      // ResolveDaemonExecutable($APPIMAGE rule) unchanged
  }
  public sealed class MuxDaemonLauncher
  {
      public static MuxDaemonLauncher CreateDefault(Action<string>? log, IReadOnlyList<string> serveArguments, MuxPaths? paths = null);
      public Task<MuxClient> EnsureConnectedAsync(CancellationToken ct);       // unchanged behaviour
      public Task<(Stream Stream, MuxEndpointDescriptor Descriptor)> EnsureEndpointStreamAsync(CancellationToken ct); // no hello
      public static bool IsTrustedEndpoint(string endpoint, string root, out string? reason);
      public const string KillServerHint = /* unchanged text */;
  }
  public static class MuxStartupProbe { public static bool IsDaemonLive(...); internal static bool IsGenuineRefusal(...); internal static bool DefaultPipeExists(string); }
  public static class MuxDaemonExit { public static bool WaitForExit(...); }
  ```
- `ResolveShell`:
  - An empty or whitespace command resolves to `defaultShell()`.
  - When the basename is one of `bash zsh fish ksh sh dash` and the arguments are empty, the arguments
    become `-l`.
  - Otherwise both pass through unchanged.
- `ResolveWorkingDirectory`: empty → home; `~` → home; `~/x` → `home/x`. If the result does not exist,
  it returns home.
- `LocalShellSessionFactory.Create` throws `NotSupportedException` for `request.Ssh != null`. Otherwise
  it returns `new RustPtySession(cmd, cols, rows, args, cwd, skipPowerShellPostLaunchInit: …, environmentOverrides: request.EnvironmentOverrides)`;
  check the exact constructor in `src/Ntilde.Pty/RustPtySession.cs`.
- `MuxServeHost.Run`:
  - It is today's `MuxDaemonProcess.Run`, with `AppPaths.LogsDirectory` replaced by
    `paths.LogDirectory`, `RotatingFileLogWriter` by `MuxLogFile`, and the hard-coded
    `DefaultTerminalSessionFactory` by the parameter.
  - Discovery calls use `paths.Endpoint` and `paths.DescriptorPath`. That fixes the gap where `serve`
    ignored `rootOverride`.
- `EnsureEndpointStreamAsync` is the existing `TryConnectExisting` / spawn / poll loop without the
  `MuxClient.ConnectAsync`. `EnsureConnectedAsync` becomes:
  ```csharp
  (Stream stream, _) = await EnsureEndpointStreamAsync(ct);
  try { return await MuxClient.ConnectAsync(stream, _clientOptions, ct); }
  catch (MuxProtocolException ex) when (ex.Code == MuxErrorCodes.VersionMismatch) { throw new MuxUnavailableException(..., versionMismatch: true); }
  ```
  Keep today's exact mismatch handling: read the current code and preserve its messages.

- [ ] **Step 1: Write the failing tests.**
  - `LocalShellSessionFactoryTests`: theories over `ResolveShell` and `ResolveWorkingDirectory`, and
    `Ssh_requests_are_refused`.
  - `MuxPathsTests`: the defaults derive from the root.
  - `MuxDaemonLauncherTests.EnsureEndpointStreamAsync_returns_a_raw_stream_without_a_hello`: an
    in-process `MuxServer` + `MuxDaemonHost` on a temp root; the stream's first write is a hello sent
    by the test.
- [ ] **Step 2: Do the moves.** Use `git mv` so history follows. Then run
  `scripts/build.ps1 build src/Ntilde.App` and fix the compile errors. `Ntilde.Mux` has
  `TreatWarningsAsErrors=true`: fix every warning in the moved code (for example, add
  `DefaultDllImportSearchPaths` on the Windows imports, as `WindowsConsoleSurface` does). Do not
  suppress warnings.
- [ ] **Step 3: Run the tests and verify the new ones fail.** Then implement `MuxPaths`, `MuxLogFile`, `LocalShellSessionFactory` and `EnsureEndpointStreamAsync`.
- [ ] **Step 4: Run.**
  - `scripts/build.ps1 test tests/Ntilde.Mux.Tests`;
  - `scripts/build.ps1 test tests/Ntilde.Architecture.Tests` (expect green: moved types are in
    `Ntilde.Mux.Daemon`, and Mux still has no App or Platform reference);
  - App.Tests `Lane!=PlatformBoot&FullyQualifiedName~Mux`;
  - App.Tests `Lane=PlatformBoot`.
- [ ] **Step 5: Commit.** `refactor(mux): move the daemon core into Ntilde.Mux.Daemon with injected paths`

### Task 9: `Ntilde.Mux.Cli`: the verbs, behind a thin App adapter

**Files:**
- Create: `src/Ntilde.Mux/Cli/MuxCli.cs`, `MuxCliHost.cs`, `MuxCliVerbs.cs`.
- Move: the verb bodies out of `src/Ntilde.App/Shell/Mux/MuxCommand.cs` (ls, kill, kill-server, KillByPid, attach and its helpers, probe-console, serve parsing, usage text, `ConsoleFactoryForTest`).
- Modify: `src/Ntilde.App/Shell/Mux/MuxCommand.cs` becomes the adapter.
- Move tests: most of `tests/Ntilde.App.Tests/Shell/Mux/MuxCommandTests.cs` → `tests/Ntilde.Mux.Tests/Cli/MuxCliTests.cs`. Keep in App.Tests: dispatch, `AttachConsoleHint`, and `Serve_honours_the_root_override` (new).
- Modify: `tests/Ntilde.Architecture.Tests/LayeringTests.cs`. Add `Mux_daemon_and_cli_namespaces_have_no_app_or_platform_dependency`, with types in `Ntilde.Mux.Daemon`/`Ntilde.Mux.Cli` forbidding `Ntilde.Shell`, `Ntilde.Platform`, `Avalonia`.

**Interfaces:**
- Produces:
  ```csharp
  namespace Ntilde.Mux.Cli;
  [Flags] public enum MuxCliVerbs { None = 0, Serve = 1, Ls = 2, Kill = 4, KillServer = 8, Attach = 16, ProbeConsole = 32, Proxy = 64, Version = 128 }
  public sealed class MuxCliHost
  {
      public required MuxPaths Paths { get; init; }
      public required string UsagePrefix { get; init; }               // "ntilde mux" | "ntilde-mux"
      public required IReadOnlyList<string> ServeArguments { get; init; }
      public required Func<ITerminalSessionFactory> SessionFactory { get; init; }
      public required MuxCliVerbs Verbs { get; init; }
      public Action? PrepareForegroundConsole { get; init; }
      public bool AttachedToParentConsole { get; init; }
      public bool IsGuiExecutable { get; init; }                       // for AttachConsoleHint (removed in Task 25)
      public string Version { get; init; } = "";
  }
  public static class MuxCli
  {
      public static int Execute(string[] verbArgs, TextWriter stdout, TextWriter stderr, MuxCliHost host);
      public static bool IsKnownVerb(string verb, MuxCliVerbs verbs);
      internal static int KillByPid(string descriptorPath, bool force, TextWriter stdout, TextWriter stderr);
      internal static Func<IConsoleSurface>? ConsoleFactoryForTest { get; set; }
  }
  ```
- The App adapter (`Ntilde.Shell.Mux.MuxCommand`) keeps `IsSupportedCliMode`, `IsServe`, `IsAttach`,
  `NeedsInteractiveConsole`, `AttachedToParentConsole` and `Execute(string[] args, TextWriter stdout, TextWriter stderr, string? rootOverride = null)`
  byte-compatible. `Execute` builds the host:
  - `Paths = new MuxPaths(rootOverride ?? MuxDiscovery.GetRootDirectory())`;
  - `UsagePrefix = "ntilde mux"`;
  - `ServeArguments = ["mux","serve"]`;
  - `SessionFactory = () => DefaultTerminalSessionFactory.Instance`;
  - `Verbs = Serve|Ls|Kill|KillServer|Attach|ProbeConsole`;
  - `PrepareForegroundConsole = CliConsoleBindings.Prepare`;
  - `AttachedToParentConsole = AttachedToParentConsole`;
  - `IsGuiExecutable = !IsCliShim()`, using whatever the attach hint uses today.

  It then returns `MuxCli.Execute(args[1..], stdout, stderr, host)`. The exit codes and usage text are
  unchanged for the App: the usage lines still read `ntilde mux …`.

- [ ] **Step 1: Write the failing test.** `Serve_honours_the_root_override` (App.Tests): `MuxCommand.Execute(["mux","serve","--foreground","--idle-exit-minutes","0"], …, rootOverride: temp)` on a background thread. Wait until `MuxDiscovery.GetDescriptorPath(temp)` exists, then `kill-server` with the same root. Expect exit 0.
- [ ] **Step 2: Move the code and the tests.** Fix the namespaces. Mux.Tests already link the Support helpers. `CliCommandDispatchTests` must stay green unchanged: `MuxCommand` remains in App with `IsSupportedCliMode`, and `Program.cs` mux lines are unchanged.
- [ ] **Step 3: Implement the adapter** and `MuxCli.IsKnownVerb`. An unknown or disabled verb prints usage and exits 2.
- [ ] **Step 4: Run.**
  - `scripts/build.ps1 test tests/Ntilde.Mux.Tests`;
  - `scripts/build.ps1 test tests/Ntilde.Architecture.Tests`;
  - App.Tests `Lane!=PlatformBoot&FullyQualifiedName~Mux`;
  - `scripts/build.ps1 build src/Ntilde.Cli`.
- [ ] **Step 5: Commit.** `refactor(mux): mux verbs live in Ntilde.Mux.Cli; the App's MuxCommand is a thin adapter`

---

## Wave C — the remote binary (spec §3, §8.1, §10)

### Task 10: `HelloParams.ClientInstanceId` evicts dead twins

**Files:**
- Modify: `src/Ntilde.Mux.Contracts/MuxMessages.cs` (`HelloParams`), `src/Ntilde.Mux/MuxClientOptions.cs`, `src/Ntilde.Mux/MuxClient.cs` (the hello), `src/Ntilde.Mux/MuxServer.cs`, `src/Ntilde.Mux/MuxServerConnection.cs` (hello handling, connection registry)
- Test: `tests/Ntilde.Mux.Tests/Server/ClientInstanceIdTests.cs` (new)

**Interfaces:**
- Produces:
  - `HelloParams.ClientInstanceId` (`string?`);
  - `MuxClientOptions.ClientInstanceId` (`string?`, default null);
  - `internal void MuxServer.EvictTwins(MuxServerConnection keep, string instanceId)`.
  - **Rule:** on a hello whose id is non-null and at most 64 characters, the server closes every other
    live connection with that same id (`connection.Close("superseded by a reconnect of the same client")`)
    **before** it sends `welcome`. A longer id is ignored and logged.

- [ ] **Step 1: Write the failing tests.**
  - `A_reconnect_with_the_same_instance_id_evicts_the_dead_twin`:
    1. Client A (id "x") attaches to a session.
    2. Client B (id "x") connects on a new in-memory connection.
    3. Assert A gets `Disconnected`, and the session's `AttachedClients` drops back to the count of B's
       attaches (0 before B attaches).
  - `Different_instance_ids_are_independent`.
  - `No_instance_id_never_evicts`.
- [ ] **Step 2: Run the tests and verify they fail.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~ClientInstanceIdTests"`
- [ ] **Step 3: Implement.**
  - The server keeps its connections in a `ConcurrentDictionary<MuxServerConnection, string?>`, or
    reuses the existing set if there is one.
  - The connection records its instance id after parsing the hello. It calls
    `_server.EvictTwins(this, id)` before writing `welcome`.
  - The client sends the option in its hello.
- [ ] **Step 4: Run the whole Mux.Tests project.**
- [ ] **Step 5: Commit.** `feat(mux): a client instance id lets a reconnect evict its dead twin`

### Task 11: `StdioMuxTransport`, `ntilde-mux proxy --stdio`, `--version`

**Files:**
- Create: `src/Ntilde.Mux/Transport/StdioMuxTransport.cs` (+ `MuxProxyHandshakeException`, `DuplexStdioStream`), `src/Ntilde.Mux/Cli/MuxProxyCommand.cs`, `src/Ntilde.Mux/Cli/MuxVersionInfo.cs` (+ `MuxCliJsonContext`)
- Modify: `src/Ntilde.Mux/Cli/MuxCli.cs` (the `proxy` and `--version` verbs)
- Test: `tests/Ntilde.Mux.Tests/Transport/StdioMuxTransportTests.cs`, `tests/Ntilde.Mux.Tests/Cli/MuxProxyTests.cs`, `tests/Ntilde.Mux.Tests/Cli/MuxVersionTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace Ntilde.Mux.Transport;
  public static class MuxProxyPreamble { public const string Prefix = "NTILDE-MUX-PROXY"; public const int Version = 1; public const int MaxScanBytes = 64 * 1024;
      public static byte[] Format(int daemonPid); }          // "NTILDE-MUX-PROXY 1 <pid>\n" ASCII
  public sealed class MuxProxyHandshakeException : IOException { public string CapturedText { get; } }
  public sealed record StdioMuxConnection(Stream Stream, int DaemonPid);
  public static class StdioMuxTransport
  {
      public static Task<StdioMuxConnection> ConnectAsync(Stream remoteStdout, Stream remoteStdin, TimeSpan preambleTimeout, CancellationToken ct);
      internal static string Escape(ReadOnlySpan<byte> captured);   // control chars → \xNN, last 2 KiB
  }
  namespace Ntilde.Mux.Cli;
  public static class MuxProxyExitCodes { public const int StdinClosed = 0, DaemonUnavailable = 1, Usage = 2, DaemonClosed = 3; }
  public static class MuxProxyCommand
  {
      public static int Run(Stream stdin, Stream stdout, TextWriter stderr, Func<CancellationToken, Task<(Stream Stream, MuxEndpointDescriptor Descriptor)>> connectDaemon);
  }
  public sealed record MuxVersionInfo(string Version, int ProtocolMin, int ProtocolMax, string Rid, string Path);
  [JsonSerializable(typeof(MuxVersionInfo))] internal partial class MuxCliJsonContext : JsonSerializerContext;   // camelCase
  ```
- **Preamble scan.**
  - Read into a growing buffer, in chunks of at most 4096 bytes.
  - After each chunk, search for `"NTILDE-MUX-PROXY "` at the start of a line: buffer start, or after
    `\n`.
  - When a full line `NTILDE-MUX-PROXY 1 <digits>\n` is present, the bytes after the `\n` become the
    leftover. A `\r` before the `\n` is tolerated.
  - Version ≠ 1 throws `MuxProxyHandshakeException("unsupported proxy version")`.
  - More than `MaxScanBytes` without a preamble, EOF, or the timeout all throw with the captured text.
- **`DuplexStdioStream`.**
  - `Read` drains the leftover first, then reads `remoteStdout`.
  - `Write` and `Flush` go to `remoteStdin`.
  - `Dispose` disposes `remoteStdin` first (EOF to the proxy), then `remoteStdout`.
  - `CanRead`, `CanWrite`: true; `CanSeek`: false.
- **`MuxProxyCommand.Run`.**
  1. Call `connectDaemon(CancellationToken.None)`. On `IOException`, `TimeoutException` or
     `MuxUnavailableException`, write `mux: <message>` to stderr and return 1.
  2. Write the preamble with the descriptor pid, and flush.
  3. Start two dedicated threads, `MuxProxyIn` (stdin → socket) and `MuxProxyOut` (socket → stdout),
     each with a 64 KiB buffer and a `Flush` after every write.
  4. Wait until either thread finishes.
     - **In finished first (stdin EOF):** `socket.Shutdown(Send)` if it is a `NetworkStream` over a
       socket (`Socket.Shutdown(SocketShutdown.Send)`), dispose the daemon stream, join out (≤ 2 s), and
       return 0.
     - **Out finished first (daemon EOF or error):** dispose stdout and return 3. Do not join the
       in-thread: it may be blocked on stdin, and the process exits.
- **The `proxy` verb in `MuxCli`.**
  - `proxy --stdio` (anything else exits 2) → `MuxProxyCommand.Run(Console.OpenStandardInput(), Console.OpenStandardOutput(), stderr, ct => MuxDaemonLauncher.CreateDefault(log: stderrLog, host.ServeArguments, host.Paths).EnsureEndpointStreamAsync(ct))`.
  - Logging goes to stderr only, prefixed `[ntilde-mux]`.
- **The `--version [--json]` verb** prints `MuxVersionInfo(host.Version, MuxProtocol.MinSupportedVersion, MuxProtocol.MaxSupportedVersion, RuntimeInformation.RuntimeIdentifier, Environment.ProcessPath ?? "")`.
  Plain text: `ntilde-mux <v> (protocol 1-2, <rid>)`.

- [ ] **Step 1: Write the failing tests.** Use `InMemoryDuplexPipe` or `System.IO.Pipelines.Pipe` streams, plus real anonymous pipes where half-close needs real EOF.
  - `Preamble_after_noise_is_found_and_leftover_bytes_reach_the_client`: write
    `"Welcome to host\r\nlast login…\n" + preamble + firstFrameBytes`. `ConnectAsync` returns the pid,
    and reading the stream yields `firstFrameBytes`.
  - `No_preamble_before_EOF_throws_with_the_captured_text`: the message contains `bash: ntilde-mux: not found`.
  - `More_than_64KiB_of_noise_throws`.
  - `A_preamble_in_the_middle_of_a_line_is_not_accepted`.
  - `Disposing_the_stream_closes_stdin_first`.
  - `MuxProxyTests`. Each test hosts an in-process `MuxServer` + `MuxDaemonHost` on a temp root, then
    runs `MuxProxyCommand.Run` on a thread with anonymous pipes for stdin and stdout. The client runs
    `StdioMuxTransport.ConnectAsync` + `MuxClient.ConnectAsync` → `SpawnAsync`, `OpenSession`, attach,
    `SendInput("echo hi\r")`, then waits for `hi` in a `TerminalBuffer`. Use the Mux.Tests
    `FakeTerminalSessionFactory` / scripted session so the test needs no real shell.
    - `Proxy_pumps_frames_both_ways_over_a_real_endpoint`: UDS on Unix, named pipe on Windows,
      through `MuxEndpointConnector`.
    - `Stdin_EOF_exits_0_and_the_daemon_keeps_the_session`: after `Run` returns, `listSessions` from
      a direct client still shows it.
    - `Daemon_closing_exits_3`.
    - `Unreachable_daemon_exits_1_with_a_message`.
    - `Daemon_is_spawned_on_demand`: `connectDaemon` built from `MuxDaemonLauncher` with a fake
      spawner that starts the in-process host on its first `Spawn()`.
  - `MuxVersionTests.Version_json_has_the_protocol_range` and `Plain_version_is_one_line`.
- [ ] **Step 2: Run the tests and verify they fail.** `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~StdioMuxTransportTests|FullyQualifiedName~MuxProxyTests|FullyQualifiedName~MuxVersionTests"`
- [ ] **Step 3: Implement** as specified.
- [ ] **Step 4: Run the whole Mux.Tests project.**
- [ ] **Step 5: Commit.** `feat(mux): proxy --stdio byte pump and the client stdio transport`

### Task 12: The `ntilde-mux` exe, the static rusty_pty, and the CI and release legs

**Files:**
- Create: `src/Ntilde.Mux.Daemon/Ntilde.Mux.Daemon.csproj`, `src/Ntilde.Mux.Daemon/Program.cs`, `scripts/docker-publish-mux-daemon.sh`
- Modify: `src/Ntilde.App/native/Cargo.toml` (`crate-type = ["cdylib", "staticlib"]`), `Ntilde.sln` (`dotnet sln add --solution-folder src`, via the wrapper or by hand), `tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj` (ProjectReference), `ProjectFileLayeringTests.cs`, `LayeringTests.cs`, `NamespaceAlignmentTests.cs`, `DiagnosticSinkTests.cs` (`ConsoleToolProjects`), `CliCommandDispatchTests.cs`, `.github/workflows/ci.yml` (job `mux_daemon_aot`), `.github/workflows/release.yml` (job `publish_mux_daemon`)

**Interfaces:**
- Consumes: `MuxCli.Execute`, `MuxCliHost`, `MuxCliVerbs`, `LocalShellSessionFactory.Instance`, `MuxPaths.Default()`.
- Produces: the published `ntilde-mux` single file per RID, the CI artifact `ntilde-mux-<rid>`, and the release assets `ntilde-mux-<rid>` + `ntilde-mux-<rid>.sha256`.

- [ ] **Step 1: The project.**
  ```xml
  <Project Sdk="Microsoft.NET.Sdk">
    <!-- The remote multiplexer (docs/superpowers/specs/2026-10-05-ntilde-mux-phase4.md §10.1). References
         exactly Ntilde.Mux - ProjectFileLayeringTests.MuxDaemon_only_references_Mux. One file per RID:
         rusty_pty is linked statically (DirectPInvoke + its staticlib). -->
    <PropertyGroup>
      <OutputType>Exe</OutputType>
      <AssemblyName>ntilde-mux</AssemblyName>
      <RootNamespace>Ntilde.MuxDaemon</RootNamespace>
      <PublishAot>true</PublishAot>
      <IsAotCompatible>true</IsAotCompatible>
      <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
      <WarningsAsErrors>$(WarningsAsErrors);IL2026;IL3050</WarningsAsErrors>
      <StripSymbols>true</StripSymbols>
      <InvariantGlobalization>true</InvariantGlobalization>
      <RustyPtyNativeDir>$(MSBuildThisFileDirectory)..\Ntilde.App\native\target\release\</RustyPtyNativeDir>
      <RustyPtyStaticLib Condition="'$(RustyPtyStaticLib)' == ''">$(RustyPtyNativeDir)librusty_pty.a</RustyPtyStaticLib>
    </PropertyGroup>
    <ItemGroup>
      <ProjectReference Include="..\Ntilde.Mux\Ntilde.Mux.csproj" />
    </ItemGroup>
    <ItemGroup Condition="'$(PublishAot)' == 'true' and ($(RuntimeIdentifier.StartsWith('linux-')) or $(RuntimeIdentifier.StartsWith('osx-')))">
      <DirectPInvoke Include="rusty_pty" />
      <NativeLibrary Include="$(RustyPtyStaticLib)" />
    </ItemGroup>
    <!-- JIT runs (dotnet run, tests) load the shared library like the App does. -->
    <ItemGroup Condition="'$(RuntimeIdentifier)' == ''">
      <None Include="$(RustyPtyNativeDir)librusty_pty.so" Condition="Exists('$(RustyPtyNativeDir)librusty_pty.so')" Link="librusty_pty.so" CopyToOutputDirectory="PreserveNewest" />
      <None Include="$(RustyPtyNativeDir)librusty_pty.dylib" Condition="Exists('$(RustyPtyNativeDir)librusty_pty.dylib')" Link="librusty_pty.dylib" CopyToOutputDirectory="PreserveNewest" />
      <None Include="$(RustyPtyNativeDir)rusty_pty.dll" Condition="Exists('$(RustyPtyNativeDir)rusty_pty.dll')" Link="rusty_pty.dll" CopyToOutputDirectory="PreserveNewest" />
    </ItemGroup>
  </Project>
  ```
  `Program.cs`:
  ```csharp
  using System.Reflection;
  using Ntilde.Mux.Cli;
  using Ntilde.Mux.Daemon;

  namespace Ntilde.MuxDaemon;

  /// <summary>ntilde-mux: the remote multiplexer (spec §10.1). Every verb is Ntilde.Mux.Cli's.</summary>
  internal static class Program
  {
      private static int Main(string[] args) => MuxCli.Execute(args, Console.Out, Console.Error, new MuxCliHost
      {
          Paths = MuxPaths.Default(),
          UsagePrefix = "ntilde-mux",
          ServeArguments = ["serve"],
          SessionFactory = () => LocalShellSessionFactory.Instance,
          Verbs = MuxCliVerbs.Serve | MuxCliVerbs.Proxy | MuxCliVerbs.Ls | MuxCliVerbs.Kill | MuxCliVerbs.KillServer | MuxCliVerbs.Attach | MuxCliVerbs.Version,
          Version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0",
      });
  }
  ```
  `--version` is accepted as a verb, as is `version` (`MuxCli` maps `--version`).
- [ ] **Step 2: Architecture rows.** Write the failing assertions first:
  - **`ProjectFileLayeringTests.MuxDaemon_only_references_Mux`:** the csproj has exactly
    `["Ntilde.Mux"]`.
  - **`LayeringTests.MuxDaemon_references_only_Mux`:** IL; assembly name `ntilde-mux`, loaded through
    the new ProjectReference (`typeof(Ntilde.MuxDaemon.Program)` is internal, so use
    `Assembly.Load("ntilde-mux")`).
  - **`NamespaceAlignmentTests`:** add `ntilde-mux` with owner prefix `Ntilde.MuxDaemon`, and check
    that `Ntilde.MuxDaemon` is not used by any other assembly.
  - **`DiagnosticSinkTests.ConsoleToolProjects`:** add `"Ntilde.Mux.Daemon"`. Also add
    `"Ntilde.Launcher"` now, so Task 24 needs no edit there. `Ntilde.Mux/Cli` writes only to the
    injected `TextWriter`s, never to `Console.Write*`; check the scan's rules and keep within them.
  - **`CliCommandDispatchTests.Mux_daemon_dispatches_every_verb_through_MuxCli`:** a source scan of
    `src/Ntilde.Mux.Daemon/Program.cs` for `MuxCli.Execute(`.

  Run `scripts/build.ps1 test tests/Ntilde.Architecture.Tests`: they fail, then pass after Step 1.
- [ ] **Step 3: Static rusty_pty.** Edit `Cargo.toml` to `crate-type = ["cdylib", "staticlib"]`.
  - `cargo build --release --locked` in `src/Ntilde.App/native`, or let the App build run it.
  - Check that `target/release/rusty_pty.dll` still appears on Windows and the App still builds:
    `scripts/build.ps1 build src/Ntilde.App`.
  - Check that `release.yml`'s artifact globs (`*pty.*`) do not break anything. The extra `.a`/`.lib`
    ride along harmlessly; the App's Content items name exact files.
- [ ] **Step 4: Local Linux publish script.** Create `scripts/docker-publish-mux-daemon.sh <rid> <outdir>`. It runs the build in `ubuntu:22.04` under Docker, mounting the repo read-only. The recipe follows `publish_linux` (read release.yml L1333-1451 for the apt list and the .NET tarball install with sha512 check):
  ```bash
  #!/usr/bin/env bash
  # Builds ntilde-mux for a Linux RID inside ubuntu:22.04 (glibc 2.35 floor), the release recipe.
  # Usage: scripts/docker-publish-mux-daemon.sh linux-x64 artifacts/mux-daemon
  set -euo pipefail
  rid="${1:?rid}"; out="${2:?outdir}"
  repo="$(cd "$(dirname "$0")/.." && pwd)"
  mkdir -p "$out"
  docker run --rm -v "$repo:/src:ro" -v "$(cd "$out" && pwd):/out" ubuntu:22.04 bash -euo pipefail -c '
    apt-get update -qq && apt-get install -y -qq --no-install-recommends ca-certificates curl clang zlib1g-dev binutils >/dev/null
    # .NET SDK version from global.json, rustup minimal - mirror release.yml publish_linux.
    ...
    mkdir -p /w && cd /w && tar -C /src --exclude="./**/bin" --exclude="./**/obj" --exclude="./src/Ntilde.App/native/target*" --exclude="./src/Ntilde.App/native/rusty_ssh/target" -cf - global.json Directory.Build.props Directory.Packages.props src/Ntilde.Mux src/Ntilde.Mux.Contracts src/Ntilde.Mux.Daemon src/Ntilde.Pty src/Ntilde.VT src/Ntilde.Replay src/Ntilde.App/native | tar -xf -
    (cd src/Ntilde.App/native && cargo build --release --locked)
    dotnet publish src/Ntilde.Mux.Daemon/Ntilde.Mux.Daemon.csproj -c Release -r '"$rid"' -nodeReuse:false -o /out/'"$rid"'
    rm -f /out/'"$rid"'/*.dbg
    objdump -T /out/'"$rid"'/ntilde-mux | grep -o "GLIBC_[0-9.]*" | sort -Vu | tail -1
  '
  ```
  Fill the `...` with the exact SDK and rustup install lines copied from `release.yml` `publish_linux`. Do not leave the ellipsis.
  - Run it with Git Bash on Windows (`MSYS_NO_PATHCONV=1`): `scripts/docker-publish-mux-daemon.sh linux-x64 artifacts/mux-daemon`.
  - Expected: one file `ntilde-mux`, a `GLIBC_` maximum ≤ 2.35, and
    `docker run --rm -v …:/m debian:12-slim /m/ntilde-mux --version --json` printing the JSON.
  - Record the size.
- [ ] **Step 5: CI job `mux_daemon_aot`** (`ci.yml`). Model it on `aot_gate_detect`/`aot_gate`: use the same detect output, `if: needs.aot_gate_detect.outputs.run == 'true'`, and `permissions: contents: read`. The matrix:
  ```yaml
  include:
    - { rid: linux-x64,   os: ubuntu-latest,    container: ubuntu:22.04 }
    - { rid: linux-arm64, os: ubuntu-24.04-arm, container: ubuntu:22.04 }
    - { rid: osx-arm64,   os: macos-latest,     container: '' }
  ```
  Each leg:
  1. Bootstrap: `apt` + the .NET tarball + rustup, as `publish_linux` does; macOS uses
     `actions/setup-dotnet` from global.json and `dtolnay/rust-toolchain`.
  2. Build: `cd src/Ntilde.App/native && cargo build --release --locked`.
  3. Publish: `dotnet publish src/Ntilde.Mux.Daemon/Ntilde.Mux.Daemon.csproj -c Release -r $RID -nodeReuse:false -o artifacts/mux-daemon/$RID`, with env `DOTNET_CLI_USE_MSBUILD_SERVER=0`.
  4. Assert one file: `ls artifacts/mux-daemon/$RID` shows only `ntilde-mux` (plus `ntilde-mux.dbg`/`.dSYM`, which are deleted).
  5. On Linux, assert `objdump -T … | grep -o 'GLIBC_[0-9.]*' | sort -Vu | tail -1` is ≤ `GLIBC_2.35`, compared with `sort -V`.
  6. Smoke:
     - `./ntilde-mux --version --json | grep '"protocolMax":2'`;
     - `NTILDE_APPDATA_ROOT=$(mktemp -d) ./ntilde-mux serve --foreground --idle-exit-minutes 0 &`;
     - poll for the descriptor;
     - `ntilde-mux ls` exits 0;
     - `ntilde-mux kill-server` exits 0.
  7. `du -b`, or `stat -f%z` on macOS, printed to `$GITHUB_STEP_SUMMARY`.
  8. Upload: `actions/upload-artifact` `ntilde-mux-${{ matrix.rid }}`.

  Every `${{ }}` value used in `run:` goes through `env:`, the injection rule at the top of `release.yml`.
- [ ] **Step 6: Release job `publish_mux_daemon`** (`release.yml`). It has the same three legs, `needs: [release_metadata, create_release]`, and `permissions: contents: write`. Each leg:
  - publishes with `"-p:Version=$RELEASE_VERSION" "-p:InformationalVersion=$RELEASE_VERSION"`;
  - copies the result to `artifacts/release/ntilde-mux-$RID`;
  - writes the checksum: `(cd artifacts/release && sha256sum ntilde-mux-$RID > ntilde-mux-$RID.sha256)`, or `shasum -a 256` on macOS;
  - checks both files with `test -f`;
  - uploads with `softprops/action-gh-release@<the same pinned sha>` and `files:` listing both.
- [ ] **Step 7: Verify.**
  - `scripts/build.ps1 build src/Ntilde.Mux.Daemon`.
  - `scripts/build.ps1 test tests/Ntilde.Architecture.Tests`.
  - Step 4's Docker publish.
  - Run Mux.Tests on Linux once: `docker run` the SDK image with the repo copied, then `scripts/build.sh test tests/Ntilde.Mux.Tests`. This picks up the Unix-gated tests (Task 4's `EAGAIN`, UDS proxy). Record the counts.
  - Lint the workflow YAML with `python -c "import yaml,sys;yaml.safe_load(open(sys.argv[1]))"` on both files.
- [ ] **Step 8: Commit.** `feat(mux): ntilde-mux single-file AOT daemon (static rusty_pty), CI AOT legs and release assets`

---

## Wave D — SSH exec transports (spec §8.2, §8.3)

### Task 13: Exec abstractions and the OpenSSH exec transport (askpass restored)

**Files:**
- Create: `src/Ntilde.Platform/Ssh/Exec/ISshExecTransport.cs` (+ `ISshExecChannel`, `SshExecResult`, `SshExec`), `src/Ntilde.Platform/Ssh/Exec/OpenSshExecTransport.cs`, `src/Ntilde.Platform/Ssh/Exec/OpenSshExecCommandLine.cs`, `src/Ntilde.Platform/Ssh/Exec/SshAskPassEnvironment.cs`, `src/Ntilde.Platform/Ssh/Exec/BoundedTail.cs`
- Modify: `src/Ntilde.App/Shell/SshAskPassCommand.cs`, so it uses the env constants from `SshAskPassEnvironment` (they move there; keep the old names as aliases if tests reference them)
- Test: `tests/Ntilde.Platform.Tests/Ssh/Exec/OpenSshExecCommandLineTests.cs`, `SshAskPassEnvironmentTests.cs`, `SshExecTests.cs`, `OpenSshExecTransportProcessTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace Ntilde.Platform.Ssh.Exec;
  public interface ISshExecChannel : IDisposable
  {
      Stream Stdout { get; }         // remote stdout, read side
      Stream Stdin { get; }          // remote stdin, write side; Dispose() = EOF
      string StderrTail { get; }     // last 8 KiB of remote stderr, UTF-8 lossy
      Task<int?> Completion { get; } // exit status; null when unknown (signal, transport loss)
  }
  public interface ISshExecTransport
  {
      string DisplayName { get; }    // "user@host" for banners
      /// <summary>Connects and starts <paramref name="remoteCommand"/>. Blocks during connect/auth (prompts go to askpass or the interaction handler); never call on the UI thread.</summary>
      ISshExecChannel Start(string remoteCommand, CancellationToken ct);
  }
  public sealed record SshExecResult(int? ExitCode, string Stdout, string Stderr);
  public static class SshExec
  {
      public static Task<SshExecResult> RunAsync(ISshExecTransport transport, string command, ReadOnlyMemory<byte> stdin, IProgress<long>? stdinProgress, TimeSpan timeout, CancellationToken ct);
  }
  public static class OpenSshExecCommandLine
  {
      /// planArguments = SshLaunchPlanner output: ["-F", cfg, alias, ...ExtraSshArgs]
      public static IReadOnlyList<string> Build(IReadOnlyList<string> diagnosticsArguments, IReadOnlyList<string> planArguments, string remoteCommand);
      // = [...diag, "-T", "-o", "ClearAllForwardings=yes", "-o", "BatchMode=no", ...planArguments, "--", remoteCommand]
  }
  public static class SshAskPassEnvironment
  {
      public const string ModeVariable = "NTILDE_SSH_ASKPASS";
      public const string ProfileIdVariable = "NTILDE_SSH_ASKPASS_PROFILE_ID"; // …NAME, USER, HOST, PORT
      public static void Apply(IDictionary<string, string?> environment, string helperPath, SshProfile profile);
      // SSH_ASKPASS=helperPath, SSH_ASKPASS_REQUIRE=force, DISPLAY=ntilde (only if unset), NTILDE_SSH_ASKPASS=1, profile vars
  }
  public sealed class OpenSshExecTransport : ISshExecTransport
  {
      public OpenSshExecTransport(SshProfile profile, string sshExecutablePath, IReadOnlyList<string> planArguments, string? askPassHelperPath, IReadOnlyList<string>? diagnosticsArguments = null, Action<string>? log = null);
  }
  internal sealed class BoundedTail { public BoundedTail(int capacityBytes); public void Append(ReadOnlySpan<byte> b); public override string ToString(); }
  ```
- `OpenSshExecTransport.Start`:
  1. Builds a `ProcessStartInfo`: `UseShellExecute=false`, redirect all three streams, `CreateNoWindow=true`, `ArgumentList` from `Build`, and the environment with askpass applied when a helper path is given.
  2. Starts the process, and a dedicated thread `SshExecStderr` that drains stderr into `BoundedTail(8192)`.
  3. Returns a channel whose `Stdout` is `process.StandardOutput.BaseStream` and whose `Stdin` is `process.StandardInput.BaseStream`, with `AutoFlush`; `Dispose` closes it.
  4. `Completion` is `process.WaitForExitAsync()` followed by the exit code. A non-zero code is returned as is: 255 is ssh's own error.
  5. Channel `Dispose` closes stdin, waits ≤ 2 s for exit, then `Kill(entireProcessTree: true)`.
  6. `ct` cancellation before or while starting kills the process.
- `SshExec.RunAsync`:
  1. Starts the channel.
  2. Writes `stdin` in 64 KiB chunks, reporting progress, then disposes stdin (EOF).
  3. Reads stdout to the end, capped at 1 MiB.
  4. Awaits `Completion` and returns. On timeout it disposes the channel and throws `TimeoutException`.
  5. `Start` runs inside `Task.Run`.

- [ ] **Step 1: Write the failing tests.**
  - The `Build` shape, including `-v` diagnostics first. The remote command is a single argv element
    after `--`.
  - `Apply` sets every variable, keeps an existing `DISPLAY`, and sets
    `NTILDE_SSH_ASKPASS_PROFILE_PORT` as a string.
  - `SshExec.RunAsync` with a fake transport whose channel echoes stdin to stdout and exits 7: the
    result has `ExitCode == 7`, the echoed bytes and the progress reports, and stdin gets EOF.
  - `OpenSshExecTransportProcessTests.Pipes_stdio_and_reports_the_exit_code`. Make the transport's
    executable configurable, as it already is through `sshExecutablePath`:
    - on Windows: `cmd.exe` with `planArguments = ["/d", "/c"]` and the command `"findstr x & exit /b 5"`;
    - on Unix: `/bin/sh` with `["-c"]`.

    The built argv then runs a real process. Write `"x\n"`, close stdin, and assert stdout contains
    `x` and `Completion == 5`. The `-T -o …` arguments: `cmd /c` ignores the trailing tokens when the
    command comes first. If the argument layout makes this impractical, test the process plumbing
    through an internal ctor taking a `Func<ProcessStartInfo>` instead, and say so.
- [ ] **Step 2: Run the tests and verify they fail.** `scripts/build.ps1 test tests/Ntilde.Platform.Tests --filter "FullyQualifiedName~Ssh.Exec"`
- [ ] **Step 3: Implement.** Move the askpass env names into `SshAskPassEnvironment` and make `SshAskPassCommand` reference them. Keep `tests/Ntilde.App.Tests/Core/SftpServiceTests.cs:702-721` (scp must not set askpass) green: scp is unchanged.
- [ ] **Step 4: Run** the filter, then the whole Platform.Tests project and App.Tests `Lane!=PlatformBoot&FullyQualifiedName~AskPass`.
- [ ] **Step 5: Commit.** `feat(ssh): exec transport abstraction and the OpenSSH exec transport with askpass`

### Task 14: `rusty_ssh` exec mode (`nova_ssh_exec`, `nova_ssh_send_eof`, stderr and exit events)

**Files:**
- Modify: `src/Ntilde.App/native/rusty_ssh/src/lib.rs`
- Test: `cargo test` in `src/Ntilde.App/native/rusty_ssh` (add unit tests next to the existing ones)

**Interfaces:**
- Produces (C ABI):
  ```rust
  #[unsafe(no_mangle)] pub extern "C" fn nova_ssh_exec(args: *const NativeConnectArgs, command: *const c_char) -> u64; // 0 = error (nova_ssh_last_error, as connect)
  #[unsafe(no_mangle)] pub extern "C" fn nova_ssh_send_eof(handle: u64) -> c_int;  // main channel EOF
  ```
- New event kinds: read the event enum and its JSON/struct encoding consumed by `NativeSshInterop.PollEvent`, and extend them additively:
  - `ExtendedData { data }`, which the C# side calls "Stderr", for `ChannelMsg::ExtendedData { ext: 1 }` on the **main** channel in exec mode;
  - `ExitStatus { code: i32 }`, for `ChannelMsg::ExitStatus`.

  Existing kinds are unchanged, and a PTY-mode session never emits the new ones.
- **`SessionMode`:** a `SessionMode { Shell { … }, Exec { command: String } }` passed into `run_session` / `establish_session`.
  - **Exec mode:** after hops and auth, skip shell detection; `channel_open_session`, then `exec(true, command)`. No `request_pty`.
  - **The main loop:** `Data` → the existing data event; `ExtendedData` → `ExtendedData`; `ExitStatus` → `ExitStatus`; `Eof`/`Close` → end the session as today, which queues `Closed`.
  - **`WorkerCommand::Write`** writes the main channel (unchanged). The new **`WorkerCommand::SendEof`** calls `channel.eof()`.
- Rewrite `detect_login_shell` over a shared `async fn run_exec_collect(session, command, limit, timeout) -> (stdout, stderr, exit)`. It keeps its 3 s timeout and 4096-byte cap, and its result is unchanged.

- [ ] **Step 1: Write the failing Rust tests.** Unit tests for the event encoding of the two new kinds (whatever function serialises events for the poll buffer), and for `run_exec_collect`'s output bounding, if it is testable without a server. Keep the existing tests green.
- [ ] **Step 2: Run the tests and verify they fail.** `cd src/Ntilde.App/native/rusty_ssh && cargo test`
- [ ] **Step 3: Implement.** Every new `extern "C"` goes through `ffi_guard`. Document both functions in the crate's doc comments, in the style of `nova_ssh_connect`.
- [ ] **Step 4: Run** `cargo test`, then `cargo build --release`, then `scripts/build.ps1 build src/Ntilde.Platform`; the C# side is unchanged yet.
- [ ] **Step 5: Commit.** `feat(ssh-native): exec-mode sessions with stderr and exit-status events`

### Task 15: Native exec transport (C#)

**Files:**
- Modify: `src/Ntilde.Platform/Ssh/Native/INativeSshInterop.cs`, `NativeSshInterop.cs` (`Exec`, `SendEof`, `NativeMethods` imports, event parsing), the `NativeSshEvent` model file
- Create: `src/Ntilde.Platform/Ssh/Exec/NativeSshExecTransport.cs`. Also `NativeSshConnectionOptionsFactory.cs` if `NativeSshSession` builds options inline: extract it there, and make `NativeSshSession` call it (behaviour identical).
- Test: `tests/Ntilde.Platform.Tests/Ssh/Exec/NativeSshExecTransportTests.cs` (fake interop)

**Interfaces:**
- Consumes: Task 14's ABI.
- Produces:
  ```csharp
  // INativeSshInterop (default interface methods throw NotSupportedException so other fakes compile):
  NovaSshSafeHandle Exec(NativeSshConnectionOptions options, string command) => throw new NotSupportedException();
  void SendEof(NovaSshSafeHandle handle) => throw new NotSupportedException();
  // NativeSshEvent kinds: Stderr (bytes), ExitStatus (int)
  public sealed class NativeSshExecTransport : ISshExecTransport
  {
      public NativeSshExecTransport(SshProfile profile, INativeSshInterop interop, ISshInteractionHandler? interactionHandler,
                                    Func<SshProfile, NativeSshConnectionOptions> optionsFactory, Action<string>? log = null);
  }
  ```
- `Start`:
  1. Calls `interop.Exec(options, command)`, then starts one dedicated thread, `SshExecPoll`. It loops on `PollEvent`, sleeping 10 ms when idle, and routes each event:
     - `Data` → a bounded `Pipe` writer (16 MiB pause threshold) backing `Stdout`;
     - `Stderr` → `BoundedTail`;
     - prompt events → `interactionHandler`, exactly as `NativeSshSession.HandleInteractionAsync`. Extract the shared code if it is cheap; otherwise call the same handler methods;
     - `ExitStatus` → record the code;
     - `Closed`/`Disconnected` → complete the pipe and `Completion` with the recorded code, or null.
  2. `Start` returns once the handle exists. Connect failures surface as an exception from `Exec`, or as an early `Closed` with an error message, which becomes an `IOException` on the first `Stdout` read.
  3. `Stdin` is a write-only stream calling `interop.Write`; its `Dispose` calls `SendEof`.
  4. Channel `Dispose` closes the handle and joins the thread (≤ 2 s).

- [ ] **Step 1: Write the failing tests** against a scripted fake `INativeSshInterop`, which returns queued events and records writes:
  - stdout bytes arrive in order;
  - stderr goes to `StderrTail`;
  - `ExitStatus(3)` then `Closed` gives `Completion == 3`;
  - `Closed` without a status gives null;
  - disposing stdin calls `SendEof` once;
  - disposing the channel closes the handle and the thread ends;
  - a host-key prompt event reaches the interaction handler, and its answer goes to `SubmitResponse`.
- [ ] **Step 2: Run the tests and verify they fail.** `scripts/build.ps1 test tests/Ntilde.Platform.Tests --filter "FullyQualifiedName~NativeSshExecTransportTests"`
- [ ] **Step 3: Implement.** Mirror the P/Invokes in `NativeMethods`, in the same `DllImport` style.
- [ ] **Step 4: Run the whole Platform.Tests project**, plus App.Tests `Lane!=PlatformBoot&FullyQualifiedName~NativeSsh`.
- [ ] **Step 5: Commit.** `feat(ssh-native): native exec transport over nova_ssh_exec`

---

## Wave E — GUI remote endpoints (spec §5, §7)

### Task 16: Per-profile flag and install metadata

**Files:**
- Modify: `src/Ntilde.Platform/Ssh/Models/SshMuxOptions.cs`. Every copy site:
  - `src/Ntilde.Platform/Ssh/Storage/JsonSshProfileStore.cs` (`CloneProfile`);
  - `src/Ntilde.App/Services/Ssh/SshConnectionService.cs` (`CloneMuxOptions`, `MergeProfile`);
  - `src/Ntilde.Platform/Ssh/OpenSsh/OpenSshConfigCompiler.cs` (`CloneProfile`: the compiler ignores the new fields, but its clone copies them so nothing is lost);
  - `src/Ntilde.App/ViewModels/Ssh/NewSshConnectionViewModel.cs`;
  - `src/Ntilde.App/Views/Ssh/NewSshConnectionView.axaml` (Reliability tab);
  - `src/Ntilde.McpServer/Tools/ConnectionProfileTools.cs` (schema text, `MuxFields`, validator).
- Test: `tests/Ntilde.Platform.Tests/Ssh/JsonSshProfileStoreTests.cs`, `tests/Ntilde.McpServer.Tests/ConnectionProfileDriftGuardTests.cs` (unchanged; must pass), `tests/Ntilde.App.Tests/ViewModels/NewSshConnectionViewModelTests.cs` (or the existing VM test file)

**Interfaces:**
- Produces:
  ```csharp
  public sealed class SshMuxOptions
  {
      public bool Enabled { get; set; }
      public bool ControlMasterAuto { get; set; } = true;
      public string ControlPath { get; set; } = string.Empty;
      public int ControlPersistSeconds { get; set; }
      /// <summary>Phase 4: tabs of this profile run in ntilde-mux on the remote host and survive disconnects.</summary>
      public bool PersistRemoteSessions { get; set; }
      /// <summary>Absolute remote path recorded by the install flow; empty = the default under $HOME.</summary>
      public string RemoteDaemonPath { get; set; } = string.Empty;
      public string RemoteDaemonVersion { get; set; } = string.Empty;
      public string RemoteDaemonRid { get; set; } = string.Empty;
  }
  ```
  - VM: `PersistRemoteSessions` (bool) and `RemoteDaemonStatusText` (string, read-only), computed by `RemoteMuxStatusText.Describe(version, appVersion)`, a pure static in App:
    - `""` gives `ntilde-mux not installed`;
    - a version equal to the app's gives `ntilde-mux <v> installed`;
    - anything else gives `ntilde-mux <v> installed — this app is <app>`.
  - An `InstallRemoteMuxCommand` (`ICommand`) is wired in Task 23; until then it is a no-op returning false from `CanExecute`.
  - The view: a new `RowDefinitions` entry under the existing "Enable connection multiplexing" row:
    - `CheckBox` "Keep remote sessions running (ntilde-mux)";
    - the status `TextBlock`;
    - `Button` "Install ntilde-mux on this host…".

- [ ] **Step 1: Write the failing tests.**
  - Store: round-trip the new fields, and an old JSON without them loads with the defaults.
  - VM: the flag survives `ToSshProfile`/`ApplySshProfile`; the status text theory.
  - Drift guard: it must fail until `MuxFields` gains the four names.
- [ ] **Step 2: Run the tests and verify they fail.** Platform.Tests (store), McpServer.Tests, App.Tests `Lane!=PlatformBoot&FullyQualifiedName~SshConnection`.
- [ ] **Step 3: Implement** every copy site. In the MCP schema text and validator, `PersistRemoteSessions` is a bool, and the other three are strings. Do not touch the native-backend "mux options" warning: it reads `MuxOptions.Enabled` only; verify with a grep and leave a test that a native profile with only `PersistRemoteSessions` gets no warning.
- [ ] **Step 4: Run** those three projects.
- [ ] **Step 5: Commit.** `feat(ssh): per-profile PersistRemoteSessions flag and ntilde-mux install metadata`

### Task 17: Endpoint identity and one connection host per endpoint

**Files:**
- Create: `src/Ntilde.App/Shell/Mux/MuxEndpointId.cs`, `src/Ntilde.App/Shell/Mux/MuxConnectionHosts.cs`, `src/Ntilde.App/Shell/Mux/MuxHostPolicy.cs`
- Modify:
  - `MuxConnectionHost.cs`: a `Policy` property; the factory reads timeouts from it.
  - `MuxTerminalSessionFactory.cs`: it takes `MuxConnectionHosts`. `Host` stays as the local host for existing tests.
  - `MainWindow.axaml.cs`: `_muxHost` becomes `_muxHosts`, while the `MuxHost` property returns the local host; `KillMuxSessionOnClose`; `PerformAppTeardown`; `CreatePersistentSessionFactory`; `MuxHostFactory`.
  - `TerminalPane.axaml.cs`: `MuxEndpoint` gets an internal setter.
  - `SessionManager.cs`: `ApplyRestoredMuxId`, `DedupeMuxIds`, and `WriteMuxIds` for pending panes.
  - `MuxOrphans.cs`: local only.
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxEndpointIdTests.cs`, `MuxConnectionHostsTests.cs`, the `SessionManager` mux tests, `MuxOrphansTests.cs`, `MainWindowMuxLifecycleTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal readonly record struct MuxEndpointId
  {
      public static readonly MuxEndpointId Local;            // ToString() == "local"
      public static MuxEndpointId ForSsh(Guid profileId);     // "ssh:<N>"
      public static MuxEndpointId Parse(string? persisted);   // null/""/"local"/a legacy pipe or socket path → Local; "ssh:<guid>" → Ssh; anything else → Local (logged by caller)
      public bool IsLocal { get; }
      public Guid? SshProfileId { get; }
      public override string ToString();
  }
  internal sealed record MuxHostPolicy(TimeSpan ConnectTimeout, TimeSpan FailureCooldown, TimeSpan RpcTimeout, bool IsRemote, string DisplayName)
  {
      public static readonly MuxHostPolicy Local = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3), false, "this computer");
      public static MuxHostPolicy Remote(string displayName) => new(TimeSpan.FromSeconds(120), TimeSpan.Zero, TimeSpan.FromSeconds(10), true, displayName);
  }
  internal sealed class MuxConnectionHosts : IDisposable
  {
      public MuxConnectionHosts(MuxConnectionHost local, Func<MuxEndpointId, MuxConnectionHost?> createRemote);
      public MuxConnectionHost Local { get; }
      public MuxConnectionHost? GetOrCreate(MuxEndpointId id);   // null when createRemote declines (profile gone / flag off)
      public MuxConnectionHost? TryGet(MuxEndpointId id);
      public IReadOnlyList<MuxConnectionHost> All { get; }
      public void Dispose();                                     // remotes first (in parallel, each bounded by its own flush), local last
  }
  // MuxConnectionHost: new ctor overload taking MuxHostPolicy; existing ctor = MuxHostPolicy.Local; FailureCooldown init stays (overrides policy for tests)
  ```
  - The persisted `PaneNode.MuxEndpoint` before Phase 4 held the local pipe or socket name (`Host.Endpoint`). `Parse` maps any value that is not `ssh:` to `Local`. From now on, `WriteMuxIds` writes `MuxEndpointId.ToString()`.
  - `PersistentSessionResult.Endpoint` carries the `MuxEndpointId` string, and the pane stores it.

- [ ] **Step 1: Write the failing tests.**
  - The `MuxEndpointId` round-trip theory, including legacy values.
  - `MuxConnectionHosts` creates a remote host once per id, `Dispose` order, a declined id returns null.
  - `DedupeMuxIds` keeps the same id under two endpoints.
  - `MuxOrphans.CollectReferencedIds` ignores `ssh:` panes.
  - A pending restore pane writes its endpoint back.
  - `Closing_a_remote_pane_tracks_the_kill_on_its_own_host`: MainWindow, with a fake remote host.
- [ ] **Step 2: Run the tests and verify they fail.** App.Tests `Lane!=PlatformBoot&FullyQualifiedName~Mux`.
- [ ] **Step 3: Implement.**
  - The factory reads `host.Policy.ConnectTimeout`/`RpcTimeout`. Its existing `ConnectTimeout`/`RpcTimeout` init properties stay as overrides for tests.
  - `MainWindow.MuxHostFactory` stays the local seam. Add `RemoteMuxHostFactory` (a `Func<MuxEndpointId, MuxConnectionHost?>` seam), null until Task 18.
- [ ] **Step 4: Run** App.Tests `Lane!=PlatformBoot&FullyQualifiedName~Mux`, then `Lane=PlatformBoot`.
- [ ] **Step 5: Commit.** `feat(mux): endpoint identity and a connection host per endpoint`

### Task 18: Remote connector, failure classification, remote command

**Files:**
- Create: `src/Ntilde.App/Shell/Mux/Remote/RemoteMuxCommand.cs`, `RemoteMuxConnector.cs`, `RemoteMuxFailureClassifier.cs`, `RemoteMuxHostFactory.cs`, `RemoteFailureKind.cs`
- Modify: `MainWindow.axaml.cs` (set `RemoteMuxHostFactory`). The remote failure type is the App's own `RemoteMuxUnavailableException`; `Ntilde.Mux`'s `MuxUnavailableException` is untouched.
- Test: `tests/Ntilde.App.Tests/Shell/Mux/Remote/RemoteMuxCommandTests.cs`, `RemoteMuxFailureClassifierTests.cs`, `RemoteMuxConnectorTests.cs`

**Interfaces:**
- Consumes: `ISshExecTransport` (Task 13), `OpenSshExecTransport`, `NativeSshExecTransport` (Task 15), `StdioMuxTransport` (Task 11), `MuxClientOptions.ClientInstanceId` (Task 10), `MuxHostPolicy.Remote` (Task 17), `SshMuxOptions` fields (Task 16).
- Produces:
  ```csharp
  internal enum RemoteFailureKind { NotInstalled, Unsupported, VersionMismatch, SshFailed, ProxyFailed }
  internal sealed record RemoteMuxFailure(RemoteFailureKind Kind, string Reason);
  internal sealed class RemoteMuxUnavailableException(RemoteMuxFailure failure, Exception? inner = null) : IOException(failure.Reason, inner) { public RemoteMuxFailure Failure { get; } = failure; }
  internal static class RemoteMuxCommand
  {
      public const string DefaultRelativePath = ".local/share/ntilde/bin/ntilde-mux";
      public static string Proxy(SshMuxOptions options);          // recorded safe absolute path → "<path> proxy --stdio"; else "sh -c 'exec \"$HOME/.local/share/ntilde/bin/ntilde-mux\" proxy --stdio'"
      public static string VersionJson(SshMuxOptions options);    // same rule with "--version --json"
      public static bool IsSafeAbsolutePath(string path);         // ^/[A-Za-z0-9._/+-]+$ and no "//" or "/../"
  }
  internal static class RemoteMuxFailureClassifier
  {
      public static RemoteMuxFailure Classify(int? exitCode, string capturedStdout, string stderr, Exception? error);
  }
  internal sealed class RemoteMuxConnector
  {
      public RemoteMuxConnector(SshProfile profile, ISshExecTransport transport, string clientInstanceId, Action<string>? log);
      public Task<MuxClient> ConnectAsync(CancellationToken ct);         // the MuxConnectionHost connect delegate
      /// <summary>How the most recent connection ended: awaits the channel's exit ≤1 s after a disconnect.</summary>
      public Task<int?> LastExitAsync(TimeSpan wait);
  }
  internal static class RemoteMuxHostFactory
  {
      public static MuxConnectionHost? Create(MuxEndpointId id, Func<Guid, SshProfile?> resolveProfile, Func<SshProfile, ISshExecTransport> transportFor, Action<string>? log);
      // null when the id is local, the profile is gone, or PersistRemoteSessions is off
  }
  ```
  - `Classify`, first match wins:
    1. `error is MuxProtocolException { Code: version_mismatch }` → `VersionMismatch`, reason `ntilde-mux on <host> speaks protocol a-b; this app speaks 1-2`.
    2. Exit 127, or stderr or captured text matching `not found` / `No such file or directory` together with `ntilde-mux` → `NotInstalled`, reason `ntilde-mux is not installed`.
    3. Exit 126, or `Exec format error` / `cannot execute binary file` / `GLIBC_[0-9.]+' not found` / `version .GLIBC` → `Unsupported`, with the reason being the first matching stderr line, trimmed to 200 chars.
    4. Exit 255 with OpenSSH, or a native connect exception → `SshFailed`, with the last non-empty stderr line as the reason.
    5. Otherwise → `ProxyFailed`, with the handshake exception's message, which carries the captured text.
  - `ConnectAsync`:
    1. Builds the command with `RemoteMuxCommand.Proxy`.
    2. Starts the channel: `await Task.Run(() => transport.Start(cmd, ct), ct)`.
    3. `StdioMuxTransport.ConnectAsync(channel.Stdout, channel.Stdin, 30 s, ct)`.
    4. `MuxClient.ConnectAsync(stream, new MuxClientOptions { ClientInstanceId = id, Log = log }, ct)`.
    5. On any failure: dispose the channel, await `Completion` ≤ 1 s, `Classify`, and throw `RemoteMuxUnavailableException`.
  - `RemoteMuxHostFactory.Create`:
    - the transport is OpenSSH or native, by `profile.BackendKind`;
    - the policy is `Remote($"{profile.User}@{profile.Host}")`;
    - the instance id is a new Guid N, reused for the host's lifetime.

  For the OpenSSH transport, the App builds `planArguments` and the ssh path from `SshConnectionService.BuildLaunchDetails`; the askpass helper path is `Environment.ProcessPath`. For native, it uses `new NativeSshInterop()` + `NativeSshConnectionOptionsFactory` + the window's `SshInteractionHandler`.

- [ ] **Step 1: Write the failing tests.**
  - `Remote_command_is_shell_agnostic` (Review Focus 3): the recorded path `/home/nova/.local/share/ntilde/bin/ntilde-mux` gives exactly `"/home/nova/.local/share/ntilde/bin/ntilde-mux proxy --stdio"`. The recorded paths `/home/a b/x`, `/x;rm -rf ~` and `relative/x` fall back to the `sh -c '…'` form. The fallback contains exactly two single quotes, and no single quote inside.
  - A classifier theory with real stderr samples:
    - dash: `sh: 1: /home/nova/.local/share/ntilde/bin/ntilde-mux: not found`;
    - bash: `bash: line 1: …: No such file or directory`;
    - glibc: `/…/ntilde-mux: /lib/x86_64-linux-gnu/libc.so.6: version 'GLIBC_2.38' not found (required by …)`;
    - musl: `Error loading shared library ld-linux-x86-64.so.2`, which gives `Unsupported`;
    - OpenSSH: `ssh: connect to host x port 22: Connection refused` with exit 255.
  - `RemoteMuxConnectorTests`, with a fake `ISshExecTransport` whose channel runs `MuxProxyCommand.Run` in-process against an in-memory `MuxServer` and `MuxDaemonHost` (helper `FakeRemoteHost` in `tests/Ntilde.App.Tests/Shell/Mux/Remote/FakeRemoteHost.cs`; reused by later tasks):
    - connect, then `ListSessionsAsync` works;
    - a channel that prints the dash error and exits 127 gives `NotInstalled`;
    - the instance id is sent.
- [ ] **Step 2: Run the tests and verify they fail.** App.Tests `Lane!=PlatformBoot&FullyQualifiedName~Shell.Mux.Remote`.
- [ ] **Step 3: Implement**, and wire `RemoteMuxHostFactory` into `MainWindow` (the profile resolver is `new SshConnectionService().GetConnectionProfile`, or the existing store instance).
- [ ] **Step 4: Run** the filter, then App.Tests `Lane!=PlatformBoot&FullyQualifiedName~Mux`.
- [ ] **Step 5: Commit.** `feat(mux): remote connector over an SSH exec channel with failure classification`

### Task 19: Factory routes persisted SSH profiles to their remote host; the toast gets an action

**Files:**
- Modify: `src/Ntilde.Pty/ITerminalSessionFactory.cs` (`TerminalSessionRequest`: trailing `bool ReattachAfterDrop = false`), `src/Ntilde.App/Shell/Mux/MuxTerminalSessionFactory.cs`, `src/Ntilde.App/Shell/Mux/PersistentSessionFactory.cs` (`PersistentSessionResult.RemoteFailure`, `HostDisplayName`), `src/Ntilde.App/MainWindow.axaml.cs` + `MainWindow.axaml` (toast action button, `EnqueueNotice` with an action), `src/Ntilde.App/Controls/TerminalPane.axaml.cs` (`PersistenceNotice` args carry an optional action)
- Test: `tests/Ntilde.App.Tests/Shell/Mux/MuxTerminalSessionFactoryRemoteTests.cs` (new), `tests/Ntilde.App.Tests/Core/MainWindowNoticeActionTests.cs` (new)

**Interfaces:**
- Consumes: `MuxConnectionHosts`, `RemoteMuxUnavailableException`, `FakeRemoteHost`.
- Produces:
  ```csharp
  // MuxTerminalSessionFactory
  public MuxTerminalSessionFactory(MuxConnectionHosts hosts, ITerminalSessionFactory fallback, Func<Guid, SshProfile?> resolveProfile, Action<string>? log);
  public bool RoutesRemote(TerminalSessionRequest request);   // Ssh != null && profile?.MuxOptions.PersistRemoteSessions == true
  // PersistentSessionResult additions
  public RemoteMuxFailure? RemoteFailure { get; init; }
  public string? HostDisplayName { get; init; }
  // Notice plumbing
  internal sealed record PersistenceNoticeAction(string Label, Action Run);
  // TerminalPane.PersistenceNotice: event Action<string title, string message, PersistenceNoticeAction? action>
  // MainWindow.EnqueueNotice(string title, string message, PersistenceNoticeAction? action = null) → last non-null action wins in a merged toast
  // ShowRecordingToast(..., PersistenceNoticeAction? action = null) → "RecordingToastAction" Button visible iff action != null; click runs it and hides the toast
  internal Action<Guid>? OpenRemoteMuxInstall { get; set; }   // MainWindow seam; Task 23 assigns the dialog
  ```
- Remote branch of `CreatePersistent`, before the local code:
  ```csharp
  if (request.Ssh is { } ssh && RoutesRemote(request))
      return CreateRemote(request, ssh.ProfileId);
  if (request.Ssh is not null) return new(_fallback.Create(request), PersistentSessionOutcome.NotPersistent, null, null);
  ```
  `CreateRemote` follows §7.5:
  - `Hosts.GetOrCreate(MuxEndpointId.ForSsh(id))`; null gives `NotPersistent`.
  - `GetClient(host.Policy.ConnectTimeout)`. On null, `host.LastFailure as RemoteMuxUnavailableException` gives `RemoteFailure`.
  - No existing id: spawn `SpawnParams { Command = "", Arguments = "", StartingDirectory = profile.WorkingDirectory ?? "", Cols, Rows, EnvironmentOverrides = null, SkipPowerShellPostLaunchInit = false, Title = profile.Name }`, then `OpenSession(id, "", null)`.
  - Existing id with `ReattachAfterDrop`: `Shared` if the session is running, else `PreviousLost` and a fresh spawn.
  - Existing id without `ReattachAfterDrop`: `IfUnattached` when v2, the v1 pre-check otherwise. There is no `MuxCommandMatch` check.
  - Failures: without an id, `Unavailable` with `_fallback.Create(request)`, a plain SSH session. With an id, `DaemonUnreachable`.
  - `Endpoint = MuxEndpointId.ForSsh(id).ToString()` and `HostDisplayName = host.Policy.DisplayName` on every result.
- Notice texts (constants on `TerminalPane`):
  - `RemoteMuxUnavailableNoticeTitle = "Persistent SSH unavailable"`;
  - message `$"[{host}: {reason} — this tab will not survive a disconnect]"`;
  - action labels `"Install ntilde-mux…"` (NotInstalled), `"Update ntilde-mux…"` (VersionMismatch), and none otherwise.

- [ ] **Step 1: Write the failing tests** (factory, with a `FakeRemoteHost`):
  - `New_persisted_ssh_tab_spawns_on_the_remote_daemon`: outcome `Spawned`, `Endpoint == "ssh:<N>"`, the spawn params carry an empty command, the profile's working directory and **null environment overrides**, even though the request had some.
  - `Profile_without_the_flag_is_NotPersistent`.
  - `Restore_by_id_attaches_IfUnattached`.
  - `ReattachAfterDrop_attaches_Shared`.
  - `Not_installed_falls_back_to_plain_ssh_with_an_install_action`: `Unavailable`, the fallback factory was called, `RemoteFailure.Kind == NotInstalled`.
  - `Version_mismatch_with_an_id_is_DaemonUnreachable`.
  - Notice: `[AvaloniaFact]` `Notice_with_an_action_shows_the_button_and_runs_it`.
- [ ] **Step 2: Run the tests and verify they fail.** App.Tests `Lane!=PlatformBoot&(FullyQualifiedName~MuxTerminalSessionFactory|FullyQualifiedName~NoticeAction)`.
- [ ] **Step 3: Implement.** Update existing constructions of `MuxTerminalSessionFactory`, including tests, through a convenience overload: `MuxTerminalSessionFactory(MuxConnectionHost local, ITerminalSessionFactory fallback, Action<string>? log)` wraps a `MuxConnectionHosts` with no remote creator, so the existing tests compile unchanged.
- [ ] **Step 4: Run** the filter, then App.Tests `Lane!=PlatformBoot&FullyQualifiedName~Mux`.
- [ ] **Step 5: Commit.** `feat(mux): persisted SSH profiles open on their remote daemon; notices can carry an action`

### Task 20: Remote liveness, reconnect loop and queued kills (on the host)

**Files:**
- Create: `src/Ntilde.App/Shell/Mux/Remote/MuxReconnectLoop.cs`, `src/Ntilde.App/Shell/Mux/Remote/IMuxTimerScheduler.cs`
- Modify: `src/Ntilde.App/Shell/Mux/MuxConnectionHost.cs` (remote policy: liveness ping, a `Disconnected` subscription, events, `KillWhenConnected`), `RemoteMuxHostFactory.cs` (disconnect classification through `RemoteMuxConnector.LastExitAsync`)
- Test: `tests/Ntilde.App.Tests/Shell/Mux/Remote/MuxReconnectLoopTests.cs`, `MuxConnectionHostRemoteTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal interface IMuxTimerScheduler { IDisposable Schedule(TimeSpan due, Action callback); long NowMs { get; } }
  internal sealed class SystemMuxTimerScheduler : IMuxTimerScheduler { /* one System.Threading.Timer per Schedule call, disposed after firing */ }
  internal enum MuxDisconnectKind { LinkLost, DaemonStopped }
  internal sealed class MuxReconnectLoop : IDisposable
  {
      public static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);
      public static TimeSpan Delay(int attempt, Random jitter);   // 1,2,4,8,16,30,30… × U(0.8,1.2), capped at 30 s
      public MuxReconnectLoop(IMuxTimerScheduler scheduler, Func<Task<bool>> attempt, Action abandoned, Random? jitter = null);
      public void Start();            // idempotent while running
      public void TryNow();           // user-initiated: cancel the pending delay, attempt at once, reset backoff
      public bool IsRunning { get; }
  }
  // MuxConnectionHost (remote policy only; local behaviour byte-identical)
  public event Action<string>? ConnectionLost;      // reason
  public event Action<MuxClient>? Reconnected;
  public event Action? ReconnectAbandoned;
  public event Action? DaemonStopped;
  public bool IsReconnecting { get; }
  public void KillWhenConnected(Guid sessionId);    // queued; sent (KillAsync, tracked) right after the next successful connect; dropped with a log line when the loop is abandoned
  internal Func<MuxClient, Task<MuxDisconnectKind>>? ClassifyDisconnect { get; init; }
  internal IMuxTimerScheduler Scheduler { get; init; } = SystemMuxTimerScheduler.Instance;
  internal TimeSpan LivenessInterval { get; init; } = TimeSpan.FromSeconds(15);
  internal TimeSpan LivenessTimeout { get; init; } = TimeSpan.FromSeconds(10);
  ```
- Behaviour on a remote host:
  - **After each successful connect,** the host subscribes to `client.Disconnected` and arms the liveness timer.
  - **Each liveness tick** runs `PingAsync` with a CTS of `LivenessTimeout`. On failure it calls `client.Dispose()`, which raises `Disconnected("ping timeout")`. A tick is skipped while a previous ping is still pending.
  - **`Disconnected`** first ignores a client that is not current, or a host that is disposed. It then awaits `ClassifyDisconnect` with a 1 s cap; the default is `LinkLost`.
    - `DaemonStopped` raises the `DaemonStopped` event.
    - `LinkLost` raises `ConnectionLost(reason)` and calls `loop.Start()`.
  - **The loop's attempt** is `TryStartConnecting` + await. Success raises `Reconnected(client)`, flushes the queued kills, and stops the loop.
  - **`GetClient` on a remote host while reconnecting** calls `loop.TryNow()`, then joins the attempt as today.
  - **`Dispose`** disposes the loop and the liveness timer and cancels the in-flight attempt; the attempt receives `_disposedToken`, which the transport honours.

- [ ] **Step 1: Write the failing tests.** Use a `FakeScheduler` with manual `Advance(ms)` and a seeded `Random`.
  - `Delay_follows_the_backoff_and_stays_within_jitter`: a theory over attempts 0..8.
  - `Loop_stops_after_the_budget_and_raises_abandoned`.
  - `TryNow_attempts_immediately_and_resets_the_backoff`.
  - `A_dropped_link_raises_ConnectionLost_then_Reconnected`, through a `FakeRemoteHost` whose link the test cuts (`CutLink()` closes the channel streams).
  - `Ping_timeout_disconnects_a_silent_link`: the fake proxy stops forwarding; the scheduler advances 15 s, then 10 s.
  - `Daemon_exit_code_3_raises_DaemonStopped_without_a_loop`.
  - `Kill_requested_while_disconnected_is_sent_after_reconnect` (Review Focus 1).
  - `Dispose_during_a_blocked_remote_connect_returns_promptly` (Review Focus 5): the fake transport's `Start` blocks until its token is cancelled. `Dispose` returns within 2 s, and the transport saw cancellation.
  - `Local_host_has_no_loop_and_no_ping`: the existing tests stay green.
- [ ] **Step 2: Run the tests and verify they fail.** App.Tests `Lane!=PlatformBoot&FullyQualifiedName~Shell.Mux`.
- [ ] **Step 3: Implement.** The liveness timer and the loop use the scheduler only: no `Task.Delay` loops and no thread-pool polling. Callbacks do bounded work and start the async connect through the existing `TryStartConnecting`.
- [ ] **Step 4: Run** the filter, then App.Tests `Lane!=PlatformBoot&FullyQualifiedName~Mux`.
- [ ] **Step 5: Commit.** `feat(mux): remote hosts ping, reconnect with backoff, and deliver kills queued while down`

### Task 21: Pane wiring for remote persisted tabs (connect off the UI thread, reconnect states, restore)

**Files:**
- Modify: `src/Ntilde.App/Controls/TerminalPane.axaml.cs` (`InitializeSessionCore` ~L3437-3476, `TakeMuxAttachShared`/`TakeMuxSessionIdToRestore` ~L3803-3823, a new `ApplyPersistentResult` extracted from `CreateLocalSession` ~L3568-3626, `WireMuxSession` ~L3830, `HandleMuxConnectionLost`, `OnKeyDown` ~L4605, `Reconnect()` ~L4642), `src/Ntilde.App/Shell/SessionManager.cs` (restore of `ssh:` endpoints), `src/Ntilde.App/MainWindow.axaml.cs` (`KillMuxSessionOnClose`: queued kill for a disconnected remote pane)
- Test: `tests/Ntilde.App.Tests/Controls/MuxRemotePaneTests.cs` (new), `tests/Ntilde.App.Tests/Core/MainWindowMuxRemoteTests.cs` (new)

**Interfaces:**
- Consumes: `MuxTerminalSessionFactory.RoutesRemote`, `PersistentSessionResult.Endpoint/HostDisplayName/RemoteFailure`, the host events (Task 20), `MuxConnectionHosts.TryGet`, `TerminalSessionRequest.ReattachAfterDrop`.
- Produces: the banner and notice constants on `TerminalPane`:
  ```csharp
  internal static string RemoteConnectingBanner(string host) => $"[Connecting to {host}…]";
  internal static string RemoteReconnectingBanner(string host) => $"[Connection to {host} lost — reconnecting…]";
  internal const string RemoteInputDroppedHint = "[Input is not sent while reconnecting]";
  internal static string RemoteAbandonedBanner(string host) => $"[Connection to {host} lost] [Press Enter to reconnect]";
  internal static string RemoteDaemonStoppedBanner(string host) => $"[ntilde-mux on {host} stopped] [Press Enter to reconnect]";
  internal static string RemoteUnreachableBanner(string host) => $"[{host} not reachable — press Enter to retry]";
  internal int RemoteConnectGenerationForTest { get; }
  internal Func<Func<PersistentSessionResult>, Task<PersistentSessionResult>> RunOffUiThread { get; set; } = f => Task.Run(f);   // test seam
  ```
- Flow:
  - **Routing.** `isSsh && persistentFactory.RoutesRemote(request)` → `BeginRemoteSession(request)`:
    1. Write the connecting banner.
    2. `int gen = ++_remoteGen`.
    3. `RunOffUiThread(() => factory.CreatePersistent(request))` → `Dispatcher.UIThread.Post(...)`.
    4. When the posted result arrives: if `gen != _remoteGen` or the pane is disposed, dispose `result.Session` and return. Otherwise call `ApplyPersistentResult(result)`, the same handling as local, plus the remote notice and action.
  - **Taking the restore id.** `TakeMuxSessionIdToRestore(isSsh)` and `TakeMuxAttachShared(isSsh)` take the id when the request routes remote. Plain SSH keeps today's behaviour, leaving the id pending.
  - **Host events.** Subscribe to the host's events when the session wires, and unsubscribe when it is replaced or disposed. Each handler posts to the UI and acts only if the event's host is this pane's host and the session is current:
    - `ConnectionLost` → `EnterRemoteReconnecting()`: the banner, `_muxReconnecting = true`, `_remoteInputHintShown = false`, `TermView.SetSession(null)`, and `ApplyMuxSharing(null)`;
    - `Reconnected` → `BeginRemoteSession(request with ExistingMuxSessionId = id, ReattachAfterDrop = true)`;
    - `ReconnectAbandoned` → the abandoned banner, `_muxReconnecting = false`, `_muxConnectionLost = true`, and keep `_muxReattachId`;
    - `DaemonStopped` → the daemon-stopped banner, same state as abandoned.
  - **Input while reconnecting** (`OnKeyDown` / text input with `_muxReconnecting`): drop it, and the first time per episode write `RemoteInputDroppedHint`. Enter does not reconnect while the loop runs; it calls `host.GetClient`, which runs `TryNow` off the UI thread.
  - **Reattach failure after `Reconnected`** (Review Focus 2): if `ApplyPersistentResult` gets `DaemonUnreachable`, or the attach throws, show `RemoteAbandonedBanner` with Enter armed. If the host is still reconnecting, stay in the reconnecting state instead.
  - **Close while disconnected** (MainWindow `KillMuxSessionOnClose`): for a remote pane whose session is disconnected, call `host.KillWhenConnected(mux.Id)` instead of `KillAsync`.
  - **Restore.** `ApplyRestoredMuxId` sets `pane.MuxEndpoint` (Task 17). SSH panes restoring an `ssh:` id route remote, and several panes share the host's in-flight connect.
  - **Spec §8.4.** A remote persisted pane is not registered in `ActiveSshSessionRegistry`. The
    remote-files sidebar request (`RequestRemoteFilesSidebarTransfer`, and the sidebar toggle) on such a
    pane shows the toast `Not available on a persistent remote tab` and does nothing else. Test:
    `Remote_files_sidebar_is_unavailable_on_a_persistent_remote_tab`.

- [ ] **Step 1: Write the failing tests.** Use the headless pane harness of `MuxPaneTests` and a `FakeRemoteHost` with a profile resolver; `RunOffUiThread` runs synchronously on a background `Task` that the test awaits.
  - `Persisted_ssh_pane_connects_off_the_ui_thread_and_attaches`: the banner, then the snapshot. Assert the factory ran on a non-UI thread and buffer equality with the daemon.
  - `Link_drop_shows_reconnecting_then_reattaches_the_same_session`: the session id is unchanged and the content equals the daemon's.
  - `Input_while_reconnecting_is_dropped_with_one_hint`: two keys give one hint line, and the fake proxy received no input bytes.
  - `Abandoned_reconnect_shows_the_enter_banner_and_enter_retries`.
  - `Daemon_stopped_then_enter_starts_a_fresh_shell_with_the_previous_lost_notice`.
  - `Reattach_failure_after_reconnect_shows_the_enter_banner` (Review Focus 2).
  - `Restoring_three_panes_of_one_profile_starts_one_exec` (Review Focus 4): MainWindow restore of a session file with three `ssh:` panes of one profile gives `fakeTransport.StartCount == 1`.
  - `Closing_a_disconnected_remote_pane_queues_the_kill`.
  - `Stale_connect_result_is_disposed`: `Reconnect()` is called while the first connect is pending, and the first result's session is disposed.
- [ ] **Step 2: Run the tests and verify they fail.** App.Tests `Lane!=PlatformBoot&(FullyQualifiedName~MuxRemotePaneTests|FullyQualifiedName~MainWindowMuxRemoteTests)`.
- [ ] **Step 3: Implement.** Extract `ApplyPersistentResult` without behaviour change for local panes. Run the existing `MuxPaneTests` before the remote additions, to prove the extraction.
- [ ] **Step 4: Run** the filter, then App.Tests `Lane!=PlatformBoot&FullyQualifiedName~Mux`, then `Lane=PlatformBoot`.
- [ ] **Step 5: Commit.** `feat(mux): remote persisted panes connect off the UI thread, reconnect and restore`

---

## Wave F — install flow (spec §9)

### Task 22: Probe parser, asset source, installer core

**Files:**
- Create: `src/Ntilde.App/Shell/Mux/Remote/RemoteHostProbe.cs`, `IMuxDaemonAssetSource.cs` (+ `GitHubReleaseMuxAssetSource`, `LocalFileMuxAssetSource`), `RemoteMuxInstaller.cs`, `RemoteMuxInstallCommands.cs`, `AppVersionInfo.cs` (if no shared version helper exists; `AboutWindow` and `BackupService` duplicate the attribute read, so add the helper and make `AboutWindow` use it)
- Test: `tests/Ntilde.App.Tests/Shell/Mux/Remote/RemoteHostProbeTests.cs`, `MuxAssetSourceTests.cs`, `RemoteMuxInstallerTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal sealed record RemoteHostFacts(string Rid, string Home);
  internal sealed record RemoteHostRefusal(string Reason);
  internal static class RemoteHostProbe
  {
      public const string Command = "sh -c 'uname -sm; (ldd --version 2>&1 || getconf GNU_LIBC_VERSION 2>&1) | head -n 1; printf \"HOME=%s\\n\" \"$HOME\"'";
      public static OneOf<RemoteHostFacts, RemoteHostRefusal> Parse(SshExecResult result);  // use a small Result type; no new package
      public static readonly Version MinimumGlibc = new(2, 35);
  }
  internal interface IMuxDaemonAssetSource { Task<MuxDaemonAsset> GetAsync(string rid, IProgress<long>? progress, CancellationToken ct); }
  internal sealed record MuxDaemonAsset(byte[] Bytes, string Sha256Hex, string Origin);
  internal sealed class GitHubReleaseMuxAssetSource(HttpClient http, string appVersion, string cacheDirectory) : IMuxDaemonAssetSource
  {
      public static string AssetUrl(string version, string rid);  // https://github.com/benyblack/ntilde/releases/download/v{version}/ntilde-mux-{rid}
  }
  internal sealed class LocalFileMuxAssetSource(string path) : IMuxDaemonAssetSource;
  internal static class RemoteMuxInstallCommands
  {
      public const string Upload = "sh -c 'set -e; d=\"$HOME/.local/share/ntilde/bin\"; mkdir -p \"$d\"; t=\"$d/.ntilde-mux.$$\"; trap \"rm -f \\\"$t\\\"\" EXIT; cat > \"$t\"; chmod 755 \"$t\"; mv -f \"$t\" \"$d/ntilde-mux\"; trap - EXIT; exec \"$d/ntilde-mux\" --version --json'";
      public static string OfflineOneLiner(string version, string rid);   // spec §9 step 2(c); shasum -a 256 for osx-*
  }
  internal enum RemoteMuxInstallStep { Probing, Downloading, Uploading, Verifying, Done }
  internal sealed record RemoteMuxInstallResult(bool Success, string Message, MuxVersionInfo? Installed);
  internal sealed class RemoteMuxInstaller(ISshExecTransport transport, IMuxDaemonAssetSource source, Action<RemoteMuxInstallStep, string> report)
  {
      public Task<RemoteMuxInstallResult> InstallAsync(CancellationToken ct);
      public static void Record(SshMuxOptions options, MuxVersionInfo installed);   // path, version, rid
  }
  ```
- Parse rules (spec §9 step 1):
  - The first line is `<kernel> <machine>`.
  - The second line is the libc line: `ldd (Ubuntu GLIBC 2.35-0ubuntu3.8) 2.35`, `ldd (GNU libc) 2.39`, `glibc 2.36` from `getconf`, or `musl libc (x86_64)`.
  - The last `HOME=` line is the home directory.
  - Darwin skips the libc check.
  - The glibc version is parsed as the last `\d+\.\d+` on the libc line.
- `GitHubReleaseMuxAssetSource.GetAsync`:
  1. Uses the cache if `<cache>/<version>/<rid>/ntilde-mux` and its `.sha256` exist and match.
  2. Otherwise downloads the asset and `<url>.sha256`, and parses the first hex token.
  3. Compares it, case-insensitively, with `SHA256.HashData(bytes)`. A mismatch throws `InvalidDataException("checksum mismatch …")`.
  4. Writes the cache atomically.
  5. A dev version (one containing `-dev`, `+`, or with no release) throws on HTTP 404 with the message `No ntilde-mux release for <version> — choose a file or copy the install command`.
- `InstallAsync`:
  1. Runs the probe through `SshExec.RunAsync(transport, RemoteHostProbe.Command, empty, null, 60 s)`. A refusal gives `Success = false` with the reason.
  2. `source.GetAsync(rid, …)`.
  3. Upload: `SshExec.RunAsync(transport, RemoteMuxInstallCommands.Upload, bytes, progress, 5 min)`.
  4. A non-zero exit gives failure with the stderr tail.
  5. Parses stdout as `MuxVersionInfo` with `MuxCliJsonContext` (internal to Mux; App.Tests sees it through `InternalsVisibleTo`; App itself needs `InternalsVisibleTo("Ntilde")` on Mux or a public context. **Make `MuxCliJsonContext` public**; it is harmless).
  6. Requires a protocol overlap with `MuxProtocol` Min..Max, then returns success.

- [ ] **Step 1: Write the failing tests.**
  - A probe theory over the real outputs: Ubuntu 22.04 x64, Debian 12 arm64, Alpine (musl), CentOS 7 (glibc 2.17), macOS arm64, macOS x86_64, FreeBSD.
  - Asset source: a fake `HttpMessageHandler` covering the match, the mismatch, a 404, and a cache hit with no HTTP call.
  - Installer: a fake transport records the commands and the stdin bytes; the probe answers Ubuntu; the upload returns version JSON. Assert the exact upload command, the bytes, the result and `Record`.
  - `Upload_exit_nonzero_reports_stderr`.
  - `Protocol_disjoint_install_fails`.
  - `OfflineOneLiner` shape for linux and osx.
- [ ] **Step 2: Run the tests and verify they fail.** App.Tests `Lane!=PlatformBoot&FullyQualifiedName~Shell.Mux.Remote`.
- [ ] **Step 3: Implement.** Keep the `sh -c '…'` commands free of single quotes inside, and add a test asserting it.
- [ ] **Step 4: Run** the filter.
- [ ] **Step 5: Commit.** `feat(mux): remote host probe, verified asset source and ntilde-mux installer`

### Task 23: Install dialog and its entry points

**Files:**
- Create: `src/Ntilde.App/Views/Ssh/RemoteMuxInstallDialog.cs` (code-built, like the picker)
- Modify: `src/Ntilde.App/ViewModels/Ssh/NewSshConnectionViewModel.cs` (`InstallRemoteMuxCommand`), the view, `src/Ntilde.App/MainWindow.axaml.cs` (`OpenRemoteMuxInstall` seam → dialog; save the profile via `SshConnectionService` after `Record`)
- Test: `tests/Ntilde.App.Tests/Core/RemoteMuxInstallDialogTests.cs` (`[AvaloniaFact]`, with a fake installer)

**Interfaces:**
- Consumes: `RemoteMuxInstaller`, `RemoteMuxInstallCommands.OfflineOneLiner`, `RemoteMuxStatusText`, `OpenRemoteMuxInstall` (Task 19), `RemoteMuxHostFactory`'s transport selection (expose `RemoteMuxTransports.For(SshProfile)`).
- Produces: `internal static Task<RemoteMuxInstallResult?> RemoteMuxInstallDialog.ShowAsync(Window owner, SshProfile profile, Func<IMuxDaemonAssetSource?, RemoteMuxInstaller> createInstaller, string appVersion, IClipboard? clipboard)`.
  - The dialog has a step list, a read-only log `TextBox`, a progress bar, and these buttons:
    - **Install**, the default, which uses the GitHub source;
    - **Choose file…**, which opens a storage-provider file picker and uses the local source;
    - **Copy install command**, which puts the one-liner on the clipboard and shows "Copied";
    - **Cancel / Close**, the `IsCancel` button.
  - On success it offers **Keep remote sessions running** (a checkbox, checked by default) and saves.

- [ ] **Step 1: Write the failing tests.**
  - `Install_success_records_the_version_and_can_turn_the_flag_on`.
  - `Copy_install_command_puts_the_one_liner_on_the_clipboard`.
  - `Failure_shows_the_reason_and_keeps_the_dialog_open`.
  - `Escape_cancels_a_running_install`: the token is cancelled.
- [ ] **Step 2: Run the tests and verify they fail.** App.Tests `Lane!=PlatformBoot&FullyQualifiedName~RemoteMuxInstallDialog`.
- [ ] **Step 3: Implement.** Wire the editor button to save pending editor changes first, so the profile exists with its host and auth, then open the dialog. Afterwards, refresh `RemoteDaemonStatusText`.
- [ ] **Step 4: Run** the filter, then App.Tests `Lane!=PlatformBoot&FullyQualifiedName~SshConnection`.
- [ ] **Step 5: Commit.** `feat(mux): Install ntilde-mux dialog from the connection editor and the unavailable toast`

---

## Wave G — `ntilde.com` (spec §11)

### Task 24: `Ntilde.Launcher`

**Files:**
- Create: `src/Ntilde.Launcher/Ntilde.Launcher.csproj`, `Program.cs`, `LauncherCommandLine.cs`, `LauncherNative.cs`
- Create (App): `src/Ntilde.App/Shell/LauncherRelease.cs`. Modify `src/Ntilde.App/Program.cs` to call `LauncherRelease.Signal()` on the GUI path.
- Modify: `Ntilde.sln`, `tests/Ntilde.Architecture.Tests` (`Launcher_has_no_references`, a csproj check that no `ProjectReference`/`PackageReference` exists; `ConsoleToolProjects` already lists it from Task 12), `tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj` (a ProjectReference to the launcher with `ReferenceOutputAssembly=true`, so tests can call `LauncherCommandLine` and find the built exe; make `LauncherCommandLine` public)
- Test: `tests/Ntilde.App.Tests/Launcher/LauncherCommandLineTests.cs`, `LauncherEndToEndTests.cs` (Windows-gated), `tests/Ntilde.App.Tests/Shell/LauncherReleaseTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace Ntilde.Launcher;
  public static class LauncherCommandLine
  {
      /// <summary>The raw command line after argv[0] (CRT rules), leading whitespace trimmed; "" when none.</summary>
      public static string Tail(string rawCommandLine);
      public static string Build(string targetExePath, string tail);   // "\"<path>\"" + (tail.Length > 0 ? " " + tail : "")
      public const string ReleaseEventVariable = "NTILDE_LAUNCHER_RELEASE";
      public const int TargetMissingExitCode = 9009;
  }
  // App
  internal static class LauncherRelease { public static void Signal(); }  // opens the named event (EVENT_MODIFY_STATE), SetEvent, clears the variable; no-op off Windows or when unset
  ```
- `Program.Main` (launcher):
  1. On a non-Windows OS: print `ntilde.com runs on Windows only` and exit 1.
  2. `target = Path.Combine(AppContext.BaseDirectory, "Ntilde.exe")`. If it is missing: stderr `ntilde: Ntilde.exe not found next to ntilde.com`, exit 9009.
  3. Create an event: `CreateEventW(null, manualReset: true, initial: false, name: "Local\\ntilde-launcher-" + Guid.NewGuid().ToString("N"))`. Set the env var for the child through an environment block built from the current one plus the variable, or by setting the process env before `CreateProcessW` with a null env block (simpler: `Environment.SetEnvironmentVariable` then inherit).
  4. `SetConsoleCtrlHandler(handler, true)`: the handler returns TRUE for C, Break and Close, and Close waits ≤ 4 s for the child.
  5. `CreateProcessW(target, Build(target, Tail(GetCommandLineW())), bInheritHandles: true, flags: 0, ...)`.
  6. `WaitForMultipleObjects([process, event], waitAll: false, INFINITE)`. The process finishing gives its `GetExitCodeProcess`; the event gives 0.
- `LauncherNative`: `DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)` with blittable structs (`STARTUPINFOW`, `PROCESS_INFORMATION`) and `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`. The callback is a static `[UnmanagedCallersOnly]` function pointer, which is AOT-safe: `delegate* unmanaged<uint, int>`.

- [ ] **Step 1: Write the failing tests.**
  - The `Tail` theory, over these inputs:
    - `ntilde.com mux attach abcd` → `mux attach abcd`;
    - `"C:\Program Files\x\ntilde.com" mux attach "a b"` → `mux attach "a b"`;
    - `ntilde.com` → `""`;
    - `ntilde.com   --x` → `--x`;
    - `"C:\a"b\ntilde.com x` (a quote in the middle, CRT rules) → `x`;
    - a tab separator.
  - `Build` quoting.
  - End to end (`Assert.SkipUnless(OperatingSystem.IsWindows())`):
    1. Copy the built launcher output (`Ntilde.Launcher.exe` + its dll + `runtimeconfig.json`, from the referenced project's output dir) into a temp dir.
    2. Copy `%SystemRoot%\System32\cmd.exe` there as `Ntilde.exe`.
    3. Run `Ntilde.Launcher.exe /d /c exit 7`: the exit code is 7.
    4. Run `Ntilde.Launcher.exe /d /c echo "a  b"&exit 0`: stdout contains `"a  b"` byte-identical, so no re-quoting happened.
    5. Release event: run `Ntilde.Launcher.exe /d /c "<path to a tiny helper that sets the event>"`. The helper is the App's `LauncherRelease.Signal()`, run in the test process: start the launcher with a child that just waits (`cmd /d /c ping -n 30 127.0.0.1`), read `NTILDE_LAUNCHER_RELEASE` from the child (capture the event name by giving the launcher an env override hook `NTILDE_LAUNCHER_RELEASE_NAME_FOR_TEST`?). Prefer the simpler route: test `LauncherRelease.Signal()` against an event the test creates. Prove the launcher's wait-on-event in a unit test of its wait logic, by extracting `int WaitForChildOrRelease(nint process, nint evt)` and testing it with a manual event and a real process handle.
  - `LauncherReleaseTests.Signal_sets_the_named_event_and_clears_the_variable` (Windows).
- [ ] **Step 2: Run the tests and verify they fail.** App.Tests `Lane!=PlatformBoot&FullyQualifiedName~Launcher`, then Architecture.Tests.
- [ ] **Step 3: Implement.** In the GUI path of `Program.cs`, call `LauncherRelease.Signal()` after the CLI-mode checks and before `BuildAvaloniaApp()...StartWithClassicDesktopLifetime`. Keep `CliCommandDispatchTests`' IL walk green: `LauncherRelease` is a different type.
- [ ] **Step 4: Verify the AOT publish locally:** `scripts/build.ps1 publish src/Ntilde.Launcher/Ntilde.Launcher.csproj -c Release -r win-x64 -o artifacts/launcher`. Copy the result as `artifacts/launcher/ntilde.com` and run `artifacts/launcher/ntilde.com` with no `Ntilde.exe` next to it: exit 9009. Record the size.
- [ ] **Step 5: Commit.** `feat(win): ntilde.com console launcher that waits, forwards exit codes, and releases GUI launches`

### Task 25: PATH registration, packaging, removal of the `cmd /c` hint

**Files:**
- Create: `src/Ntilde.App/Shell/UserPathRegistration.cs`
- Modify:
  - `src/Ntilde.App/Program.cs`: the Velopack hooks.
  - `src/Ntilde.Mux/Cli/MuxCli.cs` and the App adapter: remove `AttachConsoleHint`, the hint line, `IsGuiExecutable`, and the `cmd /c` text from the attach usage.
  - `.github/workflows/release.yml` (`publish_aot` win-x64: publish the launcher, copy it as `ntilde.com` before the smoke gate, assert it) and `.github/workflows/ci.yml` (`aot_gate`: publish and assert the launcher).
  - `docs/USER_MANUAL.md`: remove the `cmd /c` workaround text (the docs task adds the new text).
- Test: `tests/Ntilde.App.Tests/Shell/UserPathRegistrationTests.cs`; update the `MuxCommandTests`/`MuxCliTests` that pinned the hint into tests asserting its absence.

**Interfaces:**
- Produces:
  ```csharp
  internal static class UserPathRegistration
  {
      public static string Merge(string? existing, string directory, bool add);  // pure
      public static void Ensure(string directory);   // HKCU\Environment\Path REG_EXPAND_SZ + WM_SETTINGCHANGE; Windows only; logs and swallows failures
      public static void Remove(string directory);
  }
  ```
- `Merge` rules:
  - Split on `;` and drop empty entries.
  - Compare case-insensitively after `Environment.ExpandEnvironmentVariables` and `Path.TrimEndingDirectorySeparator`.
  - With `add`, append when missing; without it, remove every match.
  - Keep the order and the unexpanded spellings of the other entries.
- Velopack: `VelopackApp.Build().OnAfterInstallFastCallback(v => UserPathRegistration.Ensure(AppDir())).OnAfterUpdateFastCallback(v => UserPathRegistration.Ensure(AppDir())).OnBeforeUninstallFastCallback(v => UserPathRegistration.Remove(AppDir())).SetAutoApplyOnStartup(...).Run()`. Check the exact hook names against the Velopack 1.2.0 API in the restored package (`~/.nuget/packages/velopack/1.2.0`), and adjust if they differ.
- `AppDir()` is `Path.GetDirectoryName(Environment.ProcessPath)`.

- [ ] **Step 1: Write the failing tests.** A `Merge` theory: add to empty, add when present with a different case or a trailing `\`, keep `%USERPROFILE%\bin`, and remove all matches. Then `Attach_usage_no_longer_mentions_cmd_c`.
- [ ] **Step 2: Run the tests and verify they fail.**
- [ ] **Step 3: Implement.** Registry access: `Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment", writable: true)` with `GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames)` and `SetValue(..., RegistryValueKind.ExpandString)`. It is AOT-safe and Windows-only, guarded by `OperatingSystem.IsWindows()`. The broadcast is `SendMessageTimeoutW(HWND_BROADCAST=0xffff, WM_SETTINGCHANGE=0x1A, 0, "Environment", SMTO_ABORTIFHUNG=2, 5000, out _)`.
- [ ] **Step 4: Run** App.Tests `Lane!=PlatformBoot&(FullyQualifiedName~UserPathRegistration|FullyQualifiedName~MuxCommand)` and Mux.Tests `FullyQualifiedName~MuxCli`. Lint both workflow files.
- [ ] **Step 5: Commit.** `feat(win): put the install dir on the user PATH; ship ntilde.com; drop the cmd /c workaround`

---

## Wave H — end to end, docs, verification

### Task 26: Docker E2E for remote persistence (OpenSSH and native)

**Files:**
- Modify: `tests/Ntilde.ExternalSuites/NativeSsh/Dockerfile` (add `procps iproute2`; the comment explains v5), `tests/Ntilde.Platform.Tests/Ssh/DockerSshFixture.cs` (`ImageTag` v5; new helpers `ExecAsync(string shellCommand)`, `ContainerName`, `DisconnectNetworkAsync()`, `ReconnectNetworkAsync()`, `PauseAsync()`, `UnpauseAsync()`), `tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj` (link `DockerSshFixture.cs`, `NativeSshTestInteractionHandler.cs` and `Infra/DockerFactAttribute.cs` from Platform.Tests with `<Compile Include="..\Ntilde.Platform.Tests\…" Link="…"/>`; the fixture's namespace stays, which is fine for linked files)
- Create: `tests/Ntilde.App.Tests/Shell/Mux/Remote/RemoteMuxDockerE2eTests.cs`
- Modify: `.github/workflows/ci.yml` (`native_ssh_docker_e2e`: `needs: mux_daemon_aot` when it ran; download `ntilde-mux-linux-x64`, `chmod +x`, export `NTILDE_MUX_E2E_BINARY`; run App.Tests `Category=DockerE2E`). If `mux_daemon_aot` is skipped (no src change), skip the App.Tests step.

**Interfaces:**
- Consumes: everything from Waves C to F.
- The test class is `[Trait("Category","DockerE2E")]` and `[Trait("Lane","DockerE2E")]` (so the normal lanes' filters exclude it; check how App.Tests lanes exclude categories in ci.yml and in `scripts/check-app-tests-baseline.py`, and follow that). Each test is `[DockerFact]` plus `Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NTILDE_MUX_E2E_BINARY")), "set NTILDE_MUX_E2E_BINARY to a linux-x64 ntilde-mux")`.

- [ ] **Step 1: Write the tests.** One shared helper runs the scenario for a transport kind:
  1. Start the fixture. Build an `SshProfile` (host 127.0.0.1, mapped port, user `nova`, identity file = the plain key copied out, backend = OpenSSH or Native, `PersistRemoteSessions = true`).
     - **OpenSSH:** give the profile `ExtraSshArgs = "-o StrictHostKeyChecking=no -o UserKnownHostsFile=<temp>"`; ssh comes from `SshArgBuilder.ResolveSshExecutablePath`, so Windows uses the inbox OpenSSH.
     - **Native:** a `NativeKnownHostsStore` trusted from `GetHostKeyAsync`, with `NativeSshTestInteractionHandler`.
  2. Run `RemoteMuxInstaller` with `LocalFileMuxAssetSource(NTILDE_MUX_E2E_BINARY)`: success, and the version JSON has `rid == linux-x64`.
  3. Build `MuxConnectionHosts` with `RemoteMuxHostFactory`, and a `MuxTerminalSessionFactory` with a profile resolver returning this profile. `CreatePersistent(new TerminalSessionRequest(..., Ssh: new SshSessionDescriptor(profile.Id, 0, handler, true)))` gives `Spawned`.
  4. Attach (`AttachAsync`) with the output mirrored into a `TerminalBuffer` + `AnsiParser`. Send `top -d 1\r`, then wait for `load average` on screen. Read `topPid` via `fixture.ExecAsync("pgrep -u nova -x top")`.
  5. **Network drop:**
     1. `DisconnectNetworkAsync()`, then wait for the host's `ConnectionLost`: at most 40 s; liveness detects it within 25 s.
     2. `ReconnectNetworkAsync()`. If `docker port` no longer maps 22, the helper re-resolves the port, and the profile's port is updated.
     3. Wait for `Reconnected` (≤ 90 s), then reattach with `ReattachAfterDrop` (or through `TerminalPane` if the harness allows; the factory path is enough here).
     4. Assert `pgrep top` still gives `topPid` and the new snapshot contains `load average`.
     5. If the published port mapping is lost and cannot be restored, fall back to `PauseAsync`/`UnpauseAsync` for the drop. Log which method ran.
  6. **Proxy kill:** `pkill -9 -f 'ntilde-mux proxy'` → `ConnectionLost` → `Reconnected` → `top` intact, and the daemon pid (from the descriptor, read with `fixture.ExecAsync`) is unchanged.
  7. **Daemon kill:** `pkill -9 -f 'ntilde-mux serve'` → `DaemonStopped`. `GetClient` again, as the Enter path does, then `CreatePersistent` with the old id gives `PreviousLost`, a fresh shell.
  8. **Latency:**
     - Attach a fresh client to an empty session and measure `OpenSession` + `AttachAsync` → `SnapshotReceived`, five times; log the median.
     - In another session, run `seq 1 10000`, wait for `10000`, then measure the attach with `maxScrollbackRows = 10000`, five times; log the median.

     Use `ITestOutputHelper` lines `[latency] empty median=… ms` and `[latency] 10k median=… ms`.
- [ ] **Step 2: Build the binary and run locally on Windows.**
  1. `MSYS_NO_PATHCONV=1 bash scripts/docker-publish-mux-daemon.sh linux-x64 artifacts/mux-daemon`.
  2. PowerShell: `$env:NTILDE_ENABLE_DOCKER_E2E='1'; $env:NTILDE_MUX_E2E_BINARY="$PWD\artifacts\mux-daemon\linux-x64\ntilde-mux"; scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Category=DockerE2E" --blame-hang-timeout 5m --logger "console;verbosity=detailed" *> $env:TEMP\mux-e2e.log`.
  3. Expect both transports to pass. Save the log for the PR.
- [ ] **Step 3:** Run Platform.Tests' existing DockerE2E once against v5, to prove the image change is harmless: `$env:NTILDE_ENABLE_DOCKER_E2E='1'; scripts/build.ps1 test tests/Ntilde.Platform.Tests --filter "Category=DockerE2E"`.
- [ ] **Step 4: Commit.** `test(mux): Docker E2E - network drop, proxy kill and daemon kill against a real sshd, both transports`

### Task 27: Docs and roadmap

**Files:**
- Modify: `docs/USER_MANUAL.md` (§3.3 remote subsection plus `ntilde.com`), `docs/ARCHITECTURE.md` (the "fifteen focused .NET assemblies" count, the graph and table, the process diagram with the remote daemon and the proxy, endpoint identity, two AOT binaries), `docs/MODULE_OWNERSHIP.md` (rows for `Ntilde.Mux.Daemon`, `Ntilde.Launcher`, and the `Ntilde.Mux` `Daemon/` and `Cli/` namespaces), `docs/SSH_ROADMAP.md` (a remote persistence section), `docs/ROADMAP.md:246` and `:411` (spec §13 wording), `CONTRIBUTING.md` if it lists the projects or the Docker E2E env vars
- Test: `scripts/build.ps1 test tests/Ntilde.Architecture.Tests` and `tests/Ntilde.McpServer.Tests`: the MCP doc tools read docs; `TechnicalGapChecklist.md` must stay at the top level.

- [ ] **Step 1: Write the docs.**
  - The user manual remote section covers:
    - enabling persistence and the profile checkbox;
    - installing (download, local file, offline command);
    - what survives a disconnect: the shell and its programs, the screen and scrollback;
    - the reconnect behaviour: banners, the 10-minute budget, input not sent;
    - the limitations: no SFTP sidebar, remote files or port forwards on persistent tabs; no Windows
      remote hosts; glibc ≥ 2.35, no musl or BSD; Intel Macs unsupported; systemd
      `KillUserProcesses` (`loginctl enable-linger $USER`); a stale `SSH_AUTH_SOCK` in shells
      started before a reconnect;
    - `ntilde-mux ls` and `attach` on the remote host;
    - Windows: `ntilde mux attach <id>` works directly from PowerShell through `ntilde.com`, and the
      install dir is on PATH.
- [ ] **Step 2: Run** the two test projects.
- [ ] **Step 3: Commit.** `docs(mux): remote persistence, ntilde-mux, ntilde.com; roadmap describes what the multiplexer is and is not`

### Task 28: Full verification, review, PR

- [ ] **Step 1: Format.** `scripts/build.ps1 format whitespace --no-restore --verify-no-changes`, then fix and commit.
- [ ] **Step 2: Every test project, one at a time, on Windows.** Record passed, failed and skipped for each:
  - `scripts/build.ps1 test tests/Ntilde.Mux.Tests`
  - `scripts/build.ps1 test tests/Ntilde.VT.Tests`
  - `scripts/build.ps1 test tests/Ntilde.Architecture.Tests`
  - `scripts/build.ps1 test tests/Ntilde.McpServer.Tests`
  - `scripts/build.ps1 test tests/Ntilde.Platform.Tests`
  - `scripts/build.ps1 test tests/Ntilde.Rendering.Tests`
  - `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Lane!=PlatformBoot" --blame-hang-timeout 5m`, logged to a file
  - `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Lane=PlatformBoot" --blame-hang-timeout 5m`, logged to a file
  - `cargo test` in both crates
- [ ] **Step 3: Linux runs.** Run Mux.Tests and Platform.Tests in Docker (`mcr.microsoft.com/dotnet/sdk:10.0` with the repo copied in, rust installed, `scripts/build.sh test …`). Record the counts.
- [ ] **Step 4: AOT, local.**
  - App win-x64: `scripts/build.ps1 publish src/Ntilde.App/Ntilde.App.csproj "-p:PublishAot=true;SkipCliShim=true" -c Release -r win-x64 --self-contained true -o artifacts/aot/win-x64` (put vswhere's folder on PATH, as the memory note says). Expect 0 IL2026/IL3050.
  - The launcher, win-x64.
  - The daemon, linux-x64, through the Docker script.
- [ ] **Step 5: Push and open the PR.**
  - `git push -u origin feat/mux-phase4`.
  - `gh pr create --base dev-mux --head feat/mux-phase4`. The body covers:
    - the summary, and the files by deliverable;
    - which carry-overs landed with which test;
    - the test table;
    - the AOT table: local results plus CI `mux_daemon_aot`, and `aot_publish` once dispatched;
    - the E2E log excerpt (both transports);
    - sizes and latency;
    - the decisions;
    - the deviations from the brief;
    - the Phase 5 open items.
  - It ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- [ ] **Step 6: CI.**
  - Run `gh workflow run ci.yml --ref feat/mux-phase4` to get `aot_publish`, which covers the App's win-x64, linux-x64 and osx-arm64 legs.
  - Watch the PR checks with `rtk proxy gh pr checks <n> --watch`, in the background.
  - Re-run known flakes per memory: the Unit Tests host hang, the cargo dep fetch, the PTY backspace test.
  - Fix real failures.
- [ ] **Step 7: Manual Windows check of `ntilde.com`.** Use the AOT win-x64 publish with `ntilde.com` copied in. From PowerShell: `ntilde mux attach <id>` with no `cmd /c`. Check that Ctrl+C reaches the shell (`ping -t localhost` stops), that the detach chord exits 0, and that `ntilde mux attach nonexistent` exits 2, read as `$LASTEXITCODE`. If you cannot drive the console, use the AttachConsole + WriteConsoleInputW scratchpad pattern. Otherwise hand the user exact steps and record that.
