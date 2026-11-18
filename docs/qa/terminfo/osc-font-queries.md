# Font extension audit and bounded queries (#994)

These three IDs are separate protocols. Query replies describe configured font
size; they do not establish rendered glyphs or runtime font mutation.

| ID | Owner and exact form | Criterion and selected policy |
|---|---|---|
| `extensions.osc710-font-normal` | urxvt `OSC 710 ; font-description BEL/ST`; `;?` queries its normal font description; when its insecure option is off it still replies with an empty payload (`OSC 710 ; BEL/ST`) | Frozen probe has no termless callback and its terminal callback performs no operation, returning inconclusive. Ntilde leaves the X11/Xft font-selection service unavailable, including its query. No effect grade or inventory entry is invented. |
| `extensions.osc7770-font-size` | Mintty `OSC 7770 ; ? BEL/ST` → `OSC 7770 ; integer-size BEL/ST` | Query-only subset, integer typographic points. No font mutation. |
| `extensions.osc7777-font-window-size` | Mintty `OSC 7777 ; ? BEL/ST` → `OSC 7777 ; integer-size BEL/ST` | Query-only subset. Its reply contains the current font size, **not window dimensions**. The owner's mutation forms adapt the window to preserve character geometry where possible; that behavior remains unavailable. |

Primary references: [urxvt implementation](http://cvs.schmorp.de/rxvt-unicode/src/command.C?revision=1.600),
[Mintty sequences](https://github.com/mintty/mintty/wiki/CtrlSeqs#font-size),
[Mintty dispatch](https://github.com/mintty/mintty/blob/master/src/termout.c),
[Mintty font units](https://github.com/mintty/mintty/blob/master/src/wintext.c).
Mintty's positive configured size is converted to physical height using DPI/72;
its negative pixel-size configuration is a different applicability case. Ntilde
implements positive point-size queries only. The request terminator is mirrored.

The direct App supplies one atomic host snapshot. Font size in typographic points
is `view.FontSize × (effectiveRenderScaling / backingScale) × 72 / 96`, distinguishing
Avalonia DIPs, interface zoom and physical pixel density. VT does no UI access.
Missing, invalid, meaningfully fractional or out-of-range (1–512 points) values
stay silent. Only binary64 conversion roundoff within eight ULPs at the nearest
integer is normalized. This bounds the host arithmetic (multiplication/division
of DIP, scales and 72/96), rather than rounding a genuine fractional size. For
example, 20 DIP at140% UI zoom on a150% display can calculate as
20.999999999999996 instead of21 points. Default 14 DIP at unity zoom
is 10.5 points and therefore unavailable for this bounded query policy. Exact
integer size does not enable mutation: bare, reset, absolute and relative setter
forms remain ignored. Mux and replay/headless hosts without these authoritative
facts gain no fabricated default.

The pinned `terminfo.dev@4.0.0` bundle is
`aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`,
probe hash `16a690f34c0e`. Both size probes have termless `feedCapture` and terminal
`queryWithSentinelOutcome` paths. Each sends its exact `;? BEL` request and requires
a complete own-code unsigned-integer reply, accepting BEL or ST. No match is
inconclusive (`no-response` or `invalid-reply`), never unsupported. Digest-pinned
helper and registration seams are executed unchanged; OSC710's no-operation
callback is independently checked with a context that refuses every access.

`ntilde-osc-font-size-query-effect-v1` independently checks actual point size and
exact framing. A disposable fixture explicitly configures 16 DIP at unity zoom
(12 points). Each ID has two CSI16t delivery controls, its exact pinned request,
BEL/ST queries, and seven unavailable/malformed/setter forms. The absolute setter
control requests 18 points, distinct from the observed 12-point fixture; a baseline
equal to the setter target is refused. Each exchange has
its own complete DA barrier and retained raw replies. The parent independently
reads the actual view FontSize, effective/backing scales, cell metrics, window
client dimensions and complete stored terminal state. All geometry must remain
identical; target replies cannot generate their own expected size. Failed delivery
controls make an effect inconclusive; a valid controlled mismatch is unsupported.

The alternate-screen nonce fixture is restored to the prepared main-screen state.
No font or window mutation is planned; unchanged actual geometry is required
through final restoration. Failure and cleanup errors remain fatal and retained.
Receipt admission rederives every reply, journal frame and report from captured
sources and exact adapters after verified disposable teardown. Source and binary
hashes identify measured inputs without claiming a reproducible binary build.
Stock results, the frozen inventory baseline, Core/Modern and public common-harness
scores remain separate. Deterministic/synthetic tests alone are not runtime proof.
