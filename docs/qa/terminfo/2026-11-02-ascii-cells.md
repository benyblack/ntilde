# Owned ASCII cell observations — 2026-11-02

All **eight supplemental text/cursor experiments passed** against a fresh
disposable macOS desktop instance. The unchanged stock collector recorded
**78 supported / 0 unsupported / 177 inconclusive** observations. Seven of the
eight local experiments add state evidence for Q01's historical inconclusives;
absolute cursor movement adds state evidence beside its existing query result.
These observations do not regrade the frozen triage or stock raw run.

The [machine-readable evidence](2026-11-02-ascii-cells.json) includes all 22
parent-owned snapshots, their hashes, controls, assertions, source/input hashes
and the completion receipt. Original context, raw and ownership-proof files
remain in the local evidence directory; the published hashes support checking
those originals, not independent app-identity attestation.

| Measurement | Value |
|---|---|
| Build informational version | `0.13.0+633e333e0af7dc2f5aed95b4f5b84ff3fbd374e5` |
| App assembly SHA-256 | `ac181caa80be8f2d95bd4dd44de73bb16fc8ad3af5dca10d1c901b7108c86919` |
| Runtime / collector | Node `v24.22.1` / pinned `terminfo.dev@4.0.0` |
| Local evidence | `artifacts/terminfo/owned-58OQD0` |
| Started / cleanup completed | `2026-11-02T14:09:35.186Z` / `2026-11-02T14:09:42.307Z` |
| Viewport | 41 rows × 140 columns |
| Cell report SHA-256 | `7ab0c14df6bc91261c421411d0f8afb7b599698ed6e41b1c60fb594e652f0da5` |
| Stock raw SHA-256 | `469f6332c2df4fcb20b160877222ce5d91f4bfdf49611c9f62f99bf903442824` |

Version metadata came from the built assembly. The collector's raw identity
remains `unverified`; the runner verifies the launched executable, parent/worker
relationship, user, PTY and unique run marker separately. No production app,
profile or clipboard was accessed. Only the disposable instance enables Observe
access; Act access stays disabled.

## Observed behavior

Coordinates below are zero-based. Every experiment first established its control,
kept `KEEP!` on row 1, and required every other unaffected row to remain blank.

| Local experiment | Observed change | Result |
|---|---|---|
| `text.basic` | Empty row → `Hello`, cursor `(0,5)` | Passed |
| `text.overwrite` | `ABCDE` → `ABZDE`, cursor `(0,3)` | Passed |
| `cursor.move.absolute` | CUP moved cursor to `(1,3)`, then `(0,1)`; text stayed intact | Passed |
| `cursor.cup-scroll-region` | With margins 2–3, CUP 1;2 targeted `(0,1)` with DECOM off and `(1,1)` with DECOM on | Passed |
| `erase.line.right` | EL 0 at column 2: `ABCDE` → `AB`; cursor unchanged | Passed |
| `erase.line.left` | EL 1 at column 2: `ABCDE` → three spaces then `DE`; cursor unchanged | Passed |
| `erase.line.all` | EL 2 cleared row; cursor unchanged | Passed |
| `modes.insert-replace` | IRM insertion: `ABCD` → `AXBCD`; replacement: `AXYCD` | Passed |

The worker used a fresh DA reply after each write to establish ordering. Actual
cell and cursor evidence came from the App's `readScreen` projection, independently
recorded and graded by the parent. A nonce bound the singleton pane to the worker
before the experiments. Geometry and identity were checked around every read.
The initial and restored main-screen snapshots match exactly, including cursor
visibility. A completion receipt was written after worker/app exit and private
fixture deletion.

## Verification and limits

The local suite passed 115 tests with zero skips, including real-app collection,
retained-source hashes, completion-receipt hashes, interruption during preparation
and cleanup, repeated signals, and a worker that ignores SIGTERM. The
portable regressions reject missing barriers and an incorrect nonce cursor;
the published report is also re-derived and digest-checked offline in CI.
The Release App build completed with zero warnings and errors.

This is ASCII viewport buffer/cursor evidence. The existing projection trims
trailing whitespace and skips wide-cell continuations. No wide/combining text,
attributes, colors, glyph pixels, scrollback, SSH, mux or other terminal emulator
was measured. All fixtures used ordinary single-width ASCII with ample margins.
No VT parser, renderer or capability claim changed.

The stock run still has `suiteComplete=false` and its ungraded reverse-wrap
geometry diagnostic. Its 177 inconclusives remain 152 evidence-limited,
22 missing replies and three policy refusals. More of Q01 still needs dedicated
experiments, and Q02's color/attribute measurement remains a separate follow-up.
