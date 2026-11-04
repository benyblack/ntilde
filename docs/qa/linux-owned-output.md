# Linux held-PTY output foundation

`scripts/terminfo/linux-owned-output.mjs` is an unwired preparation/restoration
adapter. Its evidence concerns an explicitly identified private PTY. It does not
launch Ntilde, admit Linux collectors, attest desktop ownership, or qualify a
Linux terminal/App collection. The existing Darwin path and retained reports
are unchanged. Process termination/reaping and App-owned resize remain separate
caller responsibilities.

The public entry points are `createLinuxOutputDeadline(timeoutMs)` and
`prepareLinuxOwnedOutput(expected, tool, { deadlineNs })`. The latter returns a
lease with `observation()`, `observe({ deadlineNs })`, and `restore({ deadlineNs })`.
Each explicit phase must receive a future absolute monotonic deadline inside
the caller's remaining run/cleanup budget, at most 4,000 ms away. All provider,
file and utility operations consume that phase's remaining budget. There is no
per-command timeout reset. Synchronous kernel calls cannot be forcibly
preempted; checks before and after them detect an exceeded budget.

`expected` carries a run marker, current UID, exact App-role and worker PID,
lossless start ticks, executable path/file identity and argv, the App-role's
parent PID, and the proved `/dev/pts/N` path/device identity. The adapter uses the
public fixed-`/proc` provider and compares both complete launch identities,
direct ancestry, and worker fd0/fd1 against the controlling PTY before and after
each ordinary utility operation. The argv comparison is a mutable observation,
not proof of executable provenance. These are point-in-time facts on a trusted
host, boot and PID namespace; they do not form a sandbox or a persistent lease
against a hostile same-UID process. No target PID is signalled or enumerated.

`tool` is exactly `{ path, sha256 }`, allowing only `/usr/bin/stty` or
`/bin/stty`, or Ubuntu's separately installed `/usr/bin/gnustty`. The canonical root-owned executable must have no group/world-write
bits, match the explicit SHA-256, and be a regular ELF of at most 4 MiB. Full
file identity, including timestamps, is checked while held. The adapter opens
only the already-proved PTY beneath the fixed root-owned `devpts` directory,
with `O_NOCTTY`, `O_NOFOLLOW` and `O_NONBLOCK`. Its stable character-device
identity excludes mutable TTY timestamps; ordinary owned mode `0620` is valid,
but world-write is refused. The device and utility descriptors remain held
until settlement. There is no configurable proc root, shell, command, TTY or
utility argv surface.

GNU stty executes through inherited `/proc/self/fd/4` and receives the held PTY
as fd3, addressed by `--file=/proc/self/fd/3`. This avoids reopening a reusable
`/dev/pts/N` path. Only fixed version, `-g`, `size`, `-a`, `-opost`, and the
validated original saved-mode argument are permitted. The utility gets minimal
locale/PATH values and no caller environment. Its dynamic loader, libraries and
kernel remain trusted. [GNU's saved-mode format excludes window size](https://www.gnu.org/software/coreutils/manual/html_node/stty-invocation.html),
so size is separately read and compared; this adapter never resizes the PTY.

Preparation captures the original saved mode and dimensions, applies only
`-opost`, then verifies disabled translation and unchanged size. Observation
does not change modes. Restoration applies only the exact original token and
independently verifies the saved mode and original dimensions. An ownership
failure is sticky: cleanup may restore only the still-identical original held
PTY using the still-identical held tool, without reopening or reacquiring a
target. It returns an aborted failure even if that best effort restores modes.
If App-owned resizing changed size, its owner must restore it first; this
adapter refuses a successful completion while dimensions differ.

Utility stdout and stderr each have a strict 4,096-byte pre-append cap, with
latched overflow and bounded retained diagnostics. A phase permits at most 20
utility invocations. A 250 ms reserve is kept for process and stdio closure.
Timeout or overflow can signal only the exact utility `ChildProcess` created
by this adapter. [Node's `close` event includes stdio closure](https://nodejs.org/docs/latest-v24.x/api/child_process.html),
so exit or a successful signal call alone is insufficient. Unconfirmed close
is fatal, prohibits later utility commands, and remains unknown after a late
event. The result never promises that an unconfirmed process cannot mutate
later. Original descriptors are closed once, without retrying an ambiguous
numeric fd. Primary, restoration and close errors survive together with known
resource identities. Idempotent settlement returns the original outcome;
aborted/failed state cannot become success.

Run the deterministic tests and actual Linux-only fixture with:

```sh
node --test scripts/terminfo/linux-owned-output-*.test.mjs
node scripts/terminfo/linux-owned-output-selftest.mjs /tmp/linux-owned-output-foundation.json
```

The fixture spawns only a tagged Python parent and its tagged Node child on a
new private PTY. Independent `tcgetattr`/`TIOCGWINSZ` snapshots and source-known
LF/CRLF bytes verify only OPOST changed and original mode/size were restored.
Wrong birth and utility hash are refused before mutation. A deliberately
changed worker argv forces sticky abort with original-held-device restoration.
Success is written only after both owned fixture children exited/reaped and
their descriptors closed; no personal terminal or GUI is opened. Hosted Ubuntu
runs the same foundation controls in a separate read-only-permission workflow.
