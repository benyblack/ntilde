# Bold glyph painting after the preserved pixel negative

The [owned pixel run](2026-11-03-pixels.md) recorded identical normal and SGR 1
explicit-RGB glyphs. That historical failure and every PNG remain unchanged.
The later [variant audit](remaining-protocol-variants.md) correctly kept this
appearance failure separate from RGB parsing and stored bold flags.

The rendering defect was that `TerminalDrawOperation` received `IsBold` but
neither split runs at a bold transition nor applied weight to its Skia font.
The glyph atlas also omitted font emboldening from its key and raster font.
Consequently, explicit RGB prevented indexed-color intensity from masking the
missing glyph-weight effect.

App now applies Skia's algorithmic `SKFont.Embolden` to each operation-owned
normal, fallback, shaped and OSC 66 sized-text font. Setting it for every run,
including normal runs, prevents mutable font state leaking past SGR 22/0.
The atlas keys and rasterizes emboldened glyphs separately. Integer cell layout,
font selection and existing primitive block/line painting retain their owners;
this does not select a different installed bold face or change VT cell data.

`BoldGlyphPixelTests` captures the production draw path offscreen using the
bundled JetBrains Mono NL face, explicit RGB and paired same-coordinate controls.
It covers cached/direct painting, italic combinations, scales 1 and 1.5,
normal/bold/normal runs and sized-text reset. Glyph appearance must change,
coverage must increase, SGR 22 must reproduce the normal bitmap, and non-target
content must remain unchanged. The six initial appearance cases failed before
the fix. The independent Rendering cache regression also failed before the fix;
it now compares the bold atlas mask to Skia's own direct raster and verifies
normal/bold/normal cache reuse.

These deterministic tests use no desktop window. They are not a new owned
runtime collection, screen photograph or font-weight qualification on every
platform/font. The separately retained [new owned run](2026-11-04-input-observations.md) records
all25 text-pixel comparisons passing under its recorded font/configuration.
The old negative stays frozen; other fonts/platforms and desktop cadence need
their own measurements.
Basic SGR stays Partial. No historical observation, collector grade, source
inventory or receipt is rewritten.
