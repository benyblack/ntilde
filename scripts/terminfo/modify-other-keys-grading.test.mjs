import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildModifyOtherKeysGradingReport, modifyOtherKeysCounts } from './modify-other-keys-grading.mjs';
import { routedInputsDiverge } from './routed-input-grading.mjs';
import { fakeModifyOtherKeysGradingModule, syntheticModifyOtherKeysEvidence } from './fixtures/modify-other-keys-evidence.mjs';
import { makeModifyOtherKeysCases } from './modify-other-keys-cases.mjs';
import { makeRoutedInputCases } from './routed-input-cases.mjs';
import { modifyOtherKeysCaseIds } from './modify-other-keys-contracts.mjs';
import { inputModeReplyHex } from './input-state.mjs';
import { inputPlanV3 } from './input-plan.mjs';

const build = (evidence, options = {}) => buildModifyOtherKeysGradingReport(evidence, evidence.run, evidence.runId, evidence.adapter,
    { replayModule: fakeModifyOtherKeysGradingModule, inputPlan: inputPlanV3, ...options });
const byId = report => report.cases[0];
const withActual = (id, hex) => syntheticModifyOtherKeysEvidence({ actual: item => item.id === id ? hex : item.expectedHex });

test('measured child bytes that match every literal grade supported beside the inconclusive pinned verdict', () => {
    const evidence = syntheticModifyOtherKeysEvidence();
    const report = build(evidence);
    assert.deepEqual(modifyOtherKeysCounts(report), { 'inconclusive/supported': 1 });
    assert.deepEqual(report.cases.map(item => item.caseId), modifyOtherKeysCaseIds);
    const item = byId(report);
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.equal(item.conclusion.grading, 'pinned-termless-preserved-by-design');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
    assert.match(item.effectContract.attribution, /not the pinned probe/);
    assert.equal(item.effectContract.id, 'ntilde-modify-other-keys-effect-v1');
    assert.ok(item.experiments.every(experiment => experiment.matched && experiment.actualHex === experiment.expectedHex && experiment.oracleHex === experiment.expectedHex));
    assert.ok(item.experiments.every(experiment => experiment.appFacts.boundary === 'avalonia-routed'));
    assert.equal(item.pinned.response, '{"declaredModifyOtherKeys":false}');
    assert.deepEqual(item.served, { declaredModifyOtherKeys: false, source: 'child-xtgettcap-modifyOtherKeys' });
    assert.ok(item.pinnedCriterion.includes('declaredModifyOtherKeys'));
    assert.match(item.pinned.note, /No generated input event and encoded report were validated/);
    assert.equal(report.restoredSnapshot, 238);
    assert.equal(report.inputSuiteId, 'ntilde-owned-routed-input-v3');
    assert.equal(report.schemaVersion, 1);
    // Levels are subcases: the 58 experiments carry both level-1 and level-2 ids under the one benchmark id.
    assert.ok(item.experiments.some(experiment => experiment.id.startsWith('input.mok.l1.')));
    assert.ok(item.experiments.some(experiment => experiment.id.startsWith('input.mok.l2.')));
    assert.ok(item.experiments.every(experiment => !experiment.id.includes('other-keys-3')));
});

