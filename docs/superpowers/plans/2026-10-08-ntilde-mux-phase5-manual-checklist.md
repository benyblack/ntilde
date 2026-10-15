# ntilde multiplexer Phase 5: manual checklist (Windows, Linux, macOS)

One checklist for the whole multiplexer, run before Phase 5 merges. It merges:

- PR #489's eight steps (Phase 2);
- Phase 3's extensions ([spec §8 item 7](../specs/2026-09-29-ntilde-mux-phase3.md), written out in the Phase 3 plan's Task 21);
- Phase 4's Windows `ntilde.com` steps ([plan Task 22 step 7](2026-10-05-ntilde-mux-phase4.md)) and the remote drop and
  reconnect scenario ([spec §12.3](../specs/2026-10-05-ntilde-mux-phase4.md));
- Phase 5's new steps: the first-close dialog, Quit and close all shells, reboot quiet restore, update survival
  (Tasks 20-24), the picker with remote hosts, `ls --all`, SFTP on a persistent native tab, and the agent host's
  `list_sessions` with a windowless session;
- two carries from Task 24: the no-overlap update path, and the macOS update-survival run;
- the release hardening of PR #512 ([spec §H, H1-H8](../specs/2026-10-08-ntilde-mux-phase5.md)): steps 14, 23, 41 and
  51 are rewritten for it, and section N (steps 54-58) is new. The implementer's results below predate it: where a
  rewritten expectation differs from what was run, the result says so.

**Automated steps.** A step with an **Automated:** line has a test that runs in CI and says what it covers; the rest of
the step stays manual. Window steps run headlessly against a real `Ntilde mux serve` with real shells
(`tests/Ntilde.App.Tests/Core/MainWindowRealDaemonTests.cs`, Category=PtySmoke); remote steps run against the Docker
sshd and a real `ntilde-mux` (`tests/Ntilde.App.Tests/Shell/Mux/Remote/RemoteMuxListingDockerE2eTests.cs`,
Category=DockerE2E). The result lines below still record runs by hand, except where a FAIL names an automated run.

UI strings are quoted from `docs/USER_MANUAL.md` chapter 12, "Persistent sessions and the multiplexer".

**Results.** Each step has a result line per OS, and each line is one of:

- `PASS (run by implementer, <date>, <log>)`: run by the Task 30 implementer and observed;
- `PASS (scripted run, <date>, <script>, <where>)`: run by the committed script (see **Scripts** below), unattended;
- `FAIL (...)`: run, and the product did not do what the step expects;
- `OPEN — maintainer`: not run yet. These are the GUI steps (automation by SendKeys is unreliable on the maintainer's
  Windows machine), every macOS step, Linux GUI steps that could not be driven headlessly, and anything that needs a
  saved password, an installer or a reboot;
- `N/A (why)`.

**Logs.** `<logs>` is `t30-smoke\logs\` in the Task 30 session's scratchpad
(`C:\Users\behna\AppData\Local\Temp\claude\D--projects-nova2\fac44242-4e5b-4e84-9d28-dd71bfdd700a\scratchpad\`). Task 31
pastes them into the PR. The scripts that produced them are in `t30-smoke\scripts\`.

**Scripts.** Those scripts are now committed, scrubbed and updated for PR #512, under
[`scripts/mux-smoke/`](../../../scripts/mux-smoke/README.md): one per group of cells, each taking the build and a
sandbox folder, printing the same PASS/FAIL lines and exiting 1 on a failure. A result line's `Scripted:` names the
script for that step and OS, and the CI job that runs it (`mux_smoke_linux`, `mux_smoke_windows`) or "manual". A
scripted run is recorded with its date and tree only where it was run; `Scripted:` alone claims nothing.

**What the implementer ran (2026-10-09, tree `de724c3`).**

- **Windows 11** (the maintainer's machine): a dev build (`scripts/build.ps1 build src/Ntilde.App`, Debug) copied into the
  sandbox, with `ntilde.com` published from `src/Ntilde.Launcher` (`-c Release -r win-x64`) beside it. Every process ran
  with `NTILDE_APPDATA_ROOT` set to the sandbox. The CLI cells passed 30 of 30 checks (`<logs>\10-win-cli-cells.log`).
  `ls --all` against the Docker sshd passed 12 of 12, including a dropped link (`<logs>\40-win-ls-all-cells.log`), plus
  a rotated host key (`<logs>\41-win-ls-all-changed-hostkey.log`).
- **Linux**: Ubuntu 24.04 in Docker (`ntilde-linux-build:local`), with `git archive` of `de724c3` built by
  `scripts/build.sh`. The CLI and daemon cells passed 29 of 29 (`<logs>\30-linux-cli-cells.log`). `ls --all` and the
  agent host passed 7 of 7 (`<logs>\50-linux-ls-all-agent-cells.log`), and the dropped link passed too
  (`<logs>\53-linux-drop-cells.log`). Kill-the-GUI under Xvfb passed 4 of 4 (`<logs>\52-linux-gui-kill-cells.log`).
- **The remote host** for every remote cell: `novaterm-native-ssh-e2e:v5` (Debian 12, glibc 2.36), user `nova`, key
  authentication only, with `ntilde-mux` 0.12.0 built by the release recipe (`scripts/docker-publish-mux-daemon.sh
  linux-x64`: Ubuntu 22.04, highest symbol `GLIBC_2.34`, `<logs>\21-publish-ntilde-mux-ubuntu2204.log`).
- **Hidden test verbs**: sessions were started with `ntilde mux spawn-for-test` (setup only), in the local daemon and,
  through the dev App's CLI copied into the sshd container, in the remote `ntilde-mux`. Every step that uses it says so.

## Setting up a sandbox

Run every step in a sandbox, never against your real Ntilde.

**1. A data folder of its own.** Set `NTILDE_APPDATA_ROOT` in the shell you start Ntilde from. Everything started from
that shell inherits it: `Ntilde.exe`, `ntilde.com`, the multiplexer the GUI spawns, and Velopack's hooks.

```powershell
# Windows (PowerShell)
$env:NTILDE_APPDATA_ROOT = "$env:TEMP\ntilde-smoke"
& <build>\Ntilde.exe            # or <build>\ntilde.com mux ls
```

```sh
# Linux / macOS
export NTILDE_APPDATA_ROOT="${TMPDIR:-/tmp}/ntilde-smoke"
<build>/Ntilde                   # macOS: <app>/Contents/MacOS/Ntilde, started directly so the variable reaches it
```

The multiplexer's pipe or socket, its descriptor, its `bin\<version>\` copies, settings, the session file, SSH profiles and
the native known-hosts file all move with it. **Four things do not**, so check them before you start:

- **The agent host's pipe on Windows**, `ntilde-agent-<user>`, is one per user. A sandbox Ntilde with *Agent access
  (observe)* on joins your real Ntilde's pipe, and your MCP clients may reach either. Keep agent access off in the
  sandbox, except for the agent steps (44-46): for those, quit your real Ntilde first. On Linux and macOS the agent socket
  is under the data folder.
- **The quake-mode hotkey** is global. Turn *Quake mode* off in the sandbox settings.
- **The system credential store is shared with your real install**: the Windows Credential Manager, the macOS Keychain,
  or the Linux Secret Service. `NTILDE_APPDATA_ROOT` does not isolate it. *Remember password* in a sandbox writes a
  real entry. An automatic OpenSSH attempt (a reconnect, or `ntilde mux ls --all`) reads the vault for the profile's saved
  password: first by the profile's id, then by older name-based keys that can match a real profile of yours. Use key
  authentication in the sandbox, never tick *Remember password*, and test the saved-password paths only with a
  throwaway account.
- **The Windows update-survival harness** (`scripts/mux-update-survival.ps1`, steps 36-38 and 50) installs a real
  Velopack package (`NtildeSurvival`, never `NtildeApp`). That edits machine state outside the sandbox: your user `PATH`
  in `HKCU\Environment`, the `HKCU\...\Uninstall\NtildeSurvival` key, and (if a pack ever makes them) Start Menu and
  Desktop shortcuts. The script snapshots each one first and restores and checks it at the end, even when a step fails.
  While it runs, do not open new terminals that read `PATH` or change your `PATH` yourself. If a run is killed before
  its cleanup, run it again with `-CleanupOnly` and compare `PATH` with the snapshot it printed.

**2. A sandbox `settings.json`** (in `$NTILDE_APPDATA_ROOT`):

```json
{
  "SessionPersistence": "KeepOnClose",
  "AgentAccessObserveEnabled": false,
  "QuakeModeEnabled": false,
  "AutomaticUpdateChecks": false
}
```

Delete `mux-close-choice` in the same folder to be asked the first-close question again.

**3. The Docker sshd for the remote steps.** It uses its own network with an explicit subnet, because Docker's address
pools have run out on this machine before. It is published on 127.0.0.1 only, on a port other than 2222.

```sh
docker network create --driver bridge --subnet 10.230.30.0/24 t30-net
docker run -d --name t30-ssh --hostname t30-ssh --network t30-net -p 127.0.0.1:2230:22 novaterm-native-ssh-e2e:v5

# A throwaway key, authorized for nova (the image's nova-pass password exists too: do not save it anywhere).
ssh-keygen -q -t ed25519 -N '' -C ntilde-smoke -f <sandbox>/sshkey/id_ed25519
docker exec -i t30-ssh sh -c 'cat > /home/nova/.ssh/authorized_keys && chown nova:nova /home/nova/.ssh/authorized_keys && chmod 600 /home/nova/.ssh/authorized_keys' < <sandbox>/sshkey/id_ed25519.pub

# Check: key only, known_hosts kept in the sandbox.
ssh -F /dev/null -i <sandbox>/sshkey/id_ed25519 -o IdentitiesOnly=yes -o UserKnownHostsFile=<sandbox>/sshkey/known_hosts \
    -o StrictHostKeyChecking=accept-new -o BatchMode=yes -o PasswordAuthentication=no -p 2230 nova@127.0.0.1 'echo key-auth-ok'

