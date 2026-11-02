# OSC 5522 primary selection

Direct Linux/X11 desktop and SSH panes can use `loc=primary` to read and write
PRIMARY independently of the regular clipboard. An empty write clears PRIMARY,
including a selection currently owned by another application. The initial write packet chooses
the location; subsequent data and alias packets inherit it. Writes honor
`AllowOsc52ClipboardWrite`. Reads require explicit consent or a remembered grant
for that same location. The permission dialog names the primary selection.

Primary and regular clipboard approvals are distinct. Default-clipboard paste
notifications and their one-use, captured-content tokens never authorize PRIMARY.
Reset, pane/parser replacement, process exit and disposal revoke the terminal's
read grants. The X11 selection contents, like normal clipboard contents, are
owned by the application process and can remain after the writing pane closes.

## Native boundary

App owns an independent `libxcb.so.1` connection with one background worker for
the process. It neither reflects into Avalonia nor installs Xlib's process-global
error handler. It connects lazily, only for local X11 DISPLAY forms. No native
PRIMARY operation runs in VT, a mux/headless daemon, or the UI thread.

The backend advertises MIME targets, TARGETS, TIMESTAMP and MULTIPLE. Plain UTF-8
text also has the standard UTF8_STRING target; reads can convert a legacy STRING
selection from Latin-1 within the remaining UTF-8 budget. Other MIME targets and
aliases retain exact bytes. MULTIPLE conversions reject duplicate/invalid
properties. Large values use ICCCM INCR in either direction.

Reads retain at most the existing 64 MiB aggregate MIME budget. Native property
requests use 64 KiB chunks. Writes retain at most 128 MiB of materialized MIME and
alias values, sharing immutable alias buffers. At most 16 outgoing INCR streams
and 128 MiB of referenced stream payloads can remain active; streams expire after
10 seconds. The last stream on each foreign requestor window releases its event
subscription; local read-window subscriptions remain until that read ends. A
stale SelectionClear cannot erase data successfully reacquired on the worker.
Reads have bounded reply/transfer waits. A failed receive destroys
its private request window, rather than leaving an incomplete INCR handshake.

The native worker admits at most one waiting command. A stalled connection
initialization can hold that worker and the existing global clipboard gate;
reset suppresses its stale results but does not create replacement workers.
Connection failure returns an error and releases the process's X11 resources.

Windows, macOS, native Wayland without X11, remote DISPLAY connections, or missing
XCB support return ENOSYS for PRIMARY. They never fall back to the regular
clipboard. This feature handles OSC 5522 operations; it does not change mouse
selection ownership, middle-click behavior, or default paste shortcuts.

## Evidence

`KittyClipboardSessionTests` covers target inheritance across write chunks and
aliases, write-policy enforcement, permission location isolation and revocation,
and paste-token separation. `PrimaryTransferRegistryTests` covers 1,000 persistent
requestors, shared/local subscriptions, expiry/destruction, stream/payload caps,
and stale ownership loss with in-flight bytes retained. Local-display parsing
has deterministic cases.
The disposable Linux/Xvfb native workflow exchanges text, HTML, PNG, binary and
aliases with an independent GTK primary-selection peer. Large binary fixtures
exercise INCR; the host requires observed sends and receives and verifies that
a regular-clipboard sentinel survives all PRIMARY operations. Reports retain the
source revision, host/App DLL hashes and both directional fixture hashes.

Native CI results must actually pass before this implementation is considered
qualified. Local headless tests do not touch a personal selection or clipboard.

The protocol references are the [Kitty clipboard specification](https://sw.kovidgoyal.net/kitty/clipboard/)
and [ICCCM selection conventions](https://www.x.org/releases/current/doc/xorg-docs/icccm/icccm.html).
