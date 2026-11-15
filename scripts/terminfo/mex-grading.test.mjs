import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildMexGradingReport, mexCounts } from './mex-grading.mjs';
import { fakeMexGradingModule, mexFinalHex, syntheticMexEvidence } from './fixtures/mex-evidence.mjs';
import { mexCaseIds } from './mex-contracts.mjs';
import { inputModeReplyHex } from './input-state.mjs';
import { inputPlanV4 } from './input-plan.mjs';

const build = (evidence, options = {}) => buildMexGradingReport(evidence, evidence.run, evidence.runId, evidence.adapter, { inputPlan: inputPlanV4, ...options });
const byCase = report => Object.fromEntries(report.cases.map(item => [item.caseId, item]));
const withActual = (id, hex) => syntheticMexEvidence({ actual: item => item.id === 'input.mex.' + id ? hex : item.expectedHex });
const U = 'modes.utf8-mouse-1005', R = 'input.urxvt-mouse';
const modes = overrides => ({ applicationKeypad: false, mouseTracking: 0, mouseSgr: false, kittyKeyboardFlags: 0, modifyOtherKeysLevel: 0, mouseUtf8: false, mouseUrxvt: false, ...overrides });
const text = value => Buffer.from(value, 'latin1').toString('hex');
const frameOf = (evidence, id) => evidence.frames.find(frame => frame.kind === 'input-control' && frame.caseId === 'input.mex.' + id);

test('measured child bytes that match every literal grade supported beside inconclusive pinned verdicts', () => {
    const evidence = syntheticMexEvidence(), report = build(evidence, { replayModule: fakeMexGradingModule });
    assert.deepEqual(mexCounts(report), { 'inconclusive/supported': 2 });
    assert.deepEqual(report.cases.map(item => item.caseId), mexCaseIds);
    for (const item of report.cases) {
        assert.equal(item.conclusion.outcome, 'inconclusive'); assert.equal(item.conclusion.grading, 'pinned-termless-preserved-by-design');
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-by-design' });
        assert.match(item.effectContract.attribution, /not the pinned probe/);
        assert.ok(item.experiments.every(experiment => experiment.matched && experiment.actualHex === experiment.expectedHex && experiment.oracleHex === experiment.expectedHex));
        assert.ok(item.experiments.every(experiment => experiment.appFacts.boundary === 'avalonia-routed'));
    }
    const [utf8, urxvt] = report.cases;
    assert.equal(utf8.pinned.response, JSON.stringify({ response: '\x1b[?1005;1$y' }));
    assert.deepEqual(utf8.pinnedFeeds, ['\x1b[?1005h', '\x1b[?1005l']);
    assert.equal(utf8.pinned.notTested.reason, 'no-semantic-observable');
    assert.deepEqual(utf8.served, { response: '\x1b[?1005;1$y', on: true, off: false, sources: ['input.mex.u.pinned.set', 'input.mex.u.pinned.reset'] });
    assert.equal(urxvt.pinned.response, '{"currentMode":false}');
    assert.equal(urxvt.effectContract.id, 'ntilde-urxvt-mouse-effect-v1');
    assert.equal(report.restoredSnapshot, 330);
    assert.equal(report.inputSuiteId, inputPlanV4.suiteId);
});

