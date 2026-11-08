import assert from 'node:assert/strict';
import { test } from 'node:test';
import { adaptSgrUnderlineColorCollector, sgrUnderlineColorAdapterId, sgrUnderlineColorSeams } from './sgr-underline-color-grading-collector.mjs';

/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => sgrUnderlineColorSeams.join('\n/* gap */\n') + '\n';

test('derives the underline-color extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptSgrUnderlineColorCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeSgrUnderlineColorAdapter = Object.freeze({\n  id: "${sgrUnderlineColorAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunSgrUnderlineColorTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["sgr.underline-color-indexed","sgr.underline-color-rgb","sgr.underline-color-reset"]'));
    assert.equal(adaptSgrUnderlineColorCollector(source), derived);
});

test('the seams pin the three registrations and complete probe bodies, and never the aggregate sgr.underline.color case', () => {
    assert.ok(sgrUnderlineColorSeams.includes('    rgbUnderlineProbe("sgr.underline-color-rgb"),'));
    assert.ok(sgrUnderlineColorSeams.some(seam => seam.includes('probe("sgr.underline-color-indexed"')));
    assert.ok(sgrUnderlineColorSeams.some(seam => seam.includes('probe("sgr.underline-color-reset"')));
    assert.ok(!sgrUnderlineColorSeams.some(seam => seam.includes('rgbUnderlineProbe("sgr.underline.color")')),
        'the aggregate case shares the RGB body but is outside this batch');
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of sgrUnderlineColorSeams) {
        assert.throws(() => adaptSgrUnderlineColorCollector(verbatim().split(seam).join('CHANGED')), /Pinned SGR-underline-color probe seam changed/, seam.slice(0, 40));
        assert.throws(() => adaptSgrUnderlineColorCollector(verbatim() + seam + '\n'), /Pinned SGR-underline-color probe seam changed/, seam.slice(0, 40));
    }
    assert.throws(() => adaptSgrUnderlineColorCollector(''), /Pinned SGR-underline-color probe seam changed/);
});

test('refuses a pinned body whose fixture bytes, criteria or notes were substituted', () => {
    const edits = [
        ['\\x1B[4;38;5;4m\\x1B[59mA\\x1B[58;5;5mX', '\\x1B[4;38;5;4m\\x1B[59mA\\x1B[58;5;6mX'],           // indexed fixture index
        ['\\x1B[58;5;4mY', '\\x1B[58;5;3mY'],
        ['sameRgb(default5.fg, target5.underlineColor)', 'sameRgb(default4.fg, target5.underlineColor)'], // opposite-control criterion
        ['sameRgb(default4.fg, default5.fg)', 'false'],
        ['Palette controls resolve to indistinguishable colors', 'controls equal'],
        ['\\x1B[38;2;0;0;255mA\\x1B[38;2;0;255;0mB\\x1B[58;2;255;0;128mX', '\\x1B[38;2;0;0;255mA\\x1B[38;2;0;255;0mB\\x1B[58;2;255;0;129mX'],
        ['Default underline already matches the requested color', 'default matches'],
        ['\\x1B[4m\\x1B[59mC\\x1B[58;2;255;0;128mX\\x1B[59mY', '\\x1B[4m\\x1B[59mC\\x1B[58;2;255;0;128mX\\x1B[0mY'],   // SGR 59 swapped for SGR 0
        ['baseline.underlineColor === null || after.underlineColor === null', 'after.underlineColor === null'],
        ['Reset-state color readback changed representation', 'representation changed'],
        ['requestedUnderlineColor = { r: 255, g: 0, b: 128 };', 'requestedUnderlineColor = { r: 255, g: 0, b: 127 };'],
    ];
    for (const [from, to] of edits) {
        assert.ok(sgrUnderlineColorSeams.some(seam => seam.includes(from)), from);
        const mutated = sgrUnderlineColorSeams.map(seam => seam.replace(from, to)).join('\n/* gap */\n');
        assert.throws(() => adaptSgrUnderlineColorCollector(mutated), /Pinned SGR-underline-color probe seam changed/, from);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeSgrUnderlineAdapter = {};\n';
    const layered = adaptSgrUnderlineColorCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(sgrUnderlineColorAdapterId));
});
