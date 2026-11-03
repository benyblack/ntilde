import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeHistoryCases } from './history-cases.mjs';
const cases = makeHistoryCases(10, 48), find = id => cases.find(value => value.id === id);
test('history suite fits its own snapshot and actual-history bounds', () => {
    assert.equal(cases.length, 30); assert.equal(new Set(cases.map(value => value.id)).size, 30);
    assert.ok(cases.length * 2 + 3 <= 64);
    for (const item of cases) {
        assert.ok(item.after.history.length <= 4); assert.equal(item.after.grid.length, 10);
        assert.ok(item.after.grid.every(row => row.length === 48)); assert.notEqual(item.before.grid[0][0], item.after.grid[0][0]);
    }
    assert.throws(() => makeHistoryCases(9, 48)); assert.throws(() => makeHistoryCases(128, 256));
});
test('line-feed accumulation starts with independently known zero history', () => {
    for (const name of ['lf', 'crlf', 'index']) {
        const item = find('history.accumulate.' + name);
        assert.deepEqual(item.before.history, []); assert.deepEqual(item.after.history, ['HROW000-ABCDEFGH']);
        assert.equal(item.after.grid[0].map(value => value.text).join('').trimEnd(), 'HROW001-ABCDEFGH');
        assert.equal(item.after.row, 9); assert.equal(item.after.col, name === 'crlf' ? 0 : 4);
    }
});
test('main full scroll-up appends ordered rows; alt and margin scrolling preserve history', () => {
    const main = find('history.scroll-up.main.2'); assert.deepEqual(main.after.history, ['HISTORY0', 'HISTORY1', 'HROW000-ABCDEFGH', 'HROW001-ABCDEFGH']);
    for (const id of ['history.scroll-up.alt.2', 'history.scroll-up.main.margins', 'history.scroll-down.main.2', 'history.reverse-index.main.top']) {
        const item = find(id); assert.deepEqual(item.after.history, item.before.history);
    }
    const margin = find('history.scroll-up.main.margins'); assert.deepEqual(margin.before.grid[0], margin.after.grid[0]); assert.deepEqual(margin.before.grid[7], margin.after.grid[7]);
});
test('reverse index scrolls only at the observed top margin and otherwise moves the cursor', () => {
    const top = find('history.reverse-index.margins.top'); assert.equal(top.after.row, 1); assert.ok(top.after.grid[1].every(value => value.text === ' '));
    const middle = find('history.reverse-index.margins.middle'); assert.equal(middle.after.row, 2); assert.deepEqual(middle.before.grid, middle.after.grid);
    const outside = find('history.reverse-index.margins.outside'); assert.equal(outside.after.row, 0); assert.deepEqual(outside.before.grid, outside.after.grid);
});
test('margin fixtures distinguish full reset, invalid-region rejection and origin-relative homing', () => {
    for (const name of ['default', 'zero']) { const item = find('history.margins.' + name); assert.equal(item.after.top, 0); assert.equal(item.after.bottom, 9); }
    for (const name of ['reversed', 'equal']) { const item = find('history.margins.invalid-' + name); assert.deepEqual(item.after, item.before); assert.equal(item.before.row, 0); }
    const origin = find('history.margins.origin-home'); assert.equal(origin.before.originMode, true); assert.equal(origin.after.row, 2); assert.equal(origin.after.top, 2); assert.equal(origin.after.col, 0);
});