for (const [label, id, hex, caseId] of [
    ['legacy single octets instead of UTF-8 coordinates', 'u.press.left', text('\x1b[M \x80#'), U],
    ['a UTF-8 encoded button byte plus a stray coordinate octet', 'u.press.middle', text('\x1b[M!\xc2\x80\x23\x23'), U],
    ['position 95 expanded to two octets', 'u.press.col95', text('\x1b[M \xc2\x7f#'), U],
    ['position 97 reported with a wrong continuation octet', 'u.press.col97', text('\x1b[M \xc2\x80#'), U],
    ['UTF-8 release reported as SGR', 'u.release.left', text('\x1b[<0;96;3m'), U],
    ['UTF-8 wheel with the wrong code', 'u.wheel.down', text('\x1b[M`\xc2\x80#'), U],
    ['1005 lost under X10 tracking', 'u.x10.press', '', U],
    ['1006 not replacing 1005', 'u.slot.sgr.replaces', text('\x1b[M \xc2\x80#'), U],
    ['1005 not replacing 1006', 'u.slot.utf8.replaces', text('\x1b[<0;96;3M'), U],
    ['a reset of the inactive 1005 clearing SGR', 'u.slot.reset.inactive', text('\x1b[M \x80#'), U],
    ['1005 staying active after its reset', 'u.slot.reset.active', text('\x1b[M \xc2\x80#'), U],
    ['the encoding alone reporting an event without a tracking selector', 'u.pinned.set', text('\x1b[M \xc2\x80#'), U],
    ['urxvt without the +32 button offset', 'r.press.left', text('\x1b[0;96;3M'), R],
    ['urxvt release finished with m', 'r.release.left', text('\x1b[35;96;3m'), R],
    ['urxvt reporting SGR coordinates', 'r.press.right', text('\x1b[<2;96;3M'), R],
    ['urxvt modifier bits mapped wrongly', 'r.press.ctrl', text('\x1b[56;96;3M'), R],
    ['urxvt wheel reported as buttons', 'r.wheel.up', text('\x1b[64;96;3M'), R],
    ['urxvt held motion lost', 'r.motion.left', '', R],
    ['urxvt hover with the wrong code', 'r.hover.any', text('\x1b[35;96;4M'), R],
    ['urxvt X10 press carrying modifiers', 'r.x10.press.mods7', text('\x1b[60;96;3M'), R],
    ['1015 not replacing 1006', 'r.slot.urxvt.replaces', text('\x1b[<0;96;3M'), R],
    ['1006 not replacing 1015', 'r.slot.sgr.replaces', text('\x1b[32;96;3M'), R],
    ['1015 staying active after its reset', 'r.slot.reset.active', text('\x1b[32;96;3M'), R],
    ['urxvt reporting without a tracking selector', 'r.nobytes.without.tracking', text('\x1b[32;96;3M'), R],
    ['a duplicated urxvt report', 'r.press.middle', text('\x1b[33;96;3M\x1b[33;96;3M'), R],
]) test('a required mismatch (' + label + ') is unsupported with valid controls, and the other benchmark is unaffected', () => {
    const report = build(withActual(id, hex)), cases = byCase(report);
    assert.equal(cases[caseId].effectContract.outcome, 'unsupported');
    assert.deepEqual(cases[caseId].effectContract.failedRequired, ['input.mex.' + id]);
    assert.equal(cases[caseId].conclusion.outcome, 'inconclusive', 'the pinned verdict is never rewritten');
    assert.deepEqual(cases[caseId].divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'pinned-inconclusive-by-design' });
    const other = mexCaseIds.find(item => item !== caseId);
    assert.equal(cases[other].effectContract.outcome, 'supported');
});

test('a labeled rightmost-column mismatch is reported but never grades', () => {
    const report = build(withActual('u.press.rightmost', text('\x1b[M \xc2\x80#'))), cases = byCase(report);
    assert.equal(cases[U].effectContract.outcome, 'supported');
    const label = cases[U].experiments.find(item => item.id === 'input.mex.u.press.rightmost');
    assert.deepEqual([label.role, label.matched], ['label', false]);
});

for (const [label, id, hex, shared] of [
    ['a legacy control that already reports UTF-8 octets', 'control.legacy.press', text('\x1b[M \xc2\x80#'), [U, R]],
    ['an SGR control that reports legacy bytes', 'control.sgr.press', text('\x1b[M \x80#'), [U, R]],
    ['a baseline that reports a press', 'baseline.reset', text('a'), [U, R]],
]) test('an invalid control (' + label + ') leaves both benchmarks inconclusive, never unsupported', () => {
    const report = build(withActual(id, hex));
    for (const caseId of shared) {
        const item = byCase(report)[caseId];
        assert.equal(item.effectContract.outcome, 'inconclusive'); assert.equal(item.effectContract.reason, 'control-mismatch');
        assert.ok(item.effectContract.failedControls.includes('input.mex.' + id)); assert.equal(item.divergence, undefined);
    }
});

