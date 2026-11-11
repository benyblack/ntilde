import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildRoutedInputGradingReport, routedInputCounts } from './routed-input-grading.mjs';
import { fakeGradingModule, syntheticRoutedInputEvidence } from './fixtures/routed-input-evidence.mjs';
import { routedInputCaseIds } from './routed-input-contracts.mjs';
import { inputModeReplyHex } from './input-state.mjs';

const build = (evidence, options = {}) => buildRoutedInputGradingReport(evidence, evidence.run, evidence.runId, evidence.adapter, options);
const byCase = report => Object.fromEntries(report.cases.map(item => [item.caseId, item]));
const withActual = (id, hex) => syntheticRoutedInputEvidence({ actual: item => item.id === 'input.' + id ? hex : item.expectedHex });

test('measured child bytes that match every literal grade supported beside inconclusive pinned verdicts', () => {
    const evidence = syntheticRoutedInputEvidence(), report = build(evidence, { replayModule: fakeGradingModule });
    assert.deepEqual(routedInputCounts(report), { 'inconclusive/supported': 3 });
    assert.deepEqual(report.cases.map(item => item.caseId), routedInputCaseIds);
    for (const item of report.cases) {
        assert.equal(item.conclusion.outcome, 'inconclusive'); assert.equal(item.conclusion.grading, 'pinned-termless-preserved-by-design');
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
        assert.match(item.effectContract.attribution, /not the pinned probe/);
        assert.ok(item.experiments.every(experiment => experiment.matched && experiment.actualHex === experiment.expectedHex && experiment.oracleHex === experiment.expectedHex));
        assert.ok(item.experiments.every(experiment => experiment.appFacts.boundary === 'avalonia-routed'));
    }
    const [x10, , keypad] = report.cases;
    assert.equal(x10.pinned.response, '{"currentMode":false}');
    assert.deepEqual(keypad.pinnedFeeds, ['\x1b=', '\x1b>']);
    assert.equal(keypad.pinned.response, '{"on":true,"off":true}');
    assert.equal(keypad.pinned.notTested.reason, 'no-semantic-observable');
    assert.deepEqual(keypad.served.sources, ['input.keypad.application.0', 'input.keypad.restore.deckpnm.enter']);
    assert.equal(report.restoredSnapshot, 118);
});

for (const [label, id, hex, caseId] of [
    ['a modifier bit leaking into an X10 press', 'mouse.x10.left.ctrl.raw', '1b5b4d308023', 'input.x10-mouse'],
    ['UTF-8 expansion of the high coordinate byte', 'mouse.x10.left.raw', '1b5b4d20c28023', 'input.x10-mouse'],
    ['a lost X10 press', 'mouse.x10.middle.raw', '', 'input.x10-mouse'],
    ['a duplicated X10 press', 'mouse.x10.right.raw', '1b5b4d228023' + '1b5b4d228023', 'input.x10-mouse'],
    ['an X10 release reported anyway', 'mouse.x10.release.suppressed', '1b5b4d238023', 'input.x10-mouse'],
    ['X10 wheel reported anyway', 'mouse.x10.wheel.down.suppressed', '1b5b4d618023', 'input.x10-mouse'],
    ['SGR coordinates ignored under X10', 'mouse.x10.left.sgr', '1b5b4d208023', 'input.x10-mouse'],
    ['legacy bytes kept after 1006 was reset', 'mouse.x10.left.raw.afterSgrReset', Buffer.from('\x1b[<0;96;3M').toString('hex'), 'input.x10-mouse'],
    ['button-event press with the wrong button code', 'mouse.button.press.right.raw', '1b5b4d208023', 'input.button-event-mouse'],
    ['held-button motion lost under 1002', 'mouse.button.move.left.raw', '', 'input.button-event-mouse'],
    ['hover reported under 1002', 'mouse.button.hover.suppressed', '1b5b4d437e23', 'input.button-event-mouse'],
    ['a wrong modifier mapping in 1002 motion', 'mouse.button.move.middle.shiftalt.raw', '1b5b4d2d8023', 'input.button-event-mouse'],
    ['1002 release missing', 'mouse.button.release.left.raw', '', 'input.button-event-mouse'],
    ['keypad Enter sent as a CR in application mode', 'keypad.application.enter', '0d', 'modes.application-keypad'],
    ['keypad digit sent twice (key and duplicate text)', 'keypad.application.7.text.duplicate', '37', 'modes.application-keypad'],
    ['key-up sending text', 'keypad.application.7.keyUp', '37', 'modes.application-keypad'],
    ['text after release suppressed', 'keypad.application.7.text.afterUp', '', 'modes.application-keypad'],
    ['application sequence surviving DECKPNM', 'keypad.restore.deckpnm.enter', '1b4f4d', 'modes.application-keypad'],
    ['DECRST 66 ignored', 'keypad.decnkm.reset.enter', '1b4f4d', 'modes.application-keypad'],
    ['a physical keypad key lost', 'keypad.application.divide', '', 'modes.application-keypad'],
]) test('a required mismatch (' + label + ') is unsupported with valid controls, and the other benchmarks stay supported', () => {
    const report = build(withActual(id, hex)), cases = byCase(report);
    assert.equal(cases[caseId].effectContract.outcome, 'unsupported');
    assert.deepEqual(cases[caseId].effectContract.failedRequired, ['input.' + id]);
    assert.equal(cases[caseId].conclusion.outcome, 'inconclusive', 'the pinned verdict is never rewritten');
    for (const other of routedInputCaseIds.filter(item => item !== caseId)) {
        const shared = cases[other].experiments.some(item => item.id === 'input.' + id);
        assert.equal(cases[other].effectContract.outcome, shared ? 'inconclusive' : 'supported', other);
    }
});

