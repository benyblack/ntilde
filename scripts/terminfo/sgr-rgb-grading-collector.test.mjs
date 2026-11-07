import assert from 'node:assert/strict';
import { test } from 'node:test';
import { adaptSgrRgbCollector, sgrRgbAdapterId } from './sgr-rgb-grading-collector.mjs';
import { adaptSgrColorsCollector, sgrColorsAdapterId } from './sgr-colors-grading-collector.mjs';

function syntheticSgrColorsSource() {
    // A minimal synthetic SGR-colors extension carrying the exact seams.
    return 'const seam0 = `namedColorProbe("sgr.fg.standard", "fg", 31, 34)`;\n'
        + 'const seam1 = `namedColorProbe("sgr.bg.standard", "bg", 41, 44)`;\n'
        + 'const seam2 = `function namedColorProbe(id, channel, firstCode, secondCode) {`;\n'
        + 'const seam3 = `the two named colors select distinct exposed `;\n'
        + 'const seam4 = `export const ntildeSgrColorsAdapter = Object.freeze({`;\n'
        + 'const seam5 = `export function ntildeRunSgrColorsTermless(probeId, ctx) {`;\n';
}

test('derives the RGB extension by appending exports only', () => {
    const source = syntheticSgrColorsSource();
    const derived = adaptSgrRgbCollector(source);
    assert.ok(derived.startsWith(source));
    assert.ok(derived.includes(`export const ntildeSgrRgbAdapter = Object.freeze({\n  id: "${sgrRgbAdapterId}"`));
    assert.ok(derived.includes('export function ntildeRunSgrRgbTermless(probeId, ctx) {'));
    assert.ok(derived.includes('["sgr.fg.standard","sgr.bg.standard"]'));
    // Idempotent layering: deriving twice over the same input yields the same bytes.
    assert.equal(adaptSgrRgbCollector(source), derived);
});

test('refuses derivation when any pinned seam changed', () => {
    for (const seam of ['namedColorProbe("sgr.fg.standard", "fg", 31, 34)',
        'namedColorProbe("sgr.bg.standard", "bg", 41, 44)',
        'function namedColorProbe(id, channel, firstCode, secondCode) {',
        'the two named colors select distinct exposed ',
        'export const ntildeSgrColorsAdapter = Object.freeze({',
        'export function ntildeRunSgrColorsTermless(probeId, ctx) {']) {
        const broken = syntheticSgrColorsSource().split(seam).join('CHANGED');
        assert.throws(() => adaptSgrRgbCollector(broken), /Pinned SGR-RGB probe seam changed/, seam);
    }
    assert.throws(() => adaptSgrRgbCollector(''), /Pinned SGR-RGB probe seam changed/);
});

test('the layering composes with the SGR-colors derivation unchanged', () => {
    // The SGR-colors extension itself must still derive identically beneath.
    const asciiLike = 'var init_src = __esm(() => {\n'
        + 'probe("text.basic", (ctx) => {\n        ctx.feed("\\x1B[2J\\x1B[H");\n'
        + '"Hello appears after a measured empty control"\n'
        + 'probe("text.overwrite", (ctx) => {\n        ctx.feed("AB");\n'
        + '"C overwrites A while B remains"\n'
        + 'sgrProbe("sgr.bold", "\\x1B[1m", (cell) => cell.bold === true)\n'
        + 'sgrProbe("sgr.italic", "\\x1B[3m", (cell) => cell.italic === true)\n'
        + 'namedColorProbe("sgr.fg.standard", "fg", 31, 34)\n'
        + 'namedColorProbe("sgr.bg.standard", "bg", 41, 44)\n'
        + String.raw`resetProbe("sgr.selective-reset.bold", "\x1B[1;2;3m", "\x1B[22m", ["bold", "dim"], ["italic"])` + '\n'
        + String.raw`resetProbe("sgr.selective-reset.italic", "\x1B[1;3m", "\x1B[23m", ["italic"], ["bold"])` + '\n'
        + String.raw`resetProbe("sgr.reset", "\x1B[1;3;4m", "\x1B[0m", ["bold", "italic", "underline"])` + '\n'
        + 'function measuredReset(ctx, id, setup, reset, set, preserve) {\n'
        + 'function namedColorProbe(id, channel, firstCode, secondCode) {\n'
        + 'the two named colors select distinct exposed \n'
        + 'measured setup attributes clear on Y and unrelated attributes remain set\n'
        + '};\nexport const ntildeGradingAdapter = Object.freeze({ id: "x" });\n';
    const colors = adaptSgrColorsCollector(asciiLike);
    assert.ok(colors.includes(sgrColorsAdapterId));
    const rgbLayer = adaptSgrRgbCollector(colors);
    assert.ok(rgbLayer.startsWith(colors));
    assert.ok(rgbLayer.includes(sgrRgbAdapterId));
});

test('a seam appearing twice refuses derivation rather than guessing', () => {
    const source = syntheticSgrColorsSource()
        + 'const duplicate = `namedColorProbe("sgr.fg.standard", "fg", 31, 34)`;\n';
    assert.throws(() => adaptSgrRgbCollector(source), /Pinned SGR-RGB probe seam changed/);
});
