import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildXsvGradingReport, validateXsvEvidence } from './xsv-grading.mjs';
import { xsvContracts, xsvSuiteId } from './xsv-contracts.mjs';
import { syntheticXsvEvidence } from './fixtures/xsv-evidence.mjs';
import { paletteBinding } from './fixtures/col-edit-evidence.mjs';

const build = options => {
    const { frames, run, runId, adapter } = syntheticXsvEvidence(options);
    return buildXsvGradingReport(frames, run, runId, adapter);
};

test('a complete synthetic run derives the report: pinned inconclusive, effect supported, divergence recorded', () => {
    const report = build();
    assert.equal(report.suiteId, xsvSuiteId);
    assert.deepEqual(report.cases.map(item => item.caseId), xsvContracts.map(item => item.caseId));
    for (const item of report.cases) {
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.equal(item.conclusion.reason, 'insufficient-evidence');
        assert.equal(item.effectContract.outcome, 'supported');
        assert.equal(item.divergence.kind, 'pinned-inconclusive-effect-supported');
        assert.equal(item.pinned.termless.observation.evidence, 'consumed');
        assert.equal(item.pinned.terminal.response, '{"row":1,"col":1}', 'the cursor of the pinned read: home, the pinned exchange moves nothing');
        assert.ok(item.observed.groups.pinned.matches && item.observed.groups.pinned.unchanged);
    }
});

test('a mode left unrestored becomes an unsupported effect with an unchanged pinned verdict', () => {
    const wrong = (caseId, role, expected) => (caseId === 'modes.xtrestore' && role === 'wrap-restores-changed' ? { ...expected, wrap: true } : null);
    const report = build({ mutateState: wrong });
    const item = report.cases.find(entry => entry.caseId === 'modes.xtrestore');
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.equal(item.divergence.kind, 'pinned-inconclusive-effect-unsupported');
});

test('refuses tampered evidence: restoration, binding, adapter identity, pinned results and reports', () => {
    const fresh = () => syntheticXsvEvidence();
    let evidence = fresh();
    evidence.frames.at(-1).sha256 = '0'.repeat(64);
    assert.throws(() => validateXsvEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /Changed xterm-saved-modes snapshot evidence/);
    const recolored = syntheticXsvEvidence({ finishBinding: paletteBinding({ background: 0xff010203 }) });
    assert.throws(() => validateXsvEvidence(recolored.frames, recolored.run, recolored.runId, recolored.adapter), /fixture was not restored|palette or theme/);
    evidence = fresh();
    evidence.adapter.id = 'other';
    assert.throws(() => validateXsvEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /Invalid xterm-saved-modes adapter identity/);
    evidence = fresh();
    evidence.run.cases[0].pinned.termless.observation.reason = 'no-response';
    assert.throws(() => buildXsvGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /does not match its observed evidence/);
    evidence = fresh();
    evidence.run.cases[1].pinned.terminal.response = '{"row":9,"col":9}';
    assert.throws(() => buildXsvGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /does not match its observed evidence/);
    evidence = fresh();
    evidence.frames[3].report.replies = ['\x1b]11;rgb:0000/0000/0000\x1b\\'];
    assert.throws(() => buildXsvGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /replies differ from their read frame/);
    evidence = fresh();
    evidence.frames[0].report = { barrier: '\x1b[?62;4;22c', replies: [] };
    assert.throws(() => buildXsvGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /Unexpected xterm-saved-modes report frame|Changed/);
});

test('a pinned save that answered would be an invalid reply, still inconclusive, recorded as such', () => {
    const noisy = (caseId, role, read) => (caseId === 'modes.xtsave' && role === 'pinned' ? { ...read, replies: ['\x1b]11;rgb:0000/0000/0000\x1b\\'] } : null);
    const evidence = syntheticXsvEvidence({ mutate: noisy });
    // The synthetic run log replays the pinned code over the noisy exchange itself, so it agrees with the evidence; the effect contract reports the stray reply.
    const report = buildXsvGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
    const item = report.cases.find(entry => entry.caseId === 'modes.xtsave');
    assert.equal(item.pinned.termless.observation.reason, 'invalid-reply');
    assert.equal(item.effectContract.outcome, 'unsupported');
});

test('a pinned save or restore that answers with a non-OSC reply is retained: invalid-reply, early return, unsupported effect', () => {
    for (const caseId of ['modes.xtsave', 'modes.xtrestore']) {
        const noisy = (id, role, read) => (id === caseId && role === 'pinned' ? { ...read, replies: ['\x1b[?0;0#Q'] } : null);
        const evidence = syntheticXsvEvidence({ mutate: noisy });
        const report = buildXsvGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
        const item = report.cases.find(entry => entry.caseId === caseId);
        assert.equal(item.pinned.termless.observation.reason, 'invalid-reply', caseId);
        assert.equal(item.pinned.termless.response, '\x1b[?0;0#Q');
        assert.equal(item.effectContract.outcome, 'unsupported');
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.deepEqual(item.observed.groups.pinned.replies, ['\\e[?0;0#Q']);
    }
});
