# Linux original-pidfd lifecycle foundation

This unwired QA primitive holds stable kernel references for exactly one
source-known App role and its direct private-PTY worker. It never launches the
terminal App, enumerates processes, signals numeric target PIDs, or qualifies a
Linux desktop/collection. The controller is not the worker's parent: its
`exitObserved` is deliberately **not a reap acknowledgment**.

`acquireLinuxLifecycle(expected, pythonPin, { deadlineNs, lifetimeMs })` uses the
same strict expectation as the owned-output foundation: current UID, original
PID/start ticks/executable/file/argv, exact run markers, direct ancestry and
private fd0/fd1 PTY identity. `pythonPin` selects only a pinned system Python
under `/usr/bin`. Public fixed-proc facts are checked between observations that
both newly held pidfds remain alive before admission. No alternate proc root or
injected public backend exists. The caller must already own the launch; these
comparisons do not attest an arbitrary process or solve atomic App acquisition.

The opaque lease offers `observe`, `terminateWorker`, `terminateApp`,
`abortCleanup`, `close`, and a detached `observation`. Operation options contain
one absolute `deadlineNs`, created within the caller's remaining budget using
`createLinuxLifecycleDeadline(1..4000)`. Helper lifetime is 10–300 seconds,
including an original four-second abort/close reserve. Ordinary work cannot
consume that reserve. Commands, frames, retained output and descriptor/stdio
closure are bounded. Synchronous system calls are checked before/after; this is
not a hard realtime guarantee against kernel stalls or scheduler starvation.

TERM and, if needed, KILL use only the original role's pidfd, sharing the same
phase budget. App termination requires worker exit observation; a future real
collector must additionally obtain the genuine App-owned worker-wait fact.
Public provider launch facts are rechecked while both roles live. After worker
exit, the original surviving App is checked without demanding proc executable
facts from a zombie or reopening the worker's numeric PID. These are trusted-host,
point-in-time comparisons; mutable argv is not executable provenance.

Identity drift, refusal or overlap is sticky. An explicit abort may best-effort
terminate only the original held targets, retaining the original failure and
all signal/close errors. It cannot acquire replacements or become a completed
run. Normal close disarms automatic EOF/expiry cleanup before the transport is
closed and leaves live targets unsignalled. If normal settlement cannot be sent,
Node terminates only its exact helper instead of manufacturing an admitted EOF.
Unexpected admitted EOF or lifetime expiry may use the originally reserved
abort budget; it cannot extend the overall lifetime. Transport failure is never
automatically retried. Idempotent settlement repeats the original outcome.
Normal close must be requested before the lifetime's reserved cleanup boundary;
expiry has already authorized the bounded aborted cleanup, not a normal close.

Python runs isolated (`-I -S -B`) from a held root-owned SHA-pinned ELF, with the
checked-in SHA-pinned helper executed from another inherited FD. Files cannot be
group/world writable, and execution does not reopen checked paths. Parent-side
file FDs and helper pidfds close once; uncertain close is retained as uncertain.
Only the helper ChildProcess itself may be terminated by Node. The helper uses
`pidfd_open`, `pidfd_send_signal` and poll; it never waits/reaps targets or uses
subreaper behavior, process groups, arbitrary signals, ctypes or syscall numbers.

Deadline transfer does not compare Node and Python clock epochs. For every
phase, the helper samples CLOCK_MONOTONIC, then the parent adds only the remaining
duration measured after receipt, minus a close reserve. Transport/provider time
reduces the available budget. The selected Linux clocks share the same underlying
monotonic duration source: Node24's [hrtime binding](https://github.com/nodejs/node/blob/v24.22.2/src/node_process_methods.cc)
uses [uv_hrtime/UV_CLOCK_PRECISE](https://github.com/nodejs/node/blob/v24.22.2/deps/uv/src/unix/core.c),
which selects [CLOCK_MONOTONIC on Linux](https://github.com/nodejs/node/blob/v24.22.2/deps/uv/src/unix/linux.c);
the helper calls [clock_gettime_ns(CLOCK_MONOTONIC)](https://docs.python.org/3/library/time.html#time.clock_gettime_ns)
explicitly. The overall lifetime is conservatively transferred during admission.

Run deterministic regressions on any supported Node host:

```sh
node --test scripts/terminfo/linux-owned-lifecycle.test.mjs scripts/terminfo/linux-owned-lifecycle-platform.test.mjs
```

Run the bounded actual Linux own-child controls separately:

```sh
node scripts/terminfo/linux-owned-lifecycle-selftest.mjs /explicit/private/result.json
python3 -I -S -B scripts/terminfo/linux-lifecycle/helper_tests.py
```

These controls cover normal close with zero target signals, TERM resistance and
KILL, exact launch refusal, sticky argv drift, admitted EOF, and an independent
sentinel surviving every target action. A separately controlled App-role exit
must remain unexpected rather than becoming an unsent requested termination.
A private fixture parent deliberately
retains its worker zombie with WNOWAIT; the pidfd reports exit while a nonparent
P_PIDFD wait gets ECHILD. Only the fixture parent subsequently reaps that worker.
The Node harness verifies its own fixture/helper closure. The result contains no
primitive reap/completion/target-exit-status claim. Linux's
[pidfd_open](https://man7.org/linux/man-pages/man2/pidfd_open.2.html),
[pidfd_send_signal](https://man7.org/linux/man-pages/man2/pidfd_send_signal.2.html)
and [waitid](https://man7.org/linux/man-pages/man2/waitid.2.html) contracts explain
the distinction. No modern HUP behavior is assumed as portable reap evidence.

Genuine App wait-latch observation, atomic owned launch, positive parent reaping,
same-connection peer credentials, endpoint cleanup, receipt/source inventory and
full desktop collection are separate future integration stages. Linux and
Windows desktop qualification remain pending.