# ntilde-mux for the host: the image is Debian 12 (glibc 2.36), so build it with the release recipe (glibc 2.34 floor).
# A build from ntilde-linux-build:local (Ubuntu 24.04) needs GLIBC_2.39 and does not start there.
scripts/docker-publish-mux-daemon.sh linux-x64 <out>
# As nova, so every folder on the way is nova's: `install -d -o nova` leaves ~/.local/share/ntilde to root, and
# ntilde-mux then cannot make its root there ("Access to the path ... is denied").
docker exec -u nova t30-ssh mkdir -p /home/nova/.local/share/ntilde/bin
docker cp <out>/linux-x64/ntilde-mux t30-ssh:/home/nova/.local/share/ntilde/bin/ntilde-mux
docker exec t30-ssh chown nova:nova /home/nova/.local/share/ntilde/bin/ntilde-mux
# (or install it from the GUI: the profile editor's "Install ntilde-mux on this host…", then "Choose file…")
```

Instead of building it, you can take CI's: every CI run's `ntilde-mux AOT` jobs upload `ntilde-mux-linux-x64`,
`ntilde-mux-linux-arm64` and `ntilde-mux-osx-arm64` (kept 14 days), built the same way, and the Docker E2E job runs the
linux-x64 one in this image. Download it from the run's *Artifacts* (or `gh run download <run id> -R benyblack/ntilde -n
ntilde-mux-linux-x64 -D <out>/linux-x64`), `chmod +x` it (the zip drops the execute bit), and install it as above, with
`docker exec t30-ssh chmod 755 /home/nova/.local/share/ntilde/bin/ntilde-mux` after the `chown`. Do not use the install
dialog's download in a dev build: it fetches the published release of the build's version, which need not exist or
match this tree.

In the sandbox Ntilde, make an SSH profile for `nova@127.0.0.1`, port 2230, with the key as its identity file and
**Keep remote sessions running (ntilde-mux)** ticked; make one native and one OpenSSH. The host key prompt appears the
first time: accept it. For the native backend the key goes into the sandbox's `ssh\native_known_hosts.json`. **OpenSSH
uses your own `~/.ssh/known_hosts`**: the config Ntilde generates (`<data>\ssh\ssh_config.generated`) names no
`UserKnownHostsFile`, and OpenSSH's `UpdateHostKeys` may add the host's other keys there too (seen in the Linux run). Put
`-o UserKnownHostsFile=<sandbox>/sshkey/known_hosts` in the OpenSSH profile's extra SSH arguments to keep it in the
sandbox.

Drop the link with `docker network disconnect t30-net t30-ssh`, and restore it with `docker network connect t30-net
t30-ssh`. If the published port does not come back, use `docker pause t30-ssh` / `docker unpause t30-ssh` instead.

**4. Afterwards.** `ntilde mux kill-server` under the sandbox's `NTILDE_APPDATA_ROOT`, then `docker rm -f t30-ssh` and
`docker network rm t30-net`.

---

## A. Local persistence

### 1. Two shells keep running when the window closes
*PR #489 step 1.*
- **Preconditions:** a sandbox with *Keep running* (the default); `mux-close-choice` holds `keep`, or you answer
  **Keep running** at the first-close question (step 11).
- **Actions:** open two tabs. Run `Start-Sleep 1000` (Windows) or `sleep 1000` in one and `vim` in the other. Close the
  window.
- **Expected:** the window closes; nothing ends the shells.
- **Automated:**
  `MainWindowRealDaemonTests.Keep_running_leaves_the_real_shells_in_the_daemon_and_the_next_window_brings_them_back`
  (the PTY smoke lane, ubuntu-latest and windows-latest): the shells outlive the window close (pids alive). Not covered:
  `vim`, Task Manager.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 2. `ntilde mux ls` shows them, and one multiplexer owns them
*PR #489 step 2.*
- **Preconditions:** step 1.
- **Actions:** run `ntilde mux ls`. On Windows look at Task Manager → Details (and Process Explorer's tree); elsewhere
  `pgrep -af 'mux serve'` and `ps -o pid,ppid,args --ppid <daemon pid>`.
- **Expected:** 2 sessions, `running`, `0` attached. Exactly one `Ntilde mux serve` process, and both shells are its
  children.
- **Automated:**
  `MainWindowRealDaemonTests.Keep_running_leaves_the_real_shells_in_the_daemon_and_the_next_window_brings_them_back`
  (the PTY smoke lane, ubuntu-latest and windows-latest): 2 sessions `running` with 0 attached, one daemon throughout,
  and on Linux each shell's parent is that daemon.
- **Result / log:**
  - Windows: OPEN — maintainer (the GUI-made sessions). Step 9 shows the CLI half with `spawn-for-test` sessions:
    "shell processes ...: pid 43276 parent 67264; pid 70132 parent 67264", where 67264 is `Ntilde.exe mux serve`.
  - Linux: OPEN — maintainer (the GUI-made sessions). Step 9 shows the CLI half: "the daemon's children: 5091 5007 sleep
    100000; 5120 5007 sleep 100000".
  - macOS: OPEN — maintainer

### 3. A relaunch brings back the same shells
*PR #489 step 3.*
- **Preconditions:** step 1.
- **Actions:** start Ntilde again.
- **Expected:** both tabs come back with their screen and scrollback, and vim redraws. No notification is shown.
- **Automated:**
  `MainWindowRealDaemonTests.Keep_running_leaves_the_real_shells_in_the_daemon_and_the_next_window_brings_them_back`
  (the PTY smoke lane, ubuntu-latest and windows-latest): the next window reattaches the same sessions and shell pids,
  shows the earlier output, and raises no lost toast. Not covered: `vim` redrawing.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer. (The reattach half ran headlessly in step 4: the same session id, 1 attached, after a
    relaunch. The screen comparison needs eyes.)
  - macOS: OPEN — maintainer

### 4. Killing the GUI leaves the shells running
*PR #489 step 4.*
- **Preconditions:** a sandbox window with a shell.
- **Actions:** end the GUI process: Task Manager → End task on Windows, `kill -9 <pid>` elsewhere. Run `ntilde mux ls`.
  Start Ntilde again.
- **Expected:** after the kill, the session is `running` with `0` attached. The relaunch reattaches it: the same id, `1`
  attached, and no second session.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: PASS (run by implementer, 2026-10-09, `<logs>\52-linux-gui-kill-cells.log`). The GUI ran under Xvfb in
    Docker and was not otherwise driven. "after kill -9 of the GUI the session is still running, 0 attached"; "a
    relaunched GUI reattached the same session (1 attached), and started no second one"; SIGTERM likewise. (After the
    GUI died, `kill-server` in that container printed "Multiplexer did not stop within 5 s.". The daemon had exited, but
    as a zombie: the container's PID 1 is `sleep infinity`, which reaps nothing (`<logs>\51-linux-zombie-daemon-note.log`).
    A desktop's init reaps it.)
    Scripted: `scripts/mux-smoke/linux-gui-kill-cells.sh` (manual: it starts the GUI). Scripted run (2026-10-13,
    linux-arm64 in Docker, dev-mux at `b68fdf3`): 4 of 4.
  - macOS: OPEN — maintainer

### 5. Killing the multiplexer
*PR #489 step 5.*
- **Preconditions:** a sandbox window with a shell.
- **Actions:** end the `mux serve` process (Task Manager, or `kill -9`). Press Enter in the pane.
- **Expected:** the pane shows `[Multiplexer disconnected] [Press Enter to reconnect]`. Enter starts a new multiplexer
  and a new shell, writes `[Previous session was lost — started a new shell]`, and a *Previous session lost*
  notification says so.
- **Automated:**
  `MainWindowRealDaemonTests.A_killed_daemon_shows_its_banner_and_Enter_starts_a_new_daemon_and_shell_and_says_so` (the
  PTY smoke lane, ubuntu-latest and windows-latest): the banner, a new daemon and shell on Enter, the *Previous session
  lost* toast; on Linux the old shell ends with its daemon.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 6. Closing a pane ends its shell
*PR #489 step 6.*
- **Preconditions:** a sandbox window with two panes.
- **Actions:** close one pane. Run `ntilde mux ls`.
- **Expected:** its shell ends, and `ntilde mux ls` no longer lists it.
- **Automated:** `MainWindowRealDaemonTests.Closing_a_pane_ends_its_real_shell_and_no_other` (the PTY smoke lane,
  ubuntu-latest and windows-latest): the closed pane's shell pid ends and leaves the listing; the other stays.
- **Result / log:**
  - Windows: OPEN — maintainer (the CLI equivalent, `ntilde mux kill <id>`, passed in step 9)
  - Linux: OPEN — maintainer (the same)
  - macOS: OPEN — maintainer

### 7. `ntilde mux kill-server` ends everything
*PR #489 step 7.*
- **Preconditions:** a sandbox window with shells.
- **Actions:** run `ntilde mux kill-server`.
- **Expected:** "Multiplexer stopped." with exit code 0. The `mux serve` process and every shell end, and the window's
  panes show `[Multiplexer disconnected] [Press Enter to reconnect]`.
- **Automated:** `MainWindowRealDaemonTests.Kill_server_from_the_command_line_ends_the_daemon_and_its_real_shells_and_the_window_says_so`
  (the PTY smoke lane, ubuntu-latest and windows-latest): the output and exit code, the daemon and both shell pids end,
  both panes show the banner, and the window stays open.
- **Result / log:**
  - Windows: OPEN — maintainer (the panes). The CLI half passed in step 9: "kill-server: "Multiplexer stopped.", exit 0",
    "the daemon (pid 67264) has exited", "its remaining shell has exited".
  - Linux: FAIL, then fixed (automated headless run against a real `mux serve`, 2026-10-13). The window closed instead of
    showing the banner: the shutdown killed the shells while their clients were still connected, each pane saw its
    shell exit with code 0 and closed under the exit policy, and the last tab took the window with it. The shutdown now
    ends every client's connection first (`MuxDaemonHost.RequestStop`), so the panes see the multiplexer go away; the
    automated test above passes. The CLI half passed in step 9.
  - macOS: OPEN — maintainer

### 8. A second instance takes nobody's shells
*PR #489 step 8.*
- **Preconditions:** a sandbox window with shells, still open.
- **Actions:** start a second Ntilde (a second instance) with the same `NTILDE_APPDATA_ROOT`.
- **Expected:** it does not attach the first window's shells. Its restored panes start new shells, and a *Previous shell
  in use* notification reads `[Your previous shell is open in another window — started a new shell]`. No *Previous
  session lost* notification is shown.
- **Automated:** `MainWindowRealDaemonTests.A_second_instance_takes_no_shell_another_window_shows` (the PTY smoke lane,
  ubuntu-latest and windows-latest): the second instance takes no shell, toasts *Previous shell in use* with the banner
  text, and no *Previous session lost*.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 9. The command line, scripted: `ls`, `ls --json`, `kill`, `kill-server [--force]`, `attach` errors
*Phase 2/3 CLI. Setup uses the hidden `ntilde mux spawn-for-test` to start sessions.*
- **Preconditions:** a sandbox with no multiplexer running.
- **Actions:**
  1. With no multiplexer: `ntilde mux ls`, `ls --json`, `kill-server`, `attach abcd1234`.
  2. Start the multiplexer as the product does: `Ntilde mux serve`, detached from the caller's stdio. (Windows:
     `Start-Process ... -WindowStyle Hidden`, which inherits no handles; Unix: `setsid ... </dev/null >/dev/null 2>&1 &`.)
     Start two sessions with `spawn-for-test` (setup).
  3. `ls`, `ls --json`; `kill <id>`, then `ls`; `kill <unknown id>`; `kill not-a-guid`.
  4. `attach zzzz`, `attach ab`, `attach --help`.
  5. `kill-server`, then `ls` and `kill-server` again.
  6. Start it again with one session, then `kill-server --force`; `kill-server --bogus`.
- **Expected:**
  - The verbs never start a multiplexer. With none running they print "No multiplexer is running." and exit 1 (`attach`
    exits 2).
  - With an empty one, `ls` prints "No sessions." and exits 0.
  - `ls` prints `ID STATE ATTACHED SIZE TITLE` with both sessions `running`, `0` attached. `--json` prints
    `{"sessions":[...]}`.
  - `kill <id>` prints "Killed <id>." (exit 0), and its shell process ends; an unknown id prints "No session <id>."
    (exit 1); a malformed one prints the usage (exit 2).
  - `attach` exits 2 for "No session starts with 'zzzz'." and for a prefix under 4 characters. `--help` names
    **Ctrl+\ then d** and no `cmd /c`.
  - `kill-server` prints "Multiplexer stopped." (exit 0), and the daemon and its shells are gone. Afterwards `ls` exits
    1.
  - Against a multiplexer this build talks to, `kill-server --force` stops it as plain `kill-server` does.
  - On Unix the endpoint is a `0600` socket in a `0700` folder; on Windows it is the root-keyed pipe
    `ntilde-mux-<user>-<hash>`.
- **Result / log:**
  - Windows: PASS (run by implementer, 2026-10-09, `<logs>\10-win-cli-cells.log`, 30 of 30). Run through `ntilde.com`,
    plus `Ntilde.exe` with its output captured. "started: Ntilde.exe mux serve (pid 67264); descriptor: pid 67264,
    endpoint ntilde-mux-behna-21db78c2"; "Killed 417d373a-b558-46a7-96af-f5cba87d473e."; "the killed session's shell
    process (pid 43276) is gone"; "kill-server --force on a compatible daemon ...: "Multiplexer stopped.", exit 0".
    (`--force` against a multiplexer of another protocol version was not run: no such build exists. The unit tests in
    `MuxCliTests` cover it.)
    Scripted: `scripts/mux-smoke/win-cli-cells.ps1` (CI: `mux_smoke_windows`), which now also checks that the descriptor
    records the daemon's start time (H8: the kill path needs it).
  - Linux: PASS (run by implementer, 2026-10-09, `<logs>\30-linux-cli-cells.log`, 29 of 29). "endpoint
    /w/sbx/data/mux/mux.sock: 600 root socket; its folder /w/sbx/data/mux: 700 root"; "the daemon (pid 5007) has exited,
    with its shells".
    Scripted: `scripts/mux-smoke/linux-cli-cells.sh` (CI: `mux_smoke_linux`). Scripted run (2026-10-13, linux-arm64 in
    Docker, dev-mux at `b68fdf3`): 32 of 32 with step 10, now also `attach --help`, `kill-server --bogus` and the
    descriptor's recorded start time (H8).
  - macOS: OPEN — maintainer (run the same commands; `scripts/mux-smoke/linux-cli-cells.sh` is Linux only: it reads
    `/proc` and uses GNU `stat` and `ps --ppid`)

### 10. Version: `ntilde-mux --version [--json]`; `ntilde mux` has no version verb
- **Preconditions:** none.
- **Actions:** `ntilde-mux --version`, `ntilde-mux --version --json`, `ntilde-mux ls --all`; `ntilde mux version`.
- **Expected:**
  - `ntilde-mux --version` prints one line, `ntilde-mux <version> (protocol 1-2, <rid>)`.
  - `--json` prints `{"version":...,"protocolMin":1,"protocolMax":2,"rid":...,"path":...}`.
  - `ntilde-mux ls --all` exits 2 ("--all needs the ntilde app").
  - `ntilde mux version` is not a verb of the app: it prints the usage and exits 2.
- **Result / log:**
  - Windows: PASS (run by implementer, 2026-10-09, `<logs>\10-win-cli-cells.log`) for `ntilde mux version` (usage, exit
    2). `ntilde-mux` does not ship for Windows (the release has linux-x64, linux-arm64 and osx-arm64), so its binary is
    checked in the Linux cell.
    Scripted: `scripts/mux-smoke/win-cli-cells.ps1` (CI: `mux_smoke_windows`).
  - Linux: PASS (run by implementer, 2026-10-09, `<logs>\30-linux-cli-cells.log` and
    `<logs>\23-remote-ntilde-mux-version.log`). "ntilde-mux 0.12.0 (protocol 1-2, linux-x64)". The release-recipe binary
    on the Debian 12 host printed
    `{"version":"0.12.0","protocolMin":1,"protocolMax":2,"rid":"linux-x64","path":"/home/nova/.local/share/ntilde/bin/ntilde-mux"}`,
    and `ntilde-mux ls --all` there printed "--all needs the ntilde app (it connects to your SSH profiles)" (exit 2).
    Scripted: `scripts/mux-smoke/linux-cli-cells.sh` with its `ntilde-mux` argument (CI: `mux_smoke_linux`, on
    mux_daemon_aot's linux-x64 binary). Scripted run (2026-10-13, linux-arm64 in Docker, dev-mux at `b68fdf3`, CI's
    linux-arm64 binary of `b368c03`): "ntilde-mux 0.12.0 (protocol 1-2, linux-arm64)".
  - macOS: OPEN — maintainer (`ntilde-mux` osx-arm64 from the release, and the signed, notarized check:
    `codesign -dv`, `spctl -a -vv -t install`)

## B. The first close and quit

### 11. The first-close question
*Phase 5 Task 16.*
- **Preconditions:** a sandbox with no `mux-close-choice` file, and a window with two shells.
- **Actions:** close the window. Press Escape. Close it again and look at the dialog; press Enter.
- **Expected:**
  - A *Close Ntilde* dialog reads "Your shells keep running in the background." and "Reopen Ntilde to get them back.",
    with the number of shells, **Keep running**, **Close them** and **Don't ask again**.
  - Escape, or closing the dialog, leaves the window open.
  - Enter answers **Keep running**, wherever the focus is: the window closes, and `ntilde mux ls` shows both shells with
    0 attached.
  - The question appears only when local shells would be left running. A window with only remote persistent tabs closes
    without asking.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 12. **Close them**, and **Don't ask again**
*Phase 5 Task 16.*
- **Preconditions:** as step 11.
- **Actions:**
  1. Answer **Close them**. Run `ntilde mux ls`, and relaunch.
  2. Open shells, close the window, tick **Don't ask again** and answer **Keep running**. Close another window with
     shells.
  3. Change **Keep shells running when the window closes** in Settings and save, then close a window with shells.
- **Expected:**
  1. This window's shells end, and the next launch starts fresh shells with no "previous session lost" notice.
  2. `mux-close-choice` appears in the data folder, and the next close does not ask.
  3. Saving the setting forgets the answer: the question comes back. Importing or restoring settings that change it
     does the same.
- **Automated:**
  `MainWindowRealDaemonTests.Close_them_ends_the_real_shell_and_the_next_launch_starts_a_fresh_one_quietly` (the PTY
  smoke lane, ubuntu-latest and windows-latest): action 1 only: the shell pid ends, the daemon lists nothing, the next
  window starts a fresh shell with no lost toast.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 13. **Close them** keeps a shell another window shares
*Phase 5 Task 16, ruling I1.*
- **Preconditions:** a shell open in this window and in another (step 19), or in `ntilde mux attach` without
  `--read-only`.
- **Actions:** close this window and answer **Close them**.
- **Expected:** this window's unshared shells end. The shared one keeps running for the other window.
- **Automated:** `MainWindowRealDaemonTests.Close_them_keeps_the_real_shell_another_client_shows` (the PTY smoke lane,
  ubuntu-latest and windows-latest): this window's shell ends; the shared one runs on with the other client attached.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 14. Cmd+Q on macOS, and shutdown or sign-out
*Phase 5 Task 16; PR #512 H2 (amends R19: before it, Cmd+Q never asked).*
- **Preconditions:** shells running; no `mux-close-choice`.
- **Actions:**
  1. On macOS, Cmd+Q. Press Escape. Cmd+Q again and answer **Keep running**; run `ntilde mux ls`.
  2. Relaunch, Cmd+Q, and answer **Close them**; run `ntilde mux ls`.
  3. Relaunch, Cmd+Q, tick **Don't ask again** and answer **Keep running**. Relaunch and Cmd+Q again. Delete
     `mux-close-choice` and repeat 1-3 with *Quit Ntilde* from the menu bar and *Quit* from the Dock's menu.
  4. On any OS, sign out or shut down with Ntilde open and no `mux-close-choice` (on macOS also restart). Save your work
     first: signing out or shutting down also ends your real Ntilde, if it is running, and whatever else you have open.
- **Expected:**
  1. Cmd+Q shows the *Close Ntilde* question (step 11). Escape leaves Ntilde running with its window. **Keep running**
     closes the window and quits; `ls` shows the shells with 0 attached.
  2. This window's shells end, then Ntilde quits.
  3. The next Cmd+Q quits at once, keeping the shells, without asking. The menu bar's and the Dock's quit behave as
     Cmd+Q.
  4. Nothing asks and nothing holds the sign-out, restart or shutdown. Ntilde never ends a shell itself for one (with
     persistence turned Off in this window's life it does: step 54).
- **Result / log:**
  - Windows: OPEN — maintainer (sign-out / shutdown part)
  - Linux: OPEN — maintainer (logout / shutdown part)
  - macOS: OPEN — maintainer

### 15. Quit and close all shells
*Phase 5 Task 17.*
- **Preconditions:** this window with local shells, plus a detached shell (step 20) and, optionally, a remote persistent
  tab.
- **Actions:**
  1. Command palette → **Session: Quit and Close All Shells**. Read the dialog, then choose **Quit and close**.
  2. Relaunch.
  3. Repeat from Settings: the **Quit and close all shells…** link under the setting.
- **Expected:**
  1. A *Quit Ntilde* dialog reads "Close every shell?" and "N shells running in the background will be closed, including
     detached and shared ones.", with "Remote shells keep running." added when the window has remote persistent tabs,
     and a **Quit and close** button. Afterwards every local shell has ended (this window's, other windows' and detached
     ones), the multiplexer has stopped, and the window closes. Remote shells keep running (`ntilde mux ls --all`).
  2. The next launch starts fresh shells, with no "previous session lost" notice.
  3. The Settings link closes Settings without saving, then does the same. The command has no default shortcut;
     `quit_close_all_shells` can be bound under Settings → Shortcuts.
- **Automated:** `MainWindowRealDaemonTests.Quit_and_close_all_shells_ends_every_real_shell_and_stops_the_daemon` (the
  PTY smoke lane, ubuntu-latest and windows-latest): action 1 without the dialog: own, shared and detached shells end,
  the daemon exits and its descriptor is gone.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

## C. Reboot quiet restore

### 16. After a restart of the computer, fresh shells and no warning
*Phase 5 Task 19 (spec R2/R3).*
- **Preconditions:** a sandbox with shells in two tabs, one tab never shown since launch, and the window closed with
  **Keep running**.
- **Actions:** save your work (the restart also ends your real Ntilde and its local shells), then restart the
  computer. Start the sandbox Ntilde.
- **Expected:** each saved pane starts a fresh shell where it was. No notification is shown, the never-shown tab
  included. On Windows, the same after signing out and in, and after *Shut down* with Fast Startup on.
- **Result / log:**
  - Windows: OPEN — maintainer (reboot; and sign-out / Fast Startup shutdown)
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 17. Without a restart, a lost shell is announced
*Phase 5 Task 19 (the contrast case).*
- **Preconditions:** as step 16.
- **Actions:** instead of restarting, end the `mux serve` process. Start Ntilde.
- **Expected:** each pane starts a fresh shell and writes `[Previous session was lost — started a new shell]`, and one
  *Previous session lost* notification covers them all. A tab opened from *Attach to session…* closes instead, with an
  *Attach to session* notification reading `[The shell you chose has ended]`.
- **Automated:** `MainWindowRealDaemonTests.A_relaunch_after_the_daemon_died_starts_fresh_real_shells_and_says_so` (the
  PTY smoke lane, ubuntu-latest and windows-latest): fresh shells in both restored tabs and the *Previous session lost*
  toast. Not covered: the *Attach to session* tab case.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 18. Remote tabs reattach after a local restart
*Phase 5 Task 19.*
- **Preconditions:** a persistent SSH tab running `top -d 1` on a host that is **not** this computer: another machine,
  or a VM that stays up. The Docker container does not do here, because it restarts with Docker, and that ends its
  `ntilde-mux`.
- **Actions:** note `top`'s pid on the host. Save your work (the restart also ends your real Ntilde and its local
  shells), then restart this computer, and start Ntilde.
- **Expected:** the remote tab reattaches to the same `top` (the same pid), with no notification.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

## D. Several windows and `attach`

### 19. The same shell in two windows
*Phase 3 extension, step 9.*
- **Preconditions:** window A with a shell; window B, a second instance with the same `NTILDE_APPDATA_ROOT`.
- **Actions:**
  1. In B, command palette → **Session: Attach to Session…**, and choose A's shell.
  2. Type `echo from-A` in A, then type in B.
  3. Resize B's window, then click into A.
- **Expected:**
  1. The *Attach to Session* dialog reads "Attach to a running session. It opens in a new tab and stays shared with its
     other windows.", and lists A's shell with `1` attached.
  2. B gets a new tab with the same screen. Both panes show the "shared with 1" badge, and both tabs carry ⧉ in
     vertical-tab mode. Each window's typing appears in the other at once.
  3. After B's resize, A letterboxes. Clicking into A takes the size back.
- **Automated:** `MainWindowRealDaemonTests.The_same_real_shell_in_two_windows_echoes_both_ways` (the PTY smoke lane,
  ubuntu-latest and windows-latest): both windows know the shell is shared, and each one's typing appears in the other.
  Not covered: the dialog text, badges, letterboxing.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 20. Detach in one window; the other keeps the shell
*Phase 3 extension, step 10.*
- **Preconditions:** step 19.
- **Actions:** in B, **Pane: Detach** (bind `detach_pane` first; it has no default shortcut). Run `ntilde mux ls`.
- **Expected:** a *Shell detached* notification reads "Shell kept running — Attach to session… to get it back". A's
  badge disappears and A's shell still works. `ntilde mux ls` shows the session with 1 attached.
- **Automated:** `MainWindowRealDaemonTests.A_detached_real_shell_keeps_running_is_not_reopened_and_is_announced_once`
  (the PTY smoke lane, ubuntu-latest and windows-latest): the *Shell detached* toast text; the session runs on, 0
  attached, detached by the user.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 21. A detached shell stays detached across a restart
*Phase 3 extension, step 10b.*
- **Preconditions:** a shell detached with **Pane: Detach** and open in no window. Close every window.
- **Actions:** run `ntilde mux ls`, relaunch, then **Attach to session…**.
- **Expected:** `ls` shows it `running, detached` (`--json`: `"detachedByUser": true`). The relaunch does not reopen it
  as a tab. Once, a *Detached shells* notification reads "1 detached shell is running — Attach to session… to reopen it".
  *Attach to session…* reopens it, and `ls` then shows `running`.
- **Automated:** `MainWindowRealDaemonTests.A_detached_real_shell_keeps_running_is_not_reopened_and_is_announced_once`
  (the PTY smoke lane, ubuntu-latest and windows-latest): the next window does not reopen it, toasts *Detached shells*
  once, and *Attach to session…* reopens the same pid.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 22. `ntilde mux attach` from a terminal
*Phase 3 extension, step 12; Phase 4 removed the `cmd /c`.*
- **Preconditions:** a GUI pane with a shell.
- **Actions:**
  1. From a terminal, run `ntilde mux ls`, then `ntilde mux attach <first 8 characters of the id>`.
  2. Type `dir` (or `ls`) and Enter.
  3. Press **Ctrl+\ then d**, and read the exit code.
  4. Repeat with `--read-only`.
- **Expected:**
  1. The screen is painted, with colours and the cursor, and the GUI pane shows "shared with 1".
  2. The command runs in both.
  3. You get `[detached from <id>]`, the prompt is back, typing echoes, and the exit code is 0. After detaching, Linux
     and macOS `stty -a` shows `icanon echo`.
  4. With `--read-only` the status line reads "read-only", typing does nothing in the session, and detaching works.
- **Result / log:**
  - Windows: OPEN — maintainer (PowerShell and cmd, straight through `ntilde.com`; see step 47)
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

## E. The picker with remote hosts

### 23. The picker lists this computer, then every connected host, and offers the others
*Phase 5 Task 25; PR #512 H7 (the Connect row's wording).*
- **Preconditions:** the Docker sshd and two persistent profiles (setup 3). A local shell. One persistent tab open on
  profile 1; profile 2 not connected.
- **Actions:** **Session: Attach to Session…**.
- **Expected:**
  - This computer's shells come first, then lines starting `[nova@127.0.0.1]` for the connected host.
  - For profile 2, a *Connect to nova@127.0.0.1… (starts its multiplexer if none is running)* row. Choosing it shows a
    *Connecting to nova@127.0.0.1…* notification, then reopens the picker with that host's shells.
  - With the container stopped (`docker stop t30-ssh`), choosing it gives "Could not connect to nova@127.0.0.1.".
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 24. A host that cannot be listed does not hold the others up
*Phase 5 Task 25.*
- **Preconditions:** step 23, with a tab on the host.
- **Actions:** `docker network disconnect t30-net t30-ssh`, then open the picker at once.
- **Expected:** the local shells are listed, and the host shows `[nova@127.0.0.1] not reachable: <reason>`, the reason
  being one of "the connection failed", "it did not answer in time", "its multiplexer is not usable" or "the multiplexer
  is restarting". With nothing to choose, the notification gives those lines, or "No sessions are running in the
  multiplexer.". Reconnect the network afterwards.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 25. Choosing a remote shell
*Phase 5 Task 25.*
- **Preconditions:** a remote shell listed in the picker. Window B is a second instance.
- **Actions:** choose the remote shell in B. Then, in the window that already shows it, choose it again.
- **Expected:** B gets a new tab shared with the window that shows it ("shared with 1"). Choosing a shell this window
  already shows switches to that tab. If the profile stops keeping its sessions while the picker is open, the
  notification reads "The profile for nova@127.0.0.1 no longer keeps remote sessions running.".
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

## F. Remote drop and reconnect (Docker `network disconnect/connect`)

### 26. The tab survives a dropped link
*Phase 4 spec §12.3, manually.*
- **Preconditions:** a persistent tab on t30-ssh (native, then OpenSSH) running `top -d 1`. Note the pid with
  `docker exec t30-ssh pgrep -x top`.
- **Actions:**
  1. `docker network disconnect t30-net t30-ssh`. Wait; type a key.
  2. `docker network connect t30-net t30-ssh`.
- **Expected:**
  1. Within about 25 seconds: `[Connection to nova@127.0.0.1 lost — reconnecting…]`, with the last screen kept. The
     first key writes `[Input is not sent while reconnecting]`, and nothing typed is sent later.
  2. The tab reattaches by itself: the current screen replaces the banners, and `top` has the same pid.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 27. The remote multiplexer and its sessions outlive a dropped link (scripted, through `ls --all`)
*Phase 5; setup uses `spawn-for-test` in the remote `ntilde-mux`.*
- **Preconditions:** the sandbox profile keeps its remote sessions (native, key). Two sessions run in the host's
  `ntilde-mux` (setup).
- **Actions:** `ntilde mux ls --all`; `docker network disconnect t30-net t30-ssh`; `ls --all`; `docker network connect
  t30-net t30-ssh`; `ls --all`.
- **Expected:** while the host is off the network, its line reads `unreachable: ...`, within the 15 s per-host wait,
  and this computer is still listed (exit 0). Afterwards the same two remote session ids are listed, still `running`,
  and the host's `ntilde-mux serve` kept its pid.
- **Result / log:**
  - Windows: PASS (run by implementer, 2026-10-09, `<logs>\40-win-ls-all-cells.log`). "nova@127.0.0.1  unreachable: it
    did not answer in time (exit 0, 15.5 s)"; "after the host is back: the same two remote session ids, still running";
    "the remote ntilde-mux kept its pid across the drop - before 375, after 375".
    Scripted: `scripts/mux-smoke/win-ls-all-cells.ps1` J3 (manual: it needs Docker Desktop with Linux containers). Not
    run since the scripts were committed.
  - Linux: PASS (run by implementer, 2026-10-09, `<logs>\53-linux-drop-cells.log`). From the t30-linux container over
    the Docker network: "nova@t30-ssh  unreachable: the connection failed" while the host was off the network (8 s; the
    host name no longer resolves), then the same two ids `374cb101-…` and `3f0ccbcc-…`, and "remote ntilde-mux serve pid
    after: 375". (Its closing `kill-server` met the container's zombie again; see step 4.)
    Scripted: `scripts/mux-smoke/linux-ls-all-cells.sh` L3, on this computer rather than from a second container (CI:
    `mux_smoke_linux`). Scripted run (2026-10-13, linux-arm64 in Docker, dev-mux at `b68fdf3`): "nova@127.0.0.1
    unreachable: the connection failed" for both key profiles (0 s: the published port refused at once), then the same
    two ids, and the remote ntilde-mux kept its pid.
  - macOS: OPEN — maintainer

### 28. The proxy killed; the remote multiplexer killed
*Phase 4 spec §12.3.*
- **Preconditions:** step 26's tab, reattached.
- **Actions:**
  1. `docker exec t30-ssh pkill -9 -f 'ntilde-mux proxy'`.
  2. `docker exec t30-ssh pkill -9 -f 'ntilde-mux serve'`, then Enter in the tab.
- **Expected:**
  1. The tab reattaches, and the daemon's pid is unchanged.
  2. `[ntilde-mux on nova@127.0.0.1 stopped] [Press Enter to reconnect]`. Enter starts a new `ntilde-mux` and a new
     shell, and a *Previous session lost* notification reads `[Previous session was lost — started a new shell]`.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 29. Reconnecting gives up after 10 minutes, and on a host key it cannot trust
*Phase 4 §7.3; Phase 4 after-merge saved-password rules.*
- **Preconditions:** step 26's tab.
- **Actions:**
  1. Disconnect the network for more than 10 minutes, then reconnect it and press Enter.
  2. Give the host new host keys without restarting it, so `ntilde-mux` keeps running: `docker exec t30-ssh sh -c 'rm
     -f /etc/ssh/ssh_host_* && ssh-keygen -A && kill -HUP 1'` (sshd is PID 1 and re-execs on SIGHUP). Recreating the
     container does not change them: the image carries host keys from its build. Then drop and restore the link, and
     press Enter in the tab. (The command was checked: a new key is served and `ntilde-mux` keeps its pid,
     `<logs>\54-hostkey-rotation-check.log`.) An OpenSSH profile without the sandbox `UserKnownHostsFile` override
     leaves the old key in your real `~/.ssh/known_hosts`; remove it afterwards with `ssh-keygen -R "[127.0.0.1]:2230"`.
