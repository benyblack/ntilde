# OSC 777 and OSC 99 notification qualification (2026-11-14)

Qualifies exactly two pinned terminfo.dev@4.0.0 cases (bundle SHA-256 `aa5f2bf9…e1f6`, probeHash `16a690f34c0e`): `extensions.osc777-notify` and
`extensions.osc99-kitty-notify`. **This batch changed production code**: before it the App ignored both sequences (the VT parser had no OSC 99 or OSC 777 route and the
exact-query witness `q07.extensions.osc99-kitty-notify` recorded matched silence). The batch implements a stated subset, then measures it.

## What the App now does (production, `AnsiParser.Notifications.cs`)

- **OSC 777** `notify;<title>[;<body>]` raises one titled notification; every other 777 subcommand (`precmd`, `preexec`, …) is ignored.
- **OSC 99** (kitty): metadata keys `i` (id), `d` (done, default 1), `p` (`title` by default, `body`, `?`) and `e` (`1` = base64). A notification is shown when its last chunk
  (`d` absent or `1`) arrives; chunks of one id concatenate; at most 8 unfinished ids, 8192 characters per notification and 4096 per chunk are held; a bad base64 or oversized
  chunk drops the notification; RIS discards unfinished ones. Every other key and the payload types `close`, `alive`, `icon` and `buttons` are ignored.
- **Query**: `OSC 99;i=<id>:p=?;` is answered `OSC 99;i=<id>:p=?;o=always:p=title,body` with the id reduced to the protocol's alphabet (64 characters at most); a notification chunk with an invalid or longer id is refused and **no** `a`, `c`,
  `u`, `w` or `s` key: actions, close reports, urgency, expiry and sounds are not implemented and not claimed.
- **Presentation**: both routes use the existing opt-in, focus-gated in-window toast (`Osc9NotificationsEnabled`, shown unless the user is looking at the pane). A title and a body
  show as given; a lone title or body is the message under the pane's own tab title, as an OSC 9 text is. Title and body are flattened to one line and capped (80 and 200
  characters). No OS desktop notification is raised. OSC 9 behavior is unchanged (its text sanitiser was extracted and is covered by the existing tests).

## What the pinned probes can say

| Case | Pinned probe | Reachable verdicts |
|---|---|---|
| `extensions.osc777-notify` | no termless function; terminal path writes `ESC[1;1H ESC[2K`, `OSC 777;notify;test;body BEL`, asks for the cursor, returns `unmeasuredCellResult` | **inconclusive only** (`insufficient-evidence`, evidence `query`) |
| `extensions.osc99-kitty-notify` | query-only: `OSC 99;i=<uuid>:p=?;ST`; supported when the reply payload carries exactly one `p=` field listing `title` | supported, or inconclusive when no matching reply arrives |

The OSC 777 pinned verdict is preserved: the unchanged pinned terminal code is replayed over the parent-observed cursor (it writes exactly the two pinned strings) and must reproduce the
documented constant. The OSC 99 verdict is decided by the unchanged stock probe running against the App in the same owned run and corroborated by the exact-query witness under the new
**query policy v3** (v1 and v2 runs keep the silence expectation); the notification layer only identifies it.

## Result

| Case | Pinned | Effect contract | Stock grade (this run) |
|---|---|---|---|
| `extensions.osc777-notify` | **inconclusive** by design (terminal path only) | **supported** (`ntilde-osc777-notification-toast-effect-v1`) | inconclusive |
| `extensions.osc99-kitty-notify` | **supported** (stock probe and `q07` witness, both unchanged code over the real reply) | **supported** (`ntilde-osc99-notification-toast-effect-v1`) | **supported** |

The stock collector in the same owned run (stock collector run `fede60a7a1af1ebddafdccd25653142a`) grades 88 supported / 1 unsupported / 166 inconclusive (`suiteComplete=false`), one more
supported than the pre-batch 87/1/167: that is the **only** stock change, and it comes from the App now answering the unchanged OSC 99 probe, not from the owned observations. The frozen
stock baseline file keeps its original grade for this id. No Core point is added.

## What was measured (disposable direct-local pane, 40×140)

Each experiment starts from a prepared baseline (progress withdrawn, toast hidden, run-bound title, cwd, nonce row), reads a distinguishable setup, feeds the bytes, reads the effect after a
completed DA fence barrier, and compares everything else (complete screen, cursor, cwd, links, rendition, status) with the bound state. The oracle is written from the two protocols and the
documented presentation, never from the parser. The toast is read from the pane metadata: visibility, title, message, auto-hide (waited out 8 s and read hidden) and restoration. The
parent defocuses the App by activating Finder and retains each focus action (`ntf-focus.json`); the window state at control time and at delivery is what is graded.

