import assert from 'node:assert/strict';
import { test } from 'node:test';
import { gradeInsertReplaceEffect, insertReplaceContract, insertReplaceFeeds, insertReplaceModel,
    insertReplacePinnedFeeds, validateInsertReplaceFeeds } from './insert-replace-contracts.mjs';
import { syntheticLineEditScreen, rowOf, colorCell, plainCell } from './fixtures/line-edit-evidence.mjs';
import { paletteChannel, sgrPaletteCols } from './fixtures/sgr-palette-evidence.mjs';

const bold = char => colorCell(char, { attrs: ['bold'] });
const blue = char => colorCell(char, { bg: paletteChannel(4) });
const wide = text => [rowOf('ABOVE-ROW!'), rowOf(text), rowOf('BELOW-ROW!')];
const attr = [plainCell('A'), plainCell('B'), plainCell('C'), bold('D'), bold('E'), ...rowOf('FGH')];
const attrAfter = [plainCell('A'), plainCell('B'), plainCell('C'), blue('x'), blue('y'), bold('D'), bold('E'), ...rowOf('FGH')];
const edge = text => rowOf(' '.repeat(sgrPaletteCols - 3) + text);
const entries = () => ({
    initial: [[], [0, 0]], seed: [[rowOf('ABC')], [0, 3]], home: [[rowOf('ABC')], [0, 0]],
    enabled: [[rowOf('ABC')], [0, 0], true], inserted: [[rowOf('XABC')], [0, 1], true],
    disabled: [[rowOf('XABC')], [0, 1]], replaced: [[rowOf('XYBC')], [0, 2]],
    wideBefore: [wide('ABCDEFGHIJ'), [1, 2]], wideInserted: [wide('ABxyCDEFGHIJ'), [1, 4], true],
    wideReplaced: [wide('ABxyZDEFGHIJ'), [1, 5]],
    edgeBefore: [[edge('WXY')], [0, sgrPaletteCols - 3]], edgeInserted: [[edge('QRW')], [0, sgrPaletteCols - 1]],
    attrBefore: [[attr], [0, 3]], attrInserted: [[attrAfter], [0, 5]], pen: [[[blue('x'), blue('y')]], [0, 2]],
    terminal: [[rowOf('AXBCD')], [0, 2]],
});
const screens = (edit = () => {}) => {
    const data = entries();
    edit(data);
    return insertReplaceContract.roles.map(role => {
        const [rows, cursor, insert = false] = data[role];
        const screen = syntheticLineEditScreen(rows, cursor, { insert });
        screen.bufferState.autoWrap = false;
        screen.horizontalMargins = { leftRightMarginMode: false, leftMargin: 0, rightMargin: screen.cols - 1 };
        return screen;
    });
};
const grade = edit => gradeInsertReplaceEffect(screens(edit)).outcome;

test('the exact pinned feed sequence is retained separately in the ordered effect plan', () => {
    assert.deepEqual([...insertReplacePinnedFeeds], ['\x1b[4l\x1b[1;1H\x1b[2KABC', '\x1b[1;1H', '\x1b[4h', 'X', '\x1b[4l']);
    assert.doesNotThrow(() => validateInsertReplaceFeeds(insertReplaceFeeds()));
    const wrong = insertReplaceFeeds(); wrong[1] += 'X';
    assert.throws(() => validateInsertReplaceFeeds(wrong), /do not match/);
});

test('the independent model shifts stored tuples and clips at the edge', () => {
    const before = [[{ char: 'A', attr: 1 }, { char: 'B', attr: 2 }, { char: 'C', attr: 3 }]];
    assert.deepEqual(insertReplaceModel(before, [0, 1], 'xy', true, { char: 'p', attr: 9 }),
        { cells: [[{ char: 'A', attr: 1 }, { char: 'x', attr: 9 }, { char: 'y', attr: 9 }]], cursor: [0, 2] });
    assert.equal(before[0][1].char, 'B');
});

test('valid controls admit a supported effect', () => assert.equal(grade(), 'supported'));

test('ignored modes, stale controls and bad geometry or state cannot become unsupported', () => {
    assert.equal(grade(data => { data.enabled[2] = false; }), 'inconclusive');
    assert.equal(grade(data => { data.seed[0] = [rowOf('ABD')]; }), 'inconclusive');
    assert.equal(grade(data => { data.edgeBefore[1] = [0, 0]; }), 'inconclusive');
    const altered = screens(); altered[0].bufferState.originMode = true;
    assert.equal(gradeInsertReplaceEffect(altered).outcome, 'inconclusive');
    const wrapped = screens(); wrapped[3].bufferState.autoWrap = true;
    assert.equal(gradeInsertReplaceEffect(wrapped).outcome, 'inconclusive');
    const history = screens(); history[4].history.lines = ['unrelated drift'];
    assert.equal(gradeInsertReplaceEffect(history).outcome, 'inconclusive');
    const margins = screens(); margins[4].horizontalMargins.rightMargin--;
    assert.equal(gradeInsertReplaceEffect(margins).outcome, 'inconclusive');
    const missing = screens(); delete missing[4].bufferState;
    assert.throws(() => gradeInsertReplaceEffect(missing), /buffer-state/);
    assert.equal(grade(data => { data.pen[0][0][0] = colorCell('x', { bg: paletteChannel(5) }); }), 'inconclusive');
    assert.throws(() => gradeInsertReplaceEffect(screens().slice(1)), /Missing/);
});

test('valid controls expose replacement, wrong shift, cursor, attributes and unrelated drift', () => {
    assert.equal(grade(data => { data.inserted[0] = [rowOf('XBC')]; }), 'unsupported');
    assert.equal(grade(data => { data.wideInserted[0] = wide('ABxyDEFGHIJ'); }), 'unsupported');
    assert.equal(grade(data => { data.edgeInserted[0] = [edge('QRX')]; }), 'unsupported');
    assert.equal(grade(data => { data.wideReplaced[1] = [1, 4]; }), 'unsupported');
    assert.equal(grade(data => { data.attrInserted[0] = [[...attrAfter.slice(0, 5), plainCell('D'), ...attrAfter.slice(6)]]; }), 'unsupported');
    assert.equal(grade(data => { data.wideInserted[0][0] = rowOf('BROKEN'); }), 'unsupported');
});
