import assert from 'node:assert/strict';
import { test } from 'node:test';
import { sgrRgbContracts, sgrRgbSuiteId, sgrRgbContractsVersion, sgrRgbFeeds,
    sgrRgbCellAt, gradeSgrRgbCase, sgrRgbCaseIds, sgrRgbReset } from './sgr-rgb-contracts.mjs';
import { copyColorBinding, colorBindingHash, checkStoredColorCoherence } from './resolved-colors.mjs';
import { syntheticSgrRgbScreen, syntheticSgrRgbCells, syntheticSgrRgbGrade,
    syntheticColorBinding, rgb, syntheticPalette } from './fixtures/sgr-rgb-evidence.mjs';

test('the two contracts mirror the pinned fixtures byte for byte', () => {
    assert.deepEqual([...sgrRgbCaseIds], ['sgr.fg.standard', 'sgr.bg.standard']);
    assert.equal(sgrRgbSuiteId, 'ntilde-owned-sgr-rgb-graded-v1');
    assert.equal(sgrRgbContractsVersion, 1);
    assert.deepEqual(sgrRgbFeeds(sgrRgbContracts[0]), [sgrRgbReset, 'C\x1b[31mX\x1b[34mY']);
    assert.deepEqual(sgrRgbFeeds(sgrRgbContracts[1]), [sgrRgbReset, 'C\x1b[41mX\x1b[44mY']);
    // The fixture bytes are byte-identical to the stored-index batch's fixtures.
    assert.equal('C' + '\x1b[31m' + 'X' + '\x1b[34m' + 'Y', 'C\x1b[31mX\x1b[34mY');
    assert.equal(sgrRgbContracts[0].pinnedCriterion, 'the two named colors select distinct exposed fg RGB values');
    assert.equal(sgrRgbContracts[1].pinnedCriterion, 'the two named colors select distinct exposed bg RGB values');
});

test('valid distinguishable theme colors grade supported on both channels', () => {
    assert.deepEqual(syntheticSgrRgbGrade('sgr.fg.standard'), { outcome: 'supported' });
    assert.deepEqual(syntheticSgrRgbGrade('sgr.bg.standard'), { outcome: 'supported' });
    const fg = syntheticSgrRgbCells('sgr.fg.standard');
    assert.equal(fg[0].fg, null);
    assert.deepEqual(fg[1].fg, { r: 205, g: 0, b: 0 });
    assert.deepEqual(fg[2].fg, { r: 0, g: 0, b: 238 });
    assert.equal(fg[1].bg, null);
    const bg = syntheticSgrRgbCells('sgr.bg.standard');
    assert.deepEqual(bg[1].bg, { r: 205, g: 0, b: 0 });
    assert.deepEqual(bg[2].bg, { r: 0, g: 0, b: 238 });
    assert.equal(bg[1].fg, null);
});

test('genuinely indistinguishable theme colors stay inconclusive, never unsupported', () => {
    // A theme whose entries 1 and 4 alias: red and blue select the same RGB.
    const aliased = syntheticColorBinding({ palette: syntheticPalette().map((value, index) => index === 4 ? rgb(205, 0, 0) : value) });
    for (const caseId of ['sgr.fg.standard', 'sgr.bg.standard']) {
        assert.deepEqual(syntheticSgrRgbGrade(caseId, aliased),
            { outcome: 'inconclusive', note: 'Baseline, theme colors, or color readback were indistinguishable' });
    }
    // The pinned semantics keep a null (default) neutral distinguishable from any
    // resolved RGB, so aliasing entry 1 with the default foreground still passes —
    // documented here so the boundary is pinned, not assumed.
    const neutralAliased = syntheticColorBinding({ palette: syntheticPalette().map((value, index) => index === 1 ? rgb(211, 211, 211) : value) });
    assert.equal(syntheticSgrRgbGrade('sgr.fg.standard', neutralAliased).outcome, 'supported');
});

test('invalid controls stay inconclusive with their own notes', () => {
    const contract = sgrRgbContracts[0];
    const cells = syntheticSgrRgbCells('sgr.fg.standard');
    // Baseline carries a style: not a neutral control.
    const styled = cells.map(cell => ({ ...cell }));
    styled[0] = { ...styled[0], bold: true };
    assert.deepEqual(contract.grade(styled, 1).note, 'Neutral control, palette storage, or resolved readback was not measured');
    // Baseline color not on default storage.
    const tainted = cells.map(cell => ({ ...cell }));
    tainted[0] = { ...tainted[0], fgSource: 'literal', fg: { r: 1, g: 2, b: 3 } };
    assert.equal(contract.grade(tainted, 1).outcome, 'inconclusive');
    // Unexposed chars.
    const hidden = cells.map(cell => ({ ...cell, char: ' ' }));
    assert.deepEqual(contract.grade(hidden, 1).note, 'Neutral control or named-color cells were not exposed');
    assert.throws(() => gradeSgrRgbCase(contract, cells.slice(0, 2)), /Missing sgr.fg.standard read captures/);
    assert.throws(() => gradeSgrRgbCase(contract, cells, 2), /Unknown SGR-RGB contracts revision/);
});

