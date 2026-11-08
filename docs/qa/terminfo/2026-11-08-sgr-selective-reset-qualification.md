# SGR selective-reset qualification: SGR 24 and SGR 27 (2026-11-08)

Qualifies exactly two pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`):
`sgr.selective-reset.underline` and `sgr.selective-reset.inverse`. The bold and italic cases were qualified earlier
([SGR colors batch](2026-11-06-sgr-colors-qualification.md)); `sgr.reset`, `sgr.inverse`, `sgr.underline.single` and
`sgr.underline-color-reset` (SGR 59) are different cases and are not substituted.

## Pinned definitions

`resetProbe(id, setup, reset, set, preserve)` feeds ONE string `C${setup}X${reset}Y`, reads cells (0,0), (0,1), (0,2) and runs
`measuredReset`: C must measure every contracted attribute false, X true, Y must expose every one as a boolean (underline: not
false/null/"none"); supported iff every `set` attribute is false and every `preserve` attribute true on Y.

| Id | Fixture bytes | set | preserve |
|---|---|---|---|
| `sgr.selective-reset.underline` | `C\x1b[1;4mX\x1b[24mY` | underline | bold |
| `sgr.selective-reset.inverse` | `C\x1b[1;7mX\x1b[27mY` | inverse | bold |

**Naming:** Ntilde's older `ntilde-owned-sgr-indices-v1` suite (`scripts/terminfo/sgr.mjs`) has identically named experiments with
different bytes (a colored combined cell, flag/palette-index readback). Those are Ntilde observations, not these fixtures and not
benchmark grades. The pinned terminal path ("Cursor advance does not verify SGR reset") is never a grade.

## Method

Tenth digest-checked collector extension `ntilde-owned-sgr-selective-reset-grading-v1` (derived over the underline-color one; it
refuses derivation unless the complete pinned `measuredAttribute`, `measuredReset`, `resetProbe` bodies and both registrations are
present verbatim once), contracts v1, suite `ntilde-owned-sgr-selective-reset-graded-v1`, prefix `sgsr`, nonce `NTILDE_SR_`. The pinned
fixture is fed byte-exactly in one write on a measured reset screen; the pinned termless probe runs over parent-journaled cells and the
parent re-executes it; the independent contract grader must agree wherever semantics coincide (recorded divergences: none).

Controls (each a separate journaled read, all stored-state; none carries a benchmark grade):

- initial blank screen and the baseline C neutral (no attribute, default stored colors, no underline color, no overline);
- **no-op:** the same setup bytes without the reset — Z must keep the setup state, proving the readback shows persistence;
- **accidental full reset:** SGR 0 — Y must be fully neutral (bold lost), the difference a selective reset must not show;
- **wrong reset:** SGR 22 for underline (underline stays, bold clears); SGR 24 for inverse (inverse and bold stay);
- underline only: double underline then SGR 24, and SGR 59 (underline stays, color clears) to separate SGR 24 from SGR 59;
- inverse only: the same fixture under DECSCNM on — per-cell flags must be identical, and global reverse video appears in exactly that
  one frame (binding validated, restored afterward).

Verdict rules: Y must have the target cleared (underline: flag clear **and** stored style 0; inverse: flag clear), bold kept, and no
other attribute, stored fg/bg, overline or underline color on Y or X. Unsupported needs valid controls and an observed mismatch;
missing/invalid setup, DECSCNM not proven, an invalid control, stale or incoherent evidence or incomplete restoration is inconclusive or
refused. **Underline color is observed, never required**: no pinned criterion constrains it, so no color policy is introduced.

## Measurement

Committed source `5d61ec48` (scripts and App source coincide; no App change). Owned run `d4c55288e450c653a45f6fe108e9327a`, receipt schema 23, source policy v17
(inventory v24, 163 files), default theme, disposable sidecar built from this checkout. First and only collection; no failed attempt.

| Case | Pinned (parent replay + live pinned code) | Contract | Layer |
|---|---|---|---|
| `sgr.selective-reset.underline` | supported | supported | stored flags, exact style, stored colors |
| `sgr.selective-reset.inverse` | supported | supported | stored flags, stored colors, DECSCNM-separated |

Observed (flags 768 = default fg/bg markers): underline C `768`, X `777` (single, bold), Y `769` (bold, underline flag clear, style 0);
inverse X `771` (bold + inverse), Y `769`. No-op Z kept `777`/`771`; SGR 0 gave Y `768`; SGR 22 gave underline kept, bold cleared;
SGR 24 on inverse kept inverse and bold; the DECSCNM-on triple had identical flags to the DECSCNM-off one. Stored fg/bg indices,
literals, overline and underline color on every graded cell were default.

**Underline-color behavior (documented only):** with a literal underline color stored on X, SGR 24 cleared the underline (and the
color reads back default on Y), but re-enabling underline with SGR 4 on Z **brought the color back** (the stored color survives SGR 24);
SGR 59 cleared the color and kept the underline. Nothing here is a requirement or a grade.

Same-run reconfirmation (kept separate): the 29 prior unique benchmark qualifications (31 layer entries) all supported; the separate
10/10 truecolor-literal finding; the separate 3/3 App-observed underline-color resolution; stock collector counts unchanged
(86 supported, 1 unsupported, 168 inconclusive), with both pinned cases `inconclusive` in the stock run.

## Limits

Stored state only: no resolved-color, glyph or pixel claim; the SGR 27 foreground/background swap is not qualified. macOS owned
sidecar, default theme.

## Evidence

[`2026-11-08-sgr-selective-reset/`](2026-11-08-sgr-selective-reset/): summary, report, run log, common frames and one file per read
frame. `scripts/terminfo/sgr-reset-publication.test.mjs` re-derives the report from the retained journal.
