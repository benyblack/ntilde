# Owned routed-input fixture

The optional fourteenth suite observes 39 standalone declared Avalonia events in
the disposable direct-local pane. It does not generate hardware/global OS input,
read the system clipboard, or qualify remote, mux or Windows runtime delivery.
The fixed plan covers normal keypad key-down/text/key-up, seventeen application
keypad key-downs and key-up, X10 press-only reporting, normal/button/any motion,
SGR versus legacy bytes, modifiers, wheel, suppressed events and repeated hover.
Numeric keypad identities are the App API's physical NumPad keys.

Before each event the child writes the source-selected setup and independently
queries DECRQM 66/9/1000/1002/1003/1006 plus Kitty keyboard flags. Exact replies
must prove the expected modes before the parent can dispatch. The final hover
repeats only read-only queries, preserving its mode, view and cell for coalescing.
Expected event bytes are literal protocol expectations, never encoder output.

The healthy idle terminal fence detaches its listeners without changing raw input,
paused state or controlled `-opost`. One exclusive raw child-stdin reader copies
Buffers before decoding. After the one action RPC returns, it chooses two fresh,
distinct unknown private modes in 32768–65535 and requests their exact DECRPM
replies. Only that complete paired challenge, after the pre-request byte offset,
is removed. The preceding bytes are the original event observation. Limits are
512 payload bytes, 128 attempts and 4500 ms per capture. No action is retried.
The FIFO ordering argument applies to the local Rust user-input/device-reply
queue only. Missing admission, unexpected/late input, mismatch or timeout prevents
a completed receipt; silence alone never proves suppression.

The parent selects the event from the fixed case ID. A dedicated private acting
channel re-proves the unchanged executable/App marker, worker parent, UID, PTY and
single direct-local pane before and after dispatch under the original shared
four-second deadline. Original App results and partial dispatch/submitted facts
survive post-read, ownership, timeout or disconnect failures. `handled` and
`inputSubmitted` are App observations, not child-delivery acknowledgements.

Every control and resulting observation retains paired complete cell/cursor,
buffer and history reads for the nonce-bound pane and unchanged geometry. Four
phase readers stay below their independent 64-read limits: 42/42/39/39 reads.
The complete journal has 81 paired snapshots, 39 action records and 80 raw capture
challenges. The final sample proves restored input modes and original terminal
state, followed by an explicit disposed/healthy capture seal. Only an initial
typed geometry/details refusal, before binding or any action, may use an
unavailable marker. Started failures retain their raw/parent attempt files and
cannot downgrade to unavailable.

Policy `readonly-owned-source-graph-v2` captures a fixed 60-file graph before any
measured imports: the fixed captured v8 graph plus eight explicit input modules.
Receipt schema 9 requires all fourteen suite bindings, source hashes, parsed
journals, action facts and verified worker/App/fixture/source teardown. Historical
51/52-file v8 maps, policy v1, builders, published bytes and frozen grades remain
unchanged. Retained relative imports must close over the selected fixed scope;
coherently stripping input markers/files cannot turn the new runner into v8.
The launcher, Node/BCL and operator environment remain trusted, without a new
binary/source attestation claim.

Portable unit/synthetic tests cover the plan, raw Buffer ownership, challenges,
mode controls, parent/action failures, strict restoration/seal, version selection,
historical compatibility and downgrade refusal. A private PTY test checks listener
handoff, exact controlled termios restoration and original raw/paused/output
behavior; macOS PENDIN is a kernel canonical-input reprocessing flag rather than
an application mode. These tests are not fresh owned-App runtime evidence. The separately retained [first completed runtime collection](2026-11-04-input-observations.md)
records 39 passing cases and the complete schema9 receipt, together with the earlier
failed attempt. It qualifies this generated-event local-child path only; stock
grades and historical files remain separate.
