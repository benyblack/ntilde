import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildXcsGradingReport, validateXcsEvidence } from './xcs-grading.mjs';
import { xcsContracts, xcsSuiteId } from './xcs-contracts.mjs';
import { syntheticXcsEvidence } from './fixtures/xcs-evidence.mjs';
import { paletteBinding } from './fixtures/col-edit-evidence.mjs';

const build = options => {
    const { frames, run, runId, adapter } = syntheticXcsEvidence(options);
    return buildXcsGradingReport(frames, run, runId, adapter);
};

test('a complete synthetic run derives the report: pinned inconclusive, effect supported, divergence recorded', () => {
    const report = build();
    assert.equal(report.suiteId, xcsSuiteId);
    assert.deepEqual(report.cases.map(item => item.caseId), xcsContracts.map(item => item.caseId));
    for (const item of report.cases) {
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.equal(item.conclusion.reason, 'insufficient-evidence');
        assert.equal(item.effectContract.outcome, 'supported');
        assert.equal(item.divergence.kind, 'pinned-inconclusive-effect-supported');
        assert.equal(item.pinned.termless.observation.evidence, 'consumed');
        assert.equal(item.pinned.terminal.response, '{"row":1,"col":1}');
        assert.ok(item.observed.groups.pinned.matches && item.observed.groups.pinned.unchanged);
    }
});

test('a wrong restored color becomes an unsupported effect with an unchanged pinned verdict', () => {
    const wrong = (caseId, role, read) => (caseId === 'modes.xtpopcolors' && role === 'pop-lifo-1' ? { ...read, replies: [...read.replies.slice(0, 3), read.replies[3].replace('dddd', 'dddc')] } : null);
    const report = build({ mutate: wrong });
    const item = report.cases.find(entry => entry.caseId === 'modes.xtpopcolors');
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.equal(item.divergence.kind, 'pinned-inconclusive-effect-unsupported');
});

test('refuses tampered evidence: restoration, binding, adapter identity, pinned results and reports', () => {
    const fresh = () => syntheticXcsEvidence();
    let evidence = fresh();
    evidence.frames.at(-1).sha256 = '0'.repeat(64);
    assert.throws(() => validateXcsEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /Changed xterm-color-stack snapshot evidence/);
    const recolored = syntheticXcsEvidence({ finishBinding: paletteBinding({ background: 0xff010203 }) });
    assert.throws(() => validateXcsEvidence(recolored.frames, recolored.run, recolored.runId, recolored.adapter), /fixture was not restored|palette or theme/);
    evidence = fresh();
    evidence.adapter.id = 'other';
    assert.throws(() => validateXcsEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /Invalid xterm-color-stack adapter identity/);
    evidence = fresh();
    evidence.run.cases[0].pinned.termless.observation.reason = 'no-response';
    assert.throws(() => buildXcsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /does not match its observed evidence/);
    evidence = fresh();
    evidence.run.cases[1].pinned.terminal.response = '{"row":9,"col":9}';
    assert.throws(() => buildXcsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /does not match its observed evidence/);
    evidence = fresh();
    evidence.frames[3].report.replies = ['\x1b]11;rgb:0000/0000/0000\x1b\\'];
    assert.throws(() => buildXcsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /replies differ from their read frame/);
    evidence = fresh();
    evidence.frames[0].report = { barrier: '\x1b[?62;4;22c', replies: [] };
    assert.throws(() => buildXcsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /Unexpected xterm-color-stack report frame|Changed/);
});

test('a pinned push that answered would be an invalid reply, still inconclusive, recorded as such', () => {
    const noisy = (caseId, role, read) => (caseId === 'modes.xtpushcolors' && role === 'pinned' ? { ...read, replies: ['\x1b]11;rgb:0000/0000/0000\x1b\\'] } : null);
    const evidence = syntheticXcsEvidence({ mutate: noisy });
    // The synthetic run log replays the pinned code over the noisy exchange itself, so it agrees with the evidence; the effect contract reports the stray reply.
    const report = buildXcsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
    const item = report.cases.find(entry => entry.caseId === 'modes.xtpushcolors');
    assert.equal(item.pinned.termless.observation.reason, 'invalid-reply');
    assert.equal(item.effectContract.outcome, 'unsupported');
});

test('a pinned push or pop that answers with a non-OSC reply is retained: invalid-reply, early return, unsupported effect', () => {
    for (const caseId of ['modes.xtpushcolors', 'modes.xtpopcolors']) {
        const noisy = (id, role, read) => (id === caseId && role === 'pinned' ? { ...read, replies: ['\x1b[?0;0#Q'] } : null);
        const evidence = syntheticXcsEvidence({ mutate: noisy });
        const report = buildXcsGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
        const item = report.cases.find(entry => entry.caseId === caseId);
        assert.equal(item.pinned.termless.observation.reason, 'invalid-reply', caseId);
        assert.equal(item.pinned.termless.response, '\x1b[?0;0#Q');
        assert.equal(item.effectContract.outcome, 'unsupported');
        assert.equal(item.conclusion.outcome, 'inconclusive');
        assert.deepEqual(item.observed.groups.pinned.received, ['\\e[?0;0#Q']);
    }
});
