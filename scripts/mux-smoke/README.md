# Multiplexer checklist cells

Scripts for the scripted steps of the Phase 5 manual checklist
([`docs/superpowers/plans/2026-10-08-ntilde-mux-phase5-manual-checklist.md`](../../docs/superpowers/plans/2026-10-08-ntilde-mux-phase5-manual-checklist.md)).
Each step's **Expected** lines are the checks. One script per group of cells, in the OS's own shell language.

| Script | Checklist steps | Needs | CI |
|---|---|---|---|
| `linux-cli-cells.sh <app> <sandbox> [ntilde-mux]` | 9, 10 | Linux | `mux_smoke_linux` |
| `linux-ls-all-cells.sh <app> <ntilde-mux> <sandbox>` | 41, 42, 27, 43 | Linux, docker, ssh | `mux_smoke_linux` |
| `linux-gui-kill-cells.sh <app> <sandbox>` | 4 | Linux, xvfb-run | manual |
| `linux-agent-cells.sh <app> <mcp-server> <sandbox>` | 44 | Linux, xvfb-run | manual |
| `win-cli-cells.ps1 -App <dir> -Launcher <ntilde.com> -Sandbox <dir>` | 9, 10, 48 | Windows, PowerShell 7.2+ | `mux_smoke_windows` |
| `win-ls-all-cells.ps1 -App <dir> -Launcher <ntilde.com> -Mux <ntilde-mux> -Sandbox <dir>` | 41, 27, 43 | Windows, Docker Desktop (Linux containers) | manual |

`<app>` is the App build folder (the one holding `Ntilde` or `Ntilde.exe`), or the executable itself. The sandbox is a
folder of the script's own: each script writes only its own subfolder of it (`cli`, `ls-all`, `gui-kill`, `agent`).

Every script:

- sets `NTILDE_APPDATA_ROOT` to the sandbox for everything it starts, and never touches another Ntilde's processes;
- prints `PASS`/`FAIL` lines and ends with `N of M checks passed.`, as the checklist's logs do, and exits 1 when any
  check fails or a setup step does;
- cleans up after itself: `kill-server` under the sandbox root, then whatever of the sandbox is left; the Docker scripts
  remove the `t30-ssh` container and the `t30-net` network they made, and refuse to start when either exists already.

Sessions are started with the hidden `ntilde mux spawn-for-test` (setup only), as the checklist says for every step
that uses it.

## The credential store

`NTILDE_APPDATA_ROOT` does not move the system credential store (the checklist's setup, item 1). No script reads it:

- every SSH profile signs in with a throwaway key made for the run, and none remembers a password;
- on Linux the scripts point `DBUS_SESSION_BUS_ADDRESS` at a socket that does not exist, so libsecret reaches no
  keyring, and an automatic OpenSSH attempt's vault read fails instead of reaching yours;
- on Windows only the native backend is used: an automatic OpenSSH attempt there asks the Credential Manager for a saved
  password (step 42), and the native backend asks it only at a password prompt, which key authentication never reaches.

## The remote host

The `ls --all` scripts make the checklist's sshd (setup, item 3): `novaterm-native-ssh-e2e:v5` as `t30-ssh` on the
`t30-net` network (10.230.30.0/24), published on `127.0.0.1:2230`. Build the image first:

```sh
docker build -t novaterm-native-ssh-e2e:v5 tests/Ntilde.ExternalSuites/NativeSsh
```

`ntilde-mux` for it is a Linux binary with a glibc floor of 2.34 (the image is Debian 12): CI's `ntilde-mux-linux-x64`
artifact (`gh run download <run id> -R benyblack/ntilde -n ntilde-mux-linux-x64 -D <out>`, then `chmod +x`), or

```sh
scripts/docker-publish-mux-daemon.sh linux-x64 <out>    # <out>/linux-x64/ntilde-mux
```

The scripts start remote sessions with the App's `spawn-for-test` run inside the container as `nova`: the Linux script
copies the .NET runtime it runs on and the App's assemblies in; the Windows one copies the App's assemblies (the same IL
on every OS) and a runtime from `mcr.microsoft.com/dotnet/runtime:10.0`.

## Running the Linux cells from macOS or Windows

In a Linux container with the build, `ssh`, `xvfb-run` and a docker CLI, with the host network (so `127.0.0.1:2230`
is the published port) and the Docker socket:

```sh
docker run --rm --init --network host -v /var/run/docker.sock:/var/run/docker.sock \
  -v <checkout>:/src:ro -v <build>:/build -v <ntilde-mux dir>:/mux:ro <image> \
  bash /src/scripts/mux-smoke/linux-ls-all-cells.sh /build/Ntilde.App /mux/ntilde-mux /tmp/sbx
```

`--init` matters for the GUI cells: in a container whose PID 1 reaps nothing, a stopped daemon stays a zombie and
`kill-server` reports "Multiplexer did not stop within 5 s.".