- **OSC 777 (11 experiments).** Protocol: title and body (BEL and ST), the pinned fixture bytes exactly (title `test`, message `body`), a body keeping later semicolons, a lone title. Controls: an
  unmarked feed and the other subcommands raise no toast. Policy (recorded, never graded): flattening of controls, the 80/200-character caps, empty notifications dropped, focus suppression.
- **OSC 99 (14 experiments).** Protocol: the default title payload, a title and body chunked with `d=0` (**a read between the chunks proves nothing shows yet**), chunk concatenation, base64,
  a body-only notification, BEL termination. Controls: an unmarked feed and ignored payload types (`close`, `alive`, `icon`, `buttons`) raise no toast. Policy: unknown keys ignored, bad base64
  dropped, a 4097-character chunk dropped, flattening, the title cap, focus suppression.

All protocol experiments matched and every control and delivery precondition held in the final run (the window was never active at delivery). Every policy record matched the documented
policy except that the two **focus-suppression** records are not applicable: the App's own window state (`IsActive`) was never active at the control or the delivery read, even though
the retained System Events answers show the App process frontmost after the parent's `refocus` (the `defocus` record also lists the App as frontmost: the process-level answer and the window
state differ, and only the window state is graded). The inactive-window controls, which gate the verdict, held at control time and at delivery for every protocol experiment. Suppression while
looking at the pane is covered by the existing policy tests only, not measured here.

**Decision rule.** Had the inactive-window delivery control not held at control time and at delivery, or a held chunk shown early, the case would have stayed inconclusive with the concrete
reason retained.

## Not claimed

No OS desktop notification delivery, no OSC 99 actions, close reports, icons, buttons, urgency, sounds, expiry or `o` occasions, no real-program emission, no remote/mux, Windows/Linux, hidden-pane
or unobserved focus-change behavior, and no stock grade for OSC 777. Seven older query witnesses (`sgr`, `cursor-style`, three `xtgettcap`, `xtgettcap list`, `sixel-geometry-report`) record
mismatches against their frozen original expectations, as the exact-query audit documents; they are unchanged by this batch.

## Identities

App 0.13.0 (operator supplied) built from `2eb47f2816a46967ea6bd6997ee876ebd01f8b56` (arm64 Release, executable SHA-256 `f5814e51a9d361d665c5818fe3dd385086f3eec0f789eb8715ad1e6931fb9bf8`, assembly SHA-256
`a6a3c535c75dd60d6da065663b4cd5195aba67e3cf3c9b3a2ea77e8e7928e3ff`); measured scripts the same commit; source policy `readonly-owned-source-graph-v53` (353 files; manifest SHA-256
`a93a162ec6b7c5672c7f45abeec096893c874a2553352d95946877cb5e27573a`); query policy `owned-exact-query-paired-raw-state-v3`; initial settings SHA-256
`84e131e90f92b8d85270b1ba93d7e07c8cbe5eee5bb3384229189c9c323a9d7d`; receipt schema 59; Node v24.22.2; owned run `f30a8ca8d2870622548524798624b669`; collected 2026-11-14T12:08Z on macOS. The
build is not binary-to-source attestation. Predecessor suites ran in the same full run and their receipt suffixes rederive from the retained bytes.

## Retained attempts ([superseded/](2026-11-14-notify/superseded/))

1. Completed every suite, then the parent-side rederivation refused it: the static OSC 777 replay listed its keys in a different order than the live replay (`Pinned OSC 777 result differs from
   its replay`). The notification effect contracts were already supported in that run. Fixed in the grader; kept as measured, not graded.
2. Completed with both effect contracts supported and the OSC 99 stock grade supported; superseded by a local Codex finding (P2): two OSC 99 ids sharing their first 64 characters were trimmed into
   one pending-notification key, so interleaved chunks could merge or drop each other. The parser now refuses an id that is out of the protocol's alphabet or longer than 64 characters instead of
   trimming it, so the measured App changed and the run was repeated. (An earlier local finding, a pending OSC 99 notification surviving RIS, was fixed before attempt 2.)

The final run is [owned-run.zip](2026-11-14-notify/owned-run.zip); `node --test scripts/terminfo/notify-publication.test.mjs` extracts it, re-issues the receipt, rebuilds the report from the
retained journal and compares the published projection.
