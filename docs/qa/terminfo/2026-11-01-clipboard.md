# OSC 5522 clipboard follow-up — 2026-11-01

The owned macOS probe now recognizes DEC mode 5522: **77 → 78 supported,
1 → 0 unsupported, 177 inconclusive** observations. All other 254 outcomes and
reasons are unchanged. This measures protocol recognition; it does not measure
clipboard transfer permissions or native application interoperability.

| Build | Source | Local evidence | Raw SHA-256 |
|---|---|---|---|
| Before | `04019dbacf1c02aa7ccd85c39fcd2670ec2905eb` | `artifacts/terminfo/owned-WTxDq5` | `5291400dfd56c2b67ef7011cbd275cba9a9dd261fb3664b0fed2e9fd1b159144` |
| After | `190eab1575087f3c5cd95ac5770ebd08b4e9ebb9` | `artifacts/terminfo/owned-IlHkZI` | `704fc0babb01c107d0b072213ce2e78fc2c3fd17dbeefaeb39440f9d7ebaf858` |

The before probe ran at 2026-11-01T16:56:20.405Z and cleaned up at 2026-11-01T16:56:20.438Z.
The after probe ran at 2026-11-01T17:28:46.011Z and cleaned up at 2026-11-01T17:28:46.037Z.
The [derived comparison](2026-11-01-clipboard-comparison.json) records the original
query/reply traces, DLL hashes, source/input pins and ownership completion.
The executable informational-version strings matched the stated source commits.
Raw identity remains `unverified`; supplied version hints are not independent
identity attestation. Both runs use Node v24.22.1 and the same pinned
collector, dependencies, source snapshots and derived adapter from this worktree.

## Implemented behavior

The [Kitty clipboard protocol](https://sw.kovidgoyal.net/kitty/clipboard/) supplies
binary MIME reads/writes, contiguous chunks, MIME aliases and explicit status
responses. The [paste-notification specification](https://rockorager.dev/misc/bracketed-paste-mime/)
defines DEC mode 5522 and its precedence over plain/bracketed paste.

- Direct desktop and SSH panes opt in. Mux/headless hosts report unrecognized.
- Binary writes assemble privately and commit once. Invalid packets abort the
  whole transaction; later write packets are ignored until a new start.
- Base64 requires the standard alphabet, padding and canonical bits. A quartet
  can span packets; individually padded read chunks are at most 4096 bytes.
- Limits are 64 MiB decoded write data, 128 MIME types, 8192 characters per
  packet, 2048 metadata characters and 256 characters per MIME identifier.
  Native MIME/alias expansion has a separate 128 MiB budget. An unfinished
  transfer expires when another packet arrives after one minute.
- Writes honor the existing `AllowOsc52ClipboardWrite` setting, including a
  toggle between transfer start and commit. OSC 52 reads retain their denial.
- Data reads ask **Allow once / Deny**. MIME listings require no prompt. The
  program's display name is labeled as program-supplied; the terminal context
  is shown separately. No reusable password grants are stored.
- A user paste in mode 5522 sends MIME types with a cryptographic token valid
  once, for 15 seconds, on that TTY and default clipboard. A human-friendly
  name must accompany the token. No plain/bracketed paste is sent for that event.
- Primary selection returns `ENOSYS`; it never falls back to the system clipboard.
- Closing/replacing the pane, session exit and RIS invalidate pending requests
  and paste grants. Reset cancels permission prompts. Pending work stays bounded
  across resets. One clipboard operation runs globally at a time.
- Host-assembled transactions/grants are excluded from terminal snapshots.
  Raw client OSC packets can still occur in recorded PTY output and an incomplete
  parser accumulator. Replay/headless parsers have no clipboard callback.

## Verification and limits

`KittyClipboardProtocolTests` covers framing at every input split, bounded
metadata, strict base64, host opt-in, reset and complete state transfer.
`KittyClipboardReplayTests` covers both legacy and complete checkpoints.
`KittyClipboardSessionTests` covers binary/text chunks, forward aliases, bad
padding, transaction abort/recovery, read denial, listing, reply chunking,
primary-selection denial, live write settings, stale requests, global exclusion,
prompt cancellation, paste-token scope/use/expiry, both size budgets, session-exit
mode reset and response emission outside the clipboard/UI thread.
`AvaloniaKittyClipboardTests` round-trips test-owned binary/text MIME formats
through Avalonia DataTransfer values; macOS MIME/UTI conversion has a separate
deterministic regression. `PaneParserWiringTests` checks direct/mux wiring.

Local verification includes 1,488 VT tests, 57 focused clipboard/wiring tests,
6 conformance/report tests, platform boot
75 passed / 4 skipped, architecture 178, mux 1,123 passed / 20 skipped, and MCP
223. The full app main lane passed 7,633 cases and skipped 56 before the
review refinements; the later clipboard/wiring regressions also pass. Latest-head
CI supplies the full platform gate.

The owned collector has clipboard writes disabled and no clipboard fixture.
All three OSC 52 probes remain policy-refused; the OSC 5522 query only proves
recognition. No native clipboard transfer between applications was measured on
macOS, Windows or Linux, and no personal clipboard contents were read or changed
for these tests. MIME-named custom byte formats are used on Windows/Linux;
macOS maps MIME names to UTIs. Native rich-text format conventions remain a
platform interoperability limit. No other terminal emulator was measured here.

The 177 inconclusive observations, an ungraded reverse-wrap geometry diagnostic
and `suiteComplete=false` remain. Zero unsupported observations is not a complete
terminal compatibility score.
