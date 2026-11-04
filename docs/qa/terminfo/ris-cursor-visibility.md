# RIS cursor visibility

Issue #846 was found by a source-pinned owned-image preflight whose final state
kept the cursor hidden after RIS. The original captures remain unchanged; this
fix is supported by deterministic tests and does not publish a new live grade.

Ntilde initializes DECTCEM (?25) to visible. A full reset now restores that
default inside the same buffer write lock as screen selection and clearing.
The parser query, render snapshot, replay and mux paths read that shared state.
No timer, rendering fallback or new serialization field is needed.

The [DEC RIS reference](https://vt100.net/docs/vt510-rm/RIS.html) describes
restoration of saved setup defaults; [DECTCEM](https://vt100.net/docs/vt510-rm/DECTCEM.html)
selects visible or hidden cursor presentation. Ntilde follows xterm's reset
policy: [xterm-411 `ReallyReset`](https://github.com/ThomasDickey/xterm-snapshots/blob/xterm-411/charproc.c#L14347-L14378)
explicitly makes the cursor visible for both hard and soft reset. This is an
emulator contract, without DEC hardware NVR or communication-line behavior.

Before the production change, eight of nineteen new VT cases failed against
main `25d195f26aaf5c97ab9e96e5792f9d4f0e6e18e8`: hidden main/alternate
cursors survived complete/chunked RIS, and reset snapshots remained hidden.
The eleven default/set-state and unrelated-control cases passed. With the
single reset assignment, all nineteen cases pass. Additional owning tests
cover actual recorded replay restore, mux output/reattach and offscreen cursor
rendering. The screen-switch callback observes the visible mode while the
existing reset write lock is held; old render snapshots retain their prior
hidden fact.

An owned fixture must still restore its **observed original** visibility after
RIS. If the original cursor was hidden, RIS correctly makes it visible and the
fixture must explicitly hide it again. Exact final state and PNG comparison
must continue to reject either mismatch. Erasing cells, moving the cursor, and
DECSC/DECRC retain their existing visibility semantics. Broad RIS power-on
coverage and previously published negatives remain unqualified or frozen as
recorded in the coverage matrix.
