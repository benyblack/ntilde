# Windows/Linux owned qualification: prerequisites and gaps (#775)

This audit describes main `af5b0f27dafac388d5a79ea8021c332b83ce06dd`
only as a source baseline; use the current checkout when implementing changes.
**Windows and Linux owned desktop collection remain unqualified.** No portable
test, displayed variable, headless render, ConPTY unit test or unavailable/skipped
run supplies the ownership, effect, restoration and cleanup evidence required by
[#775](https://github.com/benyblack/ntilde/issues/775).

## Available resources and nonqualifying inventory

On 2026-11-04 the coordinating agent inspected the connected remote-device
inventory read-only: two devices were registered, both offline; no usable
Windows/Linux desktop was exposed. No remote process or device state was
accessed or changed. The local inventory below was actually run on macOS, not
on simulated Windows/Linux hosts. Resource availability is a dated observation,
not a permanent platform limitation.

The standalone standard-library Python 3 preflight is independent of the owned
Node graph, collector and measurement receipts:

```sh
python3 scripts/terminfo-platform-preflight.py
python3 scripts/tests/terminfo_platform_preflight_tests.py
```

On Windows, use the installed Python 3 command (for example `py -3`) with the
same paths. This is an inventory command, not a collection command. Exit status
zero means the JSON was emitted; **it never means qualification**. Every platform
reports `qualification: "unqualified"`, `liveOwnershipVerified: false`, and the
complete list of unsatisfied live gates. `adapterPlatformImplemented` means only
that the current source permits the macOS adapter; it does not verify a launch.
There is no platform/qualification override or launch flag.

The inventory checks a fixed set of five tool names on PATH, this process's
stdin/stdout TTY hints, selected display/session-presence hints, and, on Linux
only, metadata for `/proc/self/stat` and `/dev/pts`. It does not execute tools or
validate their versions/trust. Tool paths, environment values, configuration,
user names, process lists and device names are not emitted. No app, endpoint,
collector, fixture, socket or remote desktop is opened or modified. Filesystem
metadata calls use normal OS behavior; this utility does not promise a hard
wall-clock deadline for inaccessible/mounted PATH locations.

The actual macOS invocation reported all five tool names present, stdin/stdout
not TTYs, no X11/Wayland variables, and all live gates unverified. Missing those
variables on macOS is not proof that a desktop is unavailable. Tests inject
Windows/Linux facts only to guard report privacy and the nonqualifying policy;
those fixtures are not Windows/Linux execution evidence. The Python tests are runnable directly and also run in the dedicated
[preflight CLI workflow](../../../.github/workflows/terminfo-platform-preflight.yml)
on Windows, Ubuntu and macOS with Python 3.12. That read-only job runs this CLI,
checks the native platform and fixed unqualified result, and retains only the
safe inventory JSON. It has read-only repository permissions and a five-minute
timeout. These CI runs verify CLI portability, not a desktop/PTY/ownership or
collector qualification. The shared CI workflow remains unchanged.

## Source-backed portability seams

| Boundary | Current implementation | Windows prerequisite | Linux prerequisite |
|---|---|---|---|
| Admission | [Runner](../../../scripts/terminfo-owned-runner.mjs) `validateInvocation` and [worker proof](../../../scripts/terminfo/ownership.mjs) `verifyNtildeOutput` reject non-Darwin. | Add a real Windows implementation before changing both guards. | Add a real Linux implementation before changing both guards. |
| Process and child identity | [Ownership](../../../scripts/terminfo/ownership.mjs) shells out to `/bin/ps`, compares UID, executable, direct parent and run marker; its terminal regex is Darwin-shaped. Cleanup rechecks identity before escalation. | Verify process handles/start identity, user/security context, intended app and child relationship, and bind the selected ConPTY output to that child. A PID, console handle or process name alone is insufficient. | Verify app/worker executable, credentials, run marker, parent/session and process-start identity with bounded native/proc operations; bind the selected `/dev/pts/N` device to that worker. Do not merely accept the Darwin record parser. |
| Output and geometry | [Terminal output](../../../scripts/terminfo/terminal-output.mjs) accepts `/dev/ttysN`, uses `/bin/stty -f`, parses Darwin rows/columns and independently checks `-opost`; restores prior mode before stock collection. | Implement an independently observed byte/geometry contract for ConPTY; do not call Unix stty or equate console mode with a proved stream. | Implement Linux termios or the correct platform tool/geometry format, including output-translation proof and restoration. Replacing `-f` with `-F` alone does not prove ownership. |
| Private fixture and launch | [Runtime](../../../scripts/terminfo/runtime.mjs) checks UID/Unix modes and a Darwin 103-byte socket-path ceiling. [Runner](../../../scripts/terminfo-owned-runner.mjs) writes a private `worker.sh` executed through `/bin/sh`. | Use a restricted per-run directory/config ACL, safe native argument construction and a private Windows worker launch. Unix mode bits and shell quoting are not an ACL or Windows argument policy. | Retain private owner/mode/symlink checks; validate actual sockaddr path bounds, executable/arguments and controlled environment under a unique run root. |
| Owner and observer endpoints | Runner creates `owner.sock` and an `appdata/agent.sock` observer path. [AgentHost](../../../src/Ntilde.App/AgentHost/AgentHostService.cs) already supports Unix sockets and Windows named pipes with `CurrentUserOnly`. | Add a unique, private owner pipe and explicitly isolated observer pipe/discovery. Verify peer identity and per-run endpoint binding; do not reuse a real user's default agent pipe. Existing App pipe support is a seam, not QA ownership proof. | Verify socket parent/entry owner, permissions, type, symlink/collision rules and peer identity; never unlink another live endpoint. Test path-limit refusal before launch. |
| Stop, interruption and cleanup | [Worker cleanup](../../../scripts/terminfo/ownership.mjs) uses SIGTERM/SIGKILL, run-marker revalidation and awaited exit. [Runner cleanup](../../../scripts/terminfo-owned-runner.mjs) retains failures/fixtures and waits for app/server teardown. | Implement bounded verified worker/app termination and resistant-child handling with Windows primitives. Prove handles/pipes closed before fixture deletion; retain failures instead of deleting evidence. | Preserve bounded identity recheck, resistant-worker escalation, exit/reaping and endpoint shutdown. Verify process reuse, detached PTYs and cleanup after repeated signals. |
| Source and dependency provenance | [Trusted launcher](../../../scripts/terminfo-owned.mjs) captures read-only source before import; [runtime](../../../scripts/terminfo/runtime.mjs) installs pinned per-run dependencies; [receipt](../../../scripts/terminfo/receipt.mjs) binds completed cleanup. | Preserve byte-exact source/input snapshots under Windows file/ACL semantics and safe cleanup; qualify Node/npm layout and executable provenance on the actual host. | Preserve the captured graph, locked install, retained bundle and executable/source provenance on the actual host. |
| Display and effect evidence | [Current QA guide](../QA_TERMINFO.md) distinguishes portable tests from actual owned desktop runs; [pixel guide](owned-pixels.md) describes synthetic offscreen phases. | Record actual Windows/session/display backend, geometry, scale, fonts and render context; qualify a disposable App, its ConPTY worker and actual observations. | Record distribution/kernel, graphical backend/session, geometry, scale, fonts and render context. A terminal-only shell, Xvfb presence or Wayland variable alone is not this evidence. |

These are requirements for a new verified backend, not claims that the currently
blocked paths are exploitable or that the app lacks normal cross-platform PTY
support. Keep new platform-specific I/O outside VT; reuse App/PTY/QA ownership
boundaries rather than teaching the terminal parser about OS resources.

## A bounded delivery sequence

1. Choose one actual interactive host and name its OS/display backend. An
   authorized, awake self-hosted runner or connected disposable VM can supply it;
   provisioning a host and installing tooling are separate operational actions.
   A hosted unit-test job is not assumed to provide a qualified desktop.
2. Implement one platform's process/output/endpoint/launch backend with
   deterministic refusal tests for wrong user, PID/start identity reuse, foreign
   app/child/stream, endpoint collision, malformed configuration and timeout.
   Keep platform admission closed until all required facts are available.
3. Run native integration tests on that OS: real child/output binding,
   private endpoint ACL/permissions, output-mode/geometry checks, bounded
   interruption and a worker that resists the first termination request.
   Synthetic tests remain separate from these results.
4. Run a real disposable owned collection and interruption/cleanup cycle. Retain
   actual replies, stored state and applicable PNGs with complete provenance,
   restoration, worker/app exit, endpoint shutdown and fixture-removal evidence.
   Partial/unavailable stages keep their explicit outcome; failures retain data.
5. Only then wire a dedicated live CI lane that records OS/display/backend,
   tool/build versions, source policy, actual evidence and cleanup. Mark absent
   resources as unqualified; a skipped lane must never produce a passing live
   qualification. Shared CI and source-inventory/receipt version changes require
   the existing QA owner and must preserve historical archives.

Keep #775 open until the real supported Windows/Linux environments satisfy its
acceptance criteria. This foundation adds no new owned measurements, grades,
capability promotions, receipt fields or collector behavior. The published stock
78/0/177 results and every historical evidence file remain unchanged.

## Linux native rendering unit prerequisite

`Ntilde.Rendering.Tests` directly references the centrally pinned
`SkiaSharp.NativeAssets.Linux` test dependency, matching the App's Skia version
without changing shipping dependencies. Run the project independently through
`scripts/build.sh test tests/Ntilde.Rendering.Tests`. No `LD_LIBRARY_PATH` override,
App output borrowing, window or graphical session is needed for the raster lane.

The separate `LinuxNativeAssetTests` guard applies to the repository's Linux
x64/arm64 release matrix. It creates a CPU raster surface, clears an exact opaque
color and reads its pixels back. A missing/unloadable native library fails that
guard instead of silently allowing native-dependent tests to skip. Other
operating systems and architectures outside that release matrix skip this
Linux-specific guard explicitly; individual tests retain their existing
portability checks. A library's mere presence is not enough to pass the guard.

This is native unit-test prerequisite coverage, not Linux desktop or terminal
qualification. Passing it does not prove App display, process/PTY ownership,
collector effects, restoration or cleanup. Keep #775 and Windows/Linux owned
collection unqualified until their independent live criteria are met.
