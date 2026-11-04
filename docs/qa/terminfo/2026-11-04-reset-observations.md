# Owned reset observations — 2026-11-04

**All nine saved-state cases passed, including the previously failing RIS
both-screen boundary.** The [complete saved-state JSON](2026-11-04-reset-observations.json)
retains the nine fixed inputs and 57 paired observations. The
[165-file evidence archive](2026-11-04-reset-observations-evidence.zip) retains
all thirteen reports and journals, 75 PNGs, raw collector replies, captured
source/dependency inputs, settings, ownership proof and completed v8 receipt.
The [earlier failing measurement](2026-11-03-saved-state.md) remains unchanged.

| Measurement | Value |
| --- | --- |
| App build and adapter source | `dc4e2c297d9bcbf89ab0d3d34b3dfacc2fdec4d6` |
| Operator-supplied version | `0.13.0`; not independently attested |
| Runtime | macOS arm64; Node `v24.22.1`; pinned `terminfo.dev@4.0.0` |
| Viewport | 40 rows × 140 columns |
| Run | `65c6c0bd9d1c10793a084e1e79f9f9eb` |
| Local evidence | `artifacts/terminfo/owned-u8KWwg`; all 165 files byte-verified in the primary checkout |
| Saved-state report | 417,476 bytes; SHA-256 `b8c061904a2aadfa02415d69b232e28f5a0a9798289963619d7ccd28646b903f` |
| Complete archive | 602,443 bytes; SHA-256 `d3d85d29f5d90c17ef8dfb62739d94b29ce46018b258534c0c038e9fa3a7992d` |

The fixed saved-state plan is unchanged. Every control passed, and each snapshot
contains two agreeing independent reads of complete cells/attributes, cursor
presentation/position, exposed buffer modes and bounded ordered history.

| Case | Result |
| --- | --- |
| Alternate-screen 47, 1047 and 1049 | Three passed cases |
| Main 1048, alternate DEC and main ANSI saved cursor | Three passed cases |
| Independent main/alternate saved banks | Passed |
| Soft reset of the active saved bank | Passed |
| Full reset of both retained banks | Passed |

The full-reset control is snapshot 50. After RIS, snapshot 51 observes blank
main cells. Newly written main-bank/default-pen witnesses pass at 52; alternate
clearing and its default saved bank pass at 53–54; returning to main retains the
new post-reset witness at 55. Final snapshot 56 restores the original complete
fixture state. These observations replace no historical result: the earlier
run exposed old main text after resetting from alternate. The
[owning fix and limits](reset-observations.md) describe the screen-selection,
default-pen and row-metadata correction.

This is one source-defined live reset case. Other initial-state combinations
have separate VT/mux/replay coverage; this measurement does not establish every
power-on option, native input delivery or rendered reset pixels.

The same owned run records four changed **reply-based collector outcomes**
against the frozen captured-source baseline. Both runs have the same probe hash
and the same 255 feature identities.

| Probe | Fresh retained reply | Qualification |
| --- | --- | --- |
| `device.xtwinops-18` | `CSI 8;40;140 t` | Exact text-area query; dimensions agree with observed geometry |
| `device.tertiary-da` | `DCS !\|00000000 ST` | Exact DA3 response; identifier is explicitly unassigned/nonunique |
| `extensions.kitty-graphics.transmit` | Correlated image ID/number and `OK` | Transmit acknowledgement |
| `extensions.kitty-graphics.display` | Correlated transmit and placement `OK` | Placement acknowledgement; no independent image-pixel comparison |

The new raw collector summary is **82 supported / 0 unsupported / 173
inconclusive**, still `suiteComplete=false`. The original **78 / 0 / 177**
measurement remains frozen. Reply-based display grades do not qualify image
appearance, animation, placeholders or native platform delivery. Reverse-wrap
remains an ungraded `UndeclaredTerminalGeometry` collector diagnostic.

All thirteen suite bindings remain in the receipt. Pixel comparisons retain
24 passes and the RGB-bold appearance negative. Erase/editing/history/resize
retain 13/22/30/3 passing cases. Metadata records **9 passes, 1 failure and 3
inconclusive cases**: both OSC133 P probes remain inconclusive, empty title
retains its negative, and the notification case has a focused window throughout.
Its positive delivery preconditions are false and no toast is shown; this run
does not qualify inactive-window notification delivery. The earlier controlled
[inactive-window positive](2026-11-04-metadata-snapshot.md) remains unchanged.

The private App, worker, socket and fixture were cleaned up at
`2026-11-04T11:06:04.314Z`; the captured source tree was removed at
`11:06:04.733Z`; receipt finalization followed at `11:06:06.403Z`.
The trusted launcher captured the same 52-file v1 source/input scope before
measured imports. The archive and JSON bind every source hash, manifest and
retained evidence file. Original context is preserved in the archive; the JSON
omits only its executable path.

The [whole-archive reproduction procedure](2026-11-04-metadata-audit.md) also
applies to this archive: verify its pinned hash, extract a private copy, rederive
all thirteen reports from their complete journals/PNGs and reproduce the full
v8 finalizer there. The independent audit matched every receipt field except
the newly generated completion time; original evidence was left unchanged.

Source capture protects against routine checkout edits. The launcher, Node/BCL,
operator environment and system tools remain trusted; the wrapper build and
binary hashes associate the build with source without independent binary/source
attestation. Windows/Linux runtime, generated-input and clipboard qualification
remain separate work. All historical RGB-bold, RIS and title measurements stay
unchanged. Dates use Europe/Stockholm; exact UTC times remain in the evidence.
