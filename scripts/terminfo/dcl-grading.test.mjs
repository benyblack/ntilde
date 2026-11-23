import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildDclGradingReport, validateDclEvidence } from './dcl-grading.mjs';
import { kernelOf, resizedDclScreen, sttyDescription, syntheticDclEvidence } from './fixtures/dcl-evidence.mjs';
import { paletteBinding } from './fixtures/col-edit-evidence.mjs';
import { dclSuiteId, dclCaseIds } from './dcl-contracts.mjs';

const build = evidence => buildDclGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);

test('the synthetic evidence builds a report whose pinned verdicts stay inconclusive and whose effect is unsupported, recorded as a divergence', () => {
    const evidence = syntheticDclEvidence();
    const report = build(evidence);
    assert.equal(report.suiteId, dclSuiteId);
    assert.deepEqual(report.cases.map(item => item.caseId), dclCaseIds);
    const [item] = report.cases;
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.equal(item.conclusion.reason, 'insufficient-evidence');
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'pinned-inconclusive-effect-unsupported' });
    assert.equal(item.pinned.termless.observation.evidence, 'parser-state');
    assert.equal(item.pinned.terminal.observation.evidence, 'query');
    assert.equal(item.observed.groups.pinned.matches, true);
    assert.deepEqual(item.observed.groups.set.app, { rows: 13, cols: 60 });
    assert.deepEqual(item.observed.groups.set.kernel, { rows: 13, cols: 60 });
    assert.equal(item.observed.groups['control-clear'].unchanged, null, 'the clear control is judged on its own demand, not on staying unchanged');
    assert.equal(report.restoredSnapshot, evidence.frames.length - 1);
    assert.ok(report.snapshots.every(frame => frame.kernel.rows === frame.screen.rows && frame.kernel.cols === frame.screen.cols), 'every journaled frame carries its kernel grid');
    assert.ok(item.reads.every(read => Array.isArray(read.size)));
});

test('a recognised mode 3, a missed reply or a changed grid leaves the report an inconclusive effect and no divergence', () => {
    const recognised = syntheticDclEvidence({ mutate: (_case, role, read) => (role === 'set' ? { ...read, replies: ['\x1b[?3;2$y'] } : null) });
    const first = build(recognised).cases[0];
    assert.equal(first.effectContract.outcome, 'inconclusive');
    assert.match(first.effectContract.note, /needs its own supported contract/);
    assert.equal(first.divergence, undefined, 'both verdicts are inconclusive');
    // The kernel grid alone changing is a graded observation: the App and the kernel disagree.
    const kernelOnly = syntheticDclEvidence({ mutate: (_case, role, read) => (role === 'allow-set' ? { ...read, kernel: kernelOf(13, 132) } : null) });
    assert.match(build(kernelOnly).cases[0].effectContract.note, /disagree/);
    // An App grid that changed is not gradable at all: the journal holds one grid, so the run is refused instead of judged.
    const appSwitched = syntheticDclEvidence({ mutate: (_case, role, read) => (role === 'allow-set' ? { ...read, screen: resizedDclScreen(13, 132), kernel: kernelOf(13, 132) } : null) });
    assert.throws(() => build(appSwitched), /Changed deccolm snapshot evidence/);
});

test('an unanswered pinned exchange or a recognised pinned query makes the evidence impossible to replay', () => {
    assert.throws(() => syntheticDclEvidence({ mutate: (_case, role, read) => (role === 'pinned' ? { ...read, replies: [] } : null) }), /was not answered/);
    assert.throws(() => syntheticDclEvidence({ mutate: (_case, role, read) => (role === 'pinned-query' ? { ...read, replies: ['\x1b[?3;1$y'] } : null) }), /would pass without a width measurement/);
});

test('a changed reply list or size between the journal and the worker record refuses', () => {
    const replies = syntheticDclEvidence();
    replies.run.cases[0].reads[3].report.replies = ['\x1b[?3;2$y'];
    assert.throws(() => build(replies), /replies differ/);
    const size = syntheticDclEvidence();
    size.run.cases[0].reads[3].size = [13, 132];
    assert.throws(() => build(size), /size differs from its read frame/);
});

