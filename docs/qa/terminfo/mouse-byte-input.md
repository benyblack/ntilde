# Legacy mouse byte transport (#769)

A legacy mouse frame is six octets, not arbitrary UTF-8 text. Column 96 and row
223 produce `1b 5b 4d 20 80 ff`. Sending the intermediate character string through
UTF-8 expanded the last two bytes and changed what the child received.

App now converts the legacy frame with Latin-1. The optional Pty-owned
`ITerminalByteInput` interface preserves bytes through Rust PTY, native SSH,
OpenSSH wrappers and negotiated mux connections. SGR frames remain ASCII text. Routing follows the encoded frame prefix, so a concurrent
SGR-mode change cannot turn an already encoded legacy frame into UTF-8 text.
Old text-only peers keep ASCII mouse reports; high-coordinate legacy reports are
refused. A declined raw write never falls back to a different text write, and a
refused motion report can be retried at the same cell.

Mux Hello requests byte-input semantics and Welcome acknowledges them. Absence
means the older UTF-8 contract. The existing binary Input frame owns its copied
payload; the daemon's existing attach and read-only guards apply before dispatch.
Text, raw bytes and parser replies share the existing dedicated session writer.
The retained-byte budget includes in-flight writes: UTF-16 storage for text and
array length for raw input. A legacy child provider can accept strict UTF-8 via
its text API; malformed bytes cannot become replacement characters. Acceptance
is queue/transport acceptance and does not acknowledge execution by the child.
Native SSH returns true only for the ordinary native input queue's OK result;
worker closure or disposal during admission returns false, while unexpected
native errors remain exceptions. Older custom interop providers conservatively
decline byte admission.

## Evidence

- `LegacyMouseByteInputTests` exercise production coordinate conversion at the
  ASCII/high-byte boundary, coordinate clamping, text-only compatibility, SGR,
  write refusal and motion deduplication.
- `LegacyMouseByteInputPtyTests` uses production pointer conversion and Rust PTY
  with a disposable POSIX raw child. The child independently prints its six stdin
  octets. Restoring the old string route makes the test fail: expected `80`,
  received `c2`. No personal terminal input or actual desktop pointer is used.
- `ByteInputTransportTests` send all 256 octets through the mux client/server,
  mutate caller buffers, verify mixed input order and retained budget, and cover
  old peers, unsupported children and read-only attachment.
- Native SSH and wrapper tests verify exact bytes at the interop/delegation
  boundary, capability refusal, closure and absence from manual recordings.

This slice does not qualify genuine X10 tracking (`DECSET 9`), native mouse
routing, a full generated-event owned input suite, remote SSH end-to-end delivery
or Windows runtime behavior. The existing `MouseModeX10` name denotes normal
`?1000` tracking and must not be treated as evidence for mode 9. #769 remains
open; #775 tracks cross-platform qualification. Published stock and owned
measurements are unchanged.

The generated-event child bytes for modes 9 and 1002 are measured in the [routed-input qualification](2026-11-11-routed-input-qualification.md).
