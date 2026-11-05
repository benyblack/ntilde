# Linux source-owned launch foundation

This unwired Linux x64/arm64 module creates one source-selected App-role process
and retains its original kernel reference. Helper and App have independent
sessions/process groups and no controlling terminal, verified through the public
provider. It is a tooling foundation, not a
Linux desktop or complete owned-collection qualification. Its real-host tests
execute source-built private ELF fixtures, never Ntilde or the desktop.

`launchLinuxOwnedApp(artifact, fixture, pythonPin, { deadlineNs, lifetimeMs })`
returns an opaque `observe`, `stop`, `abortCleanup`, `snapshot` lease. Each phase
supplies one absolute deadline of at most four seconds. Lifetime is at most
300 seconds, including a preauthorized final four-second abort reserve.
Settlement preserves the original outcome and cannot turn an abort into success.
An indeterminate launch or transport result must never be automatically retried.

The public facade fixes Linux, the real `/proc` provider, interpreter/helper
resources, descriptor layout and spawn implementation. Tests have explicit
internal filesystem/transport seams. There is no caller-controlled proc root,
helper source, signal, arbitrary argv/environment, shell or post-launch PID.

The caller supplies expected SHA256 values for fixed `Ntilde`, `Ntilde.dll` and
`librusty_pty.so` files inside a canonical current-UID private directory. These
are source-artifact comparisons, not self-authenticating proof that caller-chosen
bytes are Ntilde. Full managed/runtime closure remains a later integration gate.
Executable/companion caps are 32/64/16 MiB; the pinned root-owned system Python
cap is 16 MiB and the exact retained helper cap is 64 KiB. Files reject symlinks,
write permission for group/others, setuid/setgid bits, unexpected owners and extra hard links.
Private directory identities are retained through open descriptors.

Initial source-known settings bytes are pinned before fork. App-owned settings
saves after startup are not misrepresented as unchanged initial bytes. Appdata
and home paths are fixed children of the independently created private fixture.
The only App argv entries are the canonical executable and matching owner/private
clipboard QA markers. The environment contains only private roots, those markers,
fixed PATH, declared version and selected UTF-8 locale. It inherits no loader,
Python, AppImage, mux, SSH, capture/receipt or desktop variables. It adds no
clipboard fixture operation, product setting, native ABI or MCP tool. GUI
variables and DOTNET_ROOT are deliberately absent from this first slice.

The source-reviewed, single-threaded CPython helper resets SIGCHLD to its default
without SA_NOCLDWAIT and has no competing waiter. It forks one child that remains
behind a finite pipe gate. A bounded child-ready byte proves setup reached the closed exec gate. The
original pidfd is acquired before release; public
provider facts then bind the original pre-exec child, helper parent, UID quartet,
executable/argv and lossless birth identity. The held App descriptor is executed
with fd-exec. Post-exec provider facts must match the same original birth and
parent plus the exact pinned App executable/argv before admission. CLOEXEC error
pipe EOF is retained as a pipe fact; it cannot replace that process evidence.

On first pidfd acquisition failure, the gate remains closed and only the original
unexecuted direct child may be positively waitpid-reaped. That yields
`preExecChildReaped`, never `appReaped`. After admission, `appReaped` can become
true only after the helper's consuming `waitid(P_PIDFD)` for its original own
child. Raw CLD code/status remain separate. Readiness, generic EOF/zero, ECHILD,
helper exit or `/proc` disappearance cannot manufacture a consuming wait fact.
There is no worker-reap or whole-run completion/receipt field.

TERM then optional KILL uses only the original pidfd, at most once per signal.
The source-owned child gives cleanup authority from fork, even if external
admission fails. Identity drift, malformed/overlapping protocol, EOF, output
bounds, interruption and expiry remain sticky aborts. Normal stop requires the
original live App identity and an actual successful original-pidfd signal attempt;
independent exit observed at dispatch remains aborted even when genuinely reaped.
Best-effort abort cleanup may target only the held
original. No PID/group signaling, retargeting, reacquisition or release-live-App
close exists. Repeated settlement returns its original result/error.
Persistent log or pipe observation failures remain abort facts without preventing
the original-reference signal/wait path. Every discarded pipe records any
unconfirmed close. Invalid settlement deadlines still close coordinator input
and resources under only the remaining preauthorized cleanup allowance.

The helper watches coordinator EOF and lifetime continuously. Before exec,
closing every duplicate gate writer prevents App launch. After admission, EOF
starts only the already-authorized bounded original-child cleanup. All waits,
signals and pipe/fd closure share the remaining phase/overall budget. The parent
may eventually terminate only its exact spawned helper if close is unconfirmed.
A hard helper death after exec can leave App cleanup unknown; it never becomes
successful evidence. Synchronous syscalls are checked around execution, not
claimed to be preemptible.

Helper command frames are capped at 4096 bytes, 64 commands, retained stdout
256 KiB and stderr 4096 bytes. The App has `/dev/null` stdin and its own bounded
1 MiB log pipe, never a personal terminal. Overflow is checked before retention.
Primary failures, actual release/signal/wait facts and cleanup/close failures are
retained together. The Node/Python clock handoff is conservative and never assumes
equal monotonic epochs. Initial hash and provider work consumes the same budget.

The dedicated Linux workflow runs deterministic resource/transport/identity
controls, actual private fork/pidfd/ECHILD/zombie tests, and source-built ELF
scenarios: normal termination, resistant KILL, fixture-owned private PTY,
early exits, output overflow, and coordinator EOF before/after exec. A distinct
held sentinel must survive the other scenarios. Tool/source/binary hashes and
private-fixture observations are retained. These tests do not attest a real App
worker or replace the output, lifecycle, endpoint or passive-native-wait gates.

Actual App launch still needs a fresh announced desktop/environment decision,
full artifact/runtime closure, the worker's independently admitted PTY identity,
genuine App-owned worker wait observed before normal App stop, credential checks
on the actual RPC connection, and central source/receipt integration. The earlier
endpoint probe authenticates only its own probe socket. Linux full collection
and Windows desktop qualification remain pending.

The kernel acquisition preconditions are documented in
[pidfd_open(2)](https://man7.org/linux/man-pages/man2/pidfd_open.2.html).
[Python's fd-exec and pidfd APIs](https://docs.python.org/3/library/os.html) and
[pipe EOF semantics](https://man7.org/linux/man-pages/man7/pipe.7.html) define the
other boundaries. The reviewed CPython
[3.12.11](https://github.com/python/cpython/blob/v3.12.11/Python/pylifecycle.c) and
[3.13.7](https://github.com/python/cpython/blob/v3.13.7/Python/pylifecycle.c) and
[3.14.4](https://github.com/python/cpython/blob/v3.14.4/Python/pylifecycle.c)
`PyOS_setsig` implementations install a fresh sigaction. The pinned
[Velopack 1.2.0 Linux locator](https://github.com/velopack/velopack/blob/f2edcbcafb81da5b3c884aaea330e225ad91d8b6/src/lib-csharp/Locators/LinuxVelopackLocator.cs)
requires an AppImage environment/path to recognize that installed restart path;
this foundation provides neither.
