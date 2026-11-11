import assert from 'node:assert/strict';
import { test } from 'node:test';
import { makeRoutedInputCases } from './routed-input-cases.mjs';
import { contractExperiments, gradeRoutedInputCase, keypadReport, oracleHex, modeFromReplies, pinnedFeedCases, pinnedOutcome, replayRoutedInputPinned,
    routedInputCaseIds, routedInputRecordingContext, servedModes, trackingFromReplies, xtermMouseReport } from './routed-input-contracts.mjs';
import { inputModeReplyHex } from './input-state.mjs';

const at = { column: 96, row: 3 };
test('oracle: X10 reports presses of the ordinary buttons only, without modifiers', () => {
    const report = (kind, extra = {}) => xtermMouseReport({ tracking: 9, sgr: false, kind, ...at, ...extra });
    assert.equal(report('mousePress', { button: 'left', modifiers: 15 }), '\x1b[M \x80#');
    assert.equal(report('mousePress', { button: 'right' }), '\x1b[M"\x80#');
    for (const [kind, extra] of [['mouseRelease', { button: 'left' }], ['mouseMove', { button: 'left' }], ['mouseMove', { button: 'none' }], ['mouseWheel', { wheelDelta: 1 }], ['mouseWheel', { wheelDelta: -1 }]]) {
        assert.equal(report(kind, extra), '', kind + JSON.stringify(extra));
    }
});
test('oracle: normal, button-event and any-event tracking differ exactly where xterm says', () => {
    const send = (tracking, kind, extra) => xtermMouseReport({ tracking, sgr: false, kind, ...at, ...extra });
    assert.equal(send(1000, 'mousePress', { button: 'left', modifiers: 2 }), '\x1b[M0\x80#');
    assert.equal(send(1000, 'mouseRelease', { button: 'left' }), '\x1b[M#\x80#');
    assert.equal(send(1000, 'mouseMove', { button: 'left' }), '');
    assert.equal(send(1002, 'mouseMove', { button: 'left' }), '\x1b[M@\x80#');
    assert.equal(send(1002, 'mouseMove', { button: 'none' }), '');
    assert.equal(send(1003, 'mouseMove', { button: 'none' }), '\x1b[MC\x80#');
    assert.equal(send(1002, 'mouseMove', { button: 'middle', modifiers: 5 }), '\x1b[MM\x80#');
    assert.equal(send(1000, 'mouseWheel', { wheelDelta: 1 }), '\x1b[M`\x80#');
    assert.equal(send(0, 'mousePress', { button: 'left' }), '', 'no tracking selected');
    assert.equal(xtermMouseReport({ tracking: 1002, sgr: true, kind: 'mouseRelease', button: 'left', ...at }), '\x1b[<0;96;3m');
    assert.equal(xtermMouseReport({ tracking: 1002, sgr: true, kind: 'mouseMove', button: 'left', ...at }), '\x1b[<32;96;3M');
    assert.throws(() => xtermMouseReport({ tracking: 9, sgr: false, kind: 'mousePress', button: 'left', column: 224, row: 3 }), /not representable/);
});
test('oracle: keypad table, numeric Enter, releases and reserved duplicates', () => {
    assert.equal(keypadReport({ applicationKeypad: true, kind: 'keyDown', key: '5' }), '\x1bOu');
    assert.equal(keypadReport({ applicationKeypad: true, kind: 'keyDown', key: 'enter' }), '\x1bOM');
    assert.equal(keypadReport({ applicationKeypad: false, kind: 'keyDown', key: 'enter' }), '\r');
    assert.equal(keypadReport({ applicationKeypad: false, kind: 'keyDown', key: '5' }), '');
    assert.equal(keypadReport({ applicationKeypad: true, kind: 'keyUp', key: '5' }), '');
    assert.equal(keypadReport({ applicationKeypad: true, kind: 'text', text: '5', reserved: true }), '');
    assert.equal(keypadReport({ applicationKeypad: true, kind: 'text', text: '5', reserved: false }), '5');
    const pair = (applicationKeypad, key, text) => oracleHex({ event: { kind: 'keyDown', key, text }, modes: { applicationKeypad } });
    assert.equal(pair(true, '7', '7'), Buffer.from('\x1bOw').toString('hex'), 'the handled key reserved its text');
    assert.equal(pair(false, '7', '7'), '37', 'an unhandled key leaves its text as the one delivery');
    assert.equal(pair(true, 'add', '+'), Buffer.from('\x1bOk').toString('hex'));
});

