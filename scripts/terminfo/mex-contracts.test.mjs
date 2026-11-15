import assert from 'node:assert/strict';
import { test } from 'node:test';
import { encodingOfModes, gradeMexCase, contractExperiments, mexCaseIds, mexEffectContracts, mexGradedSuiteId, mexPinned, mexRecordingContext, oracleDisagreements,
    oracleHex, pinnedOutcome, replayMexPinned, servedMex, utf8ReplyOf, xtermExtendedMouseReport } from './mex-contracts.mjs';
import { makeMexCases, mexBenchmarkIds, utf8PinnedCases } from './mex-cases.mjs';
import { inputModeReplyHex } from './input-state.mjs';

const press = (encoding, extra = {}) => xtermExtendedMouseReport({ tracking: 1000, encoding, kind: 'mousePress', button: 'left', modifiers: 0, column: 96, row: 3, ...extra });

test('every plan literal equals the independent oracle, for the default grid and for a wide one', () => {
    assert.deepEqual(oracleDisagreements(), []);
    assert.deepEqual(oracleDisagreements(makeMexCases(30, 200)), []);
    assert.deepEqual(oracleDisagreements(makeMexCases(12, 97)), []);
});

test('the oracle encodes UTF-8 coordinates as the code point value + 32: one octet through position 95, two octets from 96 to 2015', () => {
    assert.equal(press('utf8', { column: 95 }), '\x1b[M \x7f#');
    assert.equal(press('utf8', { column: 96 }), '\x1b[M \xc2\x80#');
    assert.equal(press('utf8', { column: 223 }), '\x1b[M \xc3\xbf#', 'the last position legacy X10 can carry is a two-octet one here');
    assert.equal(press('utf8', { column: 224 }), '\x1b[M \xc4\x80#', 'beyond the legacy limit: 224 + 32 = 256 = U+0100');
    assert.equal(press('utf8', { column: 2015 }), '\x1b[M \xdf\xbf#');
    assert.throws(() => press('utf8', { column: 2016 }), /above 2015/);
    assert.equal(press('utf8', { row: 100 }), '\x1b[M \xc2\x80\xc2\x84', 'the row is encoded the same way');
    assert.equal(press('utf8', { column: 1, row: 1 }), press('', { column: 1, row: 1 }), 'below 96 the report is identical to the legacy one');
});

test('the oracle prints urxvt reports as decimal Cb;Cx;Cy with the X10 +32 offset on Cb and a final M for every event, releases included', () => {
    assert.equal(press('urxvt'), '\x1b[32;96;3M');
    assert.equal(press('urxvt', { button: 'right', modifiers: 7 }), '\x1b[62;96;3M');
    assert.equal(press('urxvt', { column: 300 }), '\x1b[32;300;3M', 'no coordinate limit');
    assert.equal(xtermExtendedMouseReport({ tracking: 1000, encoding: 'urxvt', kind: 'mouseRelease', button: 'left', modifiers: 0, column: 5, row: 6 }), '\x1b[35;5;6M');
    assert.equal(xtermExtendedMouseReport({ tracking: 1000, encoding: 'urxvt', kind: 'mouseWheel', wheelDelta: -1, modifiers: 0, column: 5, row: 6 }), '\x1b[97;5;6M');
    assert.equal(xtermExtendedMouseReport({ tracking: 1003, encoding: 'urxvt', kind: 'mouseMove', button: 'none', modifiers: 0, column: 5, row: 6 }), '\x1b[67;5;6M');
    assert.equal(xtermExtendedMouseReport({ tracking: 1002, encoding: 'urxvt', kind: 'mouseMove', button: 'none', modifiers: 0, column: 5, row: 6 }), '', 'hover under 1002 is not reported');
    assert.equal(xtermExtendedMouseReport({ tracking: 9, encoding: 'urxvt', kind: 'mouseRelease', button: 'left', modifiers: 0, column: 5, row: 6 }), '', 'X10 reports no release');
    assert.equal(xtermExtendedMouseReport({ tracking: 0, encoding: 'urxvt', kind: 'mousePress', button: 'left', modifiers: 0, column: 5, row: 6 }), '', 'no tracking selector, no report');
});

test('legacy and SGR delegate to the routed-input oracle unchanged', () => {
    assert.equal(press(''), '\x1b[M \x80#');
    assert.equal(press('sgr'), '\x1b[<0;96;3M');
    assert.equal(xtermExtendedMouseReport({ tracking: 1000, encoding: 'sgr', kind: 'mouseRelease', button: 'left', modifiers: 0, column: 96, row: 3 }), '\x1b[<0;96;3m');
    assert.throws(() => xtermExtendedMouseReport({ tracking: 1000, encoding: 'pixel', kind: 'mousePress', button: 'left', modifiers: 0, column: 1, row: 1 }), /Unknown coordinate encoding|not representable/);
});

