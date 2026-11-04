# Linux process/PTY provider foundation (#775)

This separate provider reads one explicit PID, or an explicitly spawned parent and
child pair. It is not wired into the owned collector. It does not enumerate
processes, launch Ntilde, access a GUI/clipboard, inject input, or qualify a desktop,
App, terminal, native interchange or full owned collection. Linux and Windows
remain unqualified until a separate real owned collection satisfies every gate.

## API and limits

`linux-process-provider.mjs` is the production entry point:

- `readLinuxProcessFacts(pid, { timeoutMs = 2000 })` reads identity, credentials,
  executable object/path and exact current argv.
- `readLinuxPtyFacts(pid, { timeoutMs = 2000 })` also requires stdin/stdout to
  identify the same private `/dev/pts/N` character device and controlling terminal.
- `readLinuxOwnedChildFacts(expected, { timeoutMs = 2000 })` holds both process
  directories while reading/rechecking them, then compares the direct parent,
  caller-supplied UID, exact executable, full argv and unique run marker. Expected
  values must come from the caller's own spawn. The returned label is explicitly
  `provider-foundation-only`.

All APIs require actual Linux. The proc root is always `/proc`, checked for the
procfs filesystem type. There is no environment variable, CLI option, alternate
root or public test-reader option; unknown options are rejected. The separate
`linux-process-core.mjs` reader seam is internal synthetic-test plumbing and must
never be supplied by collection configuration. No central worker/runtime/source
inventory/receipt/policy or existing CI file is modified by this foundation.

A query has one 1–4000 ms monotonic budget, at most 160 counted operations, bounded
stat/status/cmdline sizes and at most two explicit processes. Every read/open is
checked before and after, including bounded read loops. All opened proc/file
handles close in `finally`, even after deadline, reuse or later-open failure.
Synchronous syscalls cannot be interrupted by this deadline: the provider detects
an overrun after the kernel returns. This is bounded query accounting, not a hard
preemptive timeout or protection against a malicious kernel/filesystem.

## Identity observations