- **Expected:**
  1. After 10 minutes, `[Connection to nova@127.0.0.1 lost] [Press Enter to reconnect]`; Enter reconnects.
  2. The retries stop after one attempt, with `[Host key for nova@127.0.0.1 is unknown or has changed — press Enter to
     review]` (native), or the OpenSSH line telling you to remove the old key from `known_hosts`. Enter shows the usual
     host-key question.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

## G. Shared tabs

### 30. Closing a shared shell asks
*Phase 3 extension, step 11.*
- **Preconditions:** a local shell shared by windows A and B (step 19).
- **Actions:** in A, close the pane (Ctrl+Shift+W). Choose Cancel; close it again and choose **Close**.
- **Expected:** the question offers **Close** (ends the shell for every window), **Detach** and **Cancel**. Cancel
  changes nothing. After **Close**, A's pane closes and B's pane shows `[Shell ended from another window]`.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 31. A shared remote tab stays shared and never kills while its sharing is unknown
*Phase 5 Task 26.*
- **Preconditions:** a remote shell open in window A and, through the picker, in window B.
- **Actions:**
  1. In B, close the tab. Choose **Detach**.
  2. Share it again. Disconnect the network, and close B's tab while it shows `[Connection to … lost — reconnecting…]`.
- **Expected:**
  1. The question appears as for a local share, and Detach leaves A's tab working.
  2. Closing detaches without asking, the shell keeps running, and a *Shell detached* notification reads "Shell kept
     running on nova@127.0.0.1 — Attach to session… reopens it".
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 32. Two windows on one shared remote session keep their own registrations
*Phase 5 Task 28 fix (`a9cf7c4`).*
- **Preconditions:** a native persistent tab in window A, shared into window B.
- **Actions:** close B's share. In A, open *Remote Files*.
- **Expected:** A's sidebar opens and lists the remote home. The password A typed (if any) still works for it: no second
  prompt.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

