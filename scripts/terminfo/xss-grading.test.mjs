import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildXssGradingReport, validateXssEvidence } from './xss-grading.mjs';
import { xssContracts, xssSuiteId } from './xss-contracts.mjs';
import { syntheticXssEvidence } from './fixtures/xss-evidence.mjs';
import { paletteBinding } from './fixtures/col-edit-evidence.mjs';

const build = options => {
    const { frames, run, runId, adapter } = syntheticXssEvidence(options);
    return buildXssGradingReport(frames, run, runId, adapter);
};

test('a complete synthetic run derives the report: pinned inconclusive, effect supported, divergence recorded', () => {
    const report = build();
    assert.equal(report.suiteId, xssSuiteId);
    assert.deepEqual(report.cases.map(item => item.caseId), xssContracts.map(item => item.caseId));
    for (const item of report.cases) {
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.equal(item.conclusion.reason, 'insufficient-evidence');
        assert.equal(item.effectContract.outcome, 'supported');
        assert.equal(item.divergence.kind, 'pinned-inconclusive-effect-supported');
        assert.equal(item.pinned.termless.observation.evidence, 'consumed');
        assert.equal(item.pinned.terminal.response, '{"row":8,"col":2}', 'the cursor of the pinned read: after the baseline probe on row 8');
        assert.ok(item.observed.groups.pinned.matches && item.observed.groups.pinned.unchanged);
    }
});

test('a wrong restored attribute becomes an unsupported effect with an unchanged pinned verdict', () => {
    const wrong = (caseId, role, spec) => (caseId === 'modes.xtpopsgr' && role === 'pop-lifo-1' ? { ...spec, attrs: ['bold'] } : null);
    const report = build({ mutateCell: wrong });
    const item = report.cases.find(entry => entry.caseId === 'modes.xtpopsgr');
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.equal(item.divergence.kind, 'pinned-inconclusive-effect-unsupported');
});

test('refuses tampered evidence: restoration, binding, adapter identity, pinned results and reports', () => {
    const fresh = () => syntheticXssEvidence();
    let evidence = fresh();
    evidence.frames.at(-1).sha256 = '0'.repeat(64);
    assert.throws(() => validateXssEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /Changed xterm-sgr-stack snapshot evidence/);
    const recolored = syntheticXssEvidence({ finishBinding: paletteBinding({ background: 0xff010203 }) });
    assert.throws(() => validateXssEvidence(recolored.frames, recolored.run, recolored.runId, recolored.adapter), /fixture was not restored|palette or theme/);
    evidence = fresh();
    evidence.adapter.id = 'other';
    assert.throws(() => validateXssEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /Invalid xterm-sgr-stack adapter identity/);
    evidence = fresh();
    evidence.run.cases[0].pinned.termless.observation.reason = 'no-response';
    assert.throws(() => buildXssGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /does not match its observed evidence/);
    evidence = fresh();
    evidence.run.cases[1].pinned.terminal.response = '{"row":9,"col":9}';
    assert.throws(() => buildXssGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /does not match its observed evidence/);
    evidence = fresh();
    evidence.frames[3].report.replies = ['\x1b]11;rgb:0000/0000/0000\x1b\\'];
    assert.throws(() => buildXssGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /replies differ from their read frame/);
    evidence = fresh();
    evidence.frames[0].report = { barrier: '\x1b[?62;4;22c', replies: [] };
    assert.throws(() => buildXssGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /Unexpected xterm-sgr-stack report frame|Changed/);
});

test('a pinned push that answered would be an invalid reply, still inconclusive, recorded as such', () => {
    const noisy = (caseId, role, read) => (caseId === 'modes.xtpushsgr' && role === 'pinned' ? { ...read, replies: ['\x1b]11;rgb:0000/0000/0000\x1b\\'] } : null);
    const evidence = syntheticXssEvidence({ mutate: noisy });
    // The synthetic run log replays the pinned code over the noisy exchange itself, so it agrees with the evidence; the effect contract reports the stray reply.
    const report = buildXssGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
    const item = report.cases.find(entry => entry.caseId === 'modes.xtpushsgr');
    assert.equal(item.pinned.termless.observation.reason, 'invalid-reply');
    assert.equal(item.effectContract.outcome, 'unsupported');
});

test('a pinned push or pop that answers with a non-OSC reply is retained: invalid-reply, early return, unsupported effect', () => {
    for (const caseId of ['modes.xtpushsgr', 'modes.xtpopsgr']) {
        const noisy = (id, role, read) => (id === caseId && role === 'pinned' ? { ...read, replies: ['\x1b[?0;0#Q'] } : null);
        const evidence = syntheticXssEvidence({ mutate: noisy });
        const report = buildXssGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
        const item = report.cases.find(entry => entry.caseId === caseId);
        assert.equal(item.pinned.termless.observation.reason, 'invalid-reply', caseId);
        assert.equal(item.pinned.termless.response, '\x1b[?0;0#Q');
        assert.equal(item.effectContract.outcome, 'unsupported');
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.deepEqual(item.observed.groups.pinned.replies, ['\\e[?0;0#Q']);
    }
});
