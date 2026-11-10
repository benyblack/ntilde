import assert from 'node:assert/strict';
import { test } from 'node:test';
import { lrMarginsContracts, lrMarginsFixture, lrMarginsModel, lrMarginsReads } from './lr-margins-contracts.mjs';

const contract = lrMarginsContracts[0];
const { controls, ops } = lrMarginsFixture;
const { MarginsModel, modelOf } = lrMarginsModel;
const rowText = (model, row, width = 20) => model.grid[row].slice(0, width).join('');

test('one pinned case: the pinned ESC[?69h and ESC[?69l, sixteen controls, within the observer budget', () => {
    assert.deepEqual([...contract.pinnedFeeds], ['\x1b[?69h', '\x1b[?69l']);
    assert.equal(contract.pinnedAlwaysInconclusive, true);
    assert.equal(controls.length, 16);
    assert.ok(lrMarginsReads(contract) + 3 <= 64);
    assert.equal(contract.roles[contract.setRead], 'set');
});

test('operations spell their exact bytes', () => {
    assert.equal(ops.slrm(5, 14).bytes, '\x1b[5;14s');
    assert.equal(ops.slrm().bytes, '\x1b[s');
    assert.equal(ops.slrm(4).bytes, '\x1b[4s');
    assert.equal(ops.decic(2).bytes, "\x1b[2'}");
    assert.equal(ops.sl(1).bytes, '\x1b[1 @');
    assert.equal(ops.decera(1, 1, 1, 2).bytes, '\x1b[1;1;1;2$z');
});

test('the model wraps at the right margin, bounds the cursor and scrolls only the band (hand-written expectations)', () => {
    const model = new MarginsModel(6, 20);
    for (const step of [ops.lrmm(true), ops.slrm(3, 8), ops.cup(1, 7), ops.text('abcd')]) step.apply(model);
    assert.equal(rowText(model, 0), '      ab            ');
    assert.equal(rowText(model, 1), '  cd                ');
    assert.deepEqual([model.row, model.col], [1, 4]);
    for (const step of [ops.cup(6, 4), ops.lf()]) step.apply(model);
    assert.equal(rowText(model, 0), '  cd                ', 'LF at the bottom margin scrolled the band up');
    assert.equal(rowText(model, 1), '                    ');
    for (const step of [ops.cup(3, 15), ops.cuf(50)]) step.apply(model);
    assert.equal(model.col, 19, 'outside the margins CUF reaches the page edge');
    for (const step of [ops.cup(3, 5), ops.cub(50)]) step.apply(model);
    assert.equal(model.col, 2);
    for (const step of [ops.slrm(5, 5)]) step.apply(model);
    assert.deepEqual([model.left, model.right], [2, 7], 'an equal pair is ignored');
    for (const step of [ops.lrmm(false)]) step.apply(model);
    assert.deepEqual([model.left, model.right, model.mode], [0, 19, false]);
});

test('every control changes the seeded screen or state, so none is vacuous', () => {
    const seed = lrMarginsModel.seededModel(13, 60);
    for (const item of controls) {
        const model = modelOf(item, 13, 60);
        const changed = JSON.stringify(model.grid) !== JSON.stringify(seed.grid) || model.row !== seed.row || model.col !== seed.col
            || model.left !== seed.left || model.right !== seed.right || model.mode !== seed.mode;
        assert.ok(changed, item.role);
    }
});
