# Exact query audit (#771)

The source audit starts from main `916f84232408614df58616f26c44671c8cfedb28`.
This is a source/contract audit of the eighteen Q07 spellings recorded by the
frozen triage, plus individually named Q12 settings. It does not replace any
published raw trace or grade. The frozen stock result remains **78 supported,
0 unsupported, 177 inconclusive**, with `suiteComplete=false`.

The protocol foundations add the VT-owned `CSI 18 t` report and an unassigned
DA3 unit-id report. Fresh owned
runtime query receipts and remaining implementations are separate work; #771
stays open. Source/unit evidence below is not a live terminal measurement.

## Q07 dispositions

`ESC` is byte 1b, `BEL` is 07 and `ST` is `ESC \`. The original stock collector
appended a primary-DA sentinel to most requests; that independent reply is not a
reply to the requested feature. The notification identifier is a caller-selected
correlation value; the regression uses a fixed synthetic identifier.

| Frozen feature id | Exact request | Current disposition / scope |
|---|---|---|
| `device.tertiary-da` | `CSI = c` | Implemented for default/numeric-zero Ps: `DCS ! &#124; 00000000 ST`, following xterm's zero site/serial fields. This constant is unassigned and nonunique, never a hardware/host identity. |
| `device.xtgetxres` | `DCS + Q 7465726d4e616d65 ST` | Unavailable. VT has no X resource database; `termName` is not guessed from App settings or a host environment. |
| `device.xtreportcolors` | `CSI # R` | Unavailable. Do not substitute the distinct Kitty runtime-color stack for xterm's palette-stack contract. |
| `device.xtwinops-18` | `CSI 18 t` | Implemented: `CSI 8 ; rows ; cols t`, from current VT buffer geometry. No host pixel metrics or mode-2048 subscription required. |
| `device.xtwinops-20` | `CSI 20 t` | Unavailable. Icon-label state is not modeled by the existing title callback; OSC 1/title distinctions belong to the #772 audit. |
| `device.xtwinops-21` | `CSI 21 t` | Unavailable. Actual host title state is not a VT buffer field. Do not echo stale App captions or invent restored titles. |
| `editing.decrqcra` | `CSI 1;1;1;1;1;1 * y` | Unavailable. A rectangular checksum contract must specify pages, attributes, blanks, wide/combining cells and request-id correlation before implementation. |
| `extensions.osc1337-capabilities` | `OSC 1337;RequestCapabilities BEL` | Unavailable exact spelling. iTerm2 image decoding does not establish a capability-list service. |
| `extensions.osc1337-cellsize` | `OSC 1337;ReportCellSize BEL` | Unavailable. A host report needs defined cell units and scaling, rather than a guessed physical/DIP conversion. Existing CSI 16 t is a different request. |
| `extensions.osc21-kitty-color` | `OSC 21;foreground=? ST` | Unavailable. Existing OSC 10/11 colors do not establish the separate Kitty named-color API. |
| `extensions.osc5-special-color` | `OSC 5;0;? BEL` | Unavailable. No separate special-color-slot model is claimed; indexed palette OSC 4 is a different service. |
| `extensions.osc701-locale` | `OSC 701;? BEL` | Unavailable. VT does not read the host/process locale or pretend it is the child's locale. |
| `extensions.osc702-version` | `OSC 702 BEL` | Unavailable exact spelling. XTVERSION is supported independently; no rxvt extension identity is implied. |
| `extensions.osc776-cell-size` | `OSC 776 BEL` | Unavailable exact spelling. Do not infer this extension from CSI 16 t or fallback cell metrics. |
| `extensions.osc7770-font-size` | `OSC 7770;? BEL` | Unavailable. Font settings belong to the presenting host, with no query contract here. |
| `extensions.osc7777-font-window-size` | `OSC 7777;? BEL` | Unavailable. No combined host font/window service is claimed. |
| `extensions.osc99-kitty-notify` | `OSC 99;i=<id>:p=?; ST` | Unavailable. Existing OSC 9 notification handling does not implement Kitty's correlated OSC 99 service or attest OS display. |
| `extensions.sixel-geometry-report` | `CSI ? 2;1;0 S` | Unavailable XTSMGRAPHICS spelling. Sixel layout/decoding is not a maximum-geometry attribute contract. The request must not be mistaken for scroll-up. |

The sixteen unavailable requests have exact silent/no-state-mutation
regressions in `QueryVariantAuditTests`, followed by an independent DA framing
check. This is a scoped availability policy for these spellings, not a claim
that the broader feature families are absent or that missing replies are
negative capability scores. The positive DA3 contract is in
`TertiaryDeviceAttributeTests`. Further implementations require their own contracts
and fresh runtime evidence.

## Q12 negative responses, individually

`DcsAndCsiRoutingTests` already established one DECRQSS rejection and one XTGETTCAP
rejection. `QueryVariantAuditTests` now enumerates each of these requests and
checks the complete rejection at every split in the input. It also caps retained
query text in a tiny fixture and verifies a constant-length rejection and parser
recovery after overlong payloads. No request payload is reflected into a reply.

| Family | Requested setting/name | Disposition and exact reply |
|---|---|---|
| DECRQSS | `m` (SGR), `r` (top/bottom margins), `s` (left/right margins), `SP q` (cursor style), `"p` (conformance), `"q` (protection), `t`, `$|`, `*|`, `*x` | Each unavailable setting receives `DCS 0 $ r ST`. A response rejecting SGR is not a successful SGR-state report. |
| DECRQSS | Unknown and empty setting | Same bounded `DCS 0 $ r ST` rejection. |
| XTGETTCAP | `TN`, `Co`, `colors`, `RGB`, `kcuu1`, and the `TN;Co` list (each name hex-encoded) | Each receives `DCS 0 + r ST`. A valid failure is preserved as negative query evidence, not a successful terminal-name/color/key claim. |
| XTGETTCAP | Unknown name, odd-length or invalid hex, empty list | Same bounded `DCS 0 + r ST` rejection. |

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
measurement of kernel PTY size, native window pixels or child receipt. Those
boundaries require separate owned runtime evidence.

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
The mux test checks the canonical reply at its scripted child writer. This is not
a fresh owned/native child measurement, and historical no-response observations
remain unchanged.
