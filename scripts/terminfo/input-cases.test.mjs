import assert from 'node:assert/strict';
import { test } from 'node:test';
import { inputReset, makeInputCases } from './input-cases.mjs';

test('fixed input plan distinguishes physical keypad events, literal text and three tracking protocols', () => {
    const cases = makeInputCases(24, 100), byId = id => cases.find(item => item.id === 'input.' + id);
    assert.equal(cases.length, 39); assert.equal(new Set(cases.map(item => item.id)).size, cases.length);
    assert.equal(byId('keypad.normal.keyDown').expectedHex, '');
    assert.equal(byId('keypad.normal.text').expectedHex, '37');
    assert.equal(byId('keypad.application.7').expectedHex, '1b4f77');
    assert.equal(byId('keypad.application.enter').expectedHex, '1b4f4d');
    assert.equal(byId('mouse.x10.left.raw').expectedHex, '1b5b4d208023');
    assert.equal(byId('mouse.normal.press.raw.control').expectedHex, '1b5b4d308023');
    assert.equal(byId('mouse.button.move.raw.control').expectedHex, '1b5b4d507f23');
    assert.equal(byId('mouse.normal.release.sgr.control').expectedHex, Buffer.from('\x1b[<18;96;3m').toString('hex'));
    assert.equal(byId('mouse.any.hover.coalesced').setup, '');
    assert.equal(byId('mouse.any.hover.coalesced').expectedHex, '');
    assert.equal(cases.at(-2).event.column, cases.at(-1).event.column);
    assert.equal(cases.every(item => item.event.modifiers >= 0 && item.event.modifiers <= 7), true);
    assert.equal(cases.slice(0, -1).every(item => item.setup.startsWith(inputReset)), true);
});

test('high coordinate is an explicit geometry prerequisite, never silently replaced with a low-byte test', () => {
    for (const [rows, cols] of [[5, 100], [129, 100], [24, 95], [24, 257], [40, 256], [NaN, 100], [24, 96.5]]) {
        assert.throws(() => makeInputCases(rows, cols), error => error.code === 'inputGeometryUnavailable');
    }
    assert.equal(makeInputCases(6, 96).length, makeInputCases(24, 100).length);
});
