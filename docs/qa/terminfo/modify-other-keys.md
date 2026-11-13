# modifyOtherKeys benchmark layer (input.modify-other-keys, levels 1 and 2)

This is the contract behind the modifyOtherKeys qualification. It qualifies one pinned terminfo.dev@4.0.0 case,
`input.modify-other-keys`, through the declared Avalonia events the production App input path routes into a disposable
direct-local child, and through the exact bytes an independent reader of the child's stdin received. It reuses the
[owned routed-input fixture](owned-input.md) (raw child capture, FIFO ordering fence, ownership re-proof, mode
controls, restoration seal). The benchmark plan (v3) keeps every v2 event verbatim — so the
[routed-input layer](routed-input.md) keeps re-deriving from the same journal — and appends 58 modifyOtherKeys
experiments for source policy v47 and later.

## Scope

Levels 1 and 2 are subcases of the single benchmark id. Level 3 (`input.modify-other-keys-3`), xterm's
`formatOtherKeys: 1` CSI-u form, Kitty keyboard expansion, physical keyboard/layout or NumLock qualification, IME,
dead keys, non-US layouts, AltGr and Meta/Super behavior beyond the labeled policy controls, SSH, mux, Windows/Linux
desktop qualification and any general keyboard-API redesign are out of scope and never claimed.

## What the pinned probe actually is

Read from `terminfo.dev@4.0.0` (`terminfo.bundle.mjs`, SHA-256 `aa5f2bf9…d1f6`), not inferred from the name: the probe
feeds nothing, looks at no event, reads no mode and no cell — it returns `pass: false` with
`{"declaredModifyOtherKeys": ctx.capabilities.extensions.has("modifyOtherKeys")}` and the observation
`inconclusive / insufficient-evidence / evidence: legacy`, note "No generated input event and encoded report were
validated"; the terminal path returns the same with `evidence: none` without touching the terminal. The pinned
acceptance criterion is therefore "declaration metadata only, and it never decides". The pinned verdict is preserved,
never rewritten; the requested behavior is established by the separately identified effect contract
`ntilde-modify-other-keys-effect-v1`, which is not attributed to the pinned probe. A difference is a recorded
`pinned-inconclusive-by-design` divergence.

