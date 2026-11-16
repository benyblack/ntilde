import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildPxmGradingReport, pxmCounts } from './pxm-grading.mjs';
import { fakePxmGradingModule, pxmFinalHex, syntheticPxmEvidence } from './fixtures/pxm-evidence.mjs';
import { pxmCaseIds } from './pxm-contracts.mjs';
import { inputModeReplyHex } from './input-state.mjs';
import { inputPlanV5 } from './input-plan.mjs';

const B = 'input.pixel-mouse', id = name => 'input.pxm.' + name;
const build = (evidence, options = {}) => buildPxmGradingReport(evidence, evidence.run, evidence.runId, evidence.adapter, { inputPlan: inputPlanV5, ...options });
const withActual = (name, hex) => syntheticPxmEvidence({ actual: item => item.id === id(name) ? hex : item.expectedHex });
const modes = overrides => ({ applicationKeypad: false, mouseTracking: 0, mouseSgr: false, kittyKeyboardFlags: 0, modifyOtherKeysLevel: 0, mouseUtf8: false, mouseUrxvt: false, mousePixel: false, ...overrides });
const text = value => Buffer.from(value, 'latin1').toString('hex');
const controlFrame = (evidence, name) => evidence.frames.find(frame => frame.kind === 'input-control' && frame.caseId === id(name));

test('measured child bytes that match every literal grade supported beside the inconclusive pinned verdict', () => {
    const evidence = syntheticPxmEvidence(), report = build(evidence, { replayModule: fakePxmGradingModule });
    assert.deepEqual(pxmCounts(report), { 'inconclusive/supported': 1 });
    assert.deepEqual(report.cases.map(item => item.caseId), pxmCaseIds);
    const [item] = report.cases;
    assert.equal(item.conclusion.outcome, 'inconclusive'); assert.equal(item.conclusion.grading, 'pinned-termless-preserved-by-design');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
    assert.equal(item.effectContract.id, 'ntilde-sgr-pixel-mouse-1016-effect-v1'); assert.match(item.effectContract.attribution, /not the pinned probe/);
    assert.equal(item.experiments.length, 32);
    assert.ok(item.experiments.every(experiment => experiment.matched && experiment.actualHex === experiment.expectedHex && experiment.oracleHex === experiment.expectedHex));
    assert.ok(item.experiments.every(experiment => experiment.appFacts.boundary === 'avalonia-routed'));
    assert.ok(item.experiments.filter(experiment => experiment.event.pixelX !== undefined).every(experiment => Number.isSafeInteger(experiment.pointer.column) && experiment.pointer.x > 0),
        'a pixel-declared experiment keeps the cell and DIP position the App derived, apart from the graded bytes');
    assert.equal(item.experiments.find(experiment => experiment.id === id('control.sgr.press')).pointer, undefined);
    assert.equal(item.pinned.response, '{"currentMode":false}'); assert.deepEqual(item.pinnedFeeds, []);
    assert.deepEqual(item.served, { currentMode: false, on: true, off: false, sources: ['input.pxm.alone.set', 'restored'] });
    assert.equal(report.restoredSnapshot, 394); assert.equal(report.inputSuiteId, inputPlanV5.suiteId);
});

