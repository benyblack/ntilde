# Exact query audit (#771)

The source audit starts from main `916f84232408614df58616f26c44671c8cfedb28`.
This is a source/contract audit of the eighteen Q07 spellings recorded by the
frozen triage, plus individually named Q12 settings. It does not replace any
published raw trace or grade. The frozen stock result remains **78 supported,
0 unsupported, 177 inconclusive**, with `suiteComplete=false`.

Current source disposition is checked at main
`2b8f4905cf06efeda44b17b81e03d8923655c6ef`. The foundations and their merged PRs
are mapped below. [PR #859][pr859] added the owned query collector;
[PR #864][pr864] published the first completed schema 11 collection with declared
App build source `630fe12606f24a0cf482b8756da32cf02c46ae72` and independently
retained adapter inputs at that commit. The operator-supplied build-source label
is not binary-to-source attestation.
The [dated report][measurement] preserves all 40 exact requests, 44 actions,
current provenance and original outcomes. Source/unit coverage is broader than
this one live run; neither replaces the other.

The live result is **38 matched / 2 mismatched contracts**: three matched positive
replies, nineteen matched valid failures, sixteen matched silence transactions,
and two actual positive replies that differ from the original negative
expectations. These counts are not feature-support scores. The two mismatches
and all historical inconclusive observations remain unchanged.

## Q07 dispositions

`ESC` is byte 1b, `BEL` is 07 and `ST` is `ESC \`. The original stock collector
appended a primary-DA sentinel to most requests; that independent reply is not a
reply to the requested feature. The notification identifier is a caller-selected
correlation value; the regression uses a fixed synthetic identifier.

| Frozen feature id | Exact request | Current disposition / scope | Regression / merged PR / v11 witness |
|---|---|---|---|
| `device.tertiary-da` | `CSI = c` | Implemented for default/numeric-zero Ps: `DCS ! \| 00000000 ST`, following xterm's zero site/serial fields. This constant is unassigned and nonunique, never a hardware/host identity. | [DA3 tests][da3-tests], [#814][pr814]; `q07.device.tertiary-da`: matched positive. |
| `device.xtgetxres` | `DCS + Q 7465726d4e616d65 ST` | Unavailable. VT has no X resource database; `termName` is not guessed from App settings or a host environment. | [silent tests][variant-tests], [#807][pr807]; `q07.device.xtgetxres`: matched silence. |
| `device.xtreportcolors` | `CSI # R` | Unavailable. Do not substitute the distinct Kitty runtime-color stack for xterm's palette-stack contract. | [silent tests][variant-tests], [#807][pr807]; `q07.device.xtreportcolors`: matched silence. |
| `device.xtwinops-18` | `CSI 18 t` | Implemented: `CSI 8 ; rows ; cols t`, from current VT buffer geometry. No host pixel metrics or mode-2048 subscription required. | [18t tests][window-tests], [#807][pr807]; `q07.device.xtwinops-18`: matched positive. |
| `device.xtwinops-20` | `CSI 20 t` | Unavailable exact query. [#835][pr835] retains raw icon-name state for its title stack, but does not add this report or a host icon-label service. | [silent tests][variant-tests], [#807][pr807]; `q07.device.xtwinops-20`: matched silence. |
| `device.xtwinops-21` | `CSI 21 t` | Unavailable exact query. [#835][pr835] retains raw window-title state for its stack; it does not implement this report or attest the actual presented host caption. | [silent tests][variant-tests], [#807][pr807]; `q07.device.xtwinops-21`: matched silence. |
| `editing.decrqcra` | `CSI 1;1;1;1;1;1 * y` | Silent by default. The report is implemented behind the `AllowRectangleChecksumReport` setting, off by default because a one-cell checksum lets a program read the screen back; with the default this spelling stays unanswered. The contract (pages, attributes, blanks, wide/combining cells, request id) is in [rectangle-checksum.md](rectangle-checksum.md). | [silent tests][variant-tests], [#807][pr807], [checksum tests](../../../tests/Ntilde.VT.Tests/RectangleChecksumTests.cs); `q07.editing.decrqcra`: matched silence (default settings); a run with the setting on owes and matched the `FFB2` reply ([qualification](2026-11-10-rectangle-checksum-qualification.md)). |
| `extensions.osc1337-capabilities` | `OSC 1337;RequestCapabilities BEL` | Unavailable exact spelling. iTerm2 image decoding does not establish a capability-list service. | [silent tests][variant-tests], [#807][pr807]; `q07.extensions.osc1337-capabilities`: matched silence. |
| `extensions.osc1337-cellsize` | `OSC 1337;ReportCellSize BEL` | Unavailable. A host report needs defined cell units and scaling, rather than a guessed physical/DIP conversion. Existing CSI 16 t is a different request. | [silent tests][variant-tests], [#807][pr807]; `q07.extensions.osc1337-cellsize`: matched silence. |
| `extensions.osc21-kitty-color` | `OSC 21;foreground=? ST` | Unavailable. Existing OSC 10/11 colors do not establish the separate Kitty named-color API. | [silent tests][variant-tests], [#807][pr807]; `q07.extensions.osc21-kitty-color`: matched silence. |
| `extensions.osc5-special-color` | `OSC 5;0;? BEL` | Unavailable. No separate special-color-slot model is claimed; indexed palette OSC 4 is a different service. | [silent tests][variant-tests], [#807][pr807]; `q07.extensions.osc5-special-color`: matched silence. |
| `extensions.osc701-locale` | `OSC 701;? BEL` | Unavailable. VT does not read the host/process locale or pretend it is the child's locale. | [silent tests][variant-tests], [#807][pr807]; `q07.extensions.osc701-locale`: matched silence. |
| `extensions.osc702-version` | `OSC 702 BEL` | Unavailable exact spelling. XTVERSION is supported independently; no rxvt extension identity is implied. | [silent tests][variant-tests], [#807][pr807]; `q07.extensions.osc702-version`: matched silence. |
| `extensions.osc776-cell-size` | `OSC 776 BEL` | Unavailable exact spelling. Do not infer this extension from CSI 16 t or fallback cell metrics. | [silent tests][variant-tests], [#807][pr807]; `q07.extensions.osc776-cell-size`: matched silence. |
| `extensions.osc7770-font-size` | `OSC 7770;? BEL` | Unavailable. Font settings belong to the presenting host, with no query contract here. | [silent tests][variant-tests], [#807][pr807]; `q07.extensions.osc7770-font-size`: matched silence. |
| `extensions.osc7777-font-window-size` | `OSC 7777;? BEL` | Unavailable. No combined host font/window service is claimed. | [silent tests][variant-tests], [#807][pr807]; `q07.extensions.osc7777-font-window-size`: matched silence. |
| `extensions.osc99-kitty-notify` | `OSC 99;i=<id>:p=?; ST` | Unavailable. Existing OSC 9 notification handling does not implement Kitty's correlated OSC 99 service or attest OS display. | [silent tests][variant-tests], [#807][pr807]; `q07.extensions.osc99-kitty-notify`: matched silence. |
| `extensions.sixel-geometry-report` | `CSI ? 2;1;0 S` | Implemented XTSMGRAPHICS (#872): `CSI ? 2;0;width;height S`, the text area in Sixel pixels capped at the 2000-pixel Sixel clip. Color registers and the other actions are covered below. It is never mistaken for scroll-up. | [XTSMGRAPHICS tests][xtsm-tests], [#872][issue872]; `q07.extensions.sixel-geometry-report`: matched silence before #872. |

The fifteen unavailable requests have exact silent/no-state-mutation
regressions in `QueryVariantAuditTests`, followed by an independent DA framing
check. (The sixteenth, the XTSMGRAPHICS geometry read, is answered since #872.) This is a scoped availability policy for these spellings, not a claim
that the broader feature families are absent or that missing replies are
negative capability scores. The positive DA3 contract is in
`TertiaryDeviceAttributeTests`. Further implementations require their own contracts
and fresh runtime evidence.

## Q12 settings and negative responses, individually

`DcsAndCsiRoutingTests` already established one DECRQSS rejection and one XTGETTCAP
rejection. `QueryVariantAuditTests` enumerates the unavailable settings below and
checks their complete rejection at every split in the input. The narrow positive
`r` contract is covered separately by `DecrqssMarginQueryTests`, and the `m` and
`SP q` reports by `DecrqssSettingReportTests`, and XTGETTCAP answers by
`TerminalCapabilityTests`. It also caps retained query text in a tiny fixture and
verifies a constant-length rejection and parser recovery after overlong payloads.
Apart from an XTGETTCAP name that has already been checked to be hex, no request
payload is reflected into a reply.

| Family | Requested setting/name | Current source disposition / exact reply | Regression / merged PR / v11 witness |
|---|---|---|---|
| DECRQSS | `r` (absolute vertical margins) | Implemented: `DCS 1$r top;bottom r ST`, exact complete request and standard ST. | [margin tests][margin-tests], [#819][pr819]; `q12.decrqss.vertical-margins`: matched positive. |
| DECRQSS | `m` (SGR) | Implemented: `DCS 1$r0;…m ST` for the active pen. | [setting tests][setting-tests], [#853][pr853]; `q12.decrqss.sgr`: actual `1$r0m`; original negative expectation mismatched. |
| DECRQSS | `SP q` (cursor style) | Implemented: `DCS 1$rPs SP q ST`; host-default intent reports `0`. | [setting tests][setting-tests], [#853][pr853]; `q12.decrqss.cursor-style`: actual `1$r0 q`; original negative expectation mismatched. |
| DECRQSS | `s` (left/right margins) | Implemented: `DCS 1$r Pl;Pr s ST`, absolute one-based margins, reported whether or not DECLRMM is set (the frozen Q12 observation below predates it). | `tests/Ntilde.VT.Tests/DecrqssLeftRightMarginTests.cs`; `q12.decrqss.horizontal-margins`: matched negative before this report. |
| DECRQSS | `"p` (conformance) | Unavailable: bounded `DCS 0$r ST`. | [negative tests][variant-tests], [#807][pr807]; `q12.decrqss.conformance`: matched negative. |
| DECRQSS | `"q` (protection) | Unavailable: bounded `DCS 0$r ST`. | [negative tests][variant-tests], [#807][pr807]; `q12.decrqss.protection`: matched negative. |
| DECRQSS | `t` (lines per page) | Unavailable: bounded `DCS 0$r ST`. | [negative tests][variant-tests], [#807][pr807]; `q12.decrqss.lines-per-page`: matched negative. |
| DECRQSS | `$\|` (columns per page) | Unavailable: bounded `DCS 0$r ST`. | [negative tests][variant-tests], [#807][pr807]; `q12.decrqss.columns-per-page`: matched negative. |
| DECRQSS | `*\|` (number of lines) | Unavailable: bounded `DCS 0$r ST`. | [negative tests][variant-tests], [#807][pr807]; `q12.decrqss.number-lines`: matched negative. |
| DECRQSS | `*x` (change extent) | Unavailable: bounded `DCS 0$r ST`. | [negative tests][variant-tests], [#807][pr807]; `q12.decrqss.change-extent`: matched negative. |
| DECRQSS | `unknown` | Bounded `DCS 0$r ST` rejection. | [negative tests][variant-tests], [#807][pr807]; `q12.decrqss.unknown`: matched negative. |
| DECRQSS | empty setting | Bounded `DCS 0$r ST` rejection. | [negative tests][variant-tests], [#807][pr807]; `q12.decrqss.empty`: matched negative. |
| XTGETTCAP | TN: `544e` | Bounded `DCS 0+r ST` rejection; deliberately not answered (its value depends on TERM). | [negative tests][variant-tests], [#807][pr807]; `q12.xtgettcap.terminal-name`: matched negative. |
| XTGETTCAP | Co: `436f` | Implemented: `DCS 1+r436f=323536 ST` (256). | [capability tests][capability-tests], [#861][pr861]; `q12.xtgettcap.color-count`: matched negative before #861. |
| XTGETTCAP | colors: `636f6c6f7273` | Implemented: `DCS 1+r636f6c6f7273=323536 ST` (256). | [capability tests][capability-tests], [#861][pr861]; `q12.xtgettcap.colors`: matched negative before #861. |
| XTGETTCAP | RGB: `524742` | Implemented flag: `DCS 1+r524742 ST`. | [capability tests][capability-tests], [#861][pr861]; `q12.xtgettcap.rgb`: matched negative before #861. |
| XTGETTCAP | kcuu1: `6b63757531` | Bounded `DCS 0+r ST` rejection. | [negative tests][variant-tests], [#807][pr807]; `q12.xtgettcap.cursor-up-key`: matched negative. |
| XTGETTCAP | TN;Co list: `544e;436f` | One reply per name, in order: `DCS 0+r ST` for `TN`, then `DCS 1+r436f=323536 ST`. | [capability tests][capability-tests], [#861][pr861]; `q12.xtgettcap.list`: matched negative before #861. |
| XTGETTCAP | unknown name: `6e6f742d612d6361706162696c697479` | Bounded `DCS 0+r ST` rejection. | [negative tests][variant-tests], [#807][pr807]; `q12.xtgettcap.unknown`: matched negative. |
| XTGETTCAP | odd-length hex: `5` | Bounded `DCS 0+r ST` rejection. | [negative tests][variant-tests], [#807][pr807]; `q12.xtgettcap.odd-hex`: matched negative. |
| XTGETTCAP | invalid hex: `zz` | Bounded `DCS 0+r ST` rejection. | [negative tests][variant-tests], [#807][pr807]; `q12.xtgettcap.invalid-hex`: matched negative. |
| XTGETTCAP | empty list | Bounded `DCS 0+r ST` rejection. | [negative tests][variant-tests], [#807][pr807]; `q12.xtgettcap.empty`: matched negative. |

All v11 identifiers refer to the unchanged full query report in the
[publication JSON][measurement-json] and [complete archive][measurement-zip].
A valid failure is negative query evidence, never a terminal-name/color/key
success. The SGR/cursor-style mismatch is a versioned expectation difference,
not proof of a terminal defect or a retroactive pass. Likewise, the v11
`Co`, `colors`, `RGB` and list observations predate #861; a fresh run would
differ from their v1 negative expectations, and no grade is changed here.

No terminal database or key string is synthesized from a guessed `$TERM`.

## Character-geometry contract

[The xterm reference](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html)
defines `CSI 18 t` and its `CSI 8 ; height ; width t` reply in characters.
`ExactWindowQueryTests` verifies rows-before-columns, current resize state,
absence of a subscription requirement, independence from unusable pixel metrics,
all split points and byte-for-byte complete-buffer-state preservation. A
deterministic concurrent-resize regression verifies that `Process` already holds
the buffer batch write lock through the paired geometry read and reply; resize
waits on that same lock, then the next query reports the new dimensions. Leader or
intermediate changes, extra/colon parameters and malformed forms stay silent.
`HeadlessTerminalSessionTests` independently checks the authoritative mux parser
replies through its dedicated child writer after an ordered resize, without
mode 2048. This uses a scripted transport, not a real child.
The catalog has an executable `CSI:18t` case; the matrix and embedded report name
this limited scope. Existing CSI 14/16 pixel reports retain their behavior.

The response reports the parser's current VT geometry. It is not an independent
measurement of kernel PTY size, native window pixels or child receipt. The v11
run separately records kernel geometry and exact child reply bytes for its
40×140 baseline; it does not qualify every resize or pixel boundary.

## Tertiary device-attribute contract

[The DEC DA3 reference](https://vt100.net/docs/vt510-rm/DA3.html) defines a
DECRPTUI response containing four hexadecimal byte pairs.
[The xterm reference](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html)
explicitly uses zero site/serial fields. Ntilde returns the fixed seven-bit frame
`DCS ! | 00000000 ST` for default or numeric-zero Ps, including leading-zero
representations and seven-bit/C1 CSI inputs. No host name, hardware serial,
network address, random/persistent identifier or process environment is read.
This is an unassigned, nonunique compatibility identifier; it does not assert
hardware manufacture, a unique terminal, or complete VT400 conformance.

`TertiaryDeviceAttributeTests` pins exact framing, one fixed response, every input
split, state preservation, DA1/DA2 separation and unsupported/malformed forms.
Nonzero Ps, extra/colon parameters, intermediate changes and unknown leaders
remain silent. The App hardening sweep retains `CSI = 1 c` as a negative; it is
not broadened to exempt every `=` device-attribute spelling. The shared capability
catalog and MCP explainer agree on default/zero versus ignored other parameters.
The mux test checks the canonical reply at its scripted child writer. The
separate v11 `q07.device.tertiary-da` transaction retains that exact all-zero
reply from the owned local child. The broader zero/C1/malformed forms retain
unit coverage; they were not all replayed in the live run. Historical no-response
observations remain unchanged.

## DCS framing prerequisite

DCS accumulation now records discarded input independently of the retained prefix.
The marker survives chunks, complete parser-state JSON and import clipping; an
active legacy snapshot without this marker is conservatively incomplete. Exact
cap-length input remains complete until a character is actually discarded.
Termination, malformed-escape abandonment and a new DCS clear the marker.
`DcsTruncationStateTests` pins these contracts, including the production default
cap. `StringSequenceCapTests` preserves existing truncated SIXEL decoding after
state transfer. This prerequisite alone adds no positive DECRQSS/XTGETTCAP
setting. The separate margin-report contract below requires completeness before
accepting a retained prefix; all other current bounded failures remain negative
evidence.

## Narrow DECRQSS margin report

The [xterm control reference](https://invisible-island.net/xterm/ctlseqs/ctlseqs.txt)
and [xterm-412 implementation](https://github.com/ThomasDickey/xterm-snapshots/blob/5c75ead9a75d4b2e666ca06fa94503b5766ed93f/misc.c#L4896)
agree on exact `r`, one-based current top/bottom margins, and status 1 for success
versus 0 for failure. The implementation's DECSTBM branch formats its owned margin
values, then its common response branch writes that status and `$r` framing.
The [VT510 DECRPSS manual](https://vt100.net/docs/vt510-rm/DECRPSS.html)
documents the opposite status polarity. Ntilde deliberately follows xterm and its
existing `0$r` failures; this is not a claim of the manual's full DEC conformance.
The [DECSTBM description](https://vt100.net/docs/vt510-rm/DECSTBM.html)
identifies the current page and full-page default margins.

Only exact `$qr` with a standard ST terminator succeeds. Seven-bit and decoded C1
introducers/ST use the actual parser paths; the reply is always seven-bit.
Existing BEL recovery stays a bounded negative. Completeness, valid margins and
current geometry are checked under the parser's existing batch write lock, so
origin mode does not relativize the reported values. Query handling does not
change cells, cursor, pen, margins, saved state, modes or active screen. Resize,
reset and screen changes are observed from the resulting VT state. A single-row
terminal reports its actual `1;1` region. No host geometry or terminal database is
consulted. The v11 `q12.decrqss.vertical-margins` transaction separately retains `1$r2;6r`
with independently captured absolute margins and origin mode. Its exact packet
is a live witness; all split points, alternate terminators, malformed forms and
state-transfer cases remain unit scope. Other Q12 settings and frozen grades
are not promoted by that witness.

## DECRQSS SGR and cursor-style reports

Added for #844 under the same exact-request, standard-ST and completeness rules as
`r`, and the same xterm status polarity.

- **`m`** follows xterm's report: a leading `0`, then the set attributes (`1 2 3 4
  5 7 8 9 53`) and colors. Underline styles other than straight use the `4:n`
  subparameter, the only form they have. Extended colors use the semicolon
  `38/48/58;5;n` and `;2;r;g;b` forms, which every SGR parser accepts. Palette
  indexes 0-15 report as the short ANSI codes, and theme-snapped truecolor reports
  the index the pen actually stores. Replaying a report on a terminal with a
  different pen reproduces the original state byte for byte (tested).
- **`SP q`** reports `0` while the host's configured cursor is shown and xterm's
  1-6 shape-and-blink code for an application override. VT does not know the
  host's configured shape, so it does not translate the default into 1-6. To make
  the saved value restore exactly, DECSCUSR 0 now returns to the host's configured
  cursor (kitty, foot, WezTerm, Ghostty, Alacritty and Windows Terminal do the
  same); before, it forced a blinking block.

Neither query changes terminal state in the owning regressions. The daemon
parser answers them for persistent sessions. The fresh v11 run retains exact
`1$r0m` and `1$r0 q` replies in pristine state with paired exposed-state checks.
Its original pure-model v1 expected `0$r`, so both observations remain mismatched.
Nondefault pen/cursor permutations above are source/unit evidence rather than
claims that this one live run exercised them. The existing cursor/text-pixel
`CSI 0 q` negatives likewise retain their earlier `isDefault=false` expectations;
#853 now restores host-default intent. No published grade or expectation changes.

## XTGETTCAP capability reports

XTGETTCAP (#844) answers from `TerminalCapabilities` in `Ntilde.VT`, the one table of
what Ntilde advertises beyond `xterm-256color`. The opt-in `xterm-ntilde` terminfo
entry is generated from the same table, so the two cannot disagree. The table holds
`Co`/`colors` (256), the `RGB`, `Tc`, `Su` and `hs` flags, and the strings `setrgbf`,
`setrgbb`, `Smulx`, `Setulc`, `Sync`, `Ss`, `Se`, `Enmg`, `Dsmg`, `Clmg`, `Cmg`, `Ms`,
`tsl` and `fsl`. The terminfo
entry leaves out `Co` (a termcap name), `RGB` (ncurses would read it as direct-color
`setaf`) and `Ms` (setting- and session-dependent); see
`packaging/terminfo/README.md`.
`TerminalCapabilityTests` expands every string value the way curses does and runs it
through the parser, checking the pen, mode, margins, cursor style, clipboard write or
title it claims to produce; each entry also lists the owning tests for that feature.

Replies follow kitty and foot: one per name, in request order. A string or number
is reported as `DCS 1 + r name=value ST` with the value as uppercase hex of its
compiled bytes (real ESC and BEL, `%` parameters left unexpanded); a flag as
`DCS 1 + r name ST`; a name not in the table as xterm's bare `DCS 0 + r ST`. The
name is echoed as the request spelled it, which is safe because the whole list is
checked first. `TN` is deliberately not answered: its honest value is the TERM the
session uses, which VT does not know. Names are case-sensitive (`co` and `kcuu1`
get `DCS 0 + r ST`). A list that is not entirely valid (truncated, BEL-terminated,
odd-length or non-hex, an empty name, a name that does not decode to printable
ASCII, a name over 32 characters, or more than 16 names) gets one `DCS 0 + r ST`
and nothing else, even when it also holds valid names. `Ms` is answered only while
`AnsiParser.ClipboardWriteAvailable` says the host accepts OSC 52 writes; headless
and mux parsers leave it unset, so persistent sessions never advertise it. Every
input split, complete JSON parser-state restore, the four DCS framings and the
daemon parser are covered. A [manual Neovim 0.12.6 check](2026-11-05-neovim-capability-check.md)
confirms the replies from a real application: Neovim's startup queries turn truecolor
on, and `Ms` follows the setting and is absent in persistent panes. The check doesn't
change the published grades or observations.

## XTSMGRAPHICS Sixel attributes

XTSMGRAPHICS (#872) follows xterm's handler and reports the Sixel decoder's real
limits. Only the exact three-parameter form `CSI ? Pi ; Pa ; Pv S` is answered; any
other shape stays silent, as in xterm. Success is `CSI ? Pi ; 0 ; Pv S`, and a failure
is `CSI ? Pi ; Ps S` with no value (1 unknown item, 2 unknown action, 3 failed action).

- **Color registers (`Pi` 1):** every image may define registers 0 to 4095. Read, reset
  and read-maximum report `4096`. The count is fixed, so a set succeeds only for `4096`;
  notcurses' startup attempts to set 256 and 1024 get `Ps` 3 and change nothing.
- **Sixel geometry (`Pi` 2):** read reports the text area in Sixel pixels, capped at the
  2000-pixel clip per side. That is columns and rows times the cell size Sixel placement
  uses, so an image exactly that wide fills the pane. It is the same text area `CSI 14 t`
  reports (device pixels on a scaled display, #876), capped at 2000. Read-maximum reports `2000;2000`.
  Reset and set fail with `Ps` 3, as in xterm, because the window sets the size.
- **ReGIS (`Pi` 3) and other items:** `Ps` 1.

Nothing changes terminal state, and the probe never scrolls. The daemon parser answers
for persistent sessions using the window's cell size. A Rendering test pins the
register count to what the decoder accepts. The frozen v11 silence observation and
the grades are unchanged.

A manual check with notcurses 3.0.17 (`notcurses-info`, Homebrew, macOS 27.0.1 arm64,
140 x 41 cells on a 2x display) recorded what its startup receives. It sends
`?1;3;256S`, `?1;3;1024S`, `?2;1;0S` and `?1;1;0S`. Released 0.13.0 answered none of
them. This branch answered `?1;3S` twice, then `?2;0;2000;1517S` and `?1;0;4096S`, the
same in a direct pane and a persistent pane, where the daemon answers. notcurses
prefers Kitty graphics where Ntilde accepts them, so this check confirms what it
learns, not Sixel drawing based on it.

## Issue acceptance evidence

| #771 requirement | Recorded evidence |
|---|---|
| Disposition for every audited query, with regression/PR | The 18 Q07 and 22 named Q12 rows above link the owning tests, merged implementation/policy PRs and exact v11 observation IDs. Deliberate unavailability is an explicit policy, not a missing-reply support score. |
| Implemented spellings have framing, correlation, bounds and malformed coverage | [18t][window-tests], [DA3][da3-tests], [DCS completeness][dcs-tests], [margins][margin-tests], [SGR/cursor style][setting-tests] and [XTGETTCAP][capability-tests] cover their exact families and malformed/truncated variants. Requests without a protocol request ID do not acquire an invented one: the live serialized transaction uses independent paired ordering fences and owning App-state reads. |
| Fresh owned replies/current provenance; historical observations unchanged | [#859][pr859] supplies the bounded driver; [#864][pr864] retains its genuine 630fe126 collection, all 16 receipt bindings and 74 source files. The [full report][measurement] distinguishes observed replies, silence, negative responses and v1 mismatches, and retains the older grades unchanged. |

The fresh collection has 110 validated prior input/image challenge pairs followed
by 44 distinct action pairs (40 queries and four fixture controls), healthy seals
and verified cleanup/source removal.
The state API explicitly leaves reverse-video memory unobserved. Owner and pane
checks are point-in-time facts, not a hardware identity, reconnect guarantee or
atomic PTY lease. Operator version/source hashes are not binary-to-source
attestation. This evidence map addresses the issue's finite audit/contract/runtime
scope; it does not require every deliberately unavailable extension to be added,
nor does it claim complete terminal-family conformance.

[pr807]: https://github.com/benyblack/ntilde/pull/807
[pr814]: https://github.com/benyblack/ntilde/pull/814
[pr816]: https://github.com/benyblack/ntilde/pull/816
[pr819]: https://github.com/benyblack/ntilde/pull/819
[pr835]: https://github.com/benyblack/ntilde/pull/835
[pr853]: https://github.com/benyblack/ntilde/pull/853
[pr861]: https://github.com/benyblack/ntilde/pull/861
[pr859]: https://github.com/benyblack/ntilde/pull/859
[pr864]: https://github.com/benyblack/ntilde/pull/864
[issue872]: https://github.com/benyblack/ntilde/issues/872
[variant-tests]: ../../../tests/Ntilde.VT.Tests/QueryVariantAuditTests.cs
[xtsm-tests]: ../../../tests/Ntilde.VT.Tests/XtsmgraphicsTests.cs
[window-tests]: ../../../tests/Ntilde.VT.Tests/ExactWindowQueryTests.cs
[da3-tests]: ../../../tests/Ntilde.VT.Tests/TertiaryDeviceAttributeTests.cs
[dcs-tests]: ../../../tests/Ntilde.VT.Tests/DcsTruncationStateTests.cs
[margin-tests]: ../../../tests/Ntilde.VT.Tests/DecrqssMarginQueryTests.cs
[setting-tests]: ../../../tests/Ntilde.VT.Tests/DecrqssSettingReportTests.cs
[capability-tests]: ../../../tests/Ntilde.VT.Tests/TerminalCapabilityTests.cs
[measurement]: 2026-11-05-query-observations.md
[measurement-json]: 2026-11-05-query-observations.json
[measurement-zip]: 2026-11-05-query-observations-evidence.zip
