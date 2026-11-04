# OSC title payloads and display fallback (#772)

OSC 0 and OSC 2 now publish the exact bounded title payload to the host, including
an empty string, whitespace-only text, surrounding spaces, Unicode and embedded
semicolons. An empty request replaces the previous requested title instead of
leaving it stuck. Malformed requests without the separator and other OSC codes
do not alias the title callback. BEL and the seven-bit ST terminator share this
contract, including input split across parser calls. The existing string cap
and recovery behavior are unchanged.

The [xterm control-sequence description](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html#h2-Operating-System-Commands)
specifies title changes using the supplied text parameter. Its
[primary implementation](https://github.com/ThomasDickey/xterm-snapshots/blob/5c75ead9a75d4b2e666ca06fa94503b5766ed93f/misc.c#L4140)
handles empty OSC 0/1/2 by selecting its default title. This is evidence for
clearing the previous title request, not a requirement for an empty visible tab.

VT emits semantic title data and has no UI dependency. App stores that raw
request in `CurrentOscTitle`; its existing presentation policy uses profile/cwd
when the request is empty or whitespace-only. User tab renames keep precedence.
The mux title subscriber also receives the replacement payload. Standalone `mux ls`
and App `ls --all` show the command when that title is blank. The combined-host
listing tests for blankness after cleaning the title, then cleans/caps its command
fallback with the same host policy. JSON retains the exact raw title and command. Title-query replies, host icon presentation
and native window-manager presentation remain separately unqualified.

## XTWINOPS title stack (#830)

Vim sends `CSI 22 ; 0 t` on startup (`t_ST`) and `CSI 23 ; 0 t` on exit (`t_RT`).
VT now retains the current raw title (OSC 0/2) and icon name (OSC 0/1) and a
title stack that follows xterm's `xtermPushTitle`/`xtermPopTitle` in `misc.c`:

- `Ps` 0 or omitted saves/restores both, 1 the icon name, 2 the window title.
  A one-sided push copies the other half from the entry below (xterm's pop-time
  "try higher levels" lookup); with nothing below that half is absent and
  restoring it changes nothing.
- Depth 10 (`MAX_SAVED_TITLES`). A push onto a full stack discards the oldest
  entry; pops beyond the retained ten are silent, as xterm's cleared ring slots are.
  A pop on an empty stack is a no-op.
- A title restore raises the existing title callback with the exact saved raw
  text, so App storage, tab fallback and mux listings are unchanged. An empty
  saved title (including a save before any OSC 0/2) clears to the host fallback.
  Icon restores change VT state only; there is no host icon presentation.
- Only the bare spelling with at most three parameters is accepted. Other
  selectors, colon sub-parameters, prefixed forms and a nonzero third parameter
  (xterm's direct slot access) are ignored and leave the stack unchanged.
- Lifecycle: session-wide, so alternate-screen switches do not touch it. DECSTR
  keeps it, as xterm does. RIS clears it; xterm keeps it, but #830 specifies a
  clear, matching the OSC 30001 color stack. RIS does not retitle. Complete
  parser snapshots (`AnsiParserState`: `title`, `icon`, `title_stack`) carry
  it, so a mux attach or replay checkpoint restores what the tail later pops.
  Legacy snapshots import an empty title and stack.
- Titles longer than 8 Ki chars still reach the host in full but are not
  retained, so restoring them changes nothing. xterm itself ignores title
  requests over 1000 bytes. Imports clamp depth and length the same way.

Tests: `XtWinOpsTitleStackTests` (VT), the `xtwinops-title-stack` parity-corpus
stream, `HeadlessTitleStackTests` and `MuxCliTests` (mux attach snapshot and exact
raw listing), and `MainWindowAgentMetadataTests`/`MuxPaneTests` (tab label after
a local restore and after a reattach). No manual Vim session was recorded, and no
historical terminfo grade changed.

## Executable evidence

`OscTitleTests` cover exact empty/blank/padded/Unicode payload replacement for
OSC 0/2 with both terminators, every split boundary, following printable text,
cursor/cell preservation and malformed/other-code controls. Twenty cases fail
on the old parser; four negative controls already pass.

Mux CLI regressions send title changes through the real daemon parse queue and
flush that queue before listing. Empty/blank text falls back to the command;
padded nonblank titles and JSON remain exact. Both cleared-title cases fail on
the previous listing implementation while the padded-title control passes. Three
combined-host listing cases fail before the fix; afterward local and remote rows
show the cleaned command, including a title stripped to empty by sanitization.
Raw JSON metadata remains unchanged, including control/bidi characters in the
scripted command; those characters never execute or reach the formatted rows.

Four parser-to-App metadata regressions fail on the old parser. After the fix,
independent raw-title/display-title/actual-tab-text readback observes the new raw
request and the existing nonempty profile/cwd fallback. Selection and window
size stay unchanged. These headless tests use the established private fake-session
window fixture and do not launch a desktop app or collect owned runtime evidence.

The frozen [empty-title observation](2026-11-04-metadata-snapshot.md) explicitly
requires an empty visible tab label. That historical negative and its bytes stay
unchanged; this raw-request correction does not qualify that visible-label
expectation. A future owned case can independently test title clearing and the
source-backed fallback policy. Root owns any new measured-source inventory,
receipt or expectation version. Stock grades remain 78 supported / 0 unsupported /
177 inconclusive. Existing RGB-bold appearance and RIS observations remain frozen.
#772 stays open for the remaining exact variants and qualification policies.
