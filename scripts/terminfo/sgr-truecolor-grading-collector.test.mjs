import assert from 'node:assert/strict';
import { test } from 'node:test';
import { adaptSgrTruecolorCollector, sgrTruecolorAdapterId, sgrTruecolorSeams } from './sgr-truecolor-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => sgrTruecolorSeams.join('\n/* gap */\n') + '\n';

test('derives the truecolor extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptSgrTruecolorCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeSgrTruecolorAdapter = Object.freeze({\n  id: "${sgrTruecolorAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunSgrTruecolorTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["sgr.fg.truecolor","sgr.bg.truecolor"]'));
    assert.equal(adaptSgrTruecolorCollector(source), derived);
});

test('the seams pin exactly the two pinned truecolor registrations', () => {
    for (const registration of ['truecolorProbe("sgr.fg.truecolor", "fg"),', 'truecolorProbe("sgr.bg.truecolor", "bg"),']) {
        assert.ok(sgrTruecolorSeams.includes(registration), registration);
    }
    assert.ok(!sgrTruecolorSeams.some(seam => seam.includes('extensions.truecolor')), 'the related extensions.truecolor variant is not pinned here');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of sgrTruecolorSeams) {
        const broken = verbatim().split(seam).join('CHANGED');
        assert.throws(() => adaptSgrTruecolorCollector(broken), /Pinned SGR-truecolor probe seam changed/, seam);
        const repeated = verbatim() + seam + '\n';
        assert.throws(() => adaptSgrTruecolorCollector(repeated), /Pinned SGR-truecolor probe seam changed/, seam);
    }
    assert.throws(() => adaptSgrTruecolorCollector(''), /Pinned SGR-truecolor probe seam changed/);
});

test('refuses a pinned definition whose references, ordinary controls, comparisons or notes changed', () => {
    for (const [from, to] of [
        ['{ r: 255, g: 128, b: 0 } : { r: 0, g: 255, b: 128 }', '{ r: 255, g: 128, b: 1 } : { r: 0, g: 255, b: 128 }'],
        ['{ r: 255, g: 128, b: 0 } : { r: 0, g: 255, b: 128 }', '{ r: 255, g: 128, b: 0 } : { r: 0, g: 255, b: 127 }'],
        ['const second = { r: 17, g: 97, b: 201 };', 'const second = { r: 17, g: 97, b: 202 };'],
        ['[31, 34] : [41, 44]', '[31, 34] : [41, 45]'],
        ['};2;17;97;201mY', '};2;17;97;202mY'],
        ['if (sameRgb(x, first) && sameRgb(y, second))', 'if (sameRgb(x, first))'],
        ['if (a && b && !sameRgb(a, b))', 'if (a && b)'],
        ['return parserStateResult(false, expected, state);', 'return parserStateResult(null, expected, state);'],
        ['if (!preceding || sameRgb(preceding, first)) {', 'if (!preceding) {'],
        ['"RGB target readback was absent"', '"readback missing"'],
    ]) {
        assert.ok(sgrTruecolorSeams.some(seam => seam.includes(from)), from);
        const mutated = sgrTruecolorSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptSgrTruecolorCollector(mutated), /Pinned SGR-truecolor probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeSgrPaletteAdapter = {};\n';
    const layered = adaptSgrTruecolorCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(sgrTruecolorAdapterId));
});
