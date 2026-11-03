import test from 'node:test';
import assert from 'node:assert/strict';
import { makeAlternateCases, makeCursorCases, makeBankCases, makeSavedCases } from './saved-cases.mjs';
const cases = makeAlternateCases(12, 60);
test('three explicit alternate plans fit the paired-frame budget', () => {
    assert.equal(cases.length, 3); assert.equal(cases.reduce((n, v) => n + v.stages.length, 3), 25);
    for (const item of cases) for (const stage of item.stages) {
        assert.equal(stage.expected.grid.length, 12); assert.ok(stage.expected.grid.every(row => row.length === 60));
        assert.ok(stage.expected.row >= 0 && stage.expected.row < 12 && stage.expected.col >= 0 && stage.expected.col < 60);
    }
});
test('47 retains authored cells, 1047 clears on exit while preserving cursor and pen witnesses', () => {
    const retained = cases[0].stages.find(v => v.id === 'reentry').expected;
    const cleared = cases[1].stages.find(v => v.id === 'reentry').expected;
    assert.equal(retained.grid[2].slice(3, 10).map(c => c.text).join(''), 'ALT000Z');
    assert.ok(cleared.grid.flat().every(c => c.text === ' '));
    assert.deepEqual([cleared.row, cleared.col], [2, 10]);
    assert.equal(cases[1].stages.at(-1).expected.grid[2][10].fg, 1);
    assert.equal(cases[1].stages.find(v => v.id === 'main-pen').expected.grid[1][5].fg, 2);
});
test('1049 clears initially, ignores repeated entry and retains the later alternate on 47 reentry', () => {
    const item = cases[2]; assert.ok(item.stages[1].expected.grid.flat().every(c => c.text === ' '));
    assert.deepEqual(item.stages[2].expected, item.stages[3].expected);
    assert.deepEqual(item.stages[2].expected, item.stages[6].expected);
    assert.equal(item.stages[7].expected.grid[3][5].fg, 1);
});
test('case expectations own their cell objects and never mutate another stage', () => {
    const copy = makeAlternateCases(12, 60); copy[0].stages[0].expected.grid[0][0].text = 'X';
    assert.equal(copy[0].stages[3].expected.grid[0][0].text, 'M');
    assert.equal(copy[1].stages[0].expected.grid[0][0].text, 'M');
});
for (const [rows, cols] of [[9,60],[12,47],[129,60],[128,256]]) test(`reject grid ${rows}x${cols}`, () => assert.throws(() => makeAlternateCases(rows,cols)));

test('three explicit cursor aliases witness complete pens, origin, pending wrap and both G0/G1 designations', () => {
    for (const item of makeCursorCases(12, 60)) {
        assert.equal(item.stages.length, 5);
        const [control, changed, restored, wrapped, ascii] = item.stages.map(v => v.expected);
        assert.equal(control.pendingWrap, true); assert.equal(control.originMode, true);
        assert.equal(changed.autoWrap, false); assert.equal(changed.originMode, false);
        assert.equal(restored.autoWrap, false); assert.equal(restored.originMode, true); assert.equal(restored.pendingWrap, true);
        assert.deepEqual([restored.row, restored.col], [2, 59]);
        assert.equal(wrapped.grid[3][0].flags, 255); assert.equal(wrapped.grid[3][0].overline, true);
        assert.equal(wrapped.grid[3][0].foregroundArgb, 0xff112233); assert.equal(wrapped.grid[3][0].backgroundArgb, 0xff445566);
        assert.equal(wrapped.grid[3][0].underlineArgb, 0xff778899);
        assert.deepEqual([wrapped.row, wrapped.col], [3, 1]); assert.equal(wrapped.autoWrap, true);
        assert.equal(wrapped.grid[3][0].text, '─'); assert.equal(ascii.grid[3][1].text, 'q');
        assert.equal(ascii.grid[3][1].flags, 255);
    }
});
test('combined model leaves room for independent-bank and both reset-boundary cases', () => {
    const values = makeSavedCases(12,60); assert.equal(values.length,9);
    assert.equal(values.reduce((count,item)=>count+item.stages.length,3),57);
    assert.ok(values.every(item=>item.stages.every(stage=>stage.expected.grid.flat().length===720)));
});

test('independent main and alternate banks restore distinct positions and palette pens', () => {
    const item=makeBankCases(12,60)[0]; const main=item.stages[4].expected, alt=item.stages[5].expected;
    assert.deepEqual([main.row,main.col],[2,5]); assert.equal(main.grid[2][4].fg,1);
    assert.deepEqual([alt.row,alt.col],[3,6]); assert.equal(alt.grid[3][5].fg,2);
});
test('soft reset defaults only active bank while full reset erases both banks and both screens', () => {
    const [,soft,hard]=makeBankCases(12,60);
    assert.deepEqual(soft.stages[0].expected,soft.stages[1].expected);
    assert.equal(soft.stages[2].expected.grid[0][0].flags,768);assert.equal(soft.stages[2].expected.grid[0][1].fg,3);
    assert.equal(soft.stages[3].expected.grid[2][4].fg,1);
    for(const i of [1,3])assert.ok(hard.stages[i].expected.grid.flat().every(c=>c.text===' '&&c.flags===768));
    for(const i of [2,4,5])assert.deepEqual([hard.stages[i].expected.row,hard.stages[i].expected.col],[0,1]);
});

test('fixture controls and cleanup never depend on the RIS operation under test',()=>{
    const values=makeSavedCases(12,60);
    assert.ok(values.every(item=>!item.setup.includes('\x1bc')));
    const resetStages=values.flatMap(item=>item.stages).filter(stage=>stage.sequence.includes('\x1bc'));
    assert.equal(resetStages.length,1);assert.equal(resetStages[0].id,'full-reset');
});
