# Application-keypad foundation

This is the first implementation slice of #769. It adds input-mode state and
deterministic routed-event coverage. It does not qualify real child delivery,
mouse events, native NumLock policy or another desktop platform.

VT tracks DECKPAM (`ESC =`), DECKPNM (`ESC >`) and their DECNKM private-mode alias
(`CSI ? 66 h/l`). DECRQM reports actual state. Numeric is the initial/reset value;
clones, complete mux snapshots and new/legacy replay fields preserve it. Missing
old fields default to numeric. Cursor save/restore, alternate-screen switching and
the shell prompt's transient reporting reset leave this session-wide mode alone.
DECSTR and RIS reset it.

App uses the [VT100 auxiliary-keypad table](https://vt100.net/docs/vt100-ug/chapter3.html#T3-8)
and [xterm VT220-style keypad frames](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html#h2-VT220-Style-Function-Keys)
for available unmodified framework keys. Digits use SS3 `p` through `y`;
multiply/add/comma/minus/decimal/divide use `j/k/l/m/n/o`; physical keypad Enter
uses `M`. Main Enter remains CR. Physical keypad comma is accepted through its
framework comma alias. Main-keyboard digits and logical PC navigation keep
existing routing. Kitty all-key/associated-text negotiation takes precedence.

Handled keypad digit/operator events reserve one text callback before sending. A backend's
subsequent TextInput cannot duplicate that event, even if mode/Kitty policy
changes or input reenters during the send. A release or context reset expires the
decision; subsequent text-only IME commits follow current policy. Enter keeps
the existing before/after observer ordering and legacy text-only recovery policy.

The partial scope is intentional: modified keys retain legacy routing; VT52,
additional keypad keys, host NumLock overrides and broadcast behavior are not
newly implemented or qualified. No lock-state bits are guessed. Current unit
tests traverse Avalonia routed events and the production encoder/session call,
but their mocked transport does not acknowledge real child receipt. That owned
end-to-end measurement and mouse qualification remain required by #769. Frozen
stock grades and historical measurements remain unchanged.
