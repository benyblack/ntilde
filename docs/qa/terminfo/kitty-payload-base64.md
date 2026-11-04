# Kitty image payload base64 is data

The [primary Kitty grammar](https://github.com/kovidgoyal/kitty/blob/3dc4f8d5aa0092b77c839145aff5602ca8664cd4/docs/graphics-protocol.rst#the-graphics-escape-code)
separates the APC command identifier and controls from the base64 payload with a
semicolon. A `G` after that separator is payload data, not another identifier.

The decoder previously removed a leading `G` from the already separated payload.
For example, a raw RGB pixel beginning with bytes `18 12 AB` has base64 `GBKr`;
removing the first character makes a valid transfer fail. Conversely, a spurious
extra `G` before a valid base64 string could be silently accepted.

The focused correction removes only this payload-prefix stripping. Native APC
and the existing private OSC1339 Kitty tunnel retain their command/header
handling. Standard base64 decoding, payload and inflation budgets, exact raw
length checks, allocation, quiet/correlation and image lifetime remain owned by
the existing paths.

`KittyBase64PayloadTests` covers RGB/RGBA, first bytes18/1B (base64 G) and FF
(non-G controls), native/tunneled input, and complete/chunked transfers. It checks
the exact original bytes reaching the decoder, bytes per pixel, correlated OK,
placement, unchanged cursor and deletion. Two malformed extra-prefix cases stay
negative. On the old source,18 cases failed and8 controls passed.

Four additional `KittyImageLifecyclePixelTests` cases use the production Skia
decoder to assert the exact decoded RGB/RGBA channels and released bitmap handle.

These are deterministic parser/decoder-boundary and native decode regressions.
They do not prove rendered placement, other emulators, animation, placeholders
or a new owned desktop collection. Graphics remains Partial; every historical raw
observation and image/evidence archive remains unchanged. See
[current lifecycle limits](kitty-image-lifecycle.md) and [remaining variants](remaining-protocol-variants.md).