for (const [label, name, hex] of [
    ['SGR cell coordinates instead of pixels', 'press.left.mid', text('\x1b[<0;96;3M')],
    ['unshifted pixels (zero-based)', 'press.left.origin', text('\x1b[<0;0;0M')],
    ['a legacy report', 'press.middle.mid', text('\x1b[M!\x80#')],
    ['a missing +1 on one axis', 'press.right.mid', text('\x1b[<2;144;57M')],
    ['wrong modifier bits', 'press.ctrl', text('\x1b[<20;144;58M')],
    ['a release finished with M', 'release.left', text('\x1b[<0;144;58M')],
    ['a release that lost the button', 'release.right', text('\x1b[<3;144;58m')],
    ['a wheel with the wrong code', 'wheel.down', text('\x1b[<64;144;58M')],
    ['held motion lost', 'motion.held', ''],
    ['cell-coalesced motion (an adjacent pixel not reported)', 'motion.next-pixel', ''],
    ['a hover with the held-button code', 'hover.any', text('\x1b[<32;1;1M')],
    ['a repeated pixel that is reported again', 'hover.same-pixel', text('\x1b[<35;1;1M')],
    ['X10 press carrying modifier bits', 'x10.press', text('\x1b[<28;144;58M')],
    ['1006 not replaced by 1016', 'slot.pixel.replaces.1006', text('\x1b[<0;96;3M')],
    ['1016 not replacing 1015', 'slot.pixel.replaces.1015', text('\x1b[32;96;3M')],
    ['1006 not replacing 1016', 'slot.sgr.replaces', text('\x1b[<0;144;58M')],
    ['a reset of the inactive 1016 clearing SGR', 'slot.reset.inactive', text('\x1b[M \x80#')],
    ['1016 staying active after its reset', 'slot.reset.active', text('\x1b[<0;144;58M')],
    ['the state carrier reporting bytes', 'alone.set', text('\x1b[<0;1;1M')],
    ['a duplicated report', 'press.left.small', text('\x1b[<0;4;12M\x1b[<0;4;12M')],
]) test('a required mismatch (' + label + ') is unsupported with valid controls and the pinned verdict is never rewritten', () => {
    const [item] = build(withActual(name, hex)).cases;
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.deepEqual(item.effectContract.failedRequired, [id(name)]);
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'pinned-inconclusive-by-design' });
});

for (const [label, name, hex] of [
    ['a legacy control that already reports pixels', 'control.legacy.press', text('\x1b[<0;96;3M')],
    ['an SGR control that reports legacy bytes', 'control.sgr.press', text('\x1b[M \x80#')],
]) test('an invalid control (' + label + ') leaves the benchmark inconclusive, never unsupported', () => {
    const [item] = build(withActual(name, hex)).cases;
    assert.equal(item.effectContract.outcome, 'inconclusive'); assert.equal(item.effectContract.reason, 'control-mismatch');
    assert.deepEqual(item.effectContract.failedControls, [id(name)]); assert.equal(item.divergence, undefined);
});

for (const [label, mutate] of [
    ['a stale pane in one control frame', e => { e.frames[4].paneId = '22222222-2222-2222-2222-222222222222'; }],
    ['a mode control that did not prove 1016 set', e => { controlFrame(e, 'press.left.mid').capture.hex = inputModeReplyHex(modes({ mouseTracking: 1000 })); }],
    ['a mode control that proved 1006 where 1016 was planned', e => { controlFrame(e, 'press.left.mid').capture.hex = inputModeReplyHex(modes({ mouseTracking: 1000, mouseSgr: true })); }],
    ['a mode control without the 1016 reply', e => { controlFrame(e, 'press.left.mid').capture.hex = inputModeReplyHex({ ...modes({ mouseTracking: 1000, mousePixel: true }), mousePixel: undefined }); }],
    ['a missing raw barrier', e => { e.frames[5].capture.barrier = ''; }],
    ['another App identity in an action', e => { e.actions[170].observed.after.app.pid++; }],
    ['an action that did not report the planned 1016 state', e => { e.actions[170].observed.app.mousePixel = !e.actions[170].observed.app.mousePixel; }],
    ['a pixel action whose App-derived column lies outside the grid', e => { e.actions.find(action => action.caseId === id('press.left.mid')).observed.app.column = 121; }],
    ['a pixel action whose App-derived row is not an integer', e => { e.actions.find(action => action.caseId === id('press.left.mid')).observed.app.row = 1.5; }],
    ['a pixel action without a routed position', e => { e.actions.find(action => action.caseId === id('press.left.mid')).observed.app.x = null; }],
    ['a cell action whose echoed cell differs from the plan', e => { e.actions.find(action => action.caseId === id('control.sgr.press')).observed.app.column = 95; }],
    ['a changed pixelX in the request echo', e => { e.actions.find(action => action.caseId === id('press.left.mid')).observed.request.pixelX = 144; }],
    ['a pixel request that also carries a cell', e => { e.actions.find(action => action.caseId === id('press.left.mid')).observed.request.column = 5; }],
    ['an action without dispatch', e => { e.actions[180].observed.app.eventDispatched = false; }],
    ['modes left unrestored (1016 still set)', e => { e.frames.at(-1).capture.hex = inputModeReplyHex(modes({ mousePixel: true })); }],
    ['a restoration capture from the v4 profile', e => { e.frames.at(-1).capture.hex = inputModeReplyHex({ ...modes({}), mousePixel: undefined }); }],
    ['a failed capture seal', e => { e.seal.failed = true; }],
    ['a missing frame', e => { e.frames.splice(9, 1); }],
    ['an extra action', e => { e.actions.push(structuredClone(e.actions[0])); }],
]) test('tampering with ' + label + ' prevents a graded report', () => {
    assert.throws(() => build(syntheticPxmEvidence({ mutate })));
});