test('a retained pinned result that differs from its replay refuses', () => {
    const termless = syntheticDclEvidence();
    termless.run.cases[0].pinned.termless.observation.evidence = 'query';
    assert.throws(() => build(termless), /does not match its observed evidence/);
    const terminal = syntheticDclEvidence();
    terminal.run.cases[0].pinned.terminal.note = 'DECRPM mode 3: supported';
    assert.throws(() => build(terminal), /does not match its observed evidence/);
    const width = syntheticDclEvidence();
    width.run.cases[0].pinned.termless.response = JSON.stringify({ response: '\x1b[?3;0$y', cols: 132 });
    assert.throws(() => build(width), /does not match its observed evidence/);
});

test('every frame must carry the canonical kernel grid the parent read, and the run edges must agree with the App', () => {
    const missing = syntheticDclEvidence();
    delete missing.frames[5].kernel;
    assert.throws(() => build(missing), /not read by the system stty/);
    const rewritten = syntheticDclEvidence();
    rewritten.frames[5].kernel = { ...rewritten.frames[5].kernel, rows: 14 };
    assert.throws(() => build(rewritten), /not the canonical one/);
    const foreign = syntheticDclEvidence();
    foreign.frames[5].kernel = { ...foreign.frames[5].kernel, tool: '/usr/bin/true' };
    assert.throws(() => build(foreign), /not read by the system stty/);
    for (const index of [0, 1, -1]) {
        const edge = syntheticDclEvidence();
        const frame = edge.frames.at(index);
        frame.kernel = { ...frame.kernel, ...{ cols: 132, description: sttyDescription(frame.kernel.rows, 132) } };
        assert.throws(() => build(edge), /edge frame .* kernel grid differs from the App grid/, `edge ${index}`);
    }
    // A disagreement inside a read group is not a journal fault: it is the graded observation, so the report still builds with an inconclusive effect.
    const read = syntheticDclEvidence({ mutate: (_case, role, readValue) => (role === 'reset' ? { ...readValue, kernel: kernelOf(13, 132) } : null) });
    assert.match(build(read).cases[0].effectContract.note, /disagree/);
});

test('refuses tampered evidence: restoration, binding stability, adapter identity and run identity', () => {
    const evidence = syntheticDclEvidence();
    assert.throws(() => validateDclEvidence(evidence.frames, evidence.run, evidence.runId, { ...evidence.adapter, id: 'other' }), /adapter identity/);
    const wrongAdapter = syntheticDclEvidence();
    wrongAdapter.adapter.derivedFromAdapterId = 'other';
    assert.throws(() => build(wrongAdapter), /adapter identity/);
    const wrongProbes = syntheticDclEvidence();
    wrongProbes.run.probeIds = ['other'];
    assert.throws(() => build(wrongProbes), /does not cover the contracted cases/);
    const incomplete = syntheticDclEvidence();
    assert.throws(() => validateDclEvidence(incomplete.frames.slice(0, -1), incomplete.run, incomplete.runId, incomplete.adapter), /Incomplete deccolm snapshot sequence/);
    const restored = syntheticDclEvidence();
    restored.frames.at(-1).sha256 = '0'.repeat(64);
    assert.throws(() => validateDclEvidence(restored.frames, restored.run, restored.runId, restored.adapter), /Changed deccolm snapshot evidence|fixture was not restored/);
    const recolored = syntheticDclEvidence({ finishBinding: paletteBinding({ background: 0xff010203 }) });
    assert.throws(() => build(recolored), /fixture was not restored|palette or theme/);
    const reported = syntheticDclEvidence();
    reported.frames[0].report = { barrier: '\x1b[?62;4;22c', replies: [] };
    assert.throws(() => build(reported), /Unexpected deccolm report frame|Changed deccolm/);
});