for (const [label, id, hex] of [
    ['a level-1 alias encoded anyway', 'input.mok.l1.ctrl.a.alias', '1b5b27353b3937307e'],
    ['a level-1 extended chord kept legacy', 'input.mok.l1.ctrl.digit1', '31'],
    ['a level-2 chord lost', 'input.mok.l2.ctrl.a', ''],
    ['a level-2 alias kept ESC', 'input.mok.l2.ctrl.leftbracket.alias', '1b'],
    ['a level transition that did not take', 'input.mok.trans.set2.from1', '01'],
    ['the reset form ignored', 'input.mok.trans.reset.form', '1b5b27353b3937307e'],
    ['the disable form ignored', 'input.mok.trans.disable.form', '1b5b27353b3937307e'],
    ['level 3 applied', 'input.mok.trans.l3.ignored', '01'],
    ['Shift+Space delivered twice', 'input.mok.l2.space.shift.withText', '1b5b27323b33327e20'],
    ['a shifted letter encoded anyway', 'input.mok.l2.shift.a.withText', '1b5b27323b36357e'],
    ['a key-up that sent bytes', 'input.mok.l2.keyup.ctrl.a', '1b5b27353b3937307e'],
    ['the kitty pop dropped the level', 'input.mok.l2.kitty.pop.restores', '1b5b39373b3575'],
    ['XTQMODKEYS answered after a reset that never happened', 'input.mok.l2.tab.ctrl', '09'],
]) test('a required mismatch (' + label + ') grades unsupported while the pinned verdict stays inconclusive', () => {
    const report = build(withActual(id, hex));
    const item = byId(report);
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.deepEqual(item.effectContract.failedRequired, [id]);
    assert.equal(item.conclusion.outcome, 'inconclusive', 'the pinned verdict is never rewritten');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'pinned-inconclusive-by-design' });
    assert.equal(item.experiments.find(experiment => experiment.id === id).matched, false);
});

for (const [label, id, hex] of [
    ['a disabled control encoded', 'input.mok.disabled.ctrl.a', '1b5b27353b3937307e'],
    ['a disabled Alt control kept ESC a', 'input.mok.disabled.alt.a', '1b5b27333b3937307e'],
    ['the unhandled text control delivered nothing', 'input.mok.l2.normal.a.withText', ''],
    ['the text-recovery control stayed suppressed', 'input.mok.l2.text.recovery', ''],
    ['the kitty-precedence control sent modifyOtherKeys bytes', 'input.mok.l2.kitty.wins.control', '1b5b27353b3937307e'],
]) test('an invalid control (' + label + ') leaves the benchmark inconclusive, never unsupported', () => {
    const report = build(withActual(id, hex));
    const item = byId(report);
    assert.equal(item.effectContract.outcome, 'inconclusive');
    assert.equal(item.effectContract.reason, 'control-mismatch');
    assert.ok(item.effectContract.failedControls.includes(id));
    assert.equal(item.divergence, undefined);
});

test('a paired key-down certifies only with its follow-up fact retained', () => {
    // A missing follow-up fact refuses the journal outright; a reported-unhandled follow-up is retained and the bytes decide.
    const missing = syntheticModifyOtherKeysEvidence({ mutate: journal => {
        delete journal.actions.find(action => action.caseId === 'input.mok.l2.space.shift.withText').observed.app.textHandled;
    } });
    assert.throws(() => build(missing), /Invalid or incomplete owned routed-input evidence/);
    const unhandled = syntheticModifyOtherKeysEvidence({ mutate: journal => {
        journal.actions.find(action => action.caseId === 'input.mok.l2.space.shift.withText').observed.app.textHandled = false;
    } });
    const report = build(unhandled);
    assert.equal(byId(report).effectContract.outcome, 'unsupported');
    assert.equal(byId(report).experiments.find(experiment => experiment.id === 'input.mok.l2.space.shift.withText').matched, false);
    // With the fact present the bytes alone decide: a frame that also carries the text byte mismatches.
    const doubled = withActual('input.mok.l2.space.shift.withText', '1b5b27323b33327e20');
    assert.equal(byId(build(doubled)).effectContract.outcome, 'unsupported');
});