const frameIndex = (evidence, id, kind = 'input-control') => evidence.frames.findIndex(frame => frame.kind === kind && frame.caseId === 'input.mex.' + id);
for (const [label, mutate] of [
    ['a stale pane in one control frame', e => { e.frames[4].paneId = '22222222-2222-2222-2222-222222222222'; }],
    ['a mode control that did not prove 1005 set', e => { frameOf(e, 'u.press.left').capture.hex = inputModeReplyHex(modes({ mouseTracking: 1000 })); }],
    ['a mode control that proved 1015 where 1005 was planned', e => { frameOf(e, 'u.press.left').capture.hex = inputModeReplyHex(modes({ mouseTracking: 1000, mouseUrxvt: true })); }],
    ['a mode control without the 1005/1015 replies', e => { frameOf(e, 'r.press.left').capture.hex = inputModeReplyHex({ ...modes({ mouseTracking: 1000, mouseUrxvt: true }), mouseUtf8: undefined, mouseUrxvt: undefined }); }],
    ['a missing raw barrier', e => { e.frames[5].capture.barrier = ''; }],
    ['a reused ordering challenge', e => Object.assign(e.frames[7].capture, { nonces: e.frames[5].capture.nonces, barrier: e.frames[5].capture.barrier })],
    ['another App identity in an action', e => { e.actions[130].observed.after.app.pid++; }],
    ['an action that did not report the planned 1005 state', e => { e.actions[130].observed.app.mouseUtf8 = !e.actions[130].observed.app.mouseUtf8; }],
    ['an action without dispatch', e => { e.actions[140].observed.app.eventDispatched = false; }],
    ['a different event than planned', e => { e.actions[150].observed.request.modifiers = 3; }],
    ['state that changed after the event', e => { e.frames[7].afterScreen.cursorCol++; }],
    ['modes left unrestored (1015 still set)', e => { e.frames.at(-1).capture.hex = inputModeReplyHex(modes({ mouseUrxvt: true })); }],
    ['modes left unrestored (1005 still set)', e => { e.frames.at(-1).capture.hex = inputModeReplyHex(modes({ mouseUtf8: true })); }],
    ['a restoration capture from the v3 profile', e => { e.frames.at(-1).capture.hex = inputModeReplyHex({ ...modes({}), mouseUtf8: undefined, mouseUrxvt: undefined }); }],
    ['a failed capture seal', e => { e.seal.failed = true; }],
    ['an incomplete seal attempt count', e => { e.seal.attempts--; }],
    ['a missing frame', e => { e.frames.splice(9, 1); }],
    ['an extra action', e => { e.actions.push(structuredClone(e.actions[0])); }],
]) test('tampering with ' + label + ' prevents a graded report', () => {
    const evidence = syntheticMexEvidence({ mutate: journal => { mutate(journal); } });
    assert.throws(() => build(evidence));
});

test('a journal of an earlier plan generation is refused: the layer needs the v4 plan', () => {
    const evidence = syntheticMexEvidence();
    assert.throws(() => build(evidence, { inputPlan: undefined }), /requires the policy-selected input plan/);
    assert.throws(() => build(evidence, { inputPlan: { make: () => [] } }));
});

for (const [label, mutate, pattern] of [
    ['a pinned result that differs from its replay', e => { e.run.cases[1].pinned.response = '{"currentMode":true}'; }, /does not match its observed evidence/],
    ['a 1005 pinned response the child never sent', e => { e.run.cases[0].pinned.response = JSON.stringify({ response: '\x1b[?1005;2$y' }); }, /does not match its observed evidence/],
    ['a pinned verdict that gained a decision', e => { e.run.cases[0].pinned = { probeId: U, pass: true, response: '{}', note: 'x', observation: { outcome: 'supported', evidence: 'query' } }; }, /does not match|inconclusive by design/],
    ['a served reply that the child never sent', e => { e.run.cases[0].served.response = '\x1b[?1005;2$y'; }, /served values differ/],
    ['a served state that the child never sent', e => { e.run.cases[0].served.on = false; }, /served values differ/],
    ['a changed pinned fixture', e => { e.run.cases[0].feeds = ['\x1b[?1005l', '\x1b[?1005h']; }, /fixture differs/],
    ['a reordered case list', e => { e.run.cases.reverse(); }, /case order|cover|differs/],
    ['an unnamed probe', e => { e.run.probeIds = ['input.urxvt-mouse']; }, /does not cover/],
    ['another adapter lineage', e => { e.adapter.derivedFromAdapterId = 'ntilde-owned-rectangle-checksum-grading-v1'; }, /adapter identity/],
    ['an adapter without a bundle digest', e => { e.adapter.bundleSha256 = 'x'; }, /adapter identity/],
]) test('a retained ' + label + ' is refused', () => {
    const evidence = syntheticMexEvidence(); mutate(evidence);
    assert.throws(() => build(evidence, { replayModule: fakeMexGradingModule }), pattern);
});

test('a replay module that disagrees with the live pinned result is refused', () => {
    const evidence = syntheticMexEvidence();
    for (const [label, change] of [['a different response', result => ({ ...result, response: '{}' })],
        ['an empty captured reply', result => ({ ...result, response: JSON.stringify({ response: '' }) })]]) {
        const decided = { ...fakeMexGradingModule, ntildeRunMexTermless: (probeId, ctx) => change(fakeMexGradingModule.ntildeRunMexTermless(probeId, ctx)) };
        assert.throws(() => build(evidence, { replayModule: decided }), /replay does not reproduce/, label);
    }
});

test('the shared journal must still carry the routed-input events verbatim', () => {
    const evidence = syntheticMexEvidence();
    assert.doesNotThrow(() => build(evidence));
    assert.ok(mexFinalHex().length > 0);
    const tampered = syntheticMexEvidence({ mutate: journal => { journal.actions.find(action => action.caseId === 'input.mouse.x10.left.raw').observed.request.column = 95; } });
    assert.throws(() => build(tampered));
});
