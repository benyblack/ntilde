# Routed-input benchmark layer (input.x10-mouse, input.button-event-mouse, modes.application-keypad)

This is the contract behind the [2026-11-11 routed-input qualification](2026-11-11-routed-input-qualification.md). It qualifies three pinned
terminfo.dev@4.0.0 cases through declared Avalonia events that the production App input path routes into a disposable direct-local child, and
through the exact bytes an independent reader of the child's stdin received. It reuses the [owned routed-input fixture](owned-input.md) (raw child
capture, FIFO ordering fence, ownership re-proof, mode controls, restoration seal); the benchmark plan (v2) replaces the frozen 39-event plan (v1)
for source policy v36 and later, and v1's publication keeps re-deriving unchanged.

## What the pinned probes actually are

Read from `terminfo.dev@4.0.0` (`terminfo.bundle.mjs`, SHA-256 `aa5f2bf9…d1f6`), not inferred from the names:

| Case | Pinned termless code | Pinned terminal code | Pinned verdict |
|---|---|---|---|
| `input.x10-mouse` | `mouseInputProbe(…, "mouseTracking")`: reads `ctx.getMode("mouseTracking")`, feeds nothing, returns `pass:false` with `inconclusive/legacy` and the note "No generated input event and encoded report were validated" | the same note, `evidence:none`, no terminal I/O | inconclusive by design |
| `input.button-event-mouse` | the same helper and mode name | the same | inconclusive by design |
| `modes.application-keypad` | feeds `ESC =`, reads `ctx.getMode("applicationKeypad")`, feeds `ESC >`, reads it again, returns `notTestedResult("keypad input", {on, off})` (`no-semantic-observable`) | "Keypad mode left unchanged; this probe does not measure keypad input encoding" | inconclusive by design |

Neither mouse probe feeds a byte, selects a tracking mode or looks at an event, and both read the single aggregate flag `mouseTracking`, so the
pinned acceptance criterion for all three is "mode metadata only, and it never decides". The pinned verdicts are preserved, never rewritten; the
requested behavior is established by separately identified **effect contracts**
(`ntilde-x10-mouse-tracking-effect-v1`, `ntilde-button-event-mouse-tracking-effect-v1`, `ntilde-application-keypad-effect-v1`) that are not
attributed to the pinned probes. A difference is a recorded `pinned-inconclusive-by-design` divergence, as for the other always-inconclusive probes.

The pinned code is retained and executed: a twenty-eighth digest-checked derivation (`ntilde-owned-routed-input-grading-v1`, layered over the
left/right-margin one) refuses unless the retained bundle still contains the three registrations, `mouseInputProbe`, `notTestedResult` and the
absent-event note verbatim and once, and then only appends exports. The worker runs the probe over mode values served **from the child's own
DECRQM replies** (never App metadata): the keypad probe's two feeds are the exact setups of two plan cases (`ESC =` then `ESC >`), and its reads
are the child's DECRQM 66 reply after each; the mouse probes read the restored defaults. The parent re-executes the probes from the retained
journal and requires agreement with the live result.

## Mouse semantics

Genuine X10 compatibility tracking is **DECSET 9**: presses of the three ordinary buttons only, no modifier bits, no release, motion or wheel.
The historically named `MouseModeX10` field and `m_x10` snapshot field mean normal tracking, **DECSET 1000**; they are never substituted for mode 9
([x10-tracking.md](x10-tracking.md)). Tracking selection (9/1000/1002/1003) is separate from coordinate encoding (legacy bytes, or SGR 1006), and
legacy coordinates are single octets `32 + value`: column 96 is `0x80`, and a UTF-8 expansion (`c2 80`) is never an accepted equivalent
([mouse-byte-input.md](mouse-byte-input.md)). Button codes (left 0, middle 1, right 2, release 3, wheel 64/65, motion +32) and modifier bits
(shift 4, alt 8, ctrl 16) follow xterm's control-sequence documentation; wheel events are not ordinary buttons for DECSET 9.