The provider opens `/proc/<PID>` as a no-follow directory, keeps that descriptor
open, and reads only fixed entries through `/proc/self/fd/<held-fd>/...`. Holding
a proc descriptor prevents it from silently acting on a newly reused PID;
operations against an exited process can fail instead. This is documented by the
[kernel procfs guide](https://docs.kernel.org/filesystems/proc.html).

`stat` parsing uses the final parenthesis delimiter around `comm`, which can
contain spaces, parentheses and newlines. It rejects dead/zombie states and
missing/malformed fields. PID, parent, group/session and controlling terminal are
checked. Start time stays a decimal string parsed with BigInt, preserving values
above Number's 53-bit precision. Its clock-tick meaning is defined in
[proc_pid_stat(5)](https://man7.org/linux/man-pages/man5/proc_pid_stat.5.html).
The PID/start tuple is scoped to this host boot and PID namespace, not a durable
cross-boot receipt or a pidfd-based atomic snapshot.

The real/effective/saved/filesystem UIDs must agree and match the proc-directory
owner. PID/TGID/PPID status lines must each be unique and agree with stat. Kernel
status field meanings are described in the
[procfs status table](https://docs.kernel.org/filesystems/proc.html).

The executable link must name an absolute canonical non-deleted path; its mapped
file's lossless device/inode/mode/size/timestamps must match the named regular
file. Deleted executable suffixes and dereference restrictions are documented in
[proc_pid_exe(5)](https://man7.org/linux/man-pages/man5/proc_pid_exe.5.html).
This compares object/path facts; it does not hash the executable or establish
source/build provenance. A future collector must separately pin that identity.

Cmdline is bounded, valid UTF-8 and exactly NUL-separated/terminated; empty
arguments and embedded spaces are preserved. The child comparison never shell
splits a command string. A process can rewrite its command line, so matching argv
is a comparison fact, not executable provenance or an unforgeable ownership
marker. See [proc_pid_cmdline(5)](https://man7.org/linux/man-pages/man5/proc_pid_cmdline.5.html).

## PTY observations and consistency

Only descriptor 0 and 1 facts are inspected. Each must resolve directly to a
bounded `/dev/pts/N` path, be a character device owned by the expected process UID,
and match the non-symlink named device's lossless device/inode/rdev/mode facts.
Both descriptors must match each other and the process's controlling-terminal
number. PTY identity contains only device/inode/rdev/UID/mode, excluding mutable
size and timestamps. Executable identity keeps its size and timestamps. Pipes,
ordinary files, alias paths, mismatched descriptors and missing controlling
terminals refuse qualification.

Linux stat's signed `tty_nr` representation and userspace `dev_t` packing are not
compared as raw integers. The conversion preserves signed 32-bit stat bits and
uses the kernel's `new_encode_dev` layout, including high minor bits. Out-of-range
values are rejected. The authoritative conversion is in
[Linux kdev_t.h](https://github.com/torvalds/linux/blob/master/include/linux/kdev_t.h).

Each record is read twice; a pair is rechecked in reverse order with both process
directories still held. Start/PID/parent, credentials, exact argv/executable and
requested PTY facts must remain equal. CPU state transitions are allowed. This
catches observed reuse, exec, credential or descriptor changes, but is not an
atomic kernel transaction and cannot detect every transient ABA change between
syscalls. It relies on a trusted host/launcher and does not freeze the child.
The provider supplies no permission grant, lifecycle cleanup or child-byte receipt.

## Tests and hosted check

Run the deterministic suite with:

```sh
rtk proxy node --test scripts/terminfo/linux-process-provider.test.mjs scripts/terminfo/linux-process-selftest-output.test.mjs
```

It covers adversarial comm syntax, start-time precision, signed devices, malformed
UID/argv/paths, vanished/reused processes, executable/FD changes, parent changes,
shared deadline exhaustion and handle cleanup. It never reads personal processes.

The new `terminfo-linux-provider.yml` workflow has read-only contents permission,
a five-minute job ceiling, no package install or arbitrary download execution,
and runs only checked-in code on hosted Ubuntu. Its self-test reads itself and
its own tagged Node child, launched by a checked-in Python private-PTY helper.
It checks caller-recorded spawn values, direct parent, exact argv, UID, executable,
start identity and stdin/stdout/controlling-device equality. It sends only the
fixture's shutdown command and signals only its own spawned children on failure.
It retains a provider-only report after verified child exit. Stdout and stderr
are each capped before append at 4096 bytes; overflow latches failure and later
chunks are discarded without retention. The private child performs one bounded
activity exchange before shutdown. The control crosses the kernel's actual
eight-second tty timestamp bucket (at most 8.1 seconds idle within existing
fixture ceilings), proving that ordinary traffic changes mtime while stable
device identity and executable identity stay equal. The timestamp update policy
is defined in [Linux tty_io.c](https://github.com/torvalds/linux/blob/master/drivers/tty/tty_io.c).

On 2026-11-04 this control ran independently on the explicitly authorized Linux
host in an isolated worktree: Ubuntu 26.04.1/kernel 7.0.0-31, Node 24.22.2 and
Python 3.14.4. The original core/provider SHA-256 values matched the reported
snapshot and passed 36 tests plus the idle self-test. The old timestamp-inclusive
PTY projection failed the real activity comparison with an unchanged device and
changed mtime. The corrected projection passed the same real activity control and
40 deterministic tests with zero skips. This is provider evidence only; no App,
desktop/GUI collection, clipboard or personal process enumeration was performed.

The report explicitly marks App, desktop and Linux full owned collection as
unqualified. This check is not an App/terminal smoke test, a collector receipt,
a source-inventory upgrade or a substitute for independently owned process/PTY
proof throughout an actual collection. Actual App transport, Linux termios
output restoration, launch/private-root proof, source/binary pins, disposal,
Windows support and fresh measured collector artifacts remain follow-up work.
