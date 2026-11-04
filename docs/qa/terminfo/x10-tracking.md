# X10 compatibility tracking (#769)

DECSET 9 selects X10 compatibility tracking: only presses of the three ordinary
buttons are reported, without modifier bits. It is distinct from normal tracking
(1000), button-event tracking (1002) and any-event tracking (1003). The existing
`MouseModeX10` property and `m_x10` snapshot field retain their historical 1000
meaning; the new `MouseModeX10PressOnly` / `m_9` fields represent mode 9.

The parser selects one tracking protocol at a time. Setting another replaces it;
resetting any recognized tracking selector disables tracking, matching xterm's
`really_set_mousemode` behavior. Coordinate encoding is independent: 1006 selects
SGR coordinates without selecting a tracking protocol. Unsupported highlight,
UTF-8, urxvt and pixel-coordinate modes remain outside this slice.

X10 suppresses releases, wheel events and motion, even while a button is held.
Presses use the existing production coordinate conversion and byte-preserving
legacy transport; coordinates past 223 retain the existing clamp policy. With
SGR encoding, the same press-only policy uses decimal coordinates. Frame routing
and native admission are covered by the preceding byte-transport foundation.

VT owns the session-wide tracking state. Clone and complete terminal snapshots
preserve it; new legacy replay snapshots include `m9`, and missing historical
fields mean mode 9 is off. Restoring a legacy mode-9 snapshot replaces any live
tracking protocol. DECSTR and cursor/screen switches preserve tracking; RIS and
the existing fresh-prompt policy clear it. No host setting is added.

The mux text renderer mirrors tracking only while geometry is unclipped. Every
tracking reset is emitted before enabling the desired protocol, so a later reset
cannot immediately disable the new selection. This also fixes that ordering for
normal/button/any tracking. Historical snapshots containing multiple older flags
keep the previous effective priority (any, button, normal). Foreign-console
input decoding and native pointer delivery are separate qualification scopes.

## Evidence boundary

`X10MouseTrackingTests` cover fragmented mode/query framing, mutual exclusion,
reset/screen/prompt policy, clones, complete snapshots and actual recorder/replay
cuts. All five initial query/selection cases fail on the old parser.
`X10MouseRoutingTests` exercise the production press/coordinate route and reject
nonpress inputs; the raw POSIX child fixture independently receives the exact
high-coordinate frame for modes 9, 1000 and 1002. The mux renderer test feeds
emitted transitions through a real outer parser, including clipping, invalidation
and exit cleanup.

This is a protocol foundation. The bounded generated-event owned input fixture,
its retained action/geometry/byte evidence and restoration receipt still belong
to #769. Windows and actual remote/native-pointer runtime qualification remain
in #775. No published stock or owned measurement is regraded.

Sources: [xterm mouse protocols](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html#h2-Mouse-Tracking),
[xterm mode selection](https://github.com/ThomasDickey/xterm-snapshots/blob/master/charproc.c),
[xterm mouse encoding](https://github.com/ThomasDickey/xterm-snapshots/blob/master/button.c).
