import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildDslGradingReport, validateDslEvidence } from './dsl-grading.mjs';
import { resizedDslScreen, syntheticDslEvidence, timingOf } from './fixtures/dsl-evidence.mjs';
import { paletteBinding } from './fixtures/col-edit-evidence.mjs';
import { dslSuiteId, dslCaseIds } from './dsl-contracts.mjs';

const build = evidence => buildDslGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);

test('the synthetic evidence builds a report whose pinned verdicts stay inconclusive and whose effect is unsupported, recorded as a divergence', () => {
    const evidence = syntheticDslEvidence();
    const report = build(evidence);
    assert.equal(report.suiteId, dslSuiteId);
    assert.deepEqual(report.cases.map(item => item.caseId), dslCaseIds);
    const [item] = report.cases;
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.equal(item.conclusion.reason, 'insufficient-evidence');
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'pinned-inconclusive-effect-unsupported' });
    assert.equal(item.pinned.termless.observation.evidence, 'parser-state');
    assert.equal(item.pinned.terminal.observation.evidence, 'query');
    assert.equal(item.observed.groups.pinned.matches, true);
    assert.deepEqual(item.observed.groups.set.app, { rows: 13, cols: 60 });
    assert.equal(item.observed.groups.set.elapsedMs, 15);
    assert.equal(item.observed.groups.paced.elapsedMs, 2590);
    assert.equal(item.observed.timing.boundMs, 2000);
    assert.equal(item.observed.groups['control-clear'].unchanged, null, 'the clear control is judged on its own demand, not on staying unchanged');
    assert.equal(report.restoredSnapshot, evidence.frames.length - 1);
    assert.ok(report.snapshots.every(frame => frame.timing.clock === 'parent-monotonic'), 'every journaled frame carries the parent timing');
    assert.equal(report.snapshots[0].timing.elapsedMs, 0);
    assert.ok(item.reads.every(read => Array.isArray(read.size)));
});

test('a recognised mode 4, a slow exchange or a changed grid leaves the report an inconclusive effect and no divergence', () => {
    const recognised = syntheticDslEvidence({ mutate: (_case, role, read) => (role === 'set' ? { ...read, replies: ['\x1b[?4;2$y'] } : null) });
    const first = build(recognised).cases[0];
    assert.equal(first.effectContract.outcome, 'inconclusive');
    assert.match(first.effectContract.note, /needs its own supported contract/);
    assert.equal(first.divergence, undefined, 'both verdicts are inconclusive');
    // A burst that took longer than the bound is graded, not refused: the timing is a measurement.
    const slow = syntheticDslEvidence({ mutate: (_case, role, read) => (role === 'burst-set' ? { ...read, timing: timingOf(6700) } : null) });
    assert.match(build(slow).cases[0].effectContract.note, /burst-set exchange took 6700 ms.*paced/);
    // An unanswered pinned exchange is a graded mismatch, never a pinned verdict change.
    const silent = syntheticDslEvidence({ mutate: (_case, role, read) => (role === 'pinned' ? { ...read, replies: [] } : null) });
    assert.equal(build(silent).cases[0].conclusion.outcome, 'inconclusive');
    assert.match(build(silent).cases[0].effectContract.note, /pinned replies differ/);
    // An App grid that changed is not gradable at all: the journal holds one grid, so the run is refused instead of judged.
    const appSwitched = syntheticDslEvidence({ mutate: (_case, role, read) => (role === 'burst-held' ? { ...read, screen: resizedDslScreen(13, 132) } : null) });
    assert.throws(() => build(appSwitched), /Changed decsclm snapshot evidence/);
});

test('a recognised pinned query makes the evidence impossible to replay', () => {
    assert.throws(() => syntheticDslEvidence({ mutate: (_case, role, read) => (role === 'pinned-query' ? { ...read, replies: ['\x1b[?4;1$y'] } : null) }), /would pass without any scroll timing/);
});

test('a changed reply list or size between the journal and the worker record refuses', () => {
    const replies = syntheticDslEvidence();
    replies.run.cases[0].reads[3].report.replies = ['\x1b[?4;2$y'];
    assert.throws(() => build(replies), /replies differ/);
    const size = syntheticDslEvidence();
    size.run.cases[0].reads[3].size = [13, 132];
    assert.throws(() => build(size), /size differs from its read frame/);
});

test('a retained pinned result that differs from its replay refuses', () => {
    const termless = syntheticDslEvidence();
    termless.run.cases[0].pinned.termless.observation.evidence = 'query';
    assert.throws(() => build(termless), /does not match its observed evidence/);
    const terminal = syntheticDslEvidence();
    terminal.run.cases[0].pinned.terminal.note = 'DECRPM mode 4: supported';
    assert.throws(() => build(terminal), /does not match its observed evidence/);
    const extra = syntheticDslEvidence();
    extra.run.cases[0].pinned.termless.response = JSON.stringify({ response: '\x1b[?4;0$y', cols: 132 });
    assert.throws(() => build(extra), /does not match its observed evidence/);
});

test('every frame must carry the canonical timing the parent took, and the start frame reads zero', () => {
    const missing = syntheticDslEvidence();
    delete missing.frames[5].timing;
    assert.throws(() => build(missing), /Invalid DECSCLM timing record/);
    for (const bad of [{ elapsedMs: 15.5, clock: 'parent-monotonic' }, { elapsedMs: -3, clock: 'parent-monotonic' }, { elapsedMs: 15, clock: 'worker-wall' }, { elapsedMs: 15, clock: 'parent-monotonic', extra: true }]) {
        const rewritten = syntheticDslEvidence();
        rewritten.frames[5].timing = bad;
        assert.throws(() => build(rewritten), /Invalid DECSCLM timing record/);
    }
    const start = syntheticDslEvidence();
    start.frames[0].timing = timingOf(40);
    assert.throws(() => build(start), /start frame carries a timing reading/);
});

test('refuses tampered evidence: restoration, binding stability, adapter identity and run identity', () => {
    const evidence = syntheticDslEvidence();
    assert.throws(() => validateDslEvidence(evidence.frames, evidence.run, evidence.runId, { ...evidence.adapter, id: 'other' }), /adapter identity/);
    const wrongAdapter = syntheticDslEvidence();
    wrongAdapter.adapter.derivedFromAdapterId = 'other';
    assert.throws(() => build(wrongAdapter), /adapter identity/);
    const wrongProbes = syntheticDslEvidence();
    wrongProbes.run.probeIds = ['other'];
    assert.throws(() => build(wrongProbes), /does not cover the contracted cases/);
    const incomplete = syntheticDslEvidence();
    assert.throws(() => validateDslEvidence(incomplete.frames.slice(0, -1), incomplete.run, incomplete.runId, incomplete.adapter), /Incomplete decsclm snapshot sequence/);
    const restored = syntheticDslEvidence();
    restored.frames.at(-1).sha256 = '0'.repeat(64);
    assert.throws(() => validateDslEvidence(restored.frames, restored.run, restored.runId, restored.adapter), /Changed decsclm snapshot evidence|fixture was not restored/);
    const recolored = syntheticDslEvidence({ finishBinding: paletteBinding({ background: 0xff010203 }) });
    assert.throws(() => build(recolored), /fixture was not restored|palette or theme/);
    const reported = syntheticDslEvidence();
    reported.frames[0].report = { barrier: '\x1b[?62;4;22c', replies: [] };
    assert.throws(() => build(reported), /Unexpected decsclm report frame|Changed decsclm/);
});
