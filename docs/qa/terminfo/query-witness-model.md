# Exact-query witness model (#771)

This is a finite request plan and pure raw-byte comparison model, based on main
`26d315644dedcced2578b182656a57560e0d6dee`. It does not launch an App, send a
request, read a terminal, or issue a receipt. Fresh owned query qualification
remains open in #771. The frozen 78 supported / 0 unsupported / 177 inconclusive
observations and `suiteComplete=false` are unchanged.

The [exact-query audit](exact-query-audit.md) records the source policies and
regressions. The model contains exactly 40 independently named contracts:

| Group | Cases | Expected evidence |
|---|---:|---|
| Q07 DA3 and character geometry | 2 | Exact positive reply |
| Remaining frozen Q07 spellings | 16 | Completed framed silence |
| DECRQSS absolute vertical margins | 1 | Exact positive reply |
| Individual unavailable/unknown/empty DECRQSS settings | 11 | Exact bounded negative reply |
| Individual unavailable/unknown/malformed/empty XTGETTCAP names/lists | 10 | Exact bounded negative reply |

Requests, case identifiers and expectations are frozen in each returned plan.
Rows, columns and absolute one-based margins must be supplied as bounded integer
facts; the model does not consult environment variables, pixel metrics, App title
state or a terminal database. DA3's all-zero unit ID is explicitly unassigned and
nonunique. Geometry is rows before columns. Origin mode must not relativize the
margin report. Every request is at most 64 ASCII bytes.

The [xterm control reference](https://invisible-island.net/xterm/ctlseqs/ctlseqs.txt)
defines the character-geometry, DECRQSS and XTGETTCAP request/reply families. The
[xterm-412 implementation](https://github.com/ThomasDickey/xterm-snapshots/blob/5c75ead9a75d4b2e666ca06fa94503b5766ed93f/misc.c#L4896)
is the source for status 1 success / status 0 failure used here; the existing
audit explains the DEC manual's different documented polarity. These references
define syntax, not evidence that this collector has measured a runtime.

## Raw evidence contract

`inspectQueryWitness` accepts a completed exclusive raw capture with canonical
lowercase hex, exact byte count (0 through 512), and the exact seven-bit DECRPM
ordering replies for two distinct unknown private-mode numbers in 32768..65535.
A DA sentinel is never accepted as a feature reply or as proof of these fresh
challenges. Missing/malformed capture facts throw. After capture validation,
missing, wrong, duplicate, high-bit or trailing response bytes remain exact
mismatches; they cannot be decoded or normalized into an expected reply.

The return value separates `expectedEvidence`, `observedEvidence` and
`contractMatched`. A matched protocol rejection remains `negative-reply`.
Matched silence remains `silence`, not positive capability evidence. Missing
positive bytes are observed silence with a mismatched contract. Unexpected bytes
remain `unexpected-bytes`, without guessing their protocol or availability.
There is no generated support score, `supported` flag or completed receipt.

This pure comparison **does not authenticate capture facts**. The future parent
and raw-reader integration must independently prove process/pane/parser/worker/
PTY ownership, choose challenges after writing each query, reject reused pairs
and duplicate case submissions, retain original bytes and complete paired
before/after state, check no mutation and restoration, and verify cleanup before
receipt completion. Supplying a correctly shaped fabricated object is not a
measurement. Query input and fence bytes must share the existing ordered local
writer; elapsed silence alone is insufficient. Historical source maps and v8/v9
receipts must remain fixed when that future phase versions the live graph.

## Deterministic verification

```sh
node --test scripts/terminfo/query-witness-cases.test.mjs scripts/terminfo/query-witness-frame.test.mjs
```

Thirteen tests cover all 18 frozen Q07 identities, all 22 individually named Q12
requests, independent literal request/reply controls, wrong geometry/margins,
framing/order evidence, duplicate/trailing/high-bit bytes, bounds and immutable
facts. The existing portable CI list runs these tests on Windows and Ubuntu.
Synthetic data here is not a live App, physical keyboard or cross-platform
terminal qualification claim.
