# Optional editing-state readback

Raw AgentHost `readScreen` accepts `includeBufferState: true`. Its optional
`bufferState` object contains detached values captured under the same buffer
read lock as the visible text, cursor and any requested cell details:

| Field | Meaning |
|---|---|
| `scrollTop`, `scrollBottom` | Inclusive zero-based vertical scrolling margins |
| `originMode` | Stored DECOM flag |
| `autoWrap` | Stored DECAWM flag |
| `insertMode` | Stored IRM flag |
| `pendingWrap` | Whether the next printable character will perform a deferred wrap |
| `alternateScreen` | Whether the alternate screen is currently active |
| `scrollbackLines` | Exact retained history row count for a local pane; explicitly null for windowless reads |

The opt-in is independent of `includeCellDetails`, `includeOverline` and
`includeCursorMode`. A viewport exceeding the optional complete-cell budget
can still return editing facts without cell details. With the opt-in absent or
false, the result omits `bufferState`; the false request flag is omitted too.
No parser, mode, UI state or history is changed by this read.

Windowless reads project modes from the daemon's imported viewport, including
its actual active screen. They request no history rows. The imported empty
history therefore cannot establish a zero total; `scrollbackLines` is explicit
null for these reads. `readScrollback` separately provides a bounded remote
history projection. Local history counts continue to describe retained main
history while the alternate screen is active.

This optional projection is available on the raw AgentHost endpoint. The MCP
`read_screen` tool keeps its existing parameters. A qualification client must
refuse missing or malformed requested facts from older endpoints, rather than
invent default values. Counts and mode flags alone do not qualify a protocol:
owned experiments also need known before/after cells, actual history text where
relevant, independent controls, exact inputs, and verified restoration/cleanup.
This readback supports #765 and does not regrade stock collector results.