test('labeled policy exclusions never grade: their mismatches leave the effect verdict to the required experiments', () => {
    for (const id of ['input.mok.l2.meta.a.unhandled', 'input.mok.l2.ctrlalt.a.unhandled']) {
        const report = build(withActual(id, '1b61'));
        assert.equal(byId(report).effectContract.outcome, 'supported', id);
        assert.equal(byId(report).experiments.find(experiment => experiment.id === id).matched, false);
        assert.deepEqual(byId(report).divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
    }
});

test('a served declaration that the child never answered, or that disagrees with the journal, is refused', () => {
    const evidence = syntheticModifyOtherKeysEvidence();
    const changed = structuredClone(evidence.run);
    changed.cases[0].served.declaredModifyOtherKeys = true;
    assert.throws(() => build({ ...evidence, run: changed }), /served declaration differs/);
    // A report whose recorded served value contradicts the journal is refused; the prefix-less control itself is refused at the contracts layer.
});

for (const [label, mutate, pattern] of [
    ['a stale pane in one control frame', e => { e.frames[4].paneId = '22222222-2222-2222-2222-222222222222'; }],
    ['a mode control that did not prove the negotiated level', e => { e.frames[8 + 2 * 13].capture.hex = inputModeReplyHex({ applicationKeypad: false, mouseTracking: 0, mouseSgr: false, kittyKeyboardFlags: 0, modifyOtherKeysLevel: 2 }); }],
    ['a missing raw barrier', e => { e.frames[5].capture.barrier = ''; }],
    ['a reused ordering challenge', e => Object.assign(e.frames[7].capture, { nonces: e.frames[5].capture.nonces, barrier: e.frames[5].capture.barrier })],
    ['another App identity in an action', e => { e.actions[10].observed.after.app.pid++; }],
    ['an action without dispatch', e => { e.actions[3].observed.app.eventDispatched = false; }],
    ['a different event than planned', e => { e.actions[9].observed.request.modifiers = 3; }],
    ['state that changed after the event', e => { e.frames[7].afterScreen.cursorCol++; }],
    ['modes left unrestored', e => { e.frames.at(-1).capture.hex = inputModeReplyHex({ applicationKeypad: false, mouseTracking: 0, mouseSgr: false, kittyKeyboardFlags: 0 }); }],
    ['a failed capture seal', e => { e.seal.failed = true; }],
    ['an incomplete seal attempt count', e => { e.seal.attempts--; }],
    ['a missing frame', e => { e.frames.splice(9, 1); }],
    ['an extra action', e => { e.actions.push(structuredClone(e.actions[0])); }],
]) test('tampering with ' + label + ' prevents a graded report', () => {
    const evidence = syntheticModifyOtherKeysEvidence({ mutate: journal => { mutate(journal); } });
    assert.throws(() => build(evidence));
});

test('a journal that diverges from the frozen routed-input plan cannot grade either layer', () => {
    const inputs = makeModifyOtherKeysCases(40, 140).map(item => structuredClone(item));
    assert.equal(routedInputsDiverge(inputs, makeRoutedInputCases(40, 140)), null);
    const poisoned = inputs.map(item => structuredClone(item));
    poisoned.find(item => item.id === 'input.mok.l2.ctrl.a').expectedHex = 'ff';
    // The routed subset is untouched by a poisoned mok case; its own literal check stays with the frozen v2 plan.
    assert.equal(routedInputsDiverge(poisoned.filter(item => !item.id.startsWith('input.mok.')), makeRoutedInputCases(40, 140)), null);
    const dropped = inputs.filter(item => !item.id.startsWith('input.mouse.'));
    assert.match(routedInputsDiverge(dropped, makeRoutedInputCases(40, 140)), /case count differs/);
    const reordered = [inputs[5], ...inputs.slice(0, 5), ...inputs.slice(6)];
    assert.match(routedInputsDiverge(reordered, makeRoutedInputCases(40, 140)), /case order differs/);
    const changedEvent = inputs.map(item => structuredClone(item));
    changedEvent.find(item => item.id === 'input.mouse.x10.left.raw').event.modifiers = 0;
    assert.match(routedInputsDiverge(changedEvent, makeRoutedInputCases(40, 140)), /changed its event or roles/);
});

test('the report builder refuses a run without the policy plan', () => {
    const evidence = syntheticModifyOtherKeysEvidence();
    assert.throws(() => buildModifyOtherKeysGradingReport(evidence, evidence.run, evidence.runId, evidence.adapter, {}), /requires the policy-selected input plan/);
});
