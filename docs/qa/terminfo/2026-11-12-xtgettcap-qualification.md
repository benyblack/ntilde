# XTGETTCAP qualification: capability reports (2026-11-12)

Qualifies exactly one pinned terminfo.dev@4.0.0 case (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`): `device.xtgettcap`. The pinned probe sends
`DCS + q 544e ST` — the hex spelling of the name **TN** — and its criterion wants a framed `DCS 1 + r 544e = <even-length hex> ST` value reply. Ntilde deliberately
refuses `TN` (#807/#861): its honest value is the TERM the session uses, which VT does not know, and no value is invented or inferred from another capability, the
application name or any child-TERM evidence. The pinned verdict therefore stays **inconclusive** (`insufficient-evidence`, "Requested setting or name refused; other
settings or names unmeasured") on both the termless and the terminal path, is preserved beside the graded effect contract, and **no production code changed**: this
qualifies existing behavior.

## Result

| Case | Pinned termless | Pinned terminal path | Effect contract | Experiments (reads) | Stock grade |
|---|---|---|---|---|---|
| `device.xtgettcap` | **inconclusive by design** (the refused TN) | **inconclusive by design** (same refusal) | **supported** (`ntilde-xtgettcap-capability-report-effect-v1`) | 20 exchanges (21 reads) | inconclusive |

The effect contract is separately identified — distinct fixture, criteria and verdict, never attributed to the pinned probe. The divergence
(`pinned-inconclusive-by-design`) is recorded; the published conclusion is the pinned verdict. The stock collector, re-run in the same owned run, still grades
`device.xtgettcap` inconclusive (167 inconclusive / 87 supported / 1 unsupported, `suiteComplete=false`), so this qualification **raises no stock score**.

## What the App did (disposable direct-local pane, 40×140)

Every request is one fenced exchange: the exact bytes through the owned fence, every raw reply retained before the DA ordering barrier, journaled beside one complete
parent-observed state (stored cells, attributes, protection, resolved colors, cursor, margins 2..6, origin off, alternate screen). The seeded screen carries bold and
indexed-red text and a curly-underlined cell so "unchanged" covers real ink, not just blanks; the nonce row binds the pane. Twenty exchanges, in order:

| Role | Request | Observed reply |
|---|---|---|
| pinned | `DCS +q544e ST` | `DCS 0+r ST` |
| numeric-co | `DCS +q436f ST` | `DCS 1+r436f=323536 ST` (Co = 256) |
| numeric-colors | `DCS +q636f6c6f7273 ST` | `DCS 1+r636f6c6f7273=323536 ST` |
| flag-rgb | `DCS +q524742 ST` | `DCS 1+r524742 ST` (a flag: no value) |
| flag-tc | `DCS +q5463 ST` | `DCS 1+r5463 ST` |
| string-smulx | `DCS +q536d756c78 ST` | `DCS 1+r536d756c78=1B5B343A25703125646D ST` |
| list-order | `DCS +q436f;544e;5463;436f ST` | value, refusal, flag, value — one reply per name, in order |
| list-witness | `DCS +q544e;436f ST` | refusal, value (the Q12 witness list) |
| unavailable-kcuu1 | `DCS +q6b63757531 ST` | `DCS 0+r ST` (inherited from xterm-256color) |
| unknown | `DCS +q6e6f742d612d6361706162696c697479 ST` | `DCS 0+r ST` |
| case-co | `DCS +q636f ST` | `DCS 0+r ST` (names are case-sensitive) |
| echo-upper | `DCS +q436F ST` | `DCS 1+r436F=323536 ST` (echoed as spelled) |
| malformed-odd | `DCS +q5 ST` | `DCS 0+r ST` (one refusal, nothing else) |
| recovery-tc | `DCS +q5463 ST` | `DCS 1+r5463 ST` |
| malformed-hex | `DCS +qzz ST` | `DCS 0+r ST` |
| recovery-co | `DCS +q436f ST` | `DCS 1+r436f=323536 ST` |
| malformed-empty | `DCS +q ST` | `DCS 0+r ST` |
| recovery-pinned | `DCS +q544e ST` | `DCS 0+r ST` (the exact pinned request, still refused identically) |
| malformed-mixed | `DCS +q436f;5 ST` | `DCS 0+r ST` (the whole list refused, valid name included) |
| recovery-string | `DCS +q536d756c78 ST` | `DCS 1+r536d756c78=… ST` |

Every exchange left the complete screen, cursor with its visibility, rendition, margins, origin, wrap/insert/pending-wrap modes, active screen and saved-line count
unchanged (`unchanged: true` on all twenty — the key compares the complete observed buffer state), and the fixture restored: the finish frame hashes equal to the start
frame's. All twenty matched the independent reply oracle byte for byte (`matches: true`), including reply count and order on the multi-name lists.

## Identities

App 0.13.0 (operator supplied) built from `2d8433d9` (arm64 Release, executable SHA-256 `f5814e51…9bf8` — bit-identical to the title/icon-stack run's binary; no
production source changed in this batch); scripts at `5c4edc95`; source policy `readonly-owned-source-graph-v48` (313 files); receipt schema 53; Node v24.22.2; owned
run `ddfade80…1fcc`; measured 2026-11-12T13:26Z on macOS 27.0.1. The operator-supplied build-source label is not binary-to-source
attestation. Predecessor suites reconfirmed in the same run: all thirty benchmark layers' cases are supported (or their effect contracts supported — the two saved-reset
pinned inconclusives keep their by-design verdicts), the five Core layers are receipt-bound, the query suite reproduces its frozen 33 matched / 7 mismatched, and the two
findings reconfirm. Stock counts are re-collected in that run (not carried forward) and unchanged.

Attempts, all retained with their actual reasons (archives in `superseded/`): the first two (`cd4ceee0` uncommitted, then `b88d8c1c`) each collected every suite — the
pinned refusal and all twenty controls already matched in both — but the finalizer refused their receipts because the v48 source policy was not yet admitted by the
snapshot-manifest and metadata allowlists; measurement-machinery fixes, not App or contract changes. The third (`6251515f`), fourth (`dea61d0a`) and fifth (`923253fc`)
collected cleanly and completed receipt schema 53 with identical evidence, and are superseded because review findings then changed measured modules: nine SonarCloud
findings (unused import, re-export spelling, `fromCodePoint`, `includes`, a default parameter, a serial-replay NOSONAR and the report builder's cognitive complexity), one
Codex P2 — the unchanged-state key now also compares cursor visibility and every observed buffer mode (autowrap, insert and pending-wrap, active screen, saved-line
count), so an exchange changing any of them can no longer pass as unchanged — and a last Sonar pass (the now-re-exported fixture imports and the NOSONAR marker's
placement). The sixth run (scripts `5c4edc95`) is the published one; the same refusal and the same twenty controls matched every time.

## Newly fixed versus newly qualified

Nothing was fixed in the App or the VT parser: every observed reply already held (the owning regressions are `TerminalCapabilityTests`). The only fixes were to the
measurement machinery itself (policy allowlists, the owner-response cap for the `xgtc` prefix). Newly qualified existing behavior: the preserved pinned verdicts on both
paths, the effect contract over the exact pinned request, the numeric/flag/string reply encodings, multi-name order with per-name outcomes, the documented unavailable
and unknown refusals, case-sensitivity, the uppercase echo, the malformed-list refusals, recovery after each malformed request, and unchanged state through every
exchange.

## Evidence

[summary](2026-11-12-xtgettcap/summary.json), [run log](2026-11-12-xtgettcap/run-log.json), [journal-common](2026-11-12-xtgettcap/journal-common.json), one
[journal](2026-11-12-xtgettcap/) file per read plus the finish frame, and the [case file](2026-11-12-xtgettcap/case-device.xtgettcap.json). Reproduce:

```bash
node --test scripts/terminfo/xtgettcap-publication.test.mjs
```

The test reassembles the journal, re-derives the report, the receipt digests and the pinned replay. A fresh measurement needs the shared desktop lock and
`node scripts/terminfo-owned.mjs --executable <abs>/Ntilde --app-version 0.13.0` on Node 24.

## Limits

Capability-report replies only: an advertised `RGB` flag or `colors` count proves the reply, never rendering, color accuracy, or execution of an advertised control
string (the owning VT tests cover those). The settings-dependent `Ms` clipboard capability, X resources (`device.xtgetxres` is a different case), clipboard permissions,
remote (SSH, mux, persistent-session) delivery, Windows and Linux runtime, and the stock CLI's own grades are all outside this qualification. The fence bounds the
request at 64 bytes, so the sixteen-name and 32-character-name list limits stay unit-evidenced (`TerminalCapabilityTests`), not desktop-qualified. No stock-score,
common-harness or Core-percentage claim is made.