## H. SFTP on a persistent native tab

### 33. Remote Files and SFTP transfers on a persistent tab
*Phase 5 Task 28.*
- **Preconditions:** a native persistent tab on t30-ssh (key authentication). An OpenSSH persistent tab of the same host.
- **Actions:**
  1. On the native tab, open *Remote Files*; browse, download a file, upload one.
  2. Use the palette's *SFTP: Upload…* and *SFTP: Download…*.
  3. Do the same on the OpenSSH tab.
- **Expected:**
  1. The sidebar works as on a plain SSH tab, on a connection of its own, without asking for anything again.
  2. The palette transfers work.
  3. On the OpenSSH tab only the palette's transfers work, and there is no *Remote Files* sidebar.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 34. A tab still on the old host refuses the new host's files
*Phase 5 Task 28.*
- **Preconditions:** step 33's native tab, with *Remote Files* open.
- **Actions:** edit the profile's port or user (for example, a second container on port 2231) and save.
- **Expected:** the open sidebar closes. *Remote Files*, its transfers and the palette's SFTP commands are refused on that
  tab with "Not available while this tab still runs on the host it was opened on — reopen the tab to use the new host".
  A new tab uses the new host once every tab of the profile in the window is closed.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 35. No port forwards on a persistent tab
*Phase 4 §8.4, Phase 5 R8.*
- **Preconditions:** a persistent profile with a local forward defined.
- **Actions:** open a persistent tab and try the forward.
- **Expected:** the forward is not set up, and the connection editor says so under the checkbox.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

