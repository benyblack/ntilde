import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildPclGradingReport, validatePclEvidence } from './pcl-grading.mjs';
import { syntheticPclEvidence } from './fixtures/pcl-evidence.mjs';
import { paletteBinding } from './fixtures/col-edit-evidence.mjs';
import { pclSeededBinding, pclSuiteId, pclCaseIds } from './pcl-contracts.mjs';

test('the synthetic evidence builds a report whose pinned verdicts stay inconclusive and whose effect is unsupported, recorded as a divergence', () => {
    const evidence = syntheticPclEvidence();
    const report = buildPclGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
    assert.equal(report.suiteId, pclSuiteId);
    assert.deepEqual(report.cases.map(item => item.caseId), pclCaseIds);
    for (const item of report.cases) {
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.equal(item.conclusion.reason, 'no-response');
        assert.equal(item.effectContract.outcome, 'unsupported');
        assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'pinned-inconclusive-effect-unsupported' });
        assert.equal(item.pinned.termless.observation.outcome, 'inconclusive');
        assert.equal(item.pinned.terminal.observation.outcome, 'inconclusive');
        assert.equal(item.pinned.terminal.observation.evidence, 'consumed');
        assert.equal(item.observed.groups.pinned.matches, true);
    }
    assert.equal(report.restoredSnapshot, evidence.frames.length - 1);
});

test('a pointer reply is an inconclusive effect, and a complete rgb reply refuses the pinned replay outright', () => {
    const answered = syntheticPclEvidence({ mutate: (caseId, role, read) => (role === 'query-st' ? { ...read, replies: ['\x1b]13;rgb:1/2/3\x1b\\'] } : null) });
    // The framing keeps the bytes; the contract grader marks the effect inconclusive (the case now needs its own supported contract). The pinned termless replay only sees the pinned exchange, which stayed silent.
    const report = buildPclGradingReport(answered.frames, answered.run, answered.runId, answered.adapter);
    for (const item of report.cases) {
        assert.equal(item.effectContract.outcome, 'inconclusive');
        assert.match(item.effectContract.note, /needs its own supported contract/);
        assert.equal(item.divergence, undefined, 'both verdicts are inconclusive');
    }
    // A complete color reply to the pinned query would make the pinned code continue past the unsupported contract: the evidence cannot even be replayed.
    assert.throws(() => syntheticPclEvidence({ mutate: (caseId, role, read) => (role === 'pinned' ? { ...read, replies: ['\x1b]13;rgb:1/2/3\x1b\\', '\x1b]14;rgb:1/2/3\x1b\\'] } : null) }),
        /pinned code would continue/);
});

test('a changed reply list between the journal and the worker record refuses', () => {
    const evidence = syntheticPclEvidence();
    evidence.run.cases[0].reads[2].report.replies = ['\x1b]10;rgb:0000/0000/0000\x1b\\'];
    assert.throws(() => buildPclGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /replies differ/);
});

test('a retained pinned result that differs from its replay refuses', () => {
    const termless = syntheticPclEvidence();
    termless.run.cases[0].pinned.termless.observation.reason = 'invalid-reply';
    assert.throws(() => buildPclGradingReport(termless.frames, termless.run, termless.runId, termless.adapter), /does not match its observed evidence/);
    const terminal = syntheticPclEvidence();
    terminal.run.cases[1].pinned.terminal.note = 'Cursor answered; supported';
    assert.throws(() => buildPclGradingReport(terminal.frames, terminal.run, terminal.runId, terminal.adapter), /does not match its observed evidence/);
});

test('refuses tampered evidence: restoration, binding stability, adapter identity and run identity', () => {
    const evidence = syntheticPclEvidence();
    assert.throws(() => validatePclEvidence(evidence.frames, evidence.run, evidence.runId, { ...evidence.adapter, id: 'other' }), /adapter identity/);
    const wrongAdapter = syntheticPclEvidence();
    wrongAdapter.adapter.derivedFromAdapterId = 'other';
    assert.throws(() => buildPclGradingReport(wrongAdapter.frames, wrongAdapter.run, wrongAdapter.runId, wrongAdapter.adapter), /adapter identity/);
    const wrongProbes = syntheticPclEvidence();
    wrongProbes.run.probeIds = ['other'];
    assert.throws(() => buildPclGradingReport(wrongProbes.frames, wrongProbes.run, wrongProbes.runId, wrongProbes.adapter), /does not cover the contracted cases/);
    const incomplete = syntheticPclEvidence();
    assert.throws(() => validatePclEvidence(incomplete.frames.slice(0, -1), incomplete.run, incomplete.runId, incomplete.adapter), /Incomplete pointer-color snapshot sequence/);
    const restored = syntheticPclEvidence();
    restored.frames.at(-1).sha256 = '0'.repeat(64);
    assert.throws(() => validatePclEvidence(restored.frames, restored.run, restored.runId, restored.adapter), /Changed pointer-color snapshot evidence|fixture was not restored/);
    const recolored = syntheticPclEvidence({ finishBinding: paletteBinding({ background: 0xff010203 }) });
    assert.throws(() => buildPclGradingReport(recolored.frames, recolored.run, recolored.runId, recolored.adapter), /fixture was not restored|palette or theme/);
});

test('the case reads must carry exactly the seeded binding: an unseeded, a re-seeded or a palette-changed read is refused', () => {
    const host = paletteBinding(), seeded = pclSeededBinding(host);
    const accepted = syntheticPclEvidence({ readBinding: seeded });
    assert.equal(accepted.frames[2].screen.colorBinding.foregroundOverride, true, 'the fixture reads carry the OSC 10 override');
    assert.equal(accepted.frames[0].screen.colorBinding.foregroundOverride, false, 'the edges carry the host binding');
    // Each read binding below is coherent (the screens are rebuilt with it, hashes and resolved channels included), so only the run-level binding invariant can refuse it.
    for (const [label, readBinding] of [
        ['the seed did not take (host binding on every read)', host],
        ['a different background', { ...seeded, backgroundArgb: 0xff000000 }],
        ['a palette override', { ...seeded, paletteOverrideIndices: [1] }],
        ['a changed palette', { ...seeded, ansiPaletteArgb: seeded.ansiPaletteArgb.map(() => 0xff010101) }],
        ['a foreground override flag without the value', { ...seeded, foregroundOverride: false }],
    ]) {
        const evidence = syntheticPclEvidence({ readBinding });
        assert.throws(() => buildPclGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /palette or theme change/, label);
    }
    const unrestored = syntheticPclEvidence({ finishBinding: seeded });
    assert.throws(() => buildPclGradingReport(unrestored.frames, unrestored.run, unrestored.runId, unrestored.adapter), /palette or theme change|fixture was not restored/, 'the finish frame must be back on the host binding');
});
