import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeEditingCases } from './editing-cases.mjs';
const rows = 10, cols = 48, cases = makeEditingCases(rows, cols), find = id => cases.find(value => value.id === id);
test('all bounded fixtures retain independent grids and known target spellings', () => {
    assert.equal(cases.length, 35); assert.equal(new Set(cases.map(value => value.id)).size, 35);
    for (const value of cases) {
        assert.equal(value.before.grid.length, rows); assert.equal(value.after.grid.length, rows);
        assert.ok(value.before.grid.every(row => row.length === cols)); assert.ok(value.after.grid.every(row => row.length === cols));
        assert.notEqual(value.before.grid, value.after.grid); assert.notEqual(value.before.grid[0][0], value.after.grid[0][0]);
    }
    for (const args of [[9, 48], [10, 47], [128, 256], [11.5, 48]]) assert.throws(() => makeEditingCases(...args));
});
test('ED cursor boundary is inclusive in both directions and preserves opposite rows', () => {
    const below = find('erase.display.0'), above = find('erase.display.1');
    assert.equal(below.after.grid[2][3].text, below.before.grid[2][3].text); assert.equal(below.after.grid[2][4].text, ' ');
    assert.deepEqual(below.after.grid[0], below.before.grid[0]);
    assert.equal(above.after.grid[2][4].text, ' '); assert.equal(above.after.grid[2][5].text, above.before.grid[2][5].text);
    assert.deepEqual(above.after.grid[9], above.before.grid[9]);
});
test('ECH clamps at the right edge without moving the cursor or adjacent rows', () => {
    const value = find('erase.characters.999'); assert.equal(value.after.col, 4);
    assert.deepEqual(value.before.grid[1], value.after.grid[1]); assert.equal(value.after.grid[2].slice(4).every(cell => cell.text === ' '), true);
});
test('line insertion/deletion is bounded by margins and ignores outside cursor', () => {
    for (const mode of ['insert', 'delete']) {
        const value = find(`editing.${mode}-lines.2`); assert.deepEqual(value.before.grid[0], value.after.grid[0]); assert.deepEqual(value.before.grid[7], value.after.grid[7]);
        assert.deepEqual(value.after.grid[mode === 'insert' ? 4 : 2], value.before.grid[mode === 'insert' ? 2 : 4]);
        const ignored = find(`editing.${mode}-lines.outside-margin`); assert.deepEqual(ignored.before.grid, ignored.after.grid);
    }
});
test('repeat has independent last-character control and exact count/default semantics', () => {
    for (const [parameter, count] of [['default', 1], ['0', 1], ['3', 3]]) {
        const value = find(`editing.repeat.${parameter}`); assert.equal(value.before.grid[0][0].text, 'Q');
        assert.ok(value.sequence.startsWith('Q\x1b['));
        assert.equal(value.after.col, 5 + count); assert.equal(value.after.grid[2].slice(4, 5 + count).map(cell => cell.text).join(''), 'Q'.repeat(count + 1));
    }
});

test('erase and editing batches each fit an independent 64-snapshot budget', () => {
    const erase = cases.filter(value => value.id.startsWith('erase.')), editing = cases.filter(value => value.id.startsWith('editing.'));
    assert.equal(erase.length, 13); assert.equal(editing.length, 22);
    assert.ok(erase.length * 2 + 3 <= 64); assert.ok(editing.length * 2 + 3 <= 64);
});
test('ED3 preserves viewport while changing observed history and BCE leaves a known RGB control', () => {
    const saved = find('erase.saved-lines'); assert.deepEqual(saved.before.history, ['HISTORY0', 'HISTORY1']); assert.deepEqual(saved.after.history, []);
    assert.deepEqual(saved.before.grid, saved.after.grid); assert.equal(saved.before.alternate, false);
    for (const id of ['erase.display.background-and-margins', 'erase.characters.background-and-margins']) {
        const value = find(id); assert.equal(value.before.grid[0][0].backgroundArgb, 0xff012cc7);
        assert.deepEqual(value.before.grid[0][0], value.after.grid[0][0]);
        assert.equal(value.after.grid[value.before.row][value.before.col].backgroundArgb, 0xff012cc7);
        assert.equal(value.after.grid[value.before.row][value.before.col].text, ' ');
    }
});

test('known nonempty history is retained as an independent control for every viewport operation', () => {
    for (const item of cases) {
        assert.deepEqual(item.before.history, ['HISTORY0', 'HISTORY1']);
        assert.deepEqual(item.after.history, item.id === 'erase.saved-lines' ? [] : item.before.history);
    }
    for (const prefix of ['erase.', 'editing.']) {
        const selected = cases.filter(item => item.id.startsWith(prefix));
        assert.ok(selected[0].setup.includes('HISTORY0\r\nHISTORY1'));
        assert.ok(selected.every(item => !item.setup.includes('\x1b[3J') && !item.setup.includes('\x1b[2J')), 'Display/history erase is not used to establish its own positive controls.');
    }
});

test('scene setup overwrites every physical cell without depending on ED2 history preservation', () => {
    const value = find('erase.display.2');
    for (let row = 0; row < rows; row++) assert.ok(value.setup.includes(`\x1b[${row + 1};1H` + `ROW${String(row).padStart(3, '0')}-ABCDEFGH`.padEnd(cols, ' ')));
    assert.ok(!value.setup.includes('\x1b[2J')); assert.ok(!value.setup.includes('\x1b[3J'));
});