test('a mode profile selects at most one encoding and oracleHex follows the profile', () => {
    const base = { applicationKeypad: false, mouseTracking: 1000, mouseSgr: false, kittyKeyboardFlags: 0, modifyOtherKeysLevel: 0, mouseUtf8: false, mouseUrxvt: false };
    assert.equal(encodingOfModes(base), ''); assert.equal(encodingOfModes({ ...base, mouseUtf8: true }), 'utf8'); assert.equal(encodingOfModes({ ...base, mouseUrxvt: true }), 'urxvt');
    assert.throws(() => encodingOfModes({ ...base, mouseUtf8: true, mouseSgr: true }), /at most one/);
    const event = { kind: 'mousePress', button: 'left', modifiers: 0, column: 96, row: 3 };
    assert.equal(oracleHex({ event, modes: { ...base, mouseUrxvt: true } }), Buffer.from('\x1b[32;96;3M').toString('hex'));
    assert.equal(oracleHex({ event: { kind: 'keyUp', key: 'a' }, modes: base }), '');
});

const planned = id => contractExperiments(id);
const matched = (id, overrides = {}) => planned(id).map(item => ({ ...item, matched: overrides[item.id] ?? true }));

test('grading: all matched is supported, a required mismatch with valid controls is unsupported, an invalid control is inconclusive, labels never grade', () => {
    for (const id of mexBenchmarkIds) {
        assert.deepEqual(gradeMexCase(id, matched(id)), { outcome: 'supported' });
        const required = planned(id).find(item => item.role === 'required'), control = planned(id).find(item => item.role === 'control'), label = planned(id).find(item => item.role === 'label');
        assert.deepEqual(gradeMexCase(id, matched(id, { [required.id]: false })), { outcome: 'unsupported', failedRequired: [required.id] });
        assert.deepEqual(gradeMexCase(id, matched(id, { [control.id]: false })), { outcome: 'inconclusive', reason: 'control-mismatch', failedControls: [control.id], failedRequired: [] });
        assert.deepEqual(gradeMexCase(id, matched(id, { [control.id]: false, [required.id]: false })).failedRequired, [required.id]);
        assert.deepEqual(gradeMexCase(id, matched(id, { [label.id]: false })), { outcome: 'supported' });
        assert.throws(() => gradeMexCase(id, matched(id).slice(1)), /differ from the plan/);
        assert.throws(() => gradeMexCase(id, matched(id).map(item => ({ ...item, role: 'required' }))), /differ from the plan/);
        assert.throws(() => gradeMexCase(id, matched(id).map(item => ({ id: item.id, role: item.role }))), /differ from the plan/);
        assert.throws(() => gradeMexCase(id, matched(id), 2), /Unknown extended-mouse contracts version/);
    }
});

test('experiments per benchmark: the 1005 benchmark and the urxvt benchmark share exactly the baseline and the two controls', () => {
    const u = planned('modes.utf8-mouse-1005').map(item => item.id), r = planned('input.urxvt-mouse').map(item => item.id);
    assert.deepEqual(u.filter(id => r.includes(id)), ['input.mex.baseline.reset', 'input.mex.control.legacy.press', 'input.mex.control.sgr.press']);
    assert.equal(u.length, 24); assert.equal(r.length, 25);
});

test('identities: suite id, effect contracts and case order', () => {
    assert.equal(mexGradedSuiteId, 'ntilde-owned-mouse-extended-benchmark-graded-v1');
    assert.deepEqual([...mexCaseIds], ['modes.utf8-mouse-1005', 'input.urxvt-mouse']);
    assert.deepEqual(mexEffectContracts, { 'modes.utf8-mouse-1005': 'ntilde-utf8-mouse-1005-effect-v1', 'input.urxvt-mouse': 'ntilde-urxvt-mouse-effect-v1' });
});

const replies = (modes, extra = '') => inputModeReplyHex({ applicationKeypad: false, mouseTracking: 0, mouseSgr: false, kittyKeyboardFlags: 0, modifyOtherKeysLevel: 0, mouseUtf8: false, mouseUrxvt: false, ...modes }) + extra;
const controls = { [utf8PinnedCases.set]: replies({ mouseUtf8: true }), [utf8PinnedCases.reset]: replies({}) };

