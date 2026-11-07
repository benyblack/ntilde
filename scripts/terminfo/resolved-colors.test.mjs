import assert from 'node:assert/strict';
import { test } from 'node:test';
import { copyColorBinding, copyResolvedColors, expandResolvedColors, exposedRgb,
    colorBindingHash, checkStoredColorCoherence, resolvedColorSources } from './resolved-colors.mjs';
import { syntheticSgrRgbScreen, syntheticColorBinding, rgb, syntheticPalette } from './fixtures/sgr-rgb-evidence.mjs';

const cells = 360;

test('a valid binding copies normalized with every field checked', () => {
    const binding = copyColorBinding(syntheticColorBinding());
    assert.equal(binding.stage, 'theme-resolution');
    assert.equal(binding.themeName, 'Synthetic Dark');
    assert.equal(binding.ansiPaletteArgb.length, 256);
    assert.deepEqual(binding.paletteOverrideIndices, []);
    assert.equal(binding.minimumContrast, 1);
    assert.deepEqual(resolvedColorSources, ['default', 'palette', 'literal']);
});

test('run validation enforces budgets, completeness and canonicity', () => {
    const binding = syntheticColorBinding();
    const run = count => ({ count, fg: { source: 'default', index: -1, argb: binding.foregroundArgb },
        bg: { source: 'default', index: -1, argb: binding.backgroundArgb } });
    assert.equal(copyResolvedColors([run(cells)], cells, binding).length, 1);
    assert.throws(() => copyResolvedColors([run(cells + 1)], cells, binding), /resolved-color viewport/);
    assert.throws(() => copyResolvedColors([run(cells - 1)], cells, binding), /Incomplete resolved-color viewport/);
    assert.throws(() => copyResolvedColors([run(1), run(cells - 1)], cells, binding), /Noncanonical resolved-color viewport/);
    assert.throws(() => copyResolvedColors([], cells, binding), /Invalid resolved-color budget/);
    assert.throws(() => copyResolvedColors([run(0)], cells, binding), /Invalid resolved-color run/);
    assert.throws(() => copyResolvedColors([{ ...run(1), fg: { source: 'hue', index: -1, argb: 0 } }], cells, binding), /Invalid resolved-color run/);
    assert.throws(() => copyResolvedColors([{ ...run(1), fg: { source: 'palette', index: 256, argb: binding.ansiPaletteArgb[1] } }], cells, binding), /Invalid resolved-color run/);
    assert.throws(() => copyResolvedColors([{ ...run(1), fg: { source: 'palette', index: 1, argb: rgb(9, 9, 9) } }], cells, binding), /Invalid resolved-color run/);
    assert.throws(() => copyResolvedColors([{ ...run(1), fg: { source: 'default', index: 3, argb: binding.foregroundArgb } }], cells, binding), /Invalid resolved-color run/);
    // Literal storage needs no binding entry: any opaque stored bytes are valid.
    const literal = { ...run(1), fg: { source: 'literal', index: -1, argb: rgb(12, 34, 56) } };
    assert.equal(copyResolvedColors([literal, run(cells - 1)], cells, binding).length, 2);
});

test('expansion and exposed-RGB projection behave per channel state', () => {
    const binding = syntheticColorBinding();
    const expanded = expandResolvedColors([
        { count: 1, fg: { source: 'palette', index: 1, argb: binding.ansiPaletteArgb[1] }, bg: { source: 'default', index: -1, argb: binding.backgroundArgb } },
        { count: 1, fg: { source: 'literal', index: -1, argb: rgb(1, 2, 3) }, bg: { source: 'default', index: -1, argb: binding.backgroundArgb } },
    ], 2, binding);
    assert.equal(expanded.length, 2);
    assert.deepEqual(exposedRgb(expanded[0].fg), { r: 205, g: 0, b: 0 });
    assert.deepEqual(exposedRgb(expanded[1].fg), { r: 1, g: 2, b: 3 });
    assert.equal(exposedRgb(expanded[0].bg), null);
});

test('stored coherence binds resolved channels to attribute runs', () => {
    const screen = syntheticSgrRgbScreen('sgr.fg.standard');
    assert.doesNotThrow(() => checkStoredColorCoherence(screen));
    // Palette index disagreement between the projections.
    const indexLie = structuredClone(screen);
    indexLie.attributeRuns[1] = { count: 1, flags: 0x0200 | 0x4000, fg: 3, bg: -1 };
    assert.throws(() => checkStoredColorCoherence(indexLie), /index disagrees with stored attributes/);
    // Source disagreement: stored default marker but resolved palette.
    const sourceLie = structuredClone(screen);
    sourceLie.attributeRuns[1] = { count: 1, flags: 768, fg: -1, bg: -1 };
    assert.throws(() => checkStoredColorCoherence(sourceLie), /source disagrees with stored attributes/);
    // Missing attribute runs entirely: the check cannot run.
    const bare = structuredClone(screen);
    delete bare.attributeRuns;
    assert.throws(() => checkStoredColorCoherence(bare), /Error/);
});

test('binding digests separate palettes, defaults and overrides', () => {
    const base = syntheticColorBinding();
    const overridden = syntheticColorBinding();
    overridden.ansiPaletteArgb = syntheticPalette().map((value, index) => index === 12 ? rgb(9, 8, 7) : value);
    overridden.paletteOverrideIndices = [12];
    assert.notEqual(colorBindingHash(base), colorBindingHash(overridden));
    assert.equal(typeof colorBindingHash(base), 'string');
    assert.equal(colorBindingHash(base).length, 64);
});
