import assert from 'node:assert/strict';
import { test } from 'node:test';
import { encodingOfModes, contractExperiments, gradePxmCase, oracleDisagreements, oracleHex, pinnedOutcome, pxmCaseIds, pxmEffectContracts, pxmPinned, pxmRecordingContext,
    replayPxmPinned, servedPxm, xtermPixelMouseReport } from './pxm-contracts.mjs';
import { makePxmCases } from './pxm-cases.mjs';
import { inputModeReplyHex } from './input-state.mjs';

const B = 'input.pixel-mouse';
const report = (extra = {}) => xtermPixelMouseReport({ tracking: 1000, kind: 'mousePress', button: 'left', modifiers: 0, pixelX: 143, pixelY: 57, ...extra });

test('every plan literal equals the independent oracle, including the stateful repeat rule, for several grids', () => {
    for (const [rows, cols] of [[30, 140], [24, 97], [40, 200]]) assert.deepEqual(oracleDisagreements(makePxmCases(rows, cols)), [], `${rows}x${cols}`);
});

test('the oracle prints SGR-Pixels as CSI < Cb ; px+1 ; py+1 with the SGR code (no 32 offset) and keeps the button on release with final m', () => {
    assert.equal(report(), '\x1b[<0;144;58M');
    assert.equal(report({ pixelX: 0, pixelY: 0 }), '\x1b[<0;1;1M', 'the first pixel is position 1');
    assert.equal(report({ pixelX: 2379, pixelY: 1500 }), '\x1b[<0;2380;1501M', 'no coordinate ceiling');
    assert.equal(report({ button: 'right', modifiers: 7 }), '\x1b[<30;144;58M');
    assert.equal(report({ kind: 'mouseRelease', button: 'middle' }), '\x1b[<1;144;58m');
    assert.equal(report({ kind: 'mouseWheel', button: undefined, wheelDelta: 1 }), '\x1b[<64;144;58M');
    assert.equal(report({ kind: 'mouseWheel', button: undefined, wheelDelta: -1 }), '\x1b[<65;144;58M');
    assert.equal(report({ tracking: 1003, kind: 'mouseMove', button: 'none' }), '\x1b[<35;144;58M');
    assert.equal(report({ tracking: 1002, kind: 'mouseMove', button: 'left' }), '\x1b[<32;144;58M');
    assert.equal(report({ tracking: 1002, kind: 'mouseMove', button: 'none' }), '', 'hover under 1002 is not reported');
    assert.equal(report({ tracking: 9, modifiers: 7 }), '\x1b[<0;144;58M', 'X10 press-only tracking carries no modifier bits');
    assert.equal(report({ tracking: 9, kind: 'mouseRelease' }), '', 'X10 reports no release');
    assert.equal(report({ tracking: 0 }), '', 'no tracking selector, no report');
    for (const bad of [{ pixelX: -1 }, { pixelX: 1.5 }, { pixelY: undefined }]) assert.throws(() => report(bad), /non-negative integer/);
});

test('a mode profile selects at most one encoding and oracleHex follows the profile, with the previous plan case only mattering for repeated pixel motion', () => {
    const base = { applicationKeypad: false, mouseTracking: 1003, mouseSgr: false, kittyKeyboardFlags: 0, modifyOtherKeysLevel: 0, mouseUtf8: false, mouseUrxvt: false, mousePixel: true };
    assert.equal(encodingOfModes(base), 'pixel'); assert.equal(encodingOfModes({ ...base, mousePixel: false }), '');
    assert.throws(() => encodingOfModes({ ...base, mouseSgr: true }), /at most one/);
    const move = { kind: 'mouseMove', button: 'none', modifiers: 0, pixelX: 5, pixelY: 6 }, hex = text => Buffer.from(text).toString('hex');
    const first = { event: move, modes: base, expectedHex: hex('\x1b[<35;6;7M') };
    assert.equal(oracleHex(first), first.expectedHex);
    assert.equal(oracleHex({ event: move, modes: base }, first), '', 'the same pixel again under unchanged tracking sends nothing');
    assert.notEqual(oracleHex({ event: { ...move, pixelY: 7 }, modes: base }, first), '');
    assert.notEqual(oracleHex({ event: { ...move, button: 'left' }, modes: { ...base, mouseTracking: 1002 } }, { ...first, modes: { ...base, mouseTracking: 1003 } }), '', 'another tracking protocol reports again');
    assert.notEqual(oracleHex({ event: move, modes: base }, { ...first, expectedHex: '' }), '', 'a previous motion that reported nothing leaves no last position');
    assert.equal(oracleHex({ event: { kind: 'keyUp', key: 'a' }, modes: base }), '');
    assert.equal(oracleHex({ event: { kind: 'mousePress', button: 'left', modifiers: 0, column: 96, row: 3 }, modes: { ...base, mouseTracking: 1000, mousePixel: false, mouseUrxvt: true } }), hex('\x1b[32;96;3M'));
});

