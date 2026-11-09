# SonarCloud audit of the terminfo qualification pipeline (2026-11-09)

Scope: findings under `scripts/terminfo/` and `scripts/terminfo-owned-runner.mjs` that the qualification batches (#880 to #905) introduced or touched. It is not a repository-wide cleanup. The handoff's "15 new issues", cognitive-complexity findings and regex flag were leads; the authority is the SonarCloud API snapshot in [`2026-11-09-sonar-audit.json`](2026-11-09-sonar-audit.json) (every issue key, rule, file, line, creation time, status and disposition).

## What was inspected

| Item | Result |
|---|---|
| Analysis | `main` at `595861cb` (analysis 2026-11-09T12:05:17Z), which is the current `origin/main` head, so every open finding below still applies to current main |
| Findings in scope | 188 open `javascript:*` findings in `scripts/terminfo*` (all `CODE_SMELL`; no bug, vulnerability or security hotspot) |
| Out of scope | 52 older shell, PowerShell and Python findings in `scripts/` (mux, sidecar and build wrappers; unrelated to this pipeline) |
| "15 new issues" | Not reproducible from the API. The final PR analyses show #903 head `82dcdd9f`: 5 open (`S5843`, three `S3776`, `S6582`); #904: 32; #905: 0. Their quality gates are OK |
| Main quality gate | **ERROR** (`new_reliability_rating` 3, `new_security_rating` 3). The only recent bug is `csharpsquid:S3168` in `src/Ntilde.App`, outside this pipeline. A passing PR gate or a clean pipeline does not mean the project is clean |

Reproduce: `curl -s "https://sonarcloud.io/api/issues/search?componentKeys=benyblack_NovaTerminal&resolved=false&ps=500&directories=scripts,scripts/terminfo"` (project `benyblack_NovaTerminal`, organization `benyblack`), and `.../api/issues/search?componentKeys=benyblack_NovaTerminal&pullRequest=903&resolved=false` for a PR analysis. Issue links have the form `https://sonarcloud.io/project/issues?id=benyblack_NovaTerminal&open=<key>`.

## The regex flag (`javascript:S5843`, issue `AaHANEtzVAmwayIufAJX`)

Location: [`scripts/terminfo-owned-runner.mjs:1832`](../../../scripts/terminfo-owned-runner.mjs), first seen in the PR #903 analysis (2026-11-09T11:44:49Z).

```
/^(?:cell|sgr|grading|sgcolors|sgrgb|sgattr|sgpal|sgtc|sgtf|sgus|sguc|sgur|sgsr|sgcl|erln|ched|erdi|ledt|chrs|scrl|svrs|rgb|rendition|cursor|pixel|color|erase|editing|history|saved|metadata)-(?:start|bind|read|finish)$/
```

- **Structure:** anchored at both ends; two non-nested groups of fixed literals; no `*`, `+`, `?`, `{}` or class. The "complexity 33" is the count of alternatives (one per suite prefix), which grows by one with each qualification batch. Each added suite is another alternative, which is why it keeps being flagged.
- **Input:** `request.kind`, a string the parent-owned worker picks from its compiled-in suite prefixes (`requestOwner(config, kind, ...)`) and sends over the token-authenticated owner socket; the request is rejected before dispatch unless the capability token (constant-time compare) and run id match. It is not attacker text, and in the worst case its length is bounded by the socket request size.
- **Backtracking:** unreachable. With no quantifier the engine tries each literal once per position anchored at `^`; the cost is linear in the length of `kind`, and eight-megabyte inputs complete in milliseconds.
- **Disposition: justified non-issue** (readability only). Not edited: the runner is the trusted launcher whose bytes are part of the measured source graph and it cannot be imported, so an edit would change the next run's source digests and could not be unit-tested directly. The property that matters is now pinned by `scripts/terminfo/owned-runner-dispatch.test.mjs`, which reads the runner source and requires every dispatch expression to stay a quantifier-free anchored literal alternation, checks the accepted set and near misses, and bounds the time on multi-megabyte inputs. **Residual:** the scanner will keep reporting `S5843`; the follow-up is a table-driven dispatch in the next runner change, which is the right time to take that digest change.

## The finding that was real (`javascript:S8786`, four issues)

[`scrollback-contracts.mjs`](../../../scripts/terminfo/scrollback-contracts.mjs) `compactLines` matched each viewport or history line with `/^(.*?)(\d+)$/`, up to four times per line. A lazy prefix followed by a greedy digit run retries the digit run from every start, so a long digit-rich line costs quadratic time. Inputs are App-observed viewport and history rows, bounded by the contract limits (at most 256 columns by 128 rows), so the worst case was milliseconds, not a hang; a real but bounded defect.

**Fixed** by `splitTrailingNumber`, a linear scan that returns the same split (longest all-digit tail; a line terminator in the text still refuses the match, as `.` does), and `compactLines` now parses each line once. Keys: `AaHAHCIfeb8J2fGfFwxh`, `...Fwxi`, `...Fwxj`, `...Fwxl`. Behavior preservation is tested by an equivalence check against the former expression over sampled lines (terminators, non-ASCII digits, empty and all-digit lines) and numbered runs, plus maximum-admitted (256-column) and million-character adversarial lines. Every retained publication still rederives byte for byte. Whether Sonar clears the four issues is taken from the PR analysis after the fix, not assumed.

## Dispositions by rule

| Rule | Count | Disposition | Reason |
|---|---:|---|---|
| `S8786` non-linear backtracking | 4 | **fixed** | above |
| `S3415` assertion argument order | 2 | **fixed** | test-only: literal and measured value swapped |
| `S5843` regex complexity | 1 | justified non-issue | above; guarded by test |
| `S2681` multiline block braces | 1 | justified non-issue | `scrollback-model.mjs:75`: the `return` is inside the braces of the `if` and outside the `for`, as intended |
| `S2310` loop counter assigned | 1 | justified non-issue | `compactLines` skips the lines it has folded into one entry; covered by the numbered-run tests |
| `S9382` sequential await in loop | 4 | justified non-issue | the collectors drive one disposable App through an ordered protocol (write, settle, read); sequential by design. `graded-plan.mjs` already carries `NOSONAR` markers that the scanner does not honor for this rule; none were added |
| `S3776` cognitive complexity | 27 | deferred | plan builders, graders and receipt validators whose output is byte-compared with frozen publications; refactoring is a behavior-risk change to evidence validation with no defect behind it, and validation must not be relaxed to lower a score. Follow-up: a dedicated refactor that proves byte-identical reports |
| `S3358` nested ternary | 61 | deferred | cosmetic; same follow-up |
| `S1128` unused import | 25 | deferred | import edges are part of the owned-source graph (`literalImports`, inventory consistency); removing them is a graph change needing its own policy decision |
| `S7763` `export … from` | 25 | deferred | cosmetic; several modules also use the name locally |
| `S7765` `.includes()` | 16 | deferred | cosmetic |
| `S7780` `String.raw` | 7 | deferred | cosmetic; collector-patching code is digest-pinned |
| `S6582`, `S4624`, `S107`, `S7737`, `S7744`, `S7723`, `S7758`, `S7778` | 14 | deferred | cosmetic; `S7758` is `String.fromCharCode(0x1b)` of a BMP control character, identical result |

No finding was suppressed and no validation, size, timeout, ownership, identity or filesystem check was touched. The deferrals are real open findings: the pipeline still has about 180 open style and maintainability findings and is **not** clean. A reasonable follow-up is one refactor PR for `S3776` and `S3358` in the contract and grading modules that regenerates the next measurement's source digests under a new source policy, rather than editing frozen modules piecemeal.

## Source-policy note

Fixing `S8786` edits `scrollback-contracts.mjs`, a module in the owned-source graph (`readonly-owned-source-graph-v25`). The edit is behavior-preserving: the publication and receipt tests that rederive the retained scrolling/history evidence from its journals pass unchanged, and no live collection or evidence-validation behavior changed, so no source-policy bump and no fresh desktop measurement was required. The next measurement will simply record the new digest of that file; historical receipts, journals, publications and policies are untouched.
