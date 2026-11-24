# Frozen Modern X11 mouse criteria

The [frozen requirements](2026-11-15-modern-results/requirements-v1.json)
define separate App and library paths. This batch uses the App path for each of
the three rows. The [generated-input qualification](2026-11-11-routed-input-qualification.md)
remains evidence for its existing effect contracts.

Each case starts with its own mode 1000 plus 1006 click. A complete SGR left
press and release in the same cell admits that case's delivery control. The
worker opens a fresh raw PTY observation window before every XTEST action,
including each part of the click and drag. It checks for pending bytes before
opening the window and retains timestamped chunks. Only bytes from the case's
target windows can grade the target. Mode writes and DECRPM replies use separate
exchanges.

| App criterion | Target and grading |
| --- | --- |
| `input.x10-mouse` | Reset 1000 and 1006, then set 9. A fresh left click with CSI M and exactly three payload bytes is supported. Silence after verified delivery is unsupported. An SGR report, a failed control, or an unclassifiable stream is probe invalid. |
| `input.button-event-mouse` | Set 1002 with 1006 active. An SGR left press, a motion report with the left-button motion bit and a subsequent release in a distinct destination cell establish support. A delivered press and release at distinct cells with no motion report is unsupported. Missing delivery, malformed reports or misordering is probe invalid. |
| `modes.alt-scroll-1007` | Disable mouse tracking, set 1007 and enter alternate screen. Deliver a wheel-up. CSI A or SS3 A is supported. Silence or other bytes after verified delivery are unsupported. A failed delivery control is probe invalid. |

The collector starts a new local Xvfb display with a private Xauthority cookie
and no TCP listener. It verifies the X server's UNIX peer PID before X11
actions, identifies the mapped window's client PID via XRes, and requires that
the window under the pointer and keyboard focus belong to the same owned App.
XTEST requests synchronize with the server, then independently read pointer
coordinates and button masks. These facts corroborate X server dispatch to the
owned window. The positive click and target reports supply child-side evidence
of App handling. The private agent connection checks peer credentials and
identifies the singleton local pane through a run-specific marker written by the
direct child. The Linux process provider checks the child's original ancestry,
argv and same controlling PTY on stdin and stdout.

Mode queries establish baseline and target state, and X11 coordinates keep the
drag away from window edges and across distinct cells. Restoration returns the
owned pane to its initial effective mode values and verifies its original
primary-screen marker. The child restores and reads back its initial kernel
terminal attributes. The collector verifies child exit, reaps the App and Xvfb
it launched, removes the disposable fixture and checks the collection lease.
Probe-invalid evidence stays inconclusive. A failure of run ownership or
restoration prevents publication of a graded run.
