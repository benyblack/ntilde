import assert from 'node:assert/strict';
import { test } from 'node:test';
import { copyCellDetails, expandCellDetails } from './cell-details.mjs';

const normal = (count = 180) => ({ count, flags: 768, fg: -1, bg: -1, underlineStyle: 0, underlineColorIndex: -1 });

test('complete runs copy literals, distinguish palette/default and retain underline ARGB independently', () => {
    const runs = [{ ...normal(1), flags: 8, foregroundArgb: 0xff0180ff, backgroundArgb: 0xfffe0200,
        underlineStyle: 3, underlineArgb: 0xff0c2238 },
    { ...normal(1), flags: 49160, fg: 1, bg: 4, underlineStyle: 5, underlineColorIndex: 123 }, normal(178)];
    const copied = copyCellDetails(runs, 180), cells = expandCellDetails(runs, 180);
    runs[0].foregroundArgb = 0;
    assert.equal(copied[0].foregroundArgb, 0xff0180ff);
    assert.equal(cells.length, 180);
    assert.equal(cells[0].underlineArgb, 0xff0c2238);
    assert.equal(cells[1].foregroundArgb, null);
    assert.equal(cells[1].fg, 1);
    assert.equal(cells[2].fg, -1);
    assert.equal(cells[2].foregroundArgb, null);
    cells[2].flags = 0;
    assert.equal(cells[3].flags, 768);
});

for (const [name, change] of [
    ['missing flags', value => { delete value.flags; }],
    ['dirty flag', value => { value.flags |= 1024; }],
    ['oversized flags', value => { value.flags = 65536; }],
    ['default and palette conflict', value => { value.flags |= 16384; value.fg = 1; }],
    ['palette without index', value => { value.flags = 16896; }],
    ['default with index', value => { value.fg = 1; }],
    ['default with literal', value => { value.foregroundArgb = 0xff000000; }],
    ['palette with literal', value => { value.flags = 16896; value.fg = 1; value.foregroundArgb = 0xff000000; }],
    ['literal without ARGB', value => { value.flags = 512; }],
    ['negative ARGB', value => { value.flags = 512; value.foregroundArgb = -1; }],
    ['oversized ARGB', value => { value.flags = 512; value.foregroundArgb = 0x100000000; }],
    ['fractional ARGB', value => { value.flags = 512; value.foregroundArgb = 1.5; }],
    ['background contradiction', value => { value.backgroundArgb = 0; }],
    ['missing underline index', value => { delete value.underlineColorIndex; }],
    ['style without flag', value => { value.underlineStyle = 1; }],
    ['flag without style', value => { value.flags |= 8; }],
    ['unknown style', value => { value.flags |= 8; value.underlineStyle = 6; }],
    ['disabled underline color', value => { value.underlineArgb = 0xff000000; }],
    ['underline index and literal', value => { value.flags |= 8; value.underlineStyle = 1; value.underlineColorIndex = 1; value.underlineArgb = 0; }],
    ['out-of-range palette', value => { value.underlineColorIndex = 256; }],
    ['zero count', value => { value.count = 0; }],
    ['fractional count', value => { value.count = 1.5; }],
    ['incomplete coverage', value => { value.count--; }],
    ['excess coverage', value => { value.count++; }],
]) test('refuses ' + name, () => {
    const value = normal(); change(value); assert.throws(() => copyCellDetails([value], 180));
});

test('rejects missing/noncanonical runs and enforces both budgets at the boundary', () => {
    assert.throws(() => copyCellDetails(null, 180));
    assert.throws(() => copyCellDetails([normal(90), normal(90)], 180));
    assert.equal(copyCellDetails([normal(8192)], 8192)[0].count, 8192);
    assert.throws(() => copyCellDetails([normal(8193)], 8193));
    const runs = Array.from({ length: 1025 }, (_, index) => ({ ...normal(1), flags: index % 2 ? 769 : 768 }));
    assert.equal(copyCellDetails(runs.slice(0, 1024), 1024).length, 1024);
    assert.throws(() => copyCellDetails(runs, 1025));
});


test('overline is validated only on explicit opt-in and splits canonical runs without changing legacy copies', () => {
    const plain = { ...normal(90), overline: false }, above = { ...normal(90), overline: true };
    assert.deepEqual(copyCellDetails([above], 90), copyCellDetails([normal(90)], 90));
    const runs = copyCellDetails([plain, above], 180, true);
    assert.equal(runs[0].overline, false);
    assert.equal(runs[1].overline, true);
    assert.throws(() => copyCellDetails([plain, above], 180), /Noncanonical/);
    for (const value of [undefined, null, 1, 'false'])
        assert.throws(() => copyCellDetails([{ ...plain, overline: value }], 90, true), /overline/);
});