test('a journal of an earlier plan generation is refused: the layer needs the policy-selected v5 plan', () => {
    const evidence = syntheticPxmEvidence();
    assert.throws(() => build(evidence, { inputPlan: undefined }), /requires the policy-selected input plan/);
    assert.throws(() => build(evidence, { inputPlan: { make: () => [] } }));
});

for (const [label, mutate, pattern] of [
    ['a pinned result that differs from its replay', e => { e.run.cases[0].pinned.response = '{"currentMode":true}'; }, /does not match its observed evidence/],
    ['a pinned verdict that gained a decision', e => { e.run.cases[0].pinned = { probeId: B, pass: true, response: '{}', note: 'x', observation: { outcome: 'supported', evidence: 'query' } }; }, /does not match|inconclusive by design/],
    ['a served state that the child never sent', e => { e.run.cases[0].served.on = false; }, /served value differs/],
    ['a served current mode the child never sent', e => { e.run.cases[0].served.currentMode = true; }, /served value differs/],
    ['a changed pinned fixture', e => { e.run.cases[0].feeds = ['\x1b[?1016h']; }, /fixture differs/],
    ['an unnamed probe', e => { e.run.probeIds = ['input.urxvt-mouse']; }, /does not cover/],
    ['another adapter lineage', e => { e.adapter.derivedFromAdapterId = 'ntilde-owned-rectangle-checksum-grading-v1'; }, /adapter identity/],
    ['an adapter without a bundle digest', e => { e.adapter.bundleSha256 = 'x'; }, /adapter identity/],
]) test('a retained ' + label + ' is refused', () => {
    const evidence = syntheticPxmEvidence(); mutate(evidence);
    assert.throws(() => build(evidence, { replayModule: fakePxmGradingModule }), pattern);
});

test('a replay module that disagrees with the live pinned result is refused', () => {
    const evidence = syntheticPxmEvidence(), decided = { ...fakePxmGradingModule, ntildeRunPxmTermless: (probeId, ctx) => ({ ...fakePxmGradingModule.ntildeRunPxmTermless(probeId, ctx), response: '{}' }) };
    assert.throws(() => build(evidence, { replayModule: decided }), /replay does not reproduce/);
});

test('the shared journal must still carry the routed-input events verbatim, and the final restoration profile is the v5 one', () => {
    assert.doesNotThrow(() => build(syntheticPxmEvidence()));
    assert.match(Buffer.from(pxmFinalHex(), 'hex').toString('latin1'), /\x1b\[\?1016;2\$y$/);
    const tampered = syntheticPxmEvidence({ mutate: journal => { journal.actions.find(action => action.caseId === 'input.mouse.x10.left.raw').observed.request.column = 95; } });
    assert.throws(() => build(tampered));
});