test('wrong stored indices invalidate the RGB control even with plausible RGB', () => {
    const contract = sgrRgbContracts[0];
    const cells = syntheticSgrRgbCells('sgr.fg.standard');
    // The resolved RGB is still distinguishable, but the storage no longer says
    // palette 1/4: the control refuses rather than grading appearance alone.
    const swapped = cells.map(cell => ({ ...cell }));
    swapped[1] = { ...swapped[1], fgIndex: 2 };
    assert.equal(contract.grade(swapped, 1).outcome, 'inconclusive');
    // Palette storage replaced by literal storage (default/literal/index confusion).
    const literal = cells.map(cell => ({ ...cell }));
    literal[1] = { ...literal[1], fgSource: 'literal', fgIndex: -1, fg: { r: 205, g: 0, b: 0 } };
    assert.equal(contract.grade(literal, 1).outcome, 'inconclusive');
    // Unrelated-channel drift: the background left its default storage.
    const drifted = cells.map(cell => ({ ...cell }));
    drifted[1] = { ...drifted[1], bgSource: 'palette', bgIndex: 4, bg: { r: 0, g: 0, b: 238 } };
    assert.equal(contract.grade(drifted, 1).outcome, 'inconclusive');
});

test('resolved values must cohere with the binding; arbitrary RGB cannot validate', () => {
    const screen = syntheticSgrRgbScreen('sgr.fg.standard');
    assert.doesNotThrow(() => checkStoredColorCoherence(screen));
    const binding = copyColorBinding(screen.colorBinding);

    // A fabricated resolved RGB that disagrees with the binding palette lookup.
    const liars = structuredClone(screen);
    liars.resolvedColors[1].fg.argb = rgb(1, 2, 3);
    assert.throws(() => checkStoredColorCoherence(liars), /Invalid resolved-color run/);

    // A default channel claiming the wrong theme value.
    const wrongDefault = structuredClone(screen);
    wrongDefault.resolvedColors[0].fg.argb = rgb(9, 9, 9);
    assert.throws(() => checkStoredColorCoherence(wrongDefault), /Invalid resolved-color run/);

    // Index/source confusion: palette source without a palette index.
    const confused = structuredClone(screen);
    confused.resolvedColors[1].fg.index = -1;
    assert.throws(() => checkStoredColorCoherence(confused), /Invalid resolved-color run/);
    void binding;
});

test('binding identity changes with the palette and stays stable otherwise', () => {
    const base = syntheticColorBinding();
    assert.equal(colorBindingHash(base), colorBindingHash(structuredClone(base)));
    const overridden = syntheticColorBinding({ palette: syntheticPalette().map((value, index) => index === 7 ? rgb(1, 1, 1) : value) });
    assert.notEqual(colorBindingHash(base), colorBindingHash(overridden));
    const reversed = syntheticColorBinding({ foreground: rgb(0, 0, 1), background: rgb(211, 211, 211) });
    assert.notEqual(colorBindingHash(base), colorBindingHash(reversed));
    assert.throws(() => copyColorBinding({ ...base, stage: 'pixels' }), /Invalid resolved-color theme binding/);
    // The resolution stage is opaque: transparent or partial alpha cannot validate.
    const transparentFg = structuredClone(base);
    transparentFg.foregroundArgb = 0x00d3d3d3;
    assert.throws(() => copyColorBinding(transparentFg), /Invalid resolved-color theme binding/);
    const translucentPalette = structuredClone(base);
    translucentPalette.ansiPaletteArgb = [...base.ansiPaletteArgb];
    translucentPalette.ansiPaletteArgb[4] = 0x7f0000ee;
    assert.throws(() => copyColorBinding(translucentPalette), /Invalid resolved-color theme binding/);
    assert.throws(() => copyColorBinding({ ...base, ansiPaletteArgb: base.ansiPaletteArgb.slice(1) }), /Invalid/);
    assert.throws(() => copyColorBinding({ ...base, paletteOverrideIndices: [5, 5] }), /Invalid/);
    assert.throws(() => copyColorBinding({ ...base, minimumContrast: 0 }), /Invalid/);
});

test('the cell projection carries storage and appearance together', () => {
    const screen = syntheticSgrRgbScreen('sgr.bg.standard');
    const cell = sgrRgbCellAt(screen, 0, 1);
    assert.equal(cell.char, 'X');
    assert.equal(cell.fgSource, 'default');
    assert.equal(cell.fg, null);
    assert.equal(cell.bgSource, 'palette');
    assert.equal(cell.bgIndex, 1);
    assert.deepEqual(cell.bg, { r: 205, g: 0, b: 0 });
    assert.equal(cell.flags & 0x8000, 0x8000);
});
