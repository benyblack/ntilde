# Ntilde Rich TUI baseline

The [frozen case list](requirements-v1.json) has **96 cases**. It follows the two identical feature lists on the [Rich TUI page](https://terminfo.dev/baseline/rich) captured on 2026-11-26. The [membership snapshot](upstream-membership.json) retains the page names and links. The catalogue in the existing Modern archive supplies the benchmark IDs by slug. Its three XTEST diagnostic rows for click, key and wheel were absent from the live lists. The catalogue material is attributed to Bjorn Stabell and licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).

The [evidence map](evidence-map.json) records the state of Ntilde main at `caea956de` by exact case ID. It carries the stock outcome, any owned inventory claims and their publications, and matching rows from the frozen Core, Modern and common-harness Core assessments. Related historical evidence is labeled by what it observed.

| Prior evidence at the recorded revision | Cases |
| --- | ---: |
| Owned inventory claim | 60 |
| Stock supported | 20 |
| Stock unsupported | 1 |
| Stock inconclusive | 68 |
| No stock grade | 7 |

The stock categories partition the 96 cases. An inventory claim can also have a stock outcome, so the inventory count overlaps the stock categories. The frozen Core, Modern and common-harness Core assessments contribute zero exact IDs to this list. The map is a record of existing evidence. Each Rich TUI probe still needs its own criterion applied before it can take a Rich verdict.

## First batch

The [Kitty keyboard assessment](kitty-report.md) records the bytes the child received for disambiguation, event reporting, alternate keys, all-key reporting and associated text. All five tested App effects were supported on the isolated Linux X11 run. Its separately retained query exchanges measure flag negotiation alongside the routed-input tests.

## Kitty graphics transmit and display

The [Kitty graphics assessment](../2026-11-26-rich-kitty-graphics/report.md) records the pinned transmit and display replies and the paired pixel captures of the 1 × 1 RGB fixture. It also rederives the historical numbered lifecycle and number-reuse cases.

## OSC 66 text sizing

The [OSC 66 assessment](../2026-11-26-rich-osc66/report.md) rederives the historical cursor grades, records the pinned width and scale replies on current main, and compares rendered ink against ordinary glyphs in a separate fixture.

## Sixel DA1 and geometry reporting

The [Sixel report assessment](../2026-11-26-rich-sixel-reports/report.md) rederives the retained replies and compares the exact child PTY results across a controlled resize and restoration. The [native Sixel raster qualification](../2026-11-26-sixel-native-fidelity/report.md) records the fixed pixel effect and links the earlier unsupported measurement.

## OSC 633 command-line and Cwd metadata

The [two-case OSC 633 assessment](../2026-11-26-osc633-metadata/report.md) qualifies E for a single command cycle and P for two reported-directory updates. Both owned effects were supported and both pinned probe verdicts were inconclusive.

The [aggregate reassessment](../2026-11-26-osc633-aggregate-reassessment/report.md) combines rederived A–D and E/P evidence from two App revisions. The bounded owned aggregate is supported, and the exact aggregate probe remains inconclusive.

## OSC 5522 clipboard paste

The [OSC 5522 assessment](../2026-11-27-osc5522-clipboard/report.md) replays the pinned mode query and measures a separate paste-token transfer on a disposable X11 clipboard. The pinned query and the owned paste both grade supported. The historical stock grade is unsupported.

## Font ligatures

The [font ligature assessment](../2026-11-27-rich-font-ligatures/report.md) records the owned glyph and partial-selection comparisons for two operators with a pinned Fira Code face. The pinned collector has no probe for this case.

## Kitty shell prompt clicks

The [Kitty prompt click assessment](../2026-11-27-rich-kitty-clicks/report.md) records exact child PTY frames for both coordinate modes and the primary and secondary prompt rows. The owned Linux X11 App effect is supported. The case is unprobed in the pinned bundle and has no stock grade.

## Vendor child environment

The [vendor environment assessment](../2026-11-27-rich-vendor-environment/report.md) covers iTerm2, Kitty, WezTerm and Windows Terminal identifiers. The measured macOS direct-local App path injected none of the 12 vendor names and preserved each synthetic parent value. All four cases are vendor-specific and inapplicable to Ntilde as producer. The pinned bundle leaves all four unprobed, and the stock baseline leaves them ungraded.

## OSC 176 app ID and OSC 666 terminal properties

The [two-case identity and property assessment](../2026-11-27-rich-identity/report.md) replays both inconclusive cursor probes and records the native foot app-ID and registered VTE property controls. The native app-ID effect is inapplicable to the measured Ntilde XWayland window. The registered property effect remains inconclusive on Ntilde.

Run `python3 scripts/terminfo-rich-baseline.py --check` to verify the list and map against the recorded Git revision. The checkout needs that revision in its local Git history.
