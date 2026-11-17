# rxvt OSC 720 scroll-up qualification (2026-11-17, #999)

Qualifies exactly one pinned terminfo.dev@4.0.0 case (bundle SHA-256 `aa5f2bf9…e1f6`, probeHash `16a690f34c0e`): `extensions.osc720-scroll-up`. **No terminal behavior changed.** Ntilde has no OSC 720
handler (`src/Ntilde.VT/AnsiParser.Osc.cs`), and this measures that, through the App's own view position, stored history, cursor and screen. Two **measurement hooks** were added so the viewport
can be observed and moved by an owned run (see [What changed in the App](#what-changed-in-the-app)).

## What the protocol is, and what the pinned probe sends

OSC 720 belongs to rxvt-unicode. Its manual (rxvt(7), "Operating System Commands") says: `Ps = 720  Move viewing window up by Pt lines, or clear scrollback buffer if Pt = 0 (Compile frills)`; 721 is the
same toward the live line. The reference implementation (`src/command.C`, case `URxvt_view_up` / `URxvt_view_down`) reads `lines = atoi(str)`; a non-zero count calls `scr_page(UP/DN, lines)` and a zero
count calls `scr_erase_savelines()`. The parser (`process_osc_seq`) only dispatches an OSC when the character after its digits is `;`, so the **bare** `ESC ] 720 BEL` is not dispatched at all, while an empty count after the `;` is zero. So OSC 720 **controls a viewport for `;Pt` and mutates the retained history for `;0` or `;`; the bare form does nothing in urxvt.**

The pinned probe (read from the bundle; the public terminfo.dev prose is not a frozen definition) sends the **bare** form `ESC ] 720 BEL`, with no `;` and no count:

| Path | What it does | What it can return |
|---|---|---|
| termless | feeds thirty lines `scroll-<i>\r\n`, reads `getScrollback()` (needs `totalLines > screenLines` and `viewportOffset > 0`, else `parserStateResult(null)`), feeds the bare form, reads again; passes iff `after.viewportOffset < before.viewportOffset` (`viewportOffset` is the absolute buffer row where the viewport starts) | supported / unsupported / inconclusive |
| terminal | writes `ESC[1;1H ESC[2K`, the bare form, asks the cursor position, returns `unverifiedEffect` | **always inconclusive** (evidence `consumed`); the path the stock collector grades |

The pinned form is therefore not one the protocol owner defines: **urxvt itself ignores it, so the pinned criterion cannot pass on its own reference terminal.** (An earlier revision of this page, and
of the contract, read the bare form as Pt = 0 and expected an erase; review corrected that from the parser source, and the run was repeated.) Separately, a history erase (`;0`) also drops `viewportOffset` to 0, so the
pinned termless criterion **cannot tell a viewport move from a history erase** (a synthetic regression test shows it passing over an erase). This qualification keeps those operations apart, expects no effect
for the bare form and never promotes either operation to the other. CSI SU, scrollback
growth, and keyboard or wheel viewport scrolling are different operations: the wheel and ED 3 appear only as observer controls.

## Result

| Case | Pinned terminal (stock path) | Pinned termless replay (parent-built context) | Effect contract | Stock grade |
|---|---|---|---|---|
| `extensions.osc720-scroll-up` | inconclusive by design | unsupported (viewport unchanged) | **unsupported** (`ntilde-osc720-view-up-and-erase-saved-effect-v1`) | inconclusive |

Three distinct things, recorded separately in [report.json](2026-11-17-osc720/report.json):

- **Pinned terminal path:** preserved, inconclusive by design. It never measures the viewport, so it can never move.
- **Pinned termless function:** executed **unchanged** over a parent-built scrollback context mapped from the App's own observation (`viewportOffset = scrollbackLines − scrollOffset`,
  `totalLines = scrollbackLines + rows`, `screenLines = rows`). This is not a termless backend run and not a stock grade. It returned `unsupported`: the viewport stayed at the live line after the bare form.
- **Effect contract:** independent oracle written from the rxvt text. All seven protocol experiments observed **no effect**, with every control held, so the effect is **unsupported**.

The stock collector, re-run in the same owned run (`4889417f35fe10780c3385cb19e8a51d`; stock collector run `8d1225c1db24ee9e81fb81a16e497791`), still grades the case inconclusive
(94 supported / 1 unsupported / 160 inconclusive, `suiteComplete=false`): **no stock score, no Core point and no common-harness score is added.**

## What the App did (disposable direct-local pane, 40×140)

Each experiment resets the screen and history (`ESC[3J`), moves the cursor to the last row (`CUP 999;1`, which clamps) and feeds the pinned thirty lines, so exactly 30 rows of history exist whatever the grid
(the observation API bounds retained history at 64 rows) and the live view sits at offset 0 with a maximum offset of 30. A distinguishable setup read follows, then one probe, then the effect read. Every read
is a paired complete screen, stored history (count, generation, evictions and content), cursor and view position, taken after a completed DA fence barrier.

| Experiment | Role | Bytes | rxvt expectation | Observed |
|---|---|---|---|---|
| `pinned-bare` | protocol | `ESC]720 BEL` | no effect (no `;`, not dispatched) | nothing changed |
| `count-zero` | protocol | `ESC]720;0 BEL` | saved lines erased | nothing changed |
| `count-empty` | protocol | `ESC]720; BEL` | saved lines erased | nothing changed |
| `count-3` | protocol | `ESC]720;3 BEL` | view up 3 | nothing changed |
| `count-3-st` | protocol | `ESC]720;3 ST` | view up 3 | nothing changed |
| `count-clamped` | protocol | `ESC]720;100000 BEL` | view up to the oldest row (30) | nothing changed |
| `count-2-from-scrolled` | protocol | `ESC]720;2 BEL` from offset 3 | offset 5 | nothing changed (still 3) |
| `unmarked` | control | SGR reset | nothing changes | nothing changed |
| `ed3-control` | control | `ESC[3J` | saved lines erased | 30 → 0 rows, history generation raised, screen and cursor unchanged |
| `wheel-control` | control | one routed wheel notch | view moves up | offset 0 → 3, history unchanged |

The two controls prove the measured quantities can move: ED 3 changes the history observation and one generated wheel notch (the App's own routed pointer path, verified before and after against the
disposable fixture's ownership) changes the view position. The unmarked control and every setup read prove nothing moves spontaneously and that each experiment started from the same history and view.
After each scrolled experiment two `scroll-down` wheel notches return the view to the live line, because the next experiment's reset and refill can outrun the view's own clamp. A conclusive effect needs all controls and setups to hold; a partial implementation would be inconclusive, never a guess.

## What changed in the App

- `readPaneMetadata` gains an optional read-only `viewport` (`scrollOffset`, `maxScrollOffset`) read from the terminal view on its dispatcher. Older retained frames without it stay valid.
- The generated-input API (act permission, owned fixture only) now routes a **wheel notch while mouse reporting is off** to the view's own scrollback scroll: no bytes are sent, no link can activate, and the
  alternate-scroll keys remain refused for generated events. Every other pointer event still requires active mouse reporting. This is a deliberate change to the API's earlier "no synthetic host scroll"
  stance and was made so the viewport observation has a positive control.
- Owning tests: `MainWindowAgentMetadataTests` (view position observed, unchanged by every OSC 720 form) and `MainWindowGeneratedInputTests` (the wheel scrolls and sends nothing; press, release and move
  remain refused; the reporting-branch refusals are unchanged).

## Evidence and reproduction

- Measured: App `0.14.0+85efa784…` (Release sidecar built on the tree restacked after PR 1016 and the OSC 3008 batch, executable SHA-256 `f5814e51…9bf8`), scripts at `6dcfc318…`, source policy v67 (450 files), receipt schema 74, scope `owned-osc633-without-notification-suites-v5`
  (the notification suites are omitted and independently rederived from their own publications). Layered collector extension `ntilde-owned-osc720-grading-v1` over the left/right-margin one.
- Retained: [owned-run.zip](2026-11-17-osc720/owned-run.zip) (complete run, receipt, journals, run log, wheel actions, adapter bytes) and ten superseded runs (five failed attempts, four complete runs measured on earlier trees or with an earlier grading, and one restack run whose unmarked control failed because a scrolled view survived the next reset) in
  [superseded/](2026-11-17-osc720/superseded) with reasons in [attempts.json](2026-11-17-osc720/attempts.json). `node --test scripts/terminfo/osc720-publication.test.mjs` extracts the run, reissues the complete receipt
  with the current validators, rebuilds the report from the paired journal and compares it byte for byte.
- Policy: stock unchanged; the unchanged pinned bundle's two paths and the effect contract are three separate rows; OSC 721, painted pixels, remote/mux, Windows/Linux and a variant beyond the seven
  experiments are not claimed.

## Limits

The effect is "unsupported" for these exact byte forms on the macOS disposable pane, observed through the App's view position and stored history. It is not a measurement of urxvt, OSC 721, the `Pt < 0` forms
(urxvt moves the view toward the live line), mouse-reporting panes, the alternate screen, persistent or remote sessions, or any other platform. Implementing OSC 720 (either operation) would be a separate
production change with its own App-owned policy, since a viewport-moving escape sequence is an application-driven change of what the user is looking at.
