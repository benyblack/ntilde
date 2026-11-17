# OSC 3008 hierarchical context qualification (#997)

`extensions.osc3008-context` was measured on a disposable direct-local Ntilde App built from `93b6bff1985382d0a6e161ff1888cc1a21e91c25` (main merged, including the Kitty animation PNG-frame production change; also the measured scripts revision; the labels are operator-supplied, not binary-to-source attestation). Contract and policies: [osc3008.md](osc3008.md). Authority: [UAPI.15 v1.0](https://uapi-group.org/specifications/specs/osc_context/).

## Result

| | Outcome |
|---|---|
| Pinned `terminfo.dev@4.0.0` probe (terminal path only; no termless function) | **inconclusive**, by construction: it returns `unmeasuredCellResult` over a cursor position, and its payload `type=test` is not a UAPI.15 start or end |
| Stock collector (same owned run) | inconclusive, unchanged: stock counts stay 94 supported / 1 unsupported / 160 inconclusive |
| Effect contract `ntilde-osc3008-context-effect-v1` | **supported**: 28 of 28 decided reads match the independent oracle; both validity controls held |
| Policy groups (end-ends-sub-contexts, depth 16, repeated field, bare backslash) | 10 of 10 reads match the recorded policy; they never decide |

The pinned verdict cannot move, so this is a recorded divergence between the pinned and effect outcomes, not a score change, a stock grade or a common-harness claim. Run `34dd71da0b96927426033d07458e20de` (stock run `dc6863f8ca7e18d46dfda5ad3fc3ef55`), receipt schema 73, source policy v66, the `owned-osc633-without-notification-suites-v5` scoped selection (OSC 9 and OSC 777/99 are the separately receipted predecessor's; the scoped predecessor is re-verified at finalize).

## What was measured

The App implements OSC 3008 in `AnsiParser.ContextSignalling.cs` (this PR): a stack of at most 16 contexts and the 8 most recently ended, kept as untrusted data and exposed read-only as `readPaneMetadata` `buffer.contexts`. The suite feeds exact bytes through the owned fence and reads paired complete screen, pane-metadata and session-status snapshots 37 times (41 journal frames with the start, bind and finish observations):

- **stock**: the pinned fixture on the baseline: no context appears.
- **required** (specification-determined): minimal start/end; the specification's container example verbatim; nesting with an update that flushes metadata and ends sub-contexts; BEL and ST terminators; the 64/65-character id bound and invalid ids (empty, non-ASCII); field validity (unknown, invalid, empty and over-long fields ignored, ABNF bounds, 255-character cap); escapes (`\x3b`, `\x5c`, UTF-8 text); end metadata (valid and invalid `exit`, `status`, `signal`); a soft terminal reset (DECSTR) leaving the stack alone.
- **control** (must change nothing): the pinned payload, foreign spellings (`start` alone, `begin=`, `START=`, an empty body, an end of an unknown id, OSC 3007, 30080, `133;start`, `633;start`), and a context carrying `cwd=`/`comm=` interleaved with OSC 133 A/B/C/D: the title, OSC 7 working directory and shell marks follow only the OSC 133 model, and OSC 133 creates no context.
- **policy**: recorded, never deciding.

Every read also required the complete stored screen, cursor, title, working directory, palette and shell state to equal the model of the feeds with all OSC 3008 sequences removed. The oracle (`osc3008-contracts.mjs`) is written from the specification and never calls the parser. The pinned terminal path is replayed byte-exactly over the journaled cursor, and the unchanged pinned code is re-run from the retained derived bundle (`ntilde-owned-osc3008-grading-v1`, derived from the left/right-margin layer).

The first measurement (run `e8e16d88461607069d1dbae74441d4ee`, scripts and App `9fa0fef7`, same outcomes) preceded the merge of main and several review fixes to measured scripts, so it was repeated on the merged Apps and is retained as [superseded/attempt-1-pre-merge.zip](2026-11-17-osc3008/superseded/attempt-1-pre-merge.zip); it is not a second confirmation of the final scripts.

Retained evidence: [owned-run.zip](2026-11-17-osc3008/owned-run.zip) (the complete owned output: journal, run log, report, source snapshot, ownership proof, cell receipt, desktop-lease cleanup), [report.json](2026-11-17-osc3008/report.json), [summary.json](2026-11-17-osc3008/summary.json), [manifest.json](2026-11-17-osc3008/manifest.json). `osc3008-publication.test.mjs` re-extracts the archive, re-issues the complete receipt (including the scoped predecessor), rebuilds the report from the journal and re-projects the publication.

## Limits

Direct-local disposable App on the measured platform only; the stored data is observed, not presented. Not measured: hangup (the child of the measured pane cannot be ended by the suite; unit tests in VT, App and Mux cover it), RIS, alternate-screen switches, real emitters (systemd `run0`, `systemd-nspawn`, `machinectl`, shells), remote or mux delivery, other platforms, the OSC 133/633 prompt features beyond the separation control. The ended-record ring cannot be cleared by any sequence and the initial null title and working directory are not restorable; the stack was empty again at the end. The pre-existing `cursor.reverse-wrap` ungraded collector diagnostic is unchanged. No fixture URL was opened and no native notification was used.

The measured App predates the later main commits that bring Kitty animation to persistent sessions and PNG pixel decoding; they touch no OSC 3008, pane-metadata or measured script input, and the VT, Mux and App test lanes were rerun with them merged. The OSC 3008 measurement was not repeated for them.