The primary encoding is the legacy `CSI M` form (the default); SGR frames are required only where they show that encoding is independent of the
tracking selection, and are labeled observations for button-event tracking. Pixel (1016), UTF-8 (1005) and urxvt (1015) coordinates are out of scope.

## Plan v2

Sixty standalone declared events in a fixed order, 40×140 observed grid (at least 97 columns). Before every dispatch the child writes the setup and
queries DECRQM 66/9/1000/1002/1003/1006 plus the Kitty flags; the exact reply must prove the expected modes or collection aborts without dispatching.
Each case's expected bytes are a **literal** protocol string; an independent oracle (`routed-input-contracts.mjs`, written from the xterm and VT
tables, never calling the App encoder) must agree with every literal, and the grader checks that again from the retained journal.

- **`input.x10-mouse`** (mode 9, 21 experiments): left press without modifiers, each of shift/ctrl/alt/meta alone, all four, and the original
  all-modifier press, plus middle and right presses, all of which must produce the same modifier-free frame; one SGR press; release, held-button
  motion, hover and both wheel directions suppressed; legacy bytes restored after `?1006l` with mode 9 still set. Controls (identical events under
  other protocols): normal tracking press with modifiers (`<` instead of space), release and wheel up (reported), normal motion (suppressed),
  button-event motion (reported). A control that fails leaves the case inconclusive, never unsupported.
- **`input.button-event-mouse`** (mode 1002, 11 experiments): left and right press, held-button motion (plain and with shift+alt, in a fresh cell),
  release, hover suppressed behind a completed capture barrier, and three SGR observations; controls: hover under 1003 is reported, held motion
  under 1000 is suppressed. The App reports button motion once per cell change, so each reported motion uses a cell different from the previous one.
- **`modes.application-keypad`** (30 experiments): numeric-mode controls (key-down and key-up send nothing, text sends the digit, Enter sends CR, a
  key-down carrying its backend text sends the digit once); `ESC =` selects application mode and all 17 physical keypad key-downs send their VT100
  SS3 frames; a key-down carrying its backend TextInput sends only the frame (duplicate suppression, for a digit and an operator); key-up sends
  nothing; a standalone text commit is ordinary text; `ESC >` restores numeric Enter (CR); the DECNKM alias (`?66h`/`?66l`) selects and restores.
  Physical keypad keys, ordinary text, key-down, text and key-up stay separate experiments. Main-row digit key-downs are not a declared event, so
  ordinary digits appear only as text.

## Production change this needed

Each generated keyboard event used to clear the one-shot text correlation when it returned, so no declared call could exercise the handled-key
duplicate guard (a backend's TextInput after a handled keypad key). A `keyDown` may now carry one keypad character (`text`, modifiers zero); the App
raises that TextInput right after the key-down inside the same dispatch, still refuses the action while a human key's correlation is pending, and
reports `textHandled` as a separate fact. [docs/agent-host/DIRECTION.md](../../agent-host/DIRECTION.md) carries the bound; a paired experiment
certifies only when `textHandled` is true. Nothing else in the encoder or input routing changed.

## Grading

An experiment matches when the child's bytes equal the literal and, for paired events, `textHandled` is true. A benchmark is **supported** when every
required experiment matches, **unsupported** when a required experiment mismatches while every control held, and **inconclusive** when any control
mismatches. Labels never grade. Missing admission, incomplete capture, unexpected or late input, a stale pane/process identity, a mode control that
did not match, a changed plan or a failed restoration abort or invalidate the receipt; nothing is retried.

## Boundaries

Generated App events are not physical keyboard or pointer input; the host keyboard layout, NumLock and operating-system key translation are not
established. App admission (`handled`, `inputSubmitted`) is retained beside, never instead of, the bytes the child received. Direct-local delivery does
not qualify SSH, mux or Windows. Source tests (`MainWindowGeneratedInputTests`, the ApplicationKeypad and X10 routing suites) do not replace the fresh
live measurement.
