# Erase-character and saved-lines qualification: ECH and CSI 3J (2026-11-09)

Qualifies exactly two pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`):
`erase.character` (ECH) and `erase.screen.scrollback` (ED 3, "saved lines"). Selective erasure, rectangles, DCH/ICH, ED 0/1/2, Unicode editing and
platform qualification are different cases and are not substituted. This qualifies the **ASCII fixtures only**.

## Pinned definitions

| Id | Feeds (in order) | Reads | Supported iff |
|---|---|---|---|
| `erase.character` | `ABCDE ESC[1G` (setup, one feed), `ESC[3X` (ECH, **explicit 3**) | row 0 cells 0..4 and the cursor (must be `ABCDE` and 0,0) after the setup, cells 0..4 after | cells 0..2 blank (`""` or `" "`), cell 3 `D`, cell 4 `E` |
| `erase.screen.scrollback` | `\r\n` x (rows+2), then `ESC[3J` (**explicit 3**) | the measured screen rows and total lines before any feed, after the line feeds and after `ESC[3J` | the total after the line feeds is above the rows (else inconclusive, "No measured scrollback to erase") and the total after `ESC[3J` equals the rows |

Both termless probes decide; neither is null by construction. The pinned terminal pixel/query path of each case writes other bytes and ends in a
cursor-position query (`unmeasuredCellResult`, "Cursor response does not measure erased cells or scrollback"): it never carries a verdict and is not
qualified. The pinned ECH probe never reads the cursor after the erase, any column past 4, any other row or any attribute. The pinned ED 3 probe never
reads the viewport, the cursor, the margins, a mode, the saved-line contents, or whether the pre-erase history held anything but blank lines (its fixture
scrolls blank rows only, so it cannot tell ED 3 from "ED 3 that also clears the viewport").

## Method

Twentieth digest-checked collector extension `ntilde-owned-erase-chars-history-grading-v1` (derived over the erase-attributes/margins one, which in turn sits over saved-state/reset; it refuses derivation
unless both registrations, with their termless **and** cursor-query paths, `eraseRowResult`, `isBlank`, `rowCells`, `parserStateResult` and
`unmeasuredCellResult` are present verbatim once; the ED 0/1/2 registrations and `eraseScreenResult` are deliberately not seams), contracts v1, suite
`ntilde-owned-erase-chars-history-graded-v1`, prefix `echh`, nonce `NTILDE_EH_`. One owned worker drives both cases on the disposable App's **main** screen
(never the alternate screen: an alternate-screen zero-history observation proves nothing). Every read is a parent-journaled `readScreen` (viewport text,
stored cell details, attribute runs, resolved colors, buffer state) plus the actual `readScrollback` lines; the pinned code runs over the journaled
states and the parent re-executes it. Expected states come from the byte-interpreting model of the scrolling/history suite (extended with ECH, DCH, EL,
CHA, CUF and CUB, never from readback) and from a second, plain-operation oracle in the test fixtures; the two derivations are required to agree.

**erase.character** (15 reads, nothing here is a pinned grade except `erased`):

- controls: the observed blank baseline; the seed and cursor; a three-row wide fixture (`ABOVE-ROW!` / `ABCDEFGHIJ` / `BELOW-ROW!`, cursor on the middle row's
  column 2); a right-edge fixture (`WXY` in the last three columns, cursor on `W`); a bold-seeded attribute fixture with a blue-background pen;
- graded: the whole row after the pinned ECH, the wide fixture (an in-place erase, not DCH shifting, whole-line erasure or row bleed), the edge fixture
  (`ESC[2X` erases `W` and `X` and keeps `Y`), and the attribute fixture (retained cells keep their bold-only tuple; erased cells carry **no stale text
  attribute**); the cursor must not move; margins, modes, active buffer and saved lines may not drift;
- observed, never graded: the omitted, explicit 0, 1, 5 and oversized (`ESC[999X` from the middle of a row, no wrap onto the next row) spellings, each
  recorded against ECMA-48. The pinned graded spelling is exactly `ESC[3X`;
- background: the erased cells' stored background is **observed** against a reference cell written under the same `SGR 44` pen, resolved color and stored
  representation separately. Ntilde stores the pen as palette index 4 but the erased blanks as the resolved literal (ARGB `0xff0000ee`, RGB 0,0,238): the
  same resolved blue, a different stored form. Palette storage is not assumed and no pixel or rendered-appearance claim is made.

**erase.screen.scrollback** (10 reads, `pinned` stage first):

- pinned stage: the observed empty baseline, the pinned `\r\n` x 42 (three saved blank rows, total 43), the pinned `ESC[3J` (zero saved rows, total 40);
- marked history: nine ordered `HIST-0..8` rows scrolled off a viewport of `HIST-9..47`, cursor 6,11, then `ESC[3J`: the saved lines must be exactly gone
  while the viewport, cursor, margins, modes and main buffer are untouched;
- controls: ED 2 clears the viewport and keeps every saved line (`HIST-0..8`); three more line feeds only grow the saved lines (`HIST-0..11`); ED 3 over an
  already-empty history changes nothing visible (so it cannot establish erasure on its own); after ED 3 and two more scrolled rows the saved lines are exactly
  `HIST-9..10` (the old rows are gone, not hidden);
- the pinned-fixture verdict needs only the controls that precede it on a pristine screen. Later setups use a reset that itself ends in `ESC[3J`; those
  controls validate it by observation, and a reset that depended on a broken ED 3 cannot rescue or condemn the pinned fixture. A failed later control is
  inconclusive, never unsupported;
- physical rows: no wrapped (logical) lines are written, so a history count is a count of stored physical rows and the pinned total is the rows plus that
  count. Every read records `historyCount` and `totalLines`.

Unsupported needs valid controls and an observed mismatch; a missing or invalid seed, setup cursor or baseline, DECSCNM not proven off, a too-small screen,
stale or incoherent evidence or incomplete restoration is inconclusive or refused. The contract is only ever stricter than the pinned probe; every difference
is recorded with both verdicts (`contract-stricter-than-pinned` and the like).

## Measurement

Scripts `366a7463` (the committed tree, measured from the private captured source snapshot); the App source is unchanged since `c16997c2` (the Release
sidecar was built from this checkout with no App change). Owned run `f2fdd9890744a48faac76ae92380bca5`, taken through the shared desktop lock, receipt
schema 33, source policy v27 (inventory v34, 214 files), default theme, 40x140, disposable sidecar `artifacts/sidecar-erase-chars-history`, macOS, Node 24.

| Case | Pinned (parent replay + live pinned code) | Contract | Layer |
|---|---|---|---|
| `erase.character` | supported | supported | stored cells, cursor, stored attributes |
| `erase.screen.scrollback` | supported | supported | stored cells, actual saved lines, cursor, margins, modes, buffer |

| ECH | Before | After | Cursor |
|---|---|---|---|
| `ESC[3X` (pinned) | `ABCDE` | `___DE` | (0,0) before and after |
| wide fixture | `ABCDEFGHIJ`, rows 0 and 2 intact | `AB___FGHIJ`, rows 0 and 2 intact | (1,2) |
| right edge `ESC[2X` | `WXY` in columns 137..139 | `__Y` | (0,137) |
| attributes | `ABCDEFG`, A B D E bold | `AB___FG`, A B bold, F G plain, erased cells default-text with the pen background | (0,2) |

Other observations: omitted, explicit 0 and explicit 1 give `_BCDE`; count 5 erases the whole seed; `ESC[999X` at column 2 of the middle row gives `AB` with
the rows above and below untouched. Every spelling matched ECMA-48.

| ED 3 | Saved lines before | Saved lines after | Viewport / cursor |
|---|---|---|---|
| pinned fixture | 3 blank rows (total 43) | 0 (total 40) | blank / (39,0) both times |
| marked fixture | 9 rows `HIST-0..8` | 0 | `HIST-9..47` / (6,11) unchanged |
| ED 2 over the marked fixture | 9 rows | 9 rows `HIST-0..8` | cleared / (6,11) |
| 3 line feeds | 9 rows | 12 rows `HIST-0..11` | `HIST-12..47` / (39,0) |
| ED 3 then 2 scrolled rows | 9 rows | 2 rows `HIST-9..10` | `HIST-11..47` / (39,0) |

**Preserved attempts:** (1) run `6c190bf0011e78895f46eb4b04580d20` (scripts `4e1fb402`), complete, both cases supported, superseded before review because the ECH
conclusion named actual scrollback as a measured layer although only its preserved-state controls read saved lines and the pinned total was not recorded per read;
(2) run `f3353296aafbe75f8c5ff7a91447c213` (scripts `9c4ff3dc`), complete, both cases supported, superseded after review because the Sonar findings this PR introduced
were fixed with behavior-preserving refactors, so the measurement was repeated on the exact scripts; (3) run `699523bf75e5c19de68e6fcd71258660` (scripts `70aa8518`,
receipt 32, policy v26), complete, both cases supported, superseded because main meanwhile merged the erase-attributes/margins layer on the same policy and schema, so this
batch was restacked as the twentieth extension and re-measured; (4) run `57427dd644d24dbfce145f1c8da881ab`, failed, not evidence: the owner stopped responding in the image suite after this
batch's suite had completed its frames, no receipt, repeated unchanged; (5) the final run above.

Same-run reconfirmation (kept separate): every merged predecessor qualification case supported (65 unique ids, including the saved-state/reset suite with its
two preserved pinned-inconclusive verdicts beside their supported effect contracts); the separate 10/10 truecolor-literal finding; the separate 3/3
App-observed underline-color resolution. **The stock collector was not re-run** (it needs an interactive Ntilde tab): the pinned bundle, probeHash and the
App are unchanged, so the carried counts (168 inconclusive, 86 supported, 1 unsupported) are labeled as carried; both ids are inconclusive there through
the cursor-query path.

**Integration after measurement:** main later gained DECFRA/DECCRA (`607f097d`, `5210c58d`: `src/Ntilde.VT` rectangle fill and copy). That merge changes the App source after
the measured sidecar was built from `c16997c2`. Its only change to the parser dispatch is two new `$`-intermediate final bytes (`x`, `v`); the ECH (`X`) and ED (`J`)
paths, the measured scripts and their evidence validation are untouched, so the measurement was not repeated. The merge was checked by the unchanged self-test list
(the retained evidence still rederives byte for byte).

## Limits

Stored ASCII state and the actual saved physical rows only: no wide-cell or combining-character erasure, no logical (wrapped-line) history, reflow, resize or
alternate-screen history, no resolved-color, glyph or pixel claim; the pinned pixel/query paths are not qualified. macOS owned sidecar, default theme.

## Separate main-branch status check (not part of this qualification)

SonarCloud, project `benyblack_NovaTerminal`, branch `main`, analysis of revision `bd802f67d93d8ec77bfdca94b6963292fdc440b2` (2026-11-09T14:16:15Z):
the quality gate is **ERROR** (`new_reliability_rating` 3, `new_security_rating` 3). The C# issue the consolidation audit reported is `csharpsquid:S3168`,
key `AaF3_MEICDoRfaNiLSaM` (`async void LoadWorkspaceByName`, `src/Ntilde.App/MainWindow.Sessions.cs:384`), status OPEN, created 2026-10-26, unchanged on the
current main analysis: it is a real current finding, not a stale analysis. The same new-code reliability and security conditions are also fed by six
`secrets:S2068` vulnerabilities in `src/Ntilde.App/native/rusty_ssh/src/lib.rs` and three `rust:S7446` bugs in `src/Ntilde.App/native/src/lib.rs`. None of
this is touched here and the pipeline is not claimed clean; it is a separate follow-up.

## Evidence

[`2026-11-09-erase-chars-history/`](2026-11-09-erase-chars-history/): summary, report header and per-case reports, run log, common frames and one file per read
frame. `scripts/terminfo/erase-chars-history-publication.test.mjs` re-derives the report from the retained journal.