test('the child-served values: the 1005 reply after the pinned set feed, both states, and the restored tracking state', () => {
    const served = servedMex('modes.utf8-mouse-1005', controls, replies({}));
    assert.deepEqual(served, { response: '\x1b[?1005;1$y', on: true, off: false, sources: [utf8PinnedCases.set, utf8PinnedCases.reset] });
    assert.deepEqual(servedMex('input.urxvt-mouse', controls, replies({})), { currentMode: false, sources: ['restored'] });
    assert.deepEqual(servedMex('input.urxvt-mouse', controls, replies({ mouseTracking: 1000 })), { currentMode: true, sources: ['restored'] });
    assert.equal(utf8ReplyOf(replies({})), '\x1b[?1005;2$y');
    assert.equal(utf8ReplyOf(''), undefined); assert.equal(utf8ReplyOf(undefined), undefined);
    assert.equal(servedMex('modes.utf8-mouse-1005', { ...controls, [utf8PinnedCases.set]: '' }, '').response, undefined, 'an absent reply is never invented');
});

test('pinned replay shapes: both probes return their pinned never-deciding results and only inconclusive is a valid outcome', () => {
    const served = servedMex('modes.utf8-mouse-1005', controls, replies({}));
    const u = replayMexPinned('modes.utf8-mouse-1005', served);
    assert.deepEqual(Object.keys(u), ['pass', 'response', 'note', 'notTested']);
    assert.equal(u.response, JSON.stringify({ response: '\x1b[?1005;1$y' })); assert.equal(u.notTested.reason, 'no-semantic-observable');
    assert.equal(pinnedOutcome(u), 'inconclusive');
    const r = replayMexPinned('input.urxvt-mouse', { currentMode: false });
    assert.deepEqual(Object.keys(r), ['pass', 'response', 'note', 'observation']); assert.equal(r.response, '{"currentMode":false}');
    assert.equal(pinnedOutcome(r), 'inconclusive'); assert.equal(pinnedOutcome({ observation: { outcome: 'supported' } }), 'supported'); assert.equal(pinnedOutcome({ pass: true }), 'invalid');
    assert.equal(mexPinned['input.urxvt-mouse'].feeds.length, 0); assert.deepEqual([...mexPinned['modes.utf8-mouse-1005'].feeds], ['\x1b[?1005h', '\x1b[?1005l']);
});

test('the recording context serves exactly the pinned calls and refuses everything else', () => {
    const served = servedMex('modes.utf8-mouse-1005', controls, replies({}));
    const ok = mexRecordingContext('modes.utf8-mouse-1005', served);
    ok.feed('\x1b[?1005h'); assert.equal(ok.feedCapture('\x1b[?1005$p'), '\x1b[?1005;1$y'); ok.feed('\x1b[?1005l');
    assert.deepEqual([ok.fedCount, ok.captureCount], [2, 1]);
    const fresh = () => mexRecordingContext('modes.utf8-mouse-1005', served);
    assert.throws(() => fresh().feed('\x1b[?1005l'), /feed 0 differs/);
    assert.throws(() => fresh().feed('X'), /feed 0 differs/);
    assert.throws(() => fresh().feedCapture('\x1b[?1005$p'), /captured outside/, 'the capture follows the first feed');
    const afterFeed = fresh(); afterFeed.feed('\x1b[?1005h');
    assert.throws(() => afterFeed.feedCapture('\x1b[?1015$p'), /captured outside/);
    assert.throws(() => afterFeed.feed('\x1b[?1005l'), /feed 1 differs/, 'the reset feed follows the capture');
    afterFeed.feedCapture('\x1b[?1005$p'); assert.throws(() => afterFeed.feedCapture('\x1b[?1005$p'), /captured outside/);
    assert.throws(() => afterFeed.feed('\x1b[?1005h'), /feed 1 differs/);
    assert.throws(() => fresh().getCell(0, 0), /must not read cells/); assert.throws(() => fresh().getCursor(), /must not read the cursor/);
    assert.throws(() => fresh().getMode('mouseTracking'), /uncontracted mode/);
    assert.throws(() => mexRecordingContext('modes.utf8-mouse-1005', {}).feedCapture('\x1b[?1005$p'), /captured outside/);
    const mouse = mexRecordingContext('input.urxvt-mouse', { currentMode: false });
    assert.equal(mouse.getMode('mouseTracking'), false);
    assert.throws(() => mouse.getMode('applicationKeypad'), /uncontracted mode/); assert.throws(() => mouse.feed('x'), /feed 0 differs/);
    assert.throws(() => mouse.feedCapture('\x1b[?1005$p'), /captured outside/);
    assert.throws(() => mouse.getCell(0, 0), /must not read cells/);
});