const matched = overrides => contractExperiments(B).map(item => ({ ...item, matched: overrides?.[item.id] ?? true }));

test('grading: all matched is supported, a required mismatch with valid controls is unsupported, an invalid control is inconclusive, labels never grade', () => {
    assert.deepEqual(gradePxmCase(B, matched()), { outcome: 'supported' });
    const required = contractExperiments(B).find(item => item.role === 'required').id, control = contractExperiments(B).find(item => item.role === 'control').id;
    assert.deepEqual(gradePxmCase(B, matched({ [required]: false })), { outcome: 'unsupported', failedRequired: [required] });
    assert.deepEqual(gradePxmCase(B, matched({ [control]: false, [required]: false })), { outcome: 'inconclusive', reason: 'control-mismatch', failedControls: [control], failedRequired: [required] });
    assert.equal(contractExperiments(B).filter(item => item.role === 'label').length, 0);
    assert.equal(contractExperiments(B).length, 32);
    assert.throws(() => gradePxmCase(B, matched().slice(1)), /differ from the plan/);
    assert.throws(() => gradePxmCase(B, matched().map(item => ({ ...item, matched: 1 }))), /differ from the plan/);
    assert.throws(() => gradePxmCase(B, matched(), 2), /Unknown SGR-pixel contracts version/);
});

test('the pinned definition names the one probe, feeds nothing and is always inconclusive over any served value', () => {
    assert.deepEqual(pxmCaseIds, [B]); assert.equal(pxmEffectContracts[B], 'ntilde-sgr-pixel-mouse-1016-effect-v1');
    assert.deepEqual(pxmPinned[B].feeds, []); assert.equal(pxmPinned[B].mode, 'pixelMouse');
    for (const currentMode of [true, false, undefined]) {
        const result = replayPxmPinned(B, { currentMode });
        assert.equal(result.response, JSON.stringify({ currentMode: currentMode === true }));
        assert.equal(pinnedOutcome(result), 'inconclusive'); assert.equal(result.pass, false);
    }
    assert.equal(pinnedOutcome({ pass: true }), 'invalid');
    assert.throws(() => replayPxmPinned('input.urxvt-mouse', {}), /Unknown pixel-mouse case/);
});

test('the served value comes from the child DECRQM replies: the restored state, with the set state of the selector-only case beside it', () => {
    const modes = pixel => ({ applicationKeypad: false, mouseTracking: 0, mouseSgr: false, kittyKeyboardFlags: 0, modifyOtherKeysLevel: 0, mouseUtf8: false, mouseUrxvt: false, mousePixel: pixel });
    const served = servedPxm(B, { 'input.pxm.alone.set': inputModeReplyHex(modes(true)) }, inputModeReplyHex(modes(false)));
    assert.deepEqual(served, { currentMode: false, on: true, off: false, sources: ['input.pxm.alone.set', 'restored'] });
    assert.equal(servedPxm(B, {}, inputModeReplyHex(modes(true))).currentMode, true, 'a restoration that left 1016 set would be served as set');
    assert.equal(servedPxm(B, {}, '').on, undefined);
    assert.throws(() => servedPxm('other', {}, ''), /Unknown pixel-mouse case/);
});

test('the recording context serves only the contracted read and refuses every feed, capture, other mode, cell and cursor read', () => {
    const context = pxmRecordingContext(B, { currentMode: true });
    assert.equal(context.getMode('pixelMouse'), true);
    assert.throws(() => context.getMode('mouseTracking'), /uncontracted mode/);
    assert.throws(() => context.feed('x'), /fed unexpectedly/); assert.equal(context.fedCount, 1);
    for (const call of [() => context.feedCapture('x'), () => context.getCell(0, 0), () => context.getCursor()]) assert.throws(call, /must not/);
    assert.equal(context.captureCount, 0);
});
