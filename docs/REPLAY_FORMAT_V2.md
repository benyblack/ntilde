# Ntilde Replay Format v2

This document defines the replay file format used by Ntilde (`.rec` / `.ntilderec`).

## Overview

- Encoding: UTF-8 text
- Container: JSON Lines
- Line 1: v2 header (`ReplayHeader`)
- Remaining lines: replay events (`ReplayEvent`)

## Header

The first line must be a JSON object:

```json
{"type":"ntilderec","v":2,"cols":120,"rows":30,"date":"2026-02-13T12:00:00.0000000Z","shell":"pwsh.exe"}
```

Fields:

- `type` (`string`): must be `"ntilderec"`; readers also accept the pre-rebrand `"novarec"`
- `v` (`int`): format version (`2`)
- `cols` (`int`): initial terminal columns
- `rows` (`int`): initial terminal rows
- `date` (`string`): ISO-8601 UTC timestamp
- `shell` (`string`): shell command used during recording

## Events

Each subsequent line is a JSON event with:

- `t` (`long`): milliseconds from recording start
- `type` (`string`): event kind

### `type: "data"`

```json
{"t":200,"type":"data","d":"BASE64_BYTES"}
```

- `d` (`string`): base64 raw PTY output bytes

### `type: "resize"`

```json
{"t":350,"type":"resize","cols":100,"rows":28}
```

- `cols` (`int`)
- `rows` (`int`)

### `type: "input"` (legacy read support only)

New manual and flight recordings never write input events, including passwords entered without
terminal echo. The reader continues accepting both forms below in existing recordings.

```json
{"t":420,"type":"input","d":"BASE64_BYTES","i":"legacy-string"}
```

- `d` (`string`): base64 raw user input bytes (preferred)
- `i` (`string`): legacy plain-text input field (optional fallback)

### `type: "marker"`

```json
{"t":1000,"type":"marker","n":"checkpoint-name"}
```

- `n` (`string`): marker name

### `type: "snapshot"`

```json
{
  "t":1200,
  "type":"snapshot",
  "s":{
    "cols":120,"rows":30,"cx":10,"cy":5,"alt":false,
    "st":0,"sb":29,
    "awm":true,"ckm":false,"decom":false,"bp":false,"cv":true,
    "fg":4294967295,"bg":4278190080,"fgi":-1,"bgi":-1,
    "dfg":true,"dbg":true,"inv":false,"bold":false,"faint":false,
    "italic":false,"ul":false,"blink":false,"strike":false,"hidden":false,
    "cells":"BASE64_TERMINALCELL_BYTES",
    "cells_sizeof":12,
    "cells_layout_id":"TerminalCell/v1",
    "ext":{"15":"🚀"},
    "wrap":[false,false,false]
  }
}
```

Snapshot fields:

- Geometry: `cols`, `rows`, cursor `cx`/`cy`, alt-screen `alt`
- Scroll region: `st`, `sb`
- Mode flags: `awm`, `ckm`, `decom`, `bp`, `cv`
- Current style state: `fg`, `bg`, `fgi`, `bgi`, defaults and style booleans
- Complete cut: optional `state` contains the existing `TerminalStateSnapshot`
  model (both screens, mode state, saved cursor banks, pending 1049 restoration,
  and parser continuation). Recorders omit scrollback from this cut. Pass the
  parser to `RecordSnapshot` to retain its current character sets and partial
  sequence state; the buffer-only overload emits legacy viewport fields and leaves
  the replay parser continuation untouched. It cannot provide a complete seek cut.
  The legacy fields remain populated for older readers.
- Cell payload:
  - `cells`: base64 `TerminalCell[]` bytes
  - `cells_sizeof`: serialized `sizeof(TerminalCell)` from the recorder runtime
  - `cells_layout_id`: serialized cell-layout contract id (currently `TerminalCell/v1`)
  - `ext`: sparse extended-text map (`row*cols+col -> string`)
  - `wrap`: per-row wrap flags

## Compatibility

- Reader behavior:
  - If first line is a valid v2 header (`type=ntilderec`, `v=2`), parse as v2.
  - Otherwise fallback to legacy v1 lines (`{"t":...,"d":"..."}`).
- Input events:
  - Reader prefers `d` (base64 bytes), then falls back to legacy `i`.
- Snapshot layout validation:
  - If `cells_sizeof` and/or `cells_layout_id` are present, reader validates against the current runtime layout.
  - On mismatch, reader throws `InvalidDataException` with a `cell layout mismatch` message and expected/actual values.
  - Legacy snapshots without these fields still load.
  - When `state` has the current supported state version, it is authoritative and uses the shared state-transfer
    preflight: a non-null parser half is required, and terminal and parser structure are
    validated before either is mutated.
    Live and saved palette indices, saved charset indices and the underline pen are validated.
    SIXEL metrics in the complete parser state take precedence; outer metrics are a fallback when absent.
    Legacy snapshots require positive geometry before resize or content mutation.
    An unknown future state version falls back to the legacy viewport fields.
  - Legacy snapshots without `state` reset both saved banks to default home positions,
    preventing a previous seek's saved cursor from leaking into the new position.



## Seek and playback restart boundaries

A complete snapshot with a supported state version is a restart checkpoint. The GUI
restores its paired terminal/parser state and resumes **after its physical record**,
including when output before and after the snapshot shares the same timestamp.
Among equal-time complete snapshots, the last physical record wins. Blank lines,
LF/CRLF endings and a final record without a newline do not change the boundary.
`ReplayRunOptions.StartAfterLineNumber` is a reader option, not a recording field;
zero retains normal from-start behavior. It cannot be combined with a byte offset.
`OnSkippedData` optionally observes skipped data records to prime a byte decoder;
it does not apply terminal events, publish timestamps or count replayed events.
Input payloads and snapshot records are not delivered to this observer.
Header validation still runs; header geometry/metrics are gated when a checkpoint
already supplied authoritative values.

Legacy viewport-only snapshots and unknown state versions cannot establish parser
continuation, inactive screens or saved cursor banks. For GUI seeks and playback
restarts, replay begins at an earlier supported complete checkpoint, or from the
initial paired state and the raw data prefix. The latter resets prior seek state
without replacing the configured parser/image decoder. Each run gets a fresh stateful
UTF-8 decoder so a code point can span data records without inheriting bytes from a
previous run. Playback publishes times at or after its requested start while rebuilding
the earlier prefix. The original recorded prefix must
be present for exact reconstruction; a standalone legacy viewport capture cannot
provide a complete seek cut. Complete snapshots establish the terminal/parser cut;
preceding raw data primes the UTF-8 decoder when a code point straddles that cut.
Decoded prefix text is discarded, so the checkpoint's terminal state stays authoritative.
This can make old recordings slower to seek, but avoids inventing parser state.
Malformed nearest snapshots are rejected before resetting the displayed state.

Sequential snapshot restoration is unchanged: `ReplayRunner.ApplySnapshot` leaves
an active parser continuation intact for legacy snapshots. Rewind/reset belongs to
the GUI session restart boundary, not each snapshot in a sequential stream. Closing
the replay window cancels playback and invalidates queued callbacks.
