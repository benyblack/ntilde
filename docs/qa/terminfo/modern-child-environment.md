# Modern direct-local child environment contract

The [frozen Modern inputs](2026-11-15-modern-results/requirements-v1.json) mark
all four rows manual and retain their catalogue descriptions in
`upstream-snapshot.zip` (`features.json`). The [OSC 7 assessment](2026-11-24-modern-osc7-cwd-results/report.md)
is the predecessor, with 56 pass, 0 fail and 13 inconclusive across 69 rows.

| Frozen row | Retained catalogue description | Manual grading rule |
| --- | --- | --- |
| `device.env-colorterm` | `COLORTERM` advertises truecolor capability. The catalogue describes `truecolor` and `24bit` as conventional hints. | Pass when the clean child and the child of the sentinel launch both receive Ntilde's documented `truecolor` value. The two launches establish provision and override respectively. |
| `device.env-term-program` | `TERM_PROGRAM` identifies the particular terminal emulator. The catalogue mentions the companion version variable. | Pass when both children receive `Ntilde` under the same controls. The experiment measures only `TERM_PROGRAM`. |
| `device.env-no-color` | The user or a wrapper sets a nonempty `NO_COLOR`. Programs emitting ANSI colors consult that preference. | Pass for the terminal environment criterion when the clean child leaves it absent and the inherited child receives the exact nonempty user value. This measures availability of the user policy to child programs. Suppression of ANSI color is each program's responsibility and is outside the observation. |
| `device.env-vte-version` | `VTE_VERSION` identifies the packed VTE widget library version in VTE-based terminals. | A clean child with no variable corroborates inapplicability to Ntilde's Avalonia App and native PTY implementation. Retain inconclusive in the frozen totals. Unexpected presence in a clean child requires investigation and remains inconclusive for this VTE-specific criterion. |

The two isolated launches use the same sidecar executable, assembly and fixture
source bytes. The clean launch removes all four variables from the App's spawn
environment. The inherited launch supplies distinct, nonempty sentinels for
`COLORTERM`, `TERM_PROGRAM` and `NO_COLOR` and leaves `VTE_VERSION` absent.
The disposable direct-local profile executes Node with the captured worker
script as its first argument. Shell integration and session persistence are
disabled, and Node preload variables are removed. The worker samples its own
environment before its first request. The raw child JSON distinguishes absent,
empty and present values. The authenticated owner channel carries those bytes
as a string bound to the direct PTY ancestry and the isolated App's local pane.

Only a pair with valid input controls, App/worker lineage, profile identity,
terminal state restoration, source receipts and desktop lease cleanup can be
graded. An admitted mismatch for an applicable row yields a failure. Invalid
evidence yields no updated verdict. These observations replace only the four
rows' evidence in the successor publication, preserving the frozen criteria and
denominator.
