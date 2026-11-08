import assert from 'node:assert/strict';
import { test } from 'node:test';
import { adaptSgrUnderlineCollector, sgrUnderlineAdapterId, sgrUnderlineSeams } from './sgr-underline-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => sgrUnderlineSeams.join('\n/* gap */\n') + '\n';

test('derives the underline extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptSgrUnderlineCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeSgrUnderlineAdapter = Object.freeze({\n  id: "${sgrUnderlineAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunSgrUnderlineTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["sgr.underline.double","sgr.underline.curly","sgr.underline.dotted","sgr.underline.dashed"]'));
    assert.equal(adaptSgrUnderlineCollector(source), derived);
});

test('the seams pin exactly the four exact-style registrations and their spellings', () => {
    for (const registration of [
        'sgrProbe("sgr.underline.double", "\\x1B[21m", (cell) => cell.underline === "double")',
        'sgrProbe("sgr.underline.curly", "\\x1B[4:3m", (cell) => cell.underline === "curly")',
        'sgrProbe("sgr.underline.dotted", "\\x1B[4:4m", (cell) => cell.underline === "dotted")',
        'sgrProbe("sgr.underline.dashed", "\\x1B[4:5m", (cell) => cell.underline === "dashed")',
    ]) assert.ok(sgrUnderlineSeams.includes(registration), registration);
    assert.ok(!sgrUnderlineSeams.some(seam => seam.includes('[4:2m')), 'SGR 4:2 is a control, never a pinned spelling of these cases');
    assert.ok(!sgrUnderlineSeams.some(seam => seam.includes('sgr.underline.single')), 'single is qualified by the attribute batch');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of sgrUnderlineSeams) {
        assert.throws(() => adaptSgrUnderlineCollector(verbatim().split(seam).join('CHANGED')), /Pinned SGR-underline probe seam changed/, seam);
        assert.throws(() => adaptSgrUnderlineCollector(verbatim() + seam + '\n'), /Pinned SGR-underline probe seam changed/, seam);
    }
    assert.throws(() => adaptSgrUnderlineCollector(''), /Pinned SGR-underline probe seam changed/);
});

test('refuses a pinned definition whose spelling or exact-style check was substituted', () => {
    for (const [from, to] of [
        ['"\\x1B[21m", (cell) => cell.underline === "double"', '"\\x1B[4:2m", (cell) => cell.underline === "double"'],
        ['"\\x1B[21m", (cell) => cell.underline === "double"', '"\\x1B[21m", (cell) => !!cell.underline'],
        ['"\\x1B[4:3m", (cell) => cell.underline === "curly"', '"\\x1B[4:3m", (cell) => cell.underline === "dotted"'],
        ['"\\x1B[4:4m", (cell) => cell.underline === "dotted"', '"\\x1B[4:3m", (cell) => cell.underline === "dotted"'],
        ['"\\x1B[4:5m", (cell) => cell.underline === "dashed"', '"\\x1B[4:5m", (cell) => cell.underline'],
        ['const measured = cell.char === "X" && check(cell) === true;', 'const measured = cell.char === "X";'],
        ['"No rendered X cell to evaluate"', '"no cell"'],
    ]) {
        assert.ok(sgrUnderlineSeams.some(seam => seam.includes(from)), from);
        const mutated = sgrUnderlineSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptSgrUnderlineCollector(mutated), /Pinned SGR-underline probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeSgrTruecolorAdapter = {};\n';
    const layered = adaptSgrUnderlineCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(sgrUnderlineAdapterId));
});
