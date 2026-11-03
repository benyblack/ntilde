import test from 'node:test';
import assert from 'node:assert/strict';
import { makeResizeCases } from './resize-cases.mjs';
const text = row => row.map(cell => cell.text).join('').trimEnd();

test('width round trip rewraps one hard line and maps its cursor without history', () => {
    const item = makeResizeCases(40, 140)[0];
    assert.equal(text(item.before.grid[0]).length, 138);
    assert.deepEqual([item.after.row, item.after.col, item.after.history.length], [1, 5, 0]);
    assert.equal(text(item.after.grid[1]).length, 5);
    assert.deepEqual(item.restored, item.before);
    assert.ok(!item.setup.includes(' '), 'Unused row tails must remain unwritten, not authored space padding.');
});
test('height shrink preserves complete ordered displaced hard lines', () => {
    const item = makeResizeCases(40, 140)[1];
    assert.equal(item.after.history.length, 4);
    assert.deepEqual(item.after.history, item.before.grid.slice(0, 4).map(text));
    assert.deepEqual([item.after.row, item.after.col], [35, 16]);
});
test('combined reflow retains every logical line across complete bounded history', () => {
    const item = makeResizeCases(40, 140)[2];
    assert.equal(item.after.history.length, 44);
    assert.deepEqual([item.after.row, item.after.col], [35, 5]);
    const physical = [...item.after.history, ...item.after.grid.map(text)];
    for (let index = 0; index < 40; index++)
        assert.equal(physical[index * 2] + physical[index * 2 + 1], text(item.before.grid[index]));
});
test('known baseline 41x140 fits the complete history cap', () => {
    assert.equal(makeResizeCases(41, 140)[2].after.history.length, 45);
});
for (const [rows, cols] of [[11, 140], [53, 140], [40, 54], [40, 257], [52, 256]]) {
    test(`out of budget ${rows}x${cols} is rejected`, () => assert.throws(() => makeResizeCases(rows, cols)));
}

test('height growth retains top anchoring and appends blank padding instead of pulling history', () => {
    const item = makeResizeCases(40, 140)[1];
    assert.equal(item.restored.history.length, 4); assert.equal(item.restored.row, 35);
    assert.equal(text(item.restored.grid[0]), 'RROW004-ABCDEFGH');
    assert.ok(item.restored.grid.slice(-4).every(row => text(row) === ''));
});
test('width growth remaps the first visible logical line, including a split history boundary', () => {
    for (const [rows, cursor] of [[40, 17], [41, 18]]) {
        const item = makeResizeCases(rows, 140)[2];
        assert.equal(item.restored.history.length, 22); assert.equal(item.restored.row, cursor);
        assert.equal(item.restored.col, 138); assert.ok(text(item.restored.grid[0]).startsWith('LONG022-'));
    }
});
test('every restored hard line remains ordered and intact across complete history and viewport', () => {
    for (const item of makeResizeCases(40, 140)) {
        const original = item.before.grid.map(text).filter(Boolean);
        const restored = [...item.restored.history, ...item.restored.grid.map(text)].filter(Boolean);
        assert.deepEqual(restored, original);
    }
});
