# Kitty associated text — 2026-11-01

Fresh owned macOS collections compare starting main with flag 16, using private
140-column × 41-row fixtures and separate uninstalled sidecars. The
[derived comparison](2026-11-01-associated-text-comparison.json) retains source,
input and binary pins, query traces and cleanup evidence. Raw/source archives
remain local and ignored; raw identity remains `unverified`. Generated assembly
versions and hashes bind the selected builds.

| Evidence | Before | After |
|---|---|---|
| Source | `894f833d69ec2fd16ff7db69fae6924608e9f818` | `471650d95acff311dbadbaa9f1d1629eff7e89f9` |
| App version | `0.12.0+894f833d69ec2fd16ff7db69fae6924608e9f818` | `0.12.0+471650d95acff311dbadbaa9f1d1629eff7e89f9` |
| Measured UTC | `2026-11-01T11:01:13.545Z` | `2026-11-01T11:37:19.383Z` |
| Retained directory | `artifacts/terminfo/owned-eC2dKV` | `artifacts/terminfo/owned-jjfiBp` |
| Raw SHA-256 | `61b45b928e8ddb5ca4a3df9b5262e525b2f2ac92458895aa87ec993d10c80e3a` | `8eb0d79dcd1890008d0b5c96413d7fe018e6889ca994bb9f2de2a8787fb05908` |
| Assembly SHA-256 | `58c03946942ee8d6a11b722aa28f9306a4a00dbbefb984892ef919e9ef8266c9` | `8974f0d44bebc9f5b83883e47210e0195bebe2eb406c45ab018ad3cf4890ebf2` |
| Supported / unsupported / inconclusive | 75 / 3 / 177 | 76 / 2 / 177 |

Both use terminfo.dev 4.0.0, Node v24.22.1, suite/probe `16a690f34c0e` and collector `cf7d8759c7e637aa8dba1e429f3dddd1c0124158`. Their adapter digest is `1eb43545a2fa23a2ffd8ef64d99bce92d08cf763442c6d2409e97910b7b7194a`; all input and runner/source pins match. The derived digest includes this worktree's ownership-module path.

## Observed change

Exactly one outcome changes: `extensions.kitty-keyboard.report-text`, from unsupported to supported. Before: Queried enhancement flags=9; actual key press, repeat and release events were not tested. After: Queried enhancement flags=25; actual key press, repeat and release events were not tested. The other 254 outcome/reason pairs agree. This is negotiation evidence; the collector does not synthesize key or text events.

## Implementation and tests

[Kitty's keyboard protocol](https://sw.kovidgoyal.net/kitty/keyboard-protocol/#report-associated-text)
defines flag 16 together with flag 8. Ntilde now advertises flags 1/2/4/8/16.
Actual TextInput commits append decimal Unicode scalars to press/repeat CSI-u
frames. Text-only composition uses key zero; releases have no payload. Flag 16
without flag 8 retains legacy text behavior. Alternate fields remain independent.

Text-producing keydowns stay unhandled so Avalonia can deliver native text.
A bounded pending frame takes the actual callback; the next key/release or one
dispatcher job flushes a key-only fallback. Late text uses zero-key events.
Mode changes preserve captured policy, context changes discard unsent frames,
and old-stack presses cannot acquire releases in a new stack. Complete malformed
UTF-16 and C0/C1 payloads are rejected; combining and format scalars are allowed.
Windows single-unit WM_CHAR callbacks can complete a surrogate pair across
unknown packet key events. Only one high unit and its mode are retained; a
context reset or a new known key discards an incomplete fragment.

The native callback requirement was checked against Avalonia 12.1.3's
[Win32](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/Windows/Avalonia.Win32/WindowImpl.AppWndProc.cs),
[X11](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/Avalonia.X11/X11Window.cs)
and [macOS](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/native/Avalonia.Native/src/OSX/AvnView.mm)
backends: handled keydowns suppress their text callbacks. Win32 constructs a
single UTF-16 code unit per WM_CHAR, motivating the bounded fragment handling.

Local validation: full App main **7,459 passed / 52 expected skips**; separate
PlatformBoot **75 passed / 4 font skips**; full VT **1,323 passed**; mux
**1,123 passed / 20 platform skips**; architecture **178 passed**; SSH input
boundary **41 passed**. Conformance reports 74 rows with zero errors/warnings.
Scoped whitespace formatting and diff checks pass. Two negotiation regressions
failed against the previous mask, ten payload tests failed with payload
formatting disabled, and five split-callback cases failed before coalescing.
The GUI/mux test asserts payload delivery, old-peer legacy fallback and recovery.
SSH cases use a fake native boundary; they do not claim a live SSH session.

## Limits and cleanup

Tests supply framework key/text metadata rather than qualifying every native
keyboard layout or IME. Unavailable key identities and lock-state bits are not
invented. Broadcast targets retain their existing legacy input path. No other
terminal binary was measured. The two unsupported observations remain OSC 66
text sizing and OSC 5522 clipboard. Inconclusive reasons remain 152 insufficient
evidence, 22 no response and three clipboard policy refusals. Both collections
retain the ungraded geometry diagnostic and `suiteComplete: false`.

Cleanup completed at `2026-11-01T11:01:13.571Z` and `2026-11-01T11:37:19.410Z`. Raw evidence was not edited or submitted upstream.