for (const [label, id, hex, cases] of [
    ['a normal-tracking control that suppresses the press', 'mouse.normal.press.mods7.raw.control', '', ['input.x10-mouse']],
    ['a 1003 hover control that reports nothing', 'mouse.any.hover.raw.control', '', ['input.button-event-mouse']],
    ['a normal-motion control that reports', 'mouse.normal.move.suppressed', '1b5b4d408023', ['input.x10-mouse', 'input.button-event-mouse']],
    ['a numeric Enter control that sends SS3', 'keypad.normal.enter', '1b4f4d', ['modes.application-keypad']],
]) test('an invalid control (' + label + ') leaves the dependent benchmarks inconclusive, never unsupported', () => {
    const report = build(withActual(id, hex));
    for (const caseId of cases) {
        const item = byCase(report)[caseId];
        assert.equal(item.effectContract.outcome, 'inconclusive'); assert.equal(item.effectContract.reason, 'control-mismatch');
        assert.ok(item.effectContract.failedControls.includes('input.' + id)); assert.equal(item.divergence, undefined);
    }
});

for (const [label, mutate, pattern] of [
    ['a stale pane in one control frame', e => { e.frames[4].paneId = '22222222-2222-2222-2222-222222222222'; }, undefined],
    ['a mode control that did not prove 9', e => { e.frames[2 + 2 * 24].capture.hex = inputModeReplyHex({ applicationKeypad: false, mouseTracking: 1000, mouseSgr: false, kittyKeyboardFlags: 0 }); }, undefined],
    ['a missing raw barrier', e => { e.frames[5].capture.barrier = ''; }, undefined],
    ['a reused ordering challenge', e => Object.assign(e.frames[7].capture, { nonces: e.frames[5].capture.nonces, barrier: e.frames[5].capture.barrier }), undefined],
    ['another App identity in an action', e => { e.actions[10].observed.after.app.pid++; }, undefined],
    ['an action without dispatch', e => { e.actions[3].observed.app.eventDispatched = false; }, undefined],
    ['a different event than planned', e => { e.actions[5].observed.request.modifiers = 3; }, undefined],
    ['state that changed after the event', e => { e.frames[7].afterScreen.cursorCol++; }, undefined],
    ['modes left unrestored', e => { e.frames.at(-1).capture.hex = inputModeReplyHex({ applicationKeypad: false, mouseTracking: 9, mouseSgr: false, kittyKeyboardFlags: 0 }); }, undefined],
    ['a failed capture seal', e => { e.seal.failed = true; }, undefined],
    ['an incomplete seal attempt count', e => { e.seal.attempts--; }, undefined],
    ['a missing frame', e => { e.frames.splice(9, 1); }, undefined],
    ['an extra action', e => { e.actions.push(structuredClone(e.actions[0])); }, undefined],
]) test('tampering with ' + label + ' prevents a graded report', () => {
    const evidence = syntheticRoutedInputEvidence({ mutate: journal => { mutate(journal); } });
    assert.throws(() => build(evidence), pattern);
});

for (const [label, mutate, pattern] of [
    ['a pinned result that differs from its replay', e => { e.run.cases[0].pinned.response = '{"currentMode":true}'; }, /does not match its observed evidence/],
    ['a pinned verdict that gained a decision', e => { e.run.cases[2].pinned = { probeId: 'modes.application-keypad', pass: true, response: '{"on":true,"off":true}', note: 'keypad input',
        observation: { outcome: 'supported', evidence: 'parser-state' } }; }, /does not match|inconclusive by design/],
    ['a served mode that the child never sent', e => { e.run.cases[2].served.on = false; }, /served modes differ/],
    ['a changed pinned fixture', e => { e.run.cases[2].feeds = ['\x1b[?66h', '\x1b[?66l']; }, /fixture differs/],
    ['a reordered case list', e => { e.run.cases.reverse(); }, /case order|cover|differs/],
    ['an unnamed probe', e => { e.run.probeIds = ['input.x10-mouse']; }, /does not cover/],
    ['another adapter lineage', e => { e.adapter.derivedFromAdapterId = 'ntilde-owned-rectangle-checksum-grading-v1'; }, /adapter identity/],
    ['an adapter without a bundle digest', e => { e.adapter.bundleSha256 = 'x'; }, /adapter identity/],
]) test('a retained ' + label + ' is refused', () => {
    const evidence = syntheticRoutedInputEvidence(); mutate(evidence);
    assert.throws(() => build(evidence, { replayModule: fakeGradingModule }), pattern);
});

test('a replay module that disagrees with the live pinned result is refused', () => {
    const evidence = syntheticRoutedInputEvidence();
    const decided = { ...fakeGradingModule, ntildeRunRoutedInputTermless: (probeId, ctx) => ({ ...fakeGradingModule.ntildeRunRoutedInputTermless(probeId, ctx), response: '{}' }) };
    assert.throws(() => build(evidence, { replayModule: decided }), /replay does not reproduce/);
});