## I. Updates

### 36. Shells survive an update (the update-survival harness)
*Phase 5 Tasks 20-24 (R9, R10).*
- **Preconditions:** the repository; nothing else. The harness makes its own sandbox and installs as `NtildeSurvival`,
  never as `NtildeApp`. On Windows it edits your real user `PATH`, the `Uninstall\NtildeSurvival` key and any
  shortcuts, and restores them at the end (the setup block's fourth "do not move" item): open no new terminals while it
  runs.
- **Actions:**
  - Windows: `scripts/mux-update-survival.ps1 -Sandbox <dir under %TEMP%>`.
  - Linux: `zsh scripts/mux-update-survival.sh --sandbox <dir>`, with zsh, FUSE and Xvfb (Task 24 ran it in
    `ntilde-linux-build:local` with `--device /dev/fuse --cap-add SYS_ADMIN`).
  - macOS: `zsh scripts/mux-update-survival.sh --sandbox <dir>`, with the .NET SDK, the Xcode command-line tools and git
    (**the Task 24 carry**).
- **Expected:** every check passes:
  - the daemon keeps its pid;
  - both heartbeat sessions keep beating across the apply;
  - `ntilde mux ls` lists the same ids;
  - the new GUI logs "the multiplexer on this computer is from another build (… this is …); offering a restart".
  - On Windows also: the uninstall hook stops the daemon and removes `data\bin\`, and the user PATH comes back
    byte-identical.
  - On macOS the startup auto-apply is not exercised: the script applies with `--norestart` and starts .2 itself.
- **Result / log:**
  - Windows: OPEN — maintainer. Not re-run at `de724c3` by Task 30. Task 24 ran it on 2026-10-09 against its own
    builds: 34 of 34 checks after fix round 1.
  - Linux: OPEN — maintainer. The same: Task 24 ran it in Docker, 15 of 15.
  - macOS: OPEN — maintainer (never run; the script's Portable `.app` layout and `~/Library` state paths are unverified)

### 37. "From the previous build" and **Restart multiplexer now**
*Phase 5 Task 23.*
- **Preconditions:** `scripts/mux-update-survival.ps1 -Sandbox <dir> -LeaveRunning` (Windows) or `--leave-running`
  (Linux) has finished. It leaves the .2 GUI running on the .1 daemon.
- **Actions:** in the sandbox window, read the *Multiplexer* notification. Click **Restart multiplexer now**, then
  **Restart**. Press Enter in a pane right away, and again once the restart is over. Clean up with `-CleanupOnly` /
  `--cleanup-only`.
- **Expected:**
  - The notification reads "The multiplexer is from the previous build (0.12.0-survival.1); restart it when convenient
    — this closes its N shells.".
  - A *Restart Multiplexer* dialog reads "Restart the multiplexer?" and "N shells running in the multiplexer will be
    closed.", with **Restart**.
  - The panes show `[Multiplexer disconnected] [Press Enter to reconnect]`. Enter during the restart writes `[The
    multiplexer is restarting — the new shell starts once it is back]`; after it, Enter starts a new shell. A new
    `mux serve` runs from `data\bin\0.12.0-survival.2\`.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: N/A (`--leave-running` is refused on macOS: Velopack restarts the app with `open -n`, which drops the sandbox's
    environment)

### 38. The no-overlap path: an update that cannot keep the multiplexer
*The Task 24 carry (report §7).*
- **Preconditions:** step 37's sandbox after its `-CleanupOnly`. The run below installs afresh, and `-SkipBuild` reuses
  that sandbox's builds, so the daemon it leaves running has not been through step 37's restart. As in step 36, the
  harness edits your real user `PATH` and `Uninstall` key until its `-CleanupOnly`.
- **Actions:**
  1. `scripts/mux-update-survival.ps1 -Sandbox <dir> -SkipBuild -LeaveRunning` (Linux: `--skip-build
     --leave-running`). It runs steps 1-8, packs `0.12.0-survival.3` (the .2 build with the marker
     `ntilde-mux-protocol: 3-3`) into the feed, and leaves the .2 GUI and the daemon running.
  2. In the sandbox's Ntilde window, open the palette and run "Check for updates". A notification says .3 is downloaded.
  3. Run "Restart to update", or the notification's button. Read the question, and confirm it.
  4. Check the result.
  5. `scripts/mux-update-survival.ps1 -Sandbox <dir> -CleanupOnly` (Linux: `--cleanup-only`).
- **Expected:**
  3. The *Apply Update* dialog reads "Multiplexed sessions are still running." and "N multiplexed sessions will be closed
     by the update (the new version cannot keep them).", with **Close sessions and update** and **Cancel**. Cancel
     leaves the update unapplied.
  4. The daemon's pid exits, and the heartbeat files stop growing. `current\sq.version` becomes `.3`. The restarted GUI
     starts a fresh daemon, with a new pid and its image under `<data>\bin\`. `<data>\logs\debug.log` has "[MainWindow]
     the update closes the multiplexer: protocol 1-2, the new build's 3-3; ...".
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: N/A (`--leave-running` is refused on macOS, as in step 37)

### 39. Updates with the setting off
*Phase 5 Task 22 (ruling: Off keeps pre-Phase-5 behaviour).*
- **Preconditions:** the survival sandbox with **Keep shells running when the window closes** set to *Off*, and a
  multiplexer running (`ntilde mux ls`).
- **Actions:** stage an update and apply it from the notification. Then stage another and relaunch.
- **Expected:** the in-app apply asks first ("N multiplexed sessions will be closed by the update.") and stops the
  multiplexer. While any multiplexer runs, a staged update is not applied automatically at startup.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 40. A remote `ntilde-mux` from a previous version
*Phase 5 Task 23 (the remote notice).*
- **Preconditions:** an `ntilde-mux` of an older version on t30-ssh, and a persistent tab on it with a shell. To make
  one, run `scripts/docker-publish-mux-daemon.sh` from a copy whose publish line adds `-p:Version=0.11.0
  -p:InformationalVersion=0.11.0`, then install it with *Choose file…* in the install dialog.
- **Actions:** reconnect the tab, or open a new one. Click **Restart ntilde-mux on nova@127.0.0.1**, confirm, then press
  Enter in the tab.
- **Expected:** a *Multiplexer* notification reads "ntilde-mux on nova@127.0.0.1 is from a previous version (0.11.0);
  restart it when convenient — this closes its N shells.". The restart stops that `ntilde-mux` only, and the tab shows
  `[ntilde-mux on nova@127.0.0.1 stopped] [Press Enter to reconnect]`. Enter starts the installed version once the old
  one has stopped. If the connection is down, or it is already the installed version, nothing is sent and a notification
  says so.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

## J. `ntilde mux ls --all`

### 41. `ls --all` lists this computer, then every host that keeps sessions (key authentication, scripted)
*Phase 5 Task 27; PR #512 H7 (reverses R28(a): `ls --all` no longer starts a multiplexer). Setup uses `spawn-for-test`
in the local daemon and in the host's `ntilde-mux`.*
- **Preconditions:** the sandbox has SSH profiles with **Keep remote sessions running (ntilde-mux)**, using the throwaway
  key. Their host keys are trusted in the sandbox (native: `ssh\native_known_hosts.json`). A local daemon with one
  session.
- **Actions:**
  1. `ntilde mux ls --all` while the host's `ntilde-mux` is not running (`docker exec t30-ssh pkill -f 'ntilde-mux
     serve'`, with no persistent tab on the host open). Then `docker exec t30-ssh pgrep -af 'ntilde-mux serve'`.
  2. Start two sessions on the host (setup); `ls --all`; `ls --all --json`.
  3. Stop the local multiplexer; `ls --all`; plain `ls`.
  4. Untick the profile's checkbox (here, `PersistRemoteSessions: false` in the sandbox store); `ls --all`.
- **Expected:**
  1. A `HOST` column in front of `ls`'s. `this computer` comes first, then `nova@<host>  unreachable: no multiplexer is
     running` (`--json`: `"error":"no multiplexer is running"`). Exit 0: only this computer's listing sets the exit
     code. `pgrep` prints nothing: the listing started no `ntilde-mux` on the host.
  2. Both remote sessions are listed under the host. `--json` prints
     `{"endpoints":[{"endpoint":"local","host":"this computer","sessions":[…]},{"endpoint":"ssh:<profile id>","host":"user@host","sessions":[…]}]}`.
  3. `this computer  unreachable: no multiplexer is running`, the remote host still listed, exit 1. Plain `ls` never
     connects anywhere.
  4. The table ends "No remote hosts keep sessions.".
  - Nothing prompts. A native profile that signs in with its key never asks the credential store; an OpenSSH one does
    (step 42). `logs\mux-ls-all.log` holds the last run's lines.
- **Automated:**
  `RemoteMuxListingDockerE2eTests.Ls_all_starts_no_multiplexer_lists_a_running_one_and_never_trusts_a_key_or_starts_an_older_one`
  (the native SSH Docker E2E job, ubuntu-latest): OpenSSH, Docker sshd, real `ntilde-mux`: action 1 (nothing started,
  `pgrep`), action 2 (the session listed, one daemon), text and `--json`.
- **Result / log:**
  - Windows: PASS before PR #512 (run by implementer, 2026-10-09, `<logs>\40-win-ls-all-cells.log`, 12 of 12); action 1
    is OPEN — maintainer since H7 (that run expected `No sessions.` and a started `ntilde-mux`). Native backend, key
    only:

    ```
    HOST            ID                                    STATE               ATTACHED  SIZE       TITLE
    this computer   1bdd94a6-2318-4879-a0c5-0734169d5f92  running                    0  80x24      ...\t30shell.exe
    nova@127.0.0.1  374cb101-b04c-40c2-8c54-86888a1adf6a  running                    0  80x24      spawn-for-test
    nova@127.0.0.1  3f0ccbcc-bfbe-4c4c-a7ce-e81f4b2bb5f5  running                    0  80x24      spawn-for-test
    ```

    The log shows only "[NativeSshExec] ... exec on nova@127.0.0.1:2230 via 0 jump hop(s)", "[RemoteMux]
    nova@127.0.0.1: connected (daemon pid 375, protocol 2, automatic)" and "exited with 0". (The first run's JSON check
    failed on the script's own expectation: it looked for the profile id with dashes, and the endpoint is
    `ssh:7a3c0f30000040008000000000000030`. Fixed in the script and re-run;
    `<logs>\40-win-ls-all-cells.run1-script-bug.log`.)
    Scripted: `scripts/mux-smoke/win-ls-all-cells.ps1` J1, J2, J4 and J5, updated for H7 (manual: Docker Desktop with
    Linux containers). Not run since the scripts were committed.
  - Linux: PASS before PR #512 (run by implementer, 2026-10-09, `<logs>\50-linux-ls-all-agent-cells.log`); action 1 is
    OPEN — maintainer since H7. A native and an OpenSSH profile, both key only, against `t30-ssh:22` over the Docker
    network: "ls --all: this computer's session, then both key profiles' remote sessions under nova@t30-ssh, exit 0 - 4
    remote rows"; the OpenSSH attempt ran "(batch mode: ssh will not prompt)".
    Scripted: `scripts/mux-smoke/linux-ls-all-cells.sh` L1, L2, L4 and L5 (CI: `mux_smoke_linux`). PASS (scripted run,
    2026-10-13, linux-arm64 in Docker, dev-mux at `b68fdf3`), action 1 included: "nova@127.0.0.1  unreachable: no
    multiplexer is running" for both key profiles, exit 0; `--json` "error":"no multiplexer is running"; `pgrep` printed
    nothing.
  - macOS: OPEN — maintainer

### 42. `ls --all` and the OpenSSH backend
*Phase 5 Task 27 (fix round 1: jump hosts off Windows).*
- **Preconditions:** OpenSSH profiles: one direct with key authentication, one through a jump host.
- **Actions:** `ntilde mux ls --all`.
- **Expected:** the direct profile is listed, connected in batch mode. On Linux and macOS, the jump-host profile is not
  connected to: `nova@<host>  unreachable: it goes through a jump host, which ls --all does not sign in through`
  (`--json`: `"error":"it goes through a jump host, …"`). On Windows the jump-host profile is connected to like any
  other.
- **Result / log:**
  - Windows: OPEN — maintainer. Not run by the implementer: on Windows' OpenSSH 9.5 an automatic attempt asks the
    Credential Manager whether a password is saved for the profile, by id and then by name-based keys, and the vault is
    shared with the real install. Use a throwaway Windows account, or accept that read.
  - Linux: PASS (run by implementer, 2026-10-09, `<logs>\50-linux-ls-all-agent-cells.log`). "nova@t30-ssh  unreachable:
    it goes through a jump host, which ls --all does not sign in through", and the log line "not connected to: OpenSSH
    through a jump host or proxy could prompt on this terminal".
    Scripted: `scripts/mux-smoke/linux-ls-all-cells.sh` (CI: `mux_smoke_linux`). PASS (scripted run, 2026-10-13,
    linux-arm64 in Docker, dev-mux at `b68fdf3`): the jump-host line, and the log's "(batch mode: ssh will not prompt)".
  - macOS: OPEN — maintainer

### 43. `ls --all` and a host key it cannot trust (scripted)
*Phase 5 Task 27; Phase 4 after-merge host-key rule.*
- **Preconditions:** step 41's native profile, with the host's old key trusted.
- **Actions:** rotate the host's keys with step 29's command (`ntilde-mux` keeps running). Run `ntilde mux ls --all`.
- **Expected:** the host's line reads `unreachable: signing in needs an answer, which ls --all never asks for`. Nothing
  prompts, no new key is trusted, and no password is offered.
- **Automated:**
  `RemoteMuxListingDockerE2eTests.Ls_all_starts_no_multiplexer_lists_a_running_one_and_never_trusts_a_key_or_starts_an_older_one`
  (the native SSH Docker E2E job, ubuntu-latest): the OpenSSH variant (strict checking, empty known-hosts file):
  `signing in needs an answer`, nothing trusted. The native backend stays manual: its automatic attempts read a
  known-hosts store bound once per process.
- **Result / log:**
  - Windows: PASS (run by implementer, 2026-10-09, `<logs>\41-win-ls-all-changed-hostkey.log`; the rotation itself:
    `<logs>\54-hostkey-rotation-check.log`, "ntilde-mux serve pid before: 375 ... after: 375"). "nova@127.0.0.1
    unreachable: signing in needs an answer, which ls --all never asks for (exit 1, 0.5 s)". Exit 1 because no local
    multiplexer was running. The log reads "NeedsUser ...: the host key of nova@127.0.0.1 is not one the user trusts
    (unknown, or changed), and an automatic reconnect accepts no new key".
    Scripted: `scripts/mux-smoke/win-ls-all-cells.ps1` J6 (manual). Not run since the scripts were committed.
  - Linux: PASS (scripted run, 2026-10-13, `scripts/mux-smoke/linux-ls-all-cells.sh` L6, linux-arm64 in Docker,
    dev-mux at `b68fdf3`; CI: `mux_smoke_linux`). The native profile and the OpenSSH one (batch mode: "Host key
    verification failed.") both read "unreachable: signing in needs an answer, which ls --all never asks for"; the
    sandbox's native and OpenSSH known-hosts files were unchanged, and ntilde-mux kept its pid.
  - macOS: OPEN — maintainer

## K. Agents

### 44. `ntilde.list_sessions` sees a windowless session
*Phase 5 Tasks 12-14.*
- **Preconditions:** *Agent access (observe)* on (Windows: quit your real Ntilde first; see the setup's warning). A
  window with one pane. A second shell in the same multiplexer that no pane shows: detach one with **Pane: Detach**, or
  start one with `ntilde mux spawn-for-test` (setup).
- **Actions:** from an MCP client on `Ntilde.McpServer`: `ntilde.list_sessions`, then `ntilde.read_screen` with the
  windowless id.
- **Expected:** the pane is listed as usual. The other shell is listed with kind `local (windowless)`, its profile
  column `this computer`, and its id the one `ntilde mux ls` prints. The footnote reads "windowless: running in the
  multiplexer with no window here. …". `read_screen` returns its screen.
- **Result / log:**
  - Windows: OPEN — maintainer. Not run: the agent pipe `ntilde-agent-<user>` is not keyed by `NTILDE_APPDATA_ROOT`,
    so a sandbox GUI with agent access would share it with the maintainer's running Ntilde.
  - Linux: PASS (run by implementer, 2026-10-09, `<logs>\50-linux-ls-all-agent-cells.log`). The GUI ran under Xvfb in
    Docker with agent access observe on; the session was started with `spawn-for-test`; the MCP server was driven over
    stdio:

    ```
    | 57f85db1-dd1f-4daf-bb64-31107b167c1b | Default Shell | Default Shell | local | 149x40 | yes | awaitingInput (heuristic) | c173f202-… |
    | eb783ce8-df2a-49e1-9b56-3ece1066efd6 | spawn-for-test | this computer | local (windowless) | 80x24 | no | - | - |
    ```

    `read_screen` on `eb783ce8-…`: "Screen 80x24, …  0| t30-session-W".
    Scripted: `scripts/mux-smoke/linux-agent-cells.sh` (manual: it starts the GUI). Scripted run (2026-10-13,
    linux-arm64 in Docker, dev-mux at `b68fdf3`): 6 of 6.
  - macOS: OPEN — maintainer

### 45. Status, capture, and the act tools on a windowless session
*Phase 5 Task 13.*
- **Preconditions:** step 44. A remote persistent tab's host connected, with a detached remote shell.
- **Actions:** `ntilde.get_session_status`, `ntilde.capture_screen` with `mode=render` and with `mode=live`. Turn on
  *Agent access (act)*: `ntilde.send_input`, then `ntilde.close_session` on the windowless id. Repeat on the remote
  windowless shell, with and without the profile's **Allow AI agent access to this connection**.
- **Expected:** the status is always the heuristic tier. `render` gives a picture; `live` is refused. `send_input`
  types into the shell; `close_session` ends it (`ntilde mux ls`). On the remote shell both are refused until the
  profile allows agent access. The **Agent Activity** journal records each read as `windowless · <host> · session
  <id>`, folding repeats into one line with `×N`, and records every act.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 46. Asking about windowless sessions never connects anywhere
*Phase 5 Task 12 (R4).*
- **Preconditions:** a persistent profile whose host the window is not connected to, with shells on that host.
- **Actions:** `ntilde.list_sessions`.
- **Expected:** that host's shells are not listed, and nothing connects or prompts. Only multiplexers the window is
  already connected to are asked, and one that does not answer within 7 seconds counts as not found.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

## L. `ntilde.com`

### 47. `ntilde mux attach` straight from PowerShell, with Ctrl+C and the detach chord
*Phase 4 plan Task 22 step 7.*
- **Preconditions:** a build with `ntilde.com` beside `Ntilde.exe` (an install, or the release zip), and a sandbox
  session running `ping -t localhost`.
- **Actions:** from PowerShell (no `cmd /c`): `ntilde mux attach <id>`. Press Ctrl+C, then **Ctrl+\ then d**. Read
  `$LASTEXITCODE`.
- **Expected:** the screen is painted. Ctrl+C reaches the shell (the ping stops), and the launcher keeps waiting. The
  chord prints `[detached from <id>]`, the prompt returns, and `$LASTEXITCODE` is 0.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: N/A (`ntilde.com` is Windows only)
  - macOS: N/A (the same)

### 48. `ntilde.com` forwards exit codes and passes arguments through (scripted)
*Phase 4 §11.1.*
- **Preconditions:** `ntilde.com` beside `Ntilde.exe` (here, the launcher published from `src/Ntilde.Launcher`, beside
  the dev build).
- **Actions:** `ntilde mux attach <an id that does not exist>` (with and without a multiplexer), `ntilde mux ls` with
  none running, `ntilde mux ls --bogus`, `ntilde mux`, and `ntilde mux spawn-for-test <exe> "/q /k echo t30-session-B"`
  (setup).
- **Expected:** `$LASTEXITCODE` holds the CLI mode's exit code: 2 for `attach nonexistent` and for usage errors, 1 for
  `ls` with no multiplexer. A quoted argument with spaces arrives as one argument.
- **Result / log:**
  - Windows: PASS (run by implementer, 2026-10-09, `<logs>\10-win-cli-cells.log`). Every CLI cell there ran through
    `ntilde.com`: "attach with no daemon: exit 2"; "attach to a prefix nothing matches: exit 2"; "ntilde.com forwards exit
    2 for a usage error". `ls --json` shows the spawned session's `"arguments":"/q /k echo t30-session-B"` intact.
    Scripted: `scripts/mux-smoke/win-cli-cells.ps1` (CI: `mux_smoke_windows`), which now reads the session's arguments
    back from `ls --json` as one check.
  - Linux: N/A (Windows only)
  - macOS: N/A (Windows only)

### 49. A GUI launch through `ntilde.com` returns at once
*Phase 4 §11.3; manual 12.10.*
- **Preconditions:** as step 47.
- **Actions:** in PowerShell, `ntilde`; then `ntilde | Out-Null`.
- **Expected:** a plain `ntilde` opens the window and returns at once with exit code 0. With its output captured, the
  caller waits until the window closes; this is documented, and not a regression.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: N/A (Windows only)
  - macOS: N/A (Windows only)

### 50. The installer puts `ntilde` on PATH
*Phase 4 §11.4.*
- **Preconditions:** a Velopack install. Use the survival harness's `NtildeSurvival` install (step 36 with
  `-LeaveRunning`), not your real one. Until its `-CleanupOnly`, your real user `PATH` carries that install's
  `current` folder: this step checks exactly that.
- **Actions:** open a new terminal and run `ntilde mux ls`. Uninstall (the harness's `-CleanupOnly`), then open another
  terminal.
- **Expected:** the install folder's `current` is on the user PATH, so `ntilde` runs `ntilde.com` in a new terminal. An
  update keeps the entry, and uninstalling removes it. The release `.zip` contains `ntilde.com` but adds nothing to
  PATH.
- **Result / log:**
  - Windows: OPEN — maintainer (Task 24's harness checks the PATH entry's add, remove and byte-identical restore)
  - Linux: N/A (Windows only)
  - macOS: N/A (Windows only)

## M. Turning it off

### 51. With the setting Off, nothing of the multiplexer is left visible
*Phase 5 global constraint; manual 12.11; PR #512 H1 (the close).*
- **Preconditions:** a sandbox with shells running in the multiplexer.
- **Actions:** Settings → Appearance → *Scrollback* → **Keep shells running when the window closes** → *Off*; save. Open a
  new tab, check the palette, close the window, and relaunch.
- **Expected:**
  - New tabs run normal shells, and no multiplexer is started for them.
  - **Session: Attach to Session…**, **Pane: Detach** and **Session: Quit and Close All Shells** leave the palette.
  - Closing the window asks nothing, and ends the local shells it shows, as **Close them** would (step 54). SSH tabs open
    plain, whatever their profile says.
  - Shells no window here shows (detached ones, another window's) keep running, and `ntilde mux kill-server` ends them.
    The remembered first-close answer is forgotten, so turning the setting back on asks again.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 52. The default, and an explicit Off
*Phase 5 Tasks 15 and 19.*
- **Preconditions:** two sandboxes: one whose `settings.json` has no `SessionPersistence` key, one with
  `"SessionPersistence": "Off"`.
- **Actions:** start Ntilde in each; open a tab; run `ntilde mux ls`.
- **Expected:** the first runs its shell in a multiplexer (*Keep running* is the default for local shells). The second
  starts none and stays *Off*, updates included.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 53. One profile stops keeping its remote shells
*Phase 4 §7.6, manual 12.6.*
- **Preconditions:** a persistent remote tab with a shell.
- **Actions:** untick **Keep remote sessions running (ntilde-mux)** in its profile; open a new tab of it; relaunch;
  close the old tab; tick it again and relaunch.
- **Expected:** new tabs, and saved tabs at the next launch, open plain SSH, while their shells keep running on the host
  (`ntilde mux ls --all` no longer lists the host). Closing such a tab ends its shell when Ntilde can sign in without
  asking (keys, agent). Ticking it again reattaches them at the following launch.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

## N. Release hardening (PR #512)

Steps for the rulings H1, H3, H5 and H7 of [spec §H](../specs/2026-10-08-ntilde-mux-phase5.md), and the install-folder
notice. Steps 14, 23, 41 and 51 above carry H2's and H7's other changes. Three rulings are covered by automated tests
only, since a manual run needs a build or a daemon nobody has:

- **H4** (a release build refuses a RID it has no pin for):
  `MuxAssetSourceTests.A_release_build_without_a_pin_for_the_rid_refuses_the_download` and `…_refuses_a_self_consistent_cache_too`. A dev build has no pins; every RID a release ships has one. release.yml
  checks each checksum's format with `scripts/ci/check-mux-sha256.sh`
  (`ReleaseWorkflowTests.Every_app_build_that_embeds_the_ntilde_mux_pins_checks_their_format_first`).
- **H6** (the update's protocol marker): `ReleaseWorkflowTests.Every_vpk_pack_ships_the_multiplexer_protocol_marker`
  and `MuxUpdateCompatibilityTests.A_release_build_warns_when_the_marker_is_missing`.
- **H8** (the kill path needs a matching start time):
  `MuxDaemonStopTests.A_descriptor_without_a_start_time_is_not_terminated`.
  `kill-server --force` reaches it only against a multiplexer of another protocol version, which no build has (step 9).

### 54. Turning persistence Off, then closing the window, ends its shells
*PR #512 H1.*
- **Preconditions:** a sandbox with *Keep running*. This window has three local shells, one of them also open in a
  second window (step 19), and one detached shell (step 20).
- **Actions:** Settings → **Keep shells running when the window closes** → *Off*; save. Close this window (not the
  second one). Run `ntilde mux ls`.
- **Expected:** nothing asks. This window's two unshared shells end, as **Close them** ends them. The shared shell keeps
  running in the second window, and the detached one keeps running: `ls` lists both. Remote persistent tabs' shells keep
  running on their hosts.
- **Automated:**
  `MainWindowRealDaemonTests.Turning_persistence_off_then_closing_ends_the_windows_real_shells_and_no_other` (the PTY
  smoke lane, ubuntu-latest and windows-latest): nothing asks; the window's own shell ends; a shared and a detached
  shell run on.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 55. `ls --all` and an `ntilde-mux` older than the app
*PR #512 H7.*
- **Preconditions:** step 41's profiles. An `ntilde-mux` from before PR #512 on t30-ssh: build it from a worktree of
  `ee75a32` (`git worktree add <dir> ee75a32`, then `scripts/docker-publish-mux-daemon.sh linux-x64 <out-old>` in it)
  and install it as in setup 3. No `ntilde-mux` running on the host.
- **Actions:** `ntilde mux ls --all`; `ntilde mux ls --all --json`; `docker exec t30-ssh pgrep -af 'ntilde-mux serve'`.
  Reinstall the current `ntilde-mux` afterwards.
- **Expected:** the host's line reads `nova@127.0.0.1  unreachable: its ntilde-mux is older than this app; update it to
  list its sessions` (`--json`: the same words in `"error"`). Nothing prompts, and `pgrep` prints nothing.
- **Automated:**
  `RemoteMuxListingDockerE2eTests.Ls_all_starts_no_multiplexer_lists_a_running_one_and_never_trusts_a_key_or_starts_an_older_one`
  (the native SSH Docker E2E job, ubuntu-latest): with the current binary behind a wrapper that renames `--no-spawn`, so
  it refuses it with its own usage and exit 2 (no release ships an older `ntilde-mux`).
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 56. A listing forwards no agent
*PR #512 H3.*
- **Preconditions:** an `ssh-agent` on this computer holding the throwaway key (`ssh-add <sandbox>/sshkey/id_ed25519`). An
  OpenSSH persistent profile on t30-ssh with `-A` in its extra SSH arguments, and a tab of it open: its shells'
  `SSH_AUTH_SOCK` is `ntilde-mux`'s agent link (manual 12.6, *Limitations*).
- **Actions:**
  1. In the tab: `echo $SSH_AUTH_SOCK; ls -l $SSH_AUTH_SOCK; ssh-add -l`.
  2. `ntilde mux ls --all` on this computer.
  3. In the tab again: `ls -l $SSH_AUTH_SOCK; ssh-add -l`.
- **Expected:** `ls --all` lists the host's sessions. The link points at the same socket before and after it, and
  `ssh-add -l` still lists the key. (Before H3, the listing's connection forwarded the agent and repointed the link at
  its own socket, which went away when it ended: `ssh-add -l` then failed to reach the agent.)
- **Automated:** `RemoteMuxListingDockerE2eTests.A_listing_leaves_the_agent_link_a_forwarding_tab_set` (the native SSH
  Docker E2E job, ubuntu-latest): the link's target is unchanged after `ls --all` and is still a socket.
- **Result / log:**
  - Windows: OPEN — maintainer (the Windows OpenSSH agent service)
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 57. A password the server refuses leaves the host's scope
*PR #512 H5. Uses the image's `nova-pass` password: type it, never tick **Remember password**.*
- **Preconditions:** a native persistent profile on t30-ssh with no identity file and **Remember password** unticked. A
  tab of it, signed in by typing `nova-pass`, with *Remote Files* open and listing. No agent on this computer holds a
  key the host accepts (`ssh-add -D` after step 56), so a listing can sign in only with a password.
- **Actions:**
  1. Change the password on the host: `docker exec t30-ssh sh -c 'echo nova:t30-rotated | chpasswd'`. Note
     `docker logs t30-ssh 2>&1 | grep -c 'Failed password for nova'`.
  2. Refresh *Remote Files* (or open a folder in it). Count again.
  3. Refresh it twice more, and try *SFTP: Download…*. Count again.
  4. Restore the password: `docker exec t30-ssh sh -c 'echo nova:nova-pass | chpasswd'`.
- **Expected:**
  1. The tab's shell keeps working: its connection signed in before the change.
  2. The listing fails in the sidebar, and the count goes up by one.
  3. Each fails again, and the count does not go up: the refused password is no longer offered. (Before H5 every
     refresh and transfer offered it again, one `Failed password` line each.)
- **Automated:** `RemoteMuxListingDockerE2eTests.A_listing_refused_at_sign_in_offers_the_scope_password_no_more` (the
  native SSH Docker E2E job, ubuntu-latest): through the sidebar listing service: one more `Failed password` for the
  refused listing, none for the next two, and the retyped password works. Not covered: *SFTP: Download…*.
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: OPEN — maintainer
  - macOS: OPEN — maintainer

### 58. A multiplexer running from the install folder is announced once
*PR #512 optional item.*
- **Preconditions:** the survival harness's `NtildeSurvival` install (step 36 with `-LeaveRunning`), quit, and its
  multiplexer stopped (`ntilde mux kill-server` under its `NTILDE_APPDATA_ROOT`).
- **Actions:** set `NTILDE_APPDATA_ROOT` to a folder inside that install's root (the folder holding `current\` and
  `Update.exe`), start `<root>\current\Ntilde.exe` from that shell, and open a second window. Clean up with
  `-CleanupOnly`.
- **Expected:** a *Keep shells running* notification reads "Shells cannot be kept through updates on this computer: the
  multiplexer runs from Ntilde's install folder because its own copy could not be set up (debug.log says why). Updates
  ask before they close them.", once for the launch: the second window does not show it again. `debug.log` has "[Mux] the
  multiplexer daemon runs from the install folder, and an update will stop it: the app-data root … is inside the install
  root …".
- **Result / log:**
  - Windows: OPEN — maintainer
  - Linux: N/A (only a Windows Velopack install stages the multiplexer's own copy)
  - macOS: N/A (the same)
