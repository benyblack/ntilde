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

Run `python3 scripts/terminfo-rich-baseline.py --check` to verify the list and map against the recorded Git revision. The checkout needs that revision in its local Git history.
