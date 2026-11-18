import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildCapsGradingReport, validateCapsEvidence } from './caps-grading.mjs';
import { syntheticCapsEvidence, syntheticCapsGrade } from './fixtures/caps-evidence.mjs';
import { paletteBinding } from './fixtures/col-edit-evidence.mjs';
import { capsAdapterId } from './caps-grading-collector.mjs';
import { capsSuiteId } from './caps-contracts.mjs';

test('the synthetic evidence builds a supported report whose pinned and effect verdicts agree', () => {
    const evidence = syntheticCapsEvidence();
    const report = buildCapsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
    assert.equal(report.suiteId, capsSuiteId);
    assert.equal(report.cases.length, 1);
    assert.equal(report.cases[0].conclusion.outcome, 'supported');
    assert.equal(report.cases[0].effectContract.outcome, 'supported');
    assert.equal(report.cases[0].divergence, undefined);
    assert.equal(report.cases[0].pinned.termless.observation.outcome, 'supported');
    assert.equal(report.cases[0].pinned.terminal.observation.outcome, 'supported');
    assert.equal(report.restoredSnapshot, evidence.frames.length - 1);
});

test('an un answered pinned exchange makes the pinned path inconclusive and the effect unsupported, recorded as a divergence', () => {
    const evidence = syntheticCapsEvidence({ mutate: (caseId, role, read) => (role === 'pinned' ? { ...read, replies: [] } : null) });
    const report = buildCapsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
    assert.equal(report.cases[0].conclusion.outcome, 'inconclusive');
    assert.equal(report.cases[0].conclusion.reason, 'no-response');
    assert.equal(report.cases[0].effectContract.outcome, 'unsupported');
    assert.deepEqual(report.cases[0].divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'pinned-inconclusive-effect-unsupported' });
});

test('a wrong reply (a truncated declaration over the pinned pattern) is a divergence, never absorbed', () => {
    const evidence = syntheticCapsEvidence({ mutate: (caseId, role, read) => (role === 'pinned' ? { ...read, replies: ['\x1b]1337;Capabilities=T3\x07'] } : null) });
    const report = buildCapsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
    assert.equal(report.cases[0].conclusion.outcome, 'supported', 'the pinned pattern accepts any feature string');
    assert.equal(report.cases[0].effectContract.outcome, 'unsupported');
    assert.equal(report.cases[0].divergence.kind, 'pinned-supported-effect-unsupported');
});

test('a changed reply list between the journal and the worker record refuses', () => {
    const evidence = syntheticCapsEvidence();
    evidence.run.cases[0].reads[1].report.replies = [];
    assert.throws(() => buildCapsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /replies differ/);
});

test('refuses tampered evidence: restoration, binding stability, adapter identity and run identity', () => {
    const evidence = syntheticCapsEvidence();
    assert.throws(() => validateCapsEvidence(evidence.frames, evidence.run, evidence.runId, { ...evidence.adapter, id: 'other' }), /adapter identity/);
    const wrongAdapter = syntheticCapsEvidence();
    wrongAdapter.adapter.derivedFromAdapterId = 'other';
    assert.throws(() => buildCapsGradingReport(wrongAdapter.frames, wrongAdapter.run, wrongAdapter.runId, wrongAdapter.adapter), /adapter identity/);
    const wrongProbes = syntheticCapsEvidence();
    wrongProbes.run.probeIds = ['other'];
    assert.throws(() => buildCapsGradingReport(wrongProbes.frames, wrongProbes.run, wrongProbes.runId, wrongProbes.adapter), /does not cover the contracted cases/);
    const incomplete = syntheticCapsEvidence();
    assert.throws(() => validateCapsEvidence(incomplete.frames.slice(0, -1), incomplete.run, incomplete.runId, incomplete.adapter), /Incomplete feature-reporting snapshot sequence/);
    const restored = syntheticCapsEvidence();
    restored.frames.at(-1).sha256 = '0'.repeat(64);
    assert.throws(() => validateCapsEvidence(restored.frames, restored.run, restored.runId, restored.adapter), /Changed feature-reporting snapshot evidence|fixture was not restored/);
    const recolored = syntheticCapsEvidence({ finishBinding: paletteBinding({ background: 0xff010203 }) });
    assert.throws(() => buildCapsGradingReport(recolored.frames, recolored.run, recolored.runId, recolored.adapter), /fixture was not restored|palette or theme/);
});
