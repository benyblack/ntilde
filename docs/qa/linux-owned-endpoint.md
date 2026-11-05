# Linux private endpoint observation

`scripts/terminfo/linux-owned-endpoint.mjs` is an unwired, probe-only foundation.
It opens no desktop or Ntilde instance, sends no application bytes, changes no
settings, and cannot qualify a Linux owned collection. Darwin transport and
retained reports remain unchanged. Stable process termination and genuine
App-owned worker reaping are separate later boundaries.

Call `observeLinuxPrivateAgentEndpoint(expected, { deadlineNs })`, with an
absolute monotonic deadline from `createLinuxEndpointDeadline(timeoutMs)` inside
the caller's remaining budget, at most4,000ms away. `expected` is bounded plain
data copied before effects: current UID and run marker; exact App-role PID,
parent PID, lossless birth ticks, executable/file identity and argv; creation-time
identity and canonical path of an owned0700 `appdata` directory; and
`python: { path: '/usr/bin/python3', sha256 }`.

The observer uses the existing public fixed-`/proc` provider before and after the
probe. It compares exact launch facts and the single source-known owner marker;
it does not enumerate processes or treat mutable argv as executable provenance.
The facts are point-in-time observations in the current trusted host/boot/PID
namespace, not a sandbox or a durable ownership attestation.

Socket and descriptor names are fixed to `agent.sock` and `agent-endpoint.json`,
directly under `appdata`, matching AgentHostProtocol and the current disposable
runner layout. There are no caller filename/profile-subdir selectors. The
pathname must fit the existing conservative103-byte ceiling. Abstract sockets,
symlink parents/entries, changed identities, foreign owners, and public parent
permissions are refused. The descriptor is a bounded same-UID regular file;
0644 remains valid inside the exclusively owned0700 parent. The socket itself
must be0600. Descriptor version/PID/endpoint must match the expected source-known
App and canonical socket path. No observer path deletes or replaces a socket.

The parent holds the original directory with O_DIRECTORY|O_NOFOLLOW. An isolated
stdlib-only Python helper receives that directory as fd3 and connects through
`/proc/self/fd/3/agent.sock`; it checks named entries relative to the held parent.
It reads only the descriptor and kernel credentials. It never calls application
send/receive. [SO_PEERCRED identifies the peer of that connected Unix socket](https://man7.org/linux/man-pages/man7/unix.7.html):
PID and UID must match the admitted App/current UID. GID is retained as an
unsigned fact and may legitimately differ from UID. Named filesystem socket
identity and connected kernel socket identity are distinct inode domains.

The helper is fixed source beside the module, pinned by a checked-in SHA-256 and
its regression test. It must be a regular current-user file with no group/world
write bits, at most64KiB. The system Python canonical path is constrained to
`/usr/bin/python3` or its versioned `/usr/bin/python3.N` target, root-owned,
executable, non-group/world-writable ELF of at most16MiB, matching the explicit
SHA-256. Both files stay held and are rechecked; Python executes via inherited
fd5 and runs the helper via inherited fd4 with `-I -S -B`. This avoids hashing
then reopening a replaceable executable or helper path. No arbitrary interpreter,
script, argv, proc root, loader/Python environment or UI selector is accepted.
Python's system loader/stdlib remain trusted host. A group-writable repository
helper is intentionally refused; future callers must prepare its retained
private source snapshot with safe permissions before observation, without
changing verified bytes. Actual Ubuntu26 controls used Python3.14; hosted Ubuntu
independently exercises its system Python.

Each phase shares one deadline across provider, hashing, file checks, helper,
connect and closure. A bounded remaining-time transfer starts the helper's own
monotonic budget; no equality of Python/Node clock epochs is assumed. Synchronous
kernel calls cannot be forcibly preempted. The parent reserves250ms for helper
close, caps stdout/stderr at4,096bytes each before append, and accepts one complete
UTF-8 JSON frame only after process and stdio close. Timeout/overflow may signal
only the exact helper ChildProcess it spawned. Exit alone, a successful signal
call, or a later close cannot repair an unconfirmed deadline failure. Unknown
close stays fatal; no new probe or target reacquisition is scheduled.

The detached version1 result explicitly says `connectionScope: 'probe-only'`,
`authenticatesSubsequentConnections: false`, and no App/desktop/collection
qualification. It retains accepted launch facts, parent/descriptor/named socket
identities, kernel credentials, pinned tool/source identity and actual closure
facts. Primary and close errors survive together with known original resources.

**A separate later Node connection is not authenticated by this probe.** Future
AgentHost integration must carry the actual transaction on the same connection
that produced kernel peer credentials, with its own bounded protocol decision.
The current generic listener and Darwin requestOwner must not be relabeled or
used as Linux proof. Captured helper/interpreter closure and central receipt
versions must be added separately before any production collector wiring.

Run the deterministic and actual Linux-only private-server controls with:

```sh
node --test scripts/terminfo/linux-owned-endpoint.test.mjs
node scripts/terminfo/linux-owned-endpoint-selftest.mjs /tmp/linux-endpoint-foundation.json
```

The selftest creates only tagged source-known Python servers beneath fresh0700
temporary roots. It checks0644 descriptors, zero payload bytes, kernel peer
identity and different socket inode domains; refuses bad tool hashes, directory
and socket modes, a descriptor symlink, a non-socket path, a closed listener, and
a different owned peer despite a matching descriptor claim. Its successful
report requires actual server reap/stdio closure and owned fixture removal.
Failed fixtures/evidence remain diagnostic; no personal socket or GUI is used.
