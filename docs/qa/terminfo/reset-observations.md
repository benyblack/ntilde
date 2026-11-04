# Reset observations and legacy hints (#773)

RIS is a reset of terminal state, not a report of which other protocols work.
This document separates the legacy Q13 collector entries from concrete screen
and state observations. The issue remains open until fresh owned runtime evidence
and its cleanup/source receipt are retained.

## What the legacy entries actually measured

The frozen [triage](2026-11-02-inconclusive-triage.md) lists both entries as
inconclusive. A separate source audit inspected the retained
`collector-adapter.mjs` in [the 2026-11-04 source-snapshot archive](2026-11-04-metadata-snapshot-evidence.zip)
(SHA-256 `84e99c8c739701f7b06a7d302e3d54459b00cad6f2bed52b212ef78f58ae2806`).
That archive is a later measurement, not the original triage artifact.

| Entry | Exact source behavior | Disposition |
| --- | --- | --- |
| `device.term-features` | Reads only whether `process.env.TERM_FEATURES` is present, emits no request, and returns `pass: false`, `evidence: legacy`, `insufficient-evidence` regardless of presence. | An unverified environment hint. It neither tests nor advertises a terminal capability; setting the variable cannot make it a measured pass. |
| `reset.method` | The parser-only callback writes `Hello World`, calls `ctx.reset()`, and compares cursor/text. The App callback instead returns an unavailable-method note and `insufficient-evidence`; it does not invoke a reset or read state. | A legacy collector API gap, not evidence about RIS. Its empty App trace cannot qualify clearing or cursor restoration. |

Concrete replacements are scoped observations: exact query replies in #771,
paired cells/modes/history and newly written witnesses in the owned saved-state
suite, and independent owning VT/mux/replay regressions below. None of these
turns the broad legacy hint into a support score.

## Reproduced RIS screen boundary

The [committed 2026-11-03 saved-state measurement](2026-11-03-saved-state.md)
qualified eight cases and recorded a controlled RIS negative: resetting while
alternate was active exposed the old main text. That report and all its bytes
remain frozen.

The owning regression seeds distinct text, graphemes, links, underline metadata,
row wrapping, history and saved pending-wrap/pen banks on both screens, without
using the reset under test for setup. Four combinations cover main/alternate
entry and inherited autowrap on/off. Each fails on the old implementation.

The corrected buffer returns to main, defaults its current and saved pens and
runtime colors, then performs the existing full clear. Both retained screen
banks become blank with theme-following default cells. Row wrap/authored extents
and side tables are cleared in place, preserving row identities. Resized main
screens use the authoritative live viewport rather than a stale detached array;
both resize contexts have focused regressions. Sized-text
storage is pruned after both banks are clear. Saved cursor banks, pending wrap,
charset witnesses and the wrap-on host policy are checked independently.

Full clear still owns image retirement. A separate regression proves both
screens' image handles retire exactly once and remain protected by an active
snapshot pin. Complete recorder/replay cuts and mux v1/v2 output/reattach tests
check that cleared banks are retained across transport boundaries.

These are deterministic domain/transport observations. They do not by themselves
complete the disposable App's runtime reset receipt, qualify every terminal
power-on option or create an App `reset()` API. The existing fixed owned
`saved.reset.full-both-banks` plan can supply the fresh runtime comparison after
its source/provenance upgrade is coordinated.

## Initial-state policy and provenance

The [VT100 user guide](https://vt100.net/docs/vt100-ug/chapter3.html)
defines RIS in terms of terminal power-on state. Ntilde's established soft/full
reset policy restores autowrap on, matching the xterm resource-default approach
rather than the physical VT100's wrap-off default; the
[xterm implementation](https://github.com/ThomasDickey/xterm-snapshots/blob/master/charproc.c)
documents that distinction. This slice preserves that policy and tests both
inherited wrap states.

Published stock remains 78 supported / 0 unsupported / 177 inconclusive,
`suiteComplete=false`. The historical RIS, RGB-bold and title observations and
ungraded reverse-wrap diagnostic are unchanged. A later fresh observation must
name its own source and cleanup evidence; it cannot rewrite those records.
