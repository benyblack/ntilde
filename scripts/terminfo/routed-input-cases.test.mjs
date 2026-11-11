import assert from 'node:assert/strict';
import { test } from 'node:test';
import { inputPlanV1, inputPlanV2, inputPlanForPolicy, inputAttemptCount, inputFrameCount } from './input-plan.mjs';
import { inputReset, makeInputCases } from './input-cases.mjs';
import { benchmarkIds, makeRoutedInputCases } from './routed-input-cases.mjs';
import { oracleDisagreements } from './routed-input-contracts.mjs';

const cases = makeRoutedInputCases(40, 140), byId = id => cases.find(item => item.id === 'input.' + id);

test('the benchmark plan is 58 unique source-selected events within the per-reader capture limits', () => {
    assert.equal(cases.length, 58);
    assert.equal(new Set(cases.map(item => item.id)).size, cases.length);
    // 64 captures per phase reader include start/bind/finish; 128 raw attempts include the bind and the final restoration.
    assert.ok(cases.length + 3 <= 64 && inputAttemptCount(cases.length) <= 128);
    assert.equal(inputFrameCount(cases.length), 119);
    for (const id of benchmarkIds) assert.ok(cases.some(item => item.roles[id] === 'required'), id);
    assert.ok(cases.every(item => Object.values(item.roles).every(role => ['required', 'control', 'label'].includes(role))));
    assert.ok(cases.every(item => item.event.modifiers >= 0 && item.event.modifiers <= 15));
});

test('every literal expectation agrees with the independent oracle', () => assert.deepEqual(oracleDisagreements(cases), []));

test('X10 presses carry no modifier bits and keep the high column byte single-octet latin1', () => {
    for (const id of ['x10.left.nomod.raw', 'x10.left.shift.raw', 'x10.left.ctrl.raw', 'x10.left.alt.raw', 'x10.left.meta.raw', 'x10.left.allmods.raw', 'x10.left.raw']) {
        assert.equal(byId('mouse.' + id).expectedHex, '1b5b4d208023', id);
        assert.equal(byId('mouse.' + id).modes.mouseTracking, 9);
    }
    assert.equal(byId('mouse.x10.middle.raw').expectedHex, '1b5b4d218023');
    assert.equal(byId('mouse.x10.right.raw').expectedHex, '1b5b4d228023');
    assert.ok(!cases.some(item => item.expectedHex.includes('c280')), 'a UTF-8 expansion of byte 0x80 is never an accepted expectation');
    assert.equal(byId('mouse.x10.left.sgr').expectedHex, Buffer.from('\x1b[<0;96;3M').toString('hex'));
    for (const id of ['release', 'move', 'hover', 'wheel', 'wheel.down']) assert.equal(byId(`mouse.x10.${id}.suppressed`).expectedHex, '', id);
});

test('tracking selection stays separate from coordinate encoding and from the other tracking protocols', () => {
    assert.equal(byId('mouse.x10.left.raw.afterSgrReset').setup, '\x1b[?1006l');
    assert.deepEqual(byId('mouse.x10.left.raw.afterSgrReset').modes, { applicationKeypad: false, mouseTracking: 9, mouseSgr: false, kittyKeyboardFlags: 0 });
    assert.equal(byId('mouse.normal.press.mods7.raw.control').expectedHex, '1b5b4d3c8023');
    assert.equal(byId('mouse.normal.release.raw.control').expectedHex, '1b5b4d238023');
    assert.equal(byId('mouse.button.move.left.raw').expectedHex, '1b5b4d408023');
    assert.equal(byId('mouse.normal.move.suppressed').expectedHex, '');
    assert.equal(byId('mouse.button.hover.suppressed').expectedHex, '');
    assert.equal(byId('mouse.any.hover.raw.control').expectedHex, Buffer.from('\x1b[MC~#', 'latin1').toString('hex'));
    // The suppression and the control share the exact event: only the selected tracking differs.
    assert.deepEqual(byId('mouse.button.hover.suppressed').event, byId('mouse.any.hover.raw.control').event);
    assert.deepEqual(byId('mouse.x10.move.suppressed').event, byId('mouse.normal.move.suppressed').event);
    assert.deepEqual(byId('mouse.x10.move.suppressed').event, byId('mouse.button.move.left.raw').event);
    assert.equal(byId('mouse.button.move.sgr').expectedHex, Buffer.from('\x1b[<32;96;3M').toString('hex'));
});

test('keypad plan separates numeric controls, the pinned ESC = / ESC > pair, physical keys, text and duplicate suppression', () => {
    assert.equal(byId('keypad.normal.enter').expectedHex, '0d');
    assert.equal(byId('keypad.application.0').setup, '\x1b=', 'exactly the pinned first feed');
    assert.equal(byId('keypad.restore.deckpnm.enter').setup, '\x1b>', 'exactly the pinned second feed');
    assert.equal(byId('keypad.application.enter').expectedHex, '1b4f4d');
    assert.equal(byId('keypad.restore.deckpnm.enter').expectedHex, '0d');
    assert.equal(byId('keypad.decnkm.set.enter').setup, '\x1b[?66h');
    assert.equal(byId('keypad.decnkm.reset.enter').expectedHex, '0d');
    const index = id => cases.findIndex(item => item.id === 'input.' + id);
    assert.deepEqual([1, 2, 3].map(offset => cases[index('keypad.application.7') + offset].id), ['input.keypad.application.7.text.duplicate',
        'input.keypad.application.7.keyUp', 'input.keypad.application.7.text.afterUp']);
    assert.equal(byId('keypad.application.7.text.duplicate').expectedHex, '');
    assert.equal(byId('keypad.application.7.text.afterUp').expectedHex, '37');
    assert.deepEqual(cases.filter(item => item.event.kind === 'keyDown' && item.modes.applicationKeypad && item.id.startsWith('input.keypad.application.')).length, 17);
    assert.ok(cases.filter(item => item.event.kind.startsWith('key') || item.event.kind === 'text').every(item => item.modes.mouseTracking === 0));
});

test('geometry below the pointer cell is refused, never replaced by a smaller test', () => {
    for (const [rows, cols] of [[5, 100], [129, 100], [24, 95], [24, 257], [40, 256], [NaN, 100], [24, 96.5]]) {
        assert.throws(() => makeRoutedInputCases(rows, cols), error => error.code === 'inputGeometryUnavailable');
    }
});

test('plan selection keeps the frozen v1 foundation for earlier source policies', () => {
    assert.equal(inputPlanForPolicy('readonly-owned-source-graph-v34'), inputPlanV1);
    assert.equal(inputPlanForPolicy('readonly-owned-source-graph-v2'), inputPlanV1);
    assert.equal(inputPlanForPolicy('readonly-owned-source-graph-v35'), inputPlanV2);
    assert.equal(makeInputCases(40, 140).length, 39);
    assert.ok(makeRoutedInputCases(40, 140).slice(0, 4).every(item => item.setup.startsWith(inputReset)));
});
