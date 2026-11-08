# SGR closure qualification: sgr.blink, sgr.overline, sgr.underline.color (2026-11-08)

Qualifies exactly three pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`). The selective-reset
pair merged separately ([PR #897](2026-11-08-sgr-selective-reset-qualification.md)) and is preserved untouched.

## Pinned registrations (verified in the bundle)

| Id | Registration | Fixture | Criterion |
|---|---|---|---|
| `sgr.blink` | `sgrProbe` | `\x1b[5mX` | `cell.blink === true` on the rendered X cell |
| `sgr.overline` | `sgrProbe` + no-observable guard | `\x1b[53mX` | `cell.overline === undefined ? null : cell.overline === true`; an **unavailable** field is a not-tested result, an exposed `false` is the generic inconclusive |
| `sgr.underline.color` | `rgbUnderlineProbe("sgr.underline.color")` | `\x1b[4m\x1b[59m\x1b[38;2;0;0;255mA\x1b[38;2;0;255;0mB\x1b[58;2;255;0;128mX` | X underline literal rgb(255,0,128), distinct from the default B cell |

`sgr.underline.color` shares the **same helper** as the already-qualified `sgr.underline-color-rgb`: identical fixture bytes and criteria, differing
only in the id interpolated into the pinned `expected` text. This batch executes and grades `sgr.underline.color` from its **own** journaled reads under
its own pinned text; it never copies the sibling's verdict, and the two stay two registered ids. The pinned probes never return unsupported.

## Method

Eleventh digest-checked collector extension `ntilde-owned-sgr-closure-grading-v1` (derived over the selective-reset one; derivation refuses unless the
three registrations, the complete `sgrProbe` body including the overline guard and `notTestedResult` are present verbatim once), contracts v1, suite
`ntilde-owned-sgr-closure-graded-v1`, prefix `sgcl`, nonce `NTILDE_CL_`. Pinned fixtures are fed byte-exactly; the pinned code runs live and is replayed
parent-side; the independent contract grader must agree wherever semantics coincide, and differences are recorded.

Controls: neutral baseline; reset restoration (SGR 25 / SGR 55, X unchanged and Y neutral); a **decoy** cell with every other attribute set that must
not read the target (blink flag distinct from all others; overline independent of underline flag, style and a literal underline color); for overline an
independence group (underline reset keeps overline, no-overline cell reads false); unrelated attribute and stored fg/bg drift; DECSCNM proven off. For
`sgr.underline.color` the sibling's literal-versus-index, foreground-substitution, neutral and colon-spelling controls are reused unchanged. Unsupported
needs valid controls and an exposed `false`/absent value; an unavailable overline field, missing/invalid controls or incomplete restoration cannot grade.

## Measurement

Committed source `0bc92a30` (scripts and App source coincide; no App change). Owned run `b008385f36753b447ef92ce1b5352017`, receipt schema 24, source policy v18 (inventory v25,
168 files), default theme. Second collection: the first (run `c73e8b76fdac738fdd865b3d47a5c298`, source `a58a1693`, also all supported) is preserved in the summary as superseded after a Codex review tightened the overline independence control.

| Case | Pinned | Contract | Scope |
|---|---|---|---|
| `sgr.blink` | supported | supported | stored blink flag (0x20); no animation or timing |
| `sgr.overline` | supported | supported | explicit stored overline boolean; no stroke appearance |
| `sgr.underline.color` | supported | supported (stored: supported, parent-derived RGB: supported) | stored literal bytes, null default marker; no rendered appearance, no App-resolved color |

Observed: blink X flags `800` (0x20 set), Y `768`; decoy (bold, faint, italic, underline, inverse, hidden, strike, overline) blink false. Overline X flags
`768` with overline true (independent of any flag), decoy with blink/underline/literal underline color overline false, independence group V/U/T =
true/true/false. Underline color: A, B default (color `null`), X literal `ff0080`. No divergence between pinned and contract grading.

Same-run reconfirmation (kept separate): the 31 predecessor benchmark qualifications (the earlier 29 plus the two merged selective-reset cases) all
supported; the separate 10/10 truecolor-literal finding; the separate 3/3 App-observed underline-color resolution; stock counts unchanged (86 supported,
1 unsupported, 168 inconclusive) with all three ids `inconclusive` in the stock run.

## Pinned SGR inventory

[`2026-11-08-sgr-pinned-inventory.json`](2026-11-08-sgr-pinned-inventory.json), derived from the contracts by `scripts/terminfo/sgr-inventory.mjs` and re-derived
by `sgr-inventory.test.mjs` (and, with the collector cache, checked against the bundle's 32 registrations). **All 32 pinned `sgr.*` ids now have extension
evidence; none is unqualified and no selective-reset case is pending.** This is extension evidence coverage, not a stock compatibility score.
Overlaps are identified, not merged: `sgr.fg.standard`/`sgr.bg.standard` are covered by two suites (stored indices; RGB appearance), and
`sgr.underline-color-rgb`/`sgr.underline.color` share fixture bytes through one helper.

## Limits

Stored state only: no blink timing/animation, overline or underline stroke, resolved-color, glyph or pixel claim. macOS owned sidecar, default theme.

## Evidence

[`2026-11-08-sgr-closure/`](2026-11-08-sgr-closure/): summary, report, run log, common frames and one file per read frame.
`scripts/terminfo/sgr-closure-publication.test.mjs` re-derives the report from the retained journal.