const plan = makeRoutedInputCases(40, 140);
const everyMatched = id => contractExperiments(id, plan).map(item => ({ ...item, matched: true }));
test('a mismatching required experiment is unsupported only while every control held', () => {
    for (const id of routedInputCaseIds) {
        assert.deepEqual(gradeRoutedInputCase(id, everyMatched(id)), { outcome: 'supported' });
        const required = everyMatched(id), index = required.findIndex(item => item.role === 'required');
        required[index].matched = false;
        assert.deepEqual(gradeRoutedInputCase(id, required), { outcome: 'unsupported', failedRequired: [required[index].id] });
        const controlled = everyMatched(id), control = controlled.findIndex(item => item.role === 'control');
        controlled[control].matched = false; controlled[index].matched = false;
        const result = gradeRoutedInputCase(id, controlled);
        assert.equal(result.outcome, 'inconclusive'); assert.equal(result.reason, 'control-mismatch');
        assert.deepEqual(result.failedControls, [controlled[control].id]);
    }
});
test('label experiments never grade and a changed experiment list or unknown version is refused', () => {
    const labelled = everyMatched('input.button-event-mouse'), label = labelled.findIndex(item => item.role === 'label');
    assert.ok(label >= 0); labelled[label].matched = false;
    assert.equal(gradeRoutedInputCase('input.button-event-mouse', labelled).outcome, 'supported');
    assert.throws(() => gradeRoutedInputCase('input.x10-mouse', everyMatched('input.x10-mouse').slice(1)), /differ from the plan/);
    assert.throws(() => gradeRoutedInputCase('input.x10-mouse', everyMatched('input.x10-mouse').map(item => ({ ...item, role: 'required' }))), /differ from the plan/);
    assert.throws(() => gradeRoutedInputCase('input.x10-mouse', everyMatched('input.x10-mouse').map(item => ({ ...item, matched: 1 }))), /differ from the plan/);
    assert.throws(() => gradeRoutedInputCase('input.x10-mouse', everyMatched('input.x10-mouse'), 2), /Unknown routed-input contracts version/);
});
test('child replies, not metadata, serve the pinned probes; unnamed feeds and reads are refused', () => {
    const select = modeFromReplies(inputModeReplyHex({ applicationKeypad: true, mouseTracking: 0, mouseSgr: false, kittyKeyboardFlags: 0 }), 66);
    assert.equal(select, true);
    assert.equal(modeFromReplies(inputModeReplyHex({ applicationKeypad: false, mouseTracking: 9, mouseSgr: false, kittyKeyboardFlags: 0 }), 66), false);
    assert.equal(modeFromReplies('', 66), undefined);
    assert.equal(trackingFromReplies(inputModeReplyHex({ applicationKeypad: false, mouseTracking: 1002, mouseSgr: false, kittyKeyboardFlags: 0 })), true);
    const [first, second] = pinnedFeedCases('modes.application-keypad', plan);
    assert.deepEqual([first.setup, second.setup], ['\x1b=', '\x1b>']);
    const context = routedInputRecordingContext('modes.application-keypad', { on: true, off: false });
    assert.throws(() => context.feed('\x1b[?66h'), /differs from the contract/);
    assert.throws(() => context.getMode('mouseTracking'), /uncontracted mode/);
    assert.throws(() => context.getMode('applicationKeypad'), /outside its feeds/);
    assert.throws(() => context.getCell(), /must not read cells/);
    const mouse = routedInputRecordingContext('input.x10-mouse', { currentMode: false });
    assert.throws(() => mouse.feed('\x1b[?9h'), /differs from the contract/);
    assert.throws(() => mouse.getMode('applicationKeypad'), /uncontracted mode/);
    assert.equal(mouse.getMode('mouseTracking'), false);
});
test('the replayed pinned result is inconclusive whatever the child reported, and an unknown shape is not an outcome', () => {
    for (const served of [{ on: true, off: false }, { on: false, off: true }, {}]) assert.equal(pinnedOutcome(replayRoutedInputPinned('modes.application-keypad', served)), 'inconclusive');
    for (const id of ['input.x10-mouse', 'input.button-event-mouse']) for (const currentMode of [true, false]) {
        const result = replayRoutedInputPinned(id, { currentMode });
        assert.equal(result.response, JSON.stringify({ currentMode })); assert.equal(pinnedOutcome(result), 'inconclusive');
    }
    assert.equal(pinnedOutcome({ pass: true }), 'invalid');
    assert.equal(servedModes('input.x10-mouse', {}, inputModeReplyHex({ applicationKeypad: false, mouseTracking: 0, mouseSgr: false, kittyKeyboardFlags: 0 }), plan).currentMode, false);
});
