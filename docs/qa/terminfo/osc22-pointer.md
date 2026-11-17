# OSC 22 pointer shape: bounded App contract

Scope: `extensions.osc22-pointer` from pinned `terminfo.dev@4.0.0`, bundle SHA-256
`aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6`, probeHash
`16a690f34c0e`. This document specifies the effect before collection; it is not a
measurement result. The batch starts from `b3e2dad5522d35de3de7976c33e186c9adef891c`.

The exact pinned probe has **no termless function**. Its terminal path writes
`CSI 1;1H CSI 2K`, writes `ESC ] 22;pointer BEL`, then asks for a cursor-position
report and calls `unmeasuredCellResult`. With a reply it returns inconclusive /
insufficient-evidence / query; without one, inconclusive / no-response / query.
A CPR never establishes pointer shape. Both branches of that criterion stay frozen.

The [Kitty pointer-shape specification](https://sw.kovidgoyal.net/kitty/pointer-shapes/)
defines the simple OSC 22 setter, optional `=`, empty reset, separate screen state
and reset withdrawal. The bounded implementation accepts only `pointer`, `text`,
`crosshair` and `default` (case-sensitive) and the empty reset, with BEL or ST.
Unknown names, lists, stack operations, queries and X11 aliases are ignored whole;
there is no query-based advertisement or full Kitty protocol conformance claim.
Payloads longer than 32 characters are ignored. The other CSS names remain unsupported.

VT only validates and emits a bounded request with the screen identity at parse
time, plus reset notifications. It stores no native pointer or UI state. The direct
App stores separate main/alternate requests and maps the four names to Avalonia
`Hand`, `Ibeam`, `Cross` and `Arrow`. RIS and DECSTR clear both overrides; replacing
the view's buffer clears them, while idempotent reattachment retains them. Hyperlink
hover takes priority and leaving the link restores the program's request. Retiring
the view releases its cached cursor objects and ignores later requests. This is
transient direct-view state: mux and replay persistence are not implemented or claimed.

`readPaneMetadata.pointerShape` separates `requested`, `controlCursor` and
`linkOverride`. `controlCursor` reads the actual Avalonia `Cursor` property's
`ToString()`, whose standard-kind name is stored by the Cursor constructor (see
[Avalonia 12.0.4 Cursor source](https://github.com/AvaloniaUI/Avalonia/blob/12.0.4/src/Avalonia.Base/Input/Cursor.cs)).
It is independent of the request/cache key, so a wrong constructor mapping or an
external override remains visible. Historical records without this optional field
gain no observation. The field does **not** observe the operating system's currently
displayed pointer, its location, pixel image or platform theme realization.

The owned main-screen effect contract must measure distinguishable `text`/`Ibeam`,
`pointer`/`Hand`, `crosshair`/`Cross` and `default`/`Arrow` pairs through the production
parser/App route. Empty reset must restore null request and Arrow. Positive delivery
and invalid-form controls, stable paired full-grid/metadata reads, unaffected title,
cwd, lifecycle/cells and final prepared restoration are required. The exact pinned
bytes execute separately, with the pinned result retained beside the effect grade.
Per-screen/reset/hover behavior has owning unit tests; that is distinct from the
bounded main-screen runtime claim. No physical/native pointer, SSH, mux, replay,
stock, frozen Core/Modern or public-comparison score is inferred from it.
