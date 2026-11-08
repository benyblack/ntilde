import assert from 'node:assert/strict';
import { test } from 'node:test';
import { adaptSgrPaletteCollector, sgrPaletteAdapterId, sgrPaletteSeams } from './sgr-palette-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => sgrPaletteSeams.join('\n/* gap */\n') + '\n';

test('derives the palette extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptSgrPaletteCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeSgrPaletteAdapter = Object.freeze({\n  id: "${sgrPaletteAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunSgrPaletteTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["sgr.fg.bright","sgr.bg.bright","sgr.fg.256","sgr.bg.256","sgr.fg.default","sgr.bg.default"]'));
    assert.equal(adaptSgrPaletteCollector(source), derived);
});

test('the seams pin exactly the six pinned probe registrations', () => {
    for (const registration of ['namedColorProbe("sgr.fg.bright", "fg", 91, 94),', 'namedColorProbe("sgr.bg.bright", "bg", 101, 104),',
        'defaultColorProbe("sgr.fg.default", "fg"),', 'defaultColorProbe("sgr.bg.default", "bg"),',
        'indexedColorProbe("sgr.fg.256", "fg"),', 'indexedColorProbe("sgr.bg.256", "bg"),']) {
        assert.ok(sgrPaletteSeams.includes(registration), registration);
    }
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of sgrPaletteSeams) {
        const broken = verbatim().split(seam).join('CHANGED');
        assert.throws(() => adaptSgrPaletteCollector(broken), /Pinned SGR-palette probe seam changed/, seam);
        const repeated = verbatim() + seam + '\n';
        assert.throws(() => adaptSgrPaletteCollector(repeated), /Pinned SGR-palette probe seam changed/, seam);
    }
    assert.throws(() => adaptSgrPaletteCollector(''), /Pinned SGR-palette probe seam changed/);
});

test('refuses a pinned definition whose codes, setup or reference colors changed', () => {
    for (const [from, to] of [
        ['namedColorProbe("sgr.fg.bright", "fg", 91, 94)', 'namedColorProbe("sgr.fg.bright", "fg", 31, 34)'],
        ['namedColorProbe("sgr.bg.bright", "bg", 101, 104)', 'namedColorProbe("sgr.bg.bright", "bg", 41, 44)'],
        ['const setup = channel === "fg" ? 31 : 42;', 'const setup = channel === "fg" ? 32 : 42;'],
        ['const reset = channel === "fg" ? 39 : 49;', 'const reset = channel === "fg" ? 39 : 0;'],
        ['const first = { r: 95, g: 135, b: 175 };', 'const first = { r: 95, g: 135, b: 176 };'],
        ['sameRgb(x, a) && sameRgb(y, b)', 'sameRgb(x, a)'],
        ['return parserStateResult(sameCellColor(baseColor, resetColor), expected, state);', 'return parserStateResult(true, expected, state);'],
    ]) {
        assert.ok(verbatim().includes(from) || sgrPaletteSeams.some(seam => seam.includes(from)), from);
        const mutated = sgrPaletteSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptSgrPaletteCollector(mutated), /Pinned SGR-palette probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeSgrAttrsAdapter = {};\n';
    const layered = adaptSgrPaletteCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(sgrPaletteAdapterId));
});
