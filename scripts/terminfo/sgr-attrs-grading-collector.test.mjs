import assert from 'node:assert/strict';
import { test } from 'node:test';
import { adaptSgrAttrsCollector, sgrAttrsAdapterId } from './sgr-attrs-grading-collector.mjs';

/** The exact pinned seams as they appear in the retained pinned bundle. */
const seams = [
    String.raw`sgrProbe("sgr.faint", "\x1B[2m", (cell) => cell.dim === true)`,
    String.raw`sgrProbe("sgr.underline.single", "\x1B[4m", (cell) => !!cell.underline)`,
    String.raw`sgrProbe("sgr.inverse", "\x1B[7m", (cell) => cell.inverse === true)`,
    String.raw`sgrProbe("sgr.hidden", "\x1B[8m", (cell) => cell.hidden === true)`,
    String.raw`sgrProbe("sgr.strikethrough", "\x1B[9m", (cell) => cell.strikethrough === true)`,
    'function sgrProbe(id, sequence, check, noObservableWhen) {',
    '      ctx.feed(sequence + "X");\n      const cell = ctx.getCell(0, 0);',
    'const measured = cell.char === "X" && check(cell) === true;',
    '`${id}: attribute applied to the rendered X cell`',
    '"No rendered X cell to evaluate"',
    'Cell readback cannot distinguish unsupported styling from unreported attributes or collapsed styles; a negative needs rendering review',
    'export const ntildeSgrRgbAdapter = Object.freeze({',
    'export function ntildeRunSgrRgbTermless(probeId, ctx) {',
];
/** A verbatim source: seams appear exactly once each, as plain text. */
const verbatim = () => seams.join('\n/* gap */\n') + '\n';

test('derives the attributes extension by appending exports only', () => {
    const source = verbatim();
    const derived = adaptSgrAttrsCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeSgrAttrsAdapter = Object.freeze({\n  id: "${sgrAttrsAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunSgrAttrsTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["sgr.underline.single","sgr.inverse","sgr.faint","sgr.hidden","sgr.strikethrough"]'));
    assert.equal(adaptSgrAttrsCollector(source), derived);
});

test('refuses derivation when any pinned seam changed or repeated', () => {
    for (const seam of seams) {
        const broken = verbatim().split(seam).join('CHANGED');
        assert.throws(() => adaptSgrAttrsCollector(broken), /Pinned SGR-attributes probe seam changed/, seam);
        const repeated = verbatim() + seam + '\n';
        assert.throws(() => adaptSgrAttrsCollector(repeated), /Pinned SGR-attributes probe seam changed/, seam);
    }
    assert.throws(() => adaptSgrAttrsCollector(''), /Pinned SGR-attributes probe seam changed/);
});

test('refuses a pinned definition whose underline check or sequence changed', () => {
    for (const [from, to] of [
        [String.raw`"\x1B[4m", (cell) => !!cell.underline)`, String.raw`"\x1B[4:3m", (cell) => !!cell.underline)`],
        ['(cell) => cell.dim === true', '(cell) => cell.bold === true'],
        [String.raw`"\x1B[8m"`, String.raw`"\x1B[28m"`],
    ]) {
        assert.throws(() => adaptSgrAttrsCollector(verbatim().replace(from, to)), /Pinned SGR-attributes probe seam changed/);
    }
});

test('the derivation appends after any earlier layer without rewriting it', () => {
    const earlier = verbatim() + '// earlier layers\nexport const ntildeSgrColorsAdapter = {};\n';
    const layered = adaptSgrAttrsCollector(earlier);
    assert.ok(layered.startsWith(earlier));
    assert.ok(layered.includes(sgrAttrsAdapterId));
});