The retained code runs through a layered derivation of the collector extension (`ntilde-owned-modify-other-keys-grading-v1`,
a sibling of the OSC 133, routed-input, runtime-color reset, OSC 7/8 and title/icon stack layers) that refuses unless the
retained left/right-margin extension still contains the exact `input.modify-other-keys` registration verbatim and once.
The worker replays the probe over the declaration value the child itself answered: the plan's declaration case sends
XTGETTCAP for the `modifyOtherKeys` capability name and the control capture records the child's exact refusal
(`DCS 0 + r ST` — Ntilde does not advertise the capability, matching the collector's own detection), so the pinned
replay's response is served from the child's reply, never from App metadata. The parent re-executes the probe from the
retained journal and requires agreement with the live result.

## Negotiation

XTMODKEYS `CSI > 4 ; Pv m` selects the level; `CSI > 4 m`, `CSI > 4 ; m` and `CSI > m` reset it; `CSI > 4 n` disables
it; XTQMODKEYS `CSI ? 4 m` replies `CSI > 4 ; Pv m` with the effective level. Only levels 0, 1 and 2 are honored;
level 3 and any other value is ignored and leaves the current level in place. Levels are kept per screen buffer; RIS,
DECSTR and the shell-prompt transient reset restore level 0. Every experiment's control capture proves the negotiated
level through the child's own XTQMODKEYS reply (appended after the shared DECRQM and Kitty queries) before anything is
dispatched, and the final restoration proves the original state the same way.

## Encoding contract (as documented, including the deliberate deviations)

`CSI 27 ; m ; code ~` with `m = 1 + shift(1) + alt(2) + ctrl(4)` and `code` the US-layout character the key produces
with Shift applied (`Ctrl+Shift+A` is `CSI 27;6;65~`):

- **Level 1** keeps every chord with well-known legacy behavior: X11 control aliases (`Ctrl+@[\]^_`, Space, `2`–`8`,
  `/` and their shifted forms) and all Alt chords stay legacy; `Ctrl`/`Ctrl+Shift` on digits `0`, `1`, `9` and most
  punctuation encodes; Enter with Shift and/or Ctrl encodes (`CSI 27;2;13~`); Ctrl+Tab encodes (`CSI 27;5;9~`);
  Shift+Tab stays Backtab `CSI Z`; Backspace, Escape and Space stay legacy.
- **Level 2** encodes every modified ordinary key, including the X11 aliases (`Ctrl+A` `CSI 27;5;97~`,
  `Ctrl+Shift+A` `CSI 27;6;65~`, `Alt+A` `CSI 27;3;97~`, `Ctrl+[` `CSI 27;5;91~`, `Ctrl+2` `CSI 27;5;50~`),
  `Shift+Space` `CSI 27;2;32~`, Enter and Escape with any modifier, Tab with any modifier except Shift alone, and
  Backspace with Shift and/or Alt as `CSI 27;2;127~` / `CSI 27;3;127~` and `Ctrl+Shift+Backspace` as `CSI 27;6;8~`
  while plain `Ctrl+Backspace` stays DEL, as xterm's backarrow default gives.
- **Deliberate deviations, recorded as policy and never hidden**: Shift alone on a printable key stays on TextInput
  (its shifted character is its own text; keeps IME, dead-key and non-Latin layout text there); Ctrl+Alt is AltGr on
  Windows and never encodes; Meta/Super chords are never encoded so macOS Cmd shortcuts stay with the UI; keypad,
  cursor, editing and function keys are not xterm "other keys"; only the `~` form exists. An active Kitty stack wins
  over the level (verified neutral in every control; one bounded Kitty-precedence control corroborates the routing
  contract without becoming a Kitty project).

## Plan v3 (the modifyOtherKeys experiments)

57 declared keyboard events plus the declaration case, in a fixed order. Roles: required (graded), control (must hold
before a mismatch can be attributed) and label (reported, never graded). Expected event bytes are **literal** protocol
strings; an independent oracle (`modify-other-keys-contracts.mjs`, written from xterm's control-sequence documentation
and the documented policy, never calling the App encoder) must agree with every literal, and the grader checks that
again from the retained journal.

- **Disabled-mode legacy controls** (level 0): Ctrl+A, Alt+A, Enter+Shift, Shift+Tab, Shift+Backspace and Ctrl+Escape
  keep their legacy bytes.
- **Level 1**: the X11 aliases (`Ctrl+A`, `Ctrl+[`, `Ctrl+2`, `Ctrl+/`, `Ctrl+Shift+2`) stay legacy; `Ctrl+1`,
  `Ctrl+Shift+1` and `Ctrl+;` encode; `Alt+A` stays legacy; `Enter+Shift` and `Enter+Ctrl` encode; `Ctrl+Tab` encodes;
  Shift+Tab stays Backtab; `Escape+Shift` and `Shift+Backspace` stay legacy.
- **Transitions**: 0→1→2 through the set form, 2→0 through the reset form, 0→1 again, 1→0 through the disable form,
  and 2 then an ignored `CSI > 4 ; 3 m` (level unchanged, control-proved).
- **Level 2**: Ctrl, Ctrl+Shift, Alt and Alt+Shift on letters and digits; the aliases `Ctrl+2` and `Ctrl+[` encoded;
  `Ctrl+I` (no longer Tab); `Ctrl+;` and `Ctrl+Shift+?`; the Enter/Tab/Backspace/Escape table including Backtab and
  plain `Ctrl+Backspace` legacy.
- **Paired key-down/backend-text handling** only through valid correlated events: Shift+Space handled at level 2
  reserves its backend text (`textHandled` required, the frame delivered once); Shift+A and unmodified A are unhandled,
  so their paired text is the one delivery — the positive controls that prove the fixture delivers text; a standalone
  text commit afterwards is ordinary text again (no suppression leaks across dispatches).
- **Labeled policy controls**: Meta+A and Ctrl+Alt+A deliver nothing (the documented carve-outs), never graded.
- **Key-up** sends nothing.
- **Kitty precedence control**: `CSI > 1 u` over level 2 sends `CSI 97 ; 5 u`; popping the stack restores the retained
  level.

## Grading

An experiment matches when the child's bytes equal the literal and, for paired events, the App reports
`textHandled`. A benchmark is **supported** when every required experiment matches, **unsupported** when a required
experiment mismatches while every control held, and **inconclusive** when any control mismatches. Labels never grade.
Missing admission, incomplete capture, unexpected or late input, a stale pane/process identity, a mode or level
control that did not match, a changed plan or a failed restoration abort or invalidate the receipt; nothing is retried.

## Boundaries

Generated App events are not physical keyboard input; the host keyboard layout, NumLock and operating-system key
translation are not established. App admission (`handled`, `inputSubmitted`, `textHandled`) is retained beside, never
instead of, the bytes the child received. Direct-local delivery does not qualify SSH, mux or Windows. Source tests
(`ModifyOtherKeysProtocolTests`, `ModifyOtherKeysEncodingTests`, `ModifyOtherKeysRoutingTests`,
`MainWindowGeneratedInputTests`, `AgentHostGeneratedInputProtocolTests`) do not replace the fresh live measurement.
The pinned criterion and the effect contract stay separate fields; no stock grade changes.
