# Rectangle-checksum qualification: DECRQCRA (2026-11-10)

Qualifies exactly one pinned terminfo.dev@4.0.0 case (bundle SHA-256 `aa5f2bf9…d1f6`, probeHash `16a690f34c0e`): `editing.decrqcra`.
`modes.left-right-margin`, the rectangle and column-editing cases, Unicode and wide-cell checksums, the persistent-session path, rendered appearance and any
platform other than the macOS owned sidecar are different cases and are not substituted. The implementation under test is the DECRQCRA of PR 922
([contract](rectangle-checksum.md)); before it the request was never answered.

## Off by default

DECRQCRA answers only while `AllowRectangleChecksumReport` is on, which it is not by default: a one-cell checksum names the character in that cell, so a program
answered cell by cell can read the whole screen back. The measured run turns it on in its initial settings (`initial-settings.json`, retained and digest-bound), and the
receipt refuses rectangle-checksum evidence from a run whose retained settings leave it off. The default itself is not measured live by this suite; unit and App tests
pin it (`RectangleChecksumTests`, `QueryVariantAuditTests`, `PaneParserWiringTests`), and every earlier owned run, made without the setting, witnessed the silence.

Because the setting is on for the whole run, the exact-query suite's `q07.editing.decrqcra` witness (`CSI 1;1;1;1;1;1 * y`) now owes the reply: the query suite
binds its home cell with the `N` of `NTILDE_QRY_`, so the demanded reply is `ESC P 1 ! ~ FFB2 ESC \`. A run without the setting keeps the original silence contract, so
every earlier publication re-derives unchanged.

## Pinned definition

The termless probe feeds `ABCDE ESC[1;1H`, then feeds `ESC[1;1;1;1;1;5*y` and captures what the terminal sends back. `checksumResult` grades it: supported iff
the reply contains a framed `DCS 1 ! ~ hhhh ST` (four hex digits, request id 1), otherwise inconclusive (`no-response` or `invalid-reply`). The pinned note says it
itself: the checksum arithmetic is not verified. The pinned terminal path asks `ESC[1;1;1;1;1;1*y` through `queryOutcome` with the same pattern; it is the stock
collector's path, and this suite measures the same spelling as a graded control. The derivation pins the registration and the helper verbatim.

## Effect contract

From DEC [DECRQCRA](https://vt100.net/docs/vt510-rm/DECRQCRA.html) (reply framing, request id) and Ntilde's documented checksum: each cell's character (a blank is
a space) plus xterm's bold 0x80, blink 0x40, reverse 0x20 and underline 0x10 weights, summed over the rectangle, negated modulo 2^16, as four uppercase hex digits;
DECERA's defaults, DECOM's row offset and page clipping; an inverted rectangle sums nothing. Agreement with a particular xterm `checksumExtension` or a DEC terminal
is not claimed.

## Method

Twenty-sixth digest-checked collector extension `ntilde-owned-rectangle-checksum-grading-v1` (derived over the column-editing one, `derivedFromAdapterId`
`ntilde-owned-column-editing-grading-v1`), contracts v1, suite `ntilde-owned-rectangle-checksum-graded-v1`, prefix `csum`, nonce `NTILDE_CK_`, minimum geometry
13x40 (the measured screen is 40x140). Every request is one fenced exchange (`checksumReport`): the request bytes, then a DA barrier; the raw DCS reply before the
barrier (or its explicit absence) is retained by the worker and journaled by the parent beside the stored cells of the read that follows, bound to that read's
barrier. The pinned termless code runs over the journaled pinned reply and the parent re-executes it. The independent checksum model, written for this suite,
demands the exact reply of every request from the parent-journaled cells of the screen the request saw, and no request may change the screen, the cursor, the
margins or the modes. Thirty requests, 34 reads, 37 captures:

- **pinned and terminal path**: `1;1;1;1;1;5` over `ABCDE` and the pinned terminal spelling `1;1;1;1;1;1`.
- **attributes**: one-cell requests over cells written bold, blink, reverse, single, curly and double underline, all four at once, and a space written in reverse;
  a whole-row request, and a request over italic, faint, hidden, strikethrough, overline, palette and RGB cells that must add nothing.
- **rectangle**: the whole page by omitted borders and by zero borders, partly omitted borders, oversized borders, an ignored page parameter, the largest request id
  (65535), an omitted request id (0) and two inverted rectangles (`0000`).
- **identity**: a `?` leader, a `>` leader, a seventh parameter, a colon subparameter and the `$` and `SP *` intermediates must all stay unanswered.
- **origin**: under DECOM with margins 3-6, rows offset by the top margin; borders past the region clip to the page, not the margins; zero borders take the defaults.

Unsupported needs valid controls and an observed mismatch. A missing seed, reverse video not proven off, a geometry below the minimum, unmeasured margins, a
stale palette binding or a cell outside the ASCII scope is inconclusive or refused, never conclusive.

## Measurement

Scripts (adapter) revision `6669794fd2e3614a46fa3a67883f1ae5a8c8ba12`; App source revision `7fbffce766c4eb2fbe6fb7065c3549d56e9aac68` (the last commit that
touched `src/`: PR 922's DECRQCRA; main `c46feca5` has the same tree); the Release sidecar was built from that tree with the repository wrappers. Owned run
`46d9aee12fc2dd116afafed84353a21d`, receipt schema 39, source policy v33 (inventory v40, 248 files), initial settings with `AllowRectangleChecksumReport: true`,
default theme, 40x140, App version 0.13.0 (operator supplied; not independently verified), disposable sidecar `artifacts/sidecar-decrqcra`, stock collector run
`96fa0d7bbd257844ff068fe56669ad85`, measured 2026-11-10T19:44Z on macOS with Node 24, under the shared desktop-collection lock.

| Case | Pinned (parent replay + live pinned code) | Contract | Stock grade (same settings) |
|---|---|---|---|
| `editing.decrqcra` | supported | supported | supported (`query`) |

The pinned reply was `ESC P 1 ! ~ FEB1 ESC \` (A-E sum to 0x14F), and the terminal-path spelling `ESC P 1 ! ~ FFBF ESC \` (A). The attribute cells answered
`FF3E` (B bold), `FF75` (K blink), `FF8E` (R reverse), `FF9B`, `FFAD`, `FFAC` (U, C, D underlined in any style), `FEB8` (X with all four) and `FFC0` (a reverse space),
and the unweighted cells added nothing. The whole page answered `3E0C` by omitted, zero and oversized borders alike; the page parameter changed nothing; the request ids
65535 and 0 were echoed; both inverted rectangles answered `0000`; the six non-admitted spellings stayed unanswered. Under DECOM the rows were offset by the top
margin and borders past the region clipped to the page. Every request left the screen, cursor, margins and modes unchanged.

**Exact queries.** The `q07.editing.decrqcra` witness received exactly `ESC P 1 ! ~ FFB2 ESC \`. The query suite's other results are unchanged from the column-editing
run (33 matched, 7 mismatched: the same seven frozen expectations of features implemented since).

**Stock collector.** With the setting on, the stock run's terminal path for `editing.decrqcra` was answered and graded `supported` (stock counts 87 supported,
1 unsupported, 167 inconclusive; previously 86/1/168). A stock run with default settings leaves it unanswered.

**Predecessors and findings.** Every merged predecessor suite (twenty-four, including column editing) and both finding suites reconfirm in the same run with the
same outcomes as their own publications; the receipt re-derives all of them.

**Attempts:** the published run is the only attempt.

## Layout

`summary.json` (run identity, context, receipt, same-run predecessor outcomes, the query witness and counts, stock grades, attempts), `run-log.json`,
`journal-common.json` (the report header and the start, bind and finish frames), `case-editing.decrqcra.json` and one `journal-editing.decrqcra-read<n>.json` per read
frame. `rect-checksum-publication.test.mjs` reassembles the journal and re-derives the report byte for byte.

## Boundaries

Exact replies over stored ASCII cells only: no wide, combining or supplementary character (unit tests cover them), persistent-session, rendered-appearance or platform
claim. The default-off gate is pinned by unit and App tests, not by this live run. Agreement with xterm's `checksumExtension` variants or DEC hardware is not claimed.
`modes.left-right-margin` is a later batch.
