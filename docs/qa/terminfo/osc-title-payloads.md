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
fallback with the same host policy. JSON retains the exact raw title and command. OSC 1 icon-name state, title stacks, title-query
replies and native window-manager presentation remain separately unqualified.

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
