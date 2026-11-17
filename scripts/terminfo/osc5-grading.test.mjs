import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildOsc5GradingReport, validateOsc5Evidence } from './osc5-grading.mjs';
import { osc5Contracts, osc5SuiteId, replayOsc5Pinned } from './osc5-contracts.mjs';
import { syntheticOsc5Evidence } from './fixtures/osc5-evidence.mjs';
import { paletteBinding } from './fixtures/col-edit-evidence.mjs';

const osc5Frame = (selector, value) => `\x1b]5;${selector};${value}\x1b\\`;
const build = (options, extra) => {
    const { frames, run, runId, adapter } = syntheticOsc5Evidence(options);
    return buildOsc5GradingReport(frames, run, runId, adapter, extra);
};
const onlyPinned = replies => (caseId, role, read) => (role === 'pinned' ? { ...read, replies } : null);
const refuses = (mutate, pattern) => {
    const evidence = syntheticOsc5Evidence();
    mutate(evidence);
    assert.throws(() => buildOsc5GradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter), pattern);
};

test('a complete synthetic run derives the report: pinned supported, effect supported, no divergence', () => {
    const report = build();
    assert.equal(report.suiteId, osc5SuiteId);
    assert.deepEqual(report.cases.map(item => item.caseId), osc5Contracts.map(item => item.caseId));
    const [item] = report.cases;
    assert.equal(item.conclusion.outcome, 'supported');
    assert.equal(item.conclusion.reason, null);
    assert.equal(item.effectContract.outcome, 'supported');
    assert.equal(item.divergence, undefined);
    assert.equal(item.pinned.termless.observation.evidence, 'query');
    assert.equal(item.pinned.terminal.response, osc5Frame(0, 'rgb:cccc/cccc/cccc') + '\x1b[?62;4;22c', 'the terminal path answers with the whole raw text it saw');
    assert.equal(item.pinned.termless.assertions[0].kind, 'positive');
    assert.ok(item.observed.groups.pinned.matches && item.observed.groups.pinned.unchanged);
    assert.deepEqual(Object.keys(item.observed.groups), osc5Contracts[0].groups.map(group => group.role));
    assert.deepEqual(item.observed.baseline, { 0: 'rgb:cccc/cccc/cccc', 1: 'rgb:cccc/cccc/cccc', 2: 'rgb:cccc/cccc/cccc', 3: 'rgb:cccc/cccc/cccc', 4: 'rgb:cccc/cccc/cccc',
        'legacy-10': '\x1b]10;rgb:d3d3/d3d3/d3d3\x1b\\', 'legacy-4;200': '\x1b]4;200;rgb:ffff/0000/d7d7\x1b\\', 'legacy-21-cursor': '\x1b]21;cursor=rgb:ff/ff/ff\x1b\\' });
    assert.deepEqual(item.reads.filter(read => read.kind === 'report').length, osc5Contracts[0].groups.length);
    assert.equal(report.restoredSnapshot, report.snapshots.length - 1);
});

test('the real replay of the pinned termless code over the journal agrees with the recorded result', () => {
    const replayModule = { ntildeRunOsc5Termless: (probeId, ctx) => ({ probeId, ...osc5Replay(ctx) }) };
    assert.doesNotThrow(() => build({}, { replayModule }));
    const lying = { ntildeRunOsc5Termless: (probeId, ctx) => ({ probeId, ...osc5Replay(ctx), pass: false }) };
    assert.throws(() => build({}, { replayModule: lying }), /replay does not reproduce the live grading/);
    const skipping = { ntildeRunOsc5Termless: probeId => ({ probeId, ...replayOsc5Pinned(probeId, 'termless', { replies: osc5Frame(0, 'rgb:cccc/cccc/cccc') }) }) };
    assert.throws(() => build({}, { replayModule: skipping }), /different number of calls/);
});
function osc5Replay(ctx) {
    const replies = ctx.feedCapture('\x1b]5;0;?\x07');
    const frame = /\x1b\]5;0;rgb:[0-9a-f]{1,4}\/[0-9a-f]{1,4}\/[0-9a-f]{1,4}(?:\x07|\x1b\\)/i.exec(replies);
    if (frame) return { pass: true, response: replies, observation: { outcome: 'supported', evidence: 'query' },
        assertions: [{ kind: 'positive', expected: 'Complete OSC 5 index-0 color reply', observed: frame[0] }] };
    return { pass: false, response: replies, observation: { outcome: 'inconclusive', evidence: 'query', reason: /\x1b\]5;0;/i.test(replies) ? 'invalid-reply' : 'no-response' } };
}

test('a wrong special-color reply becomes an unsupported effect beside an unchanged supported pinned verdict', () => {
    const wrong = (caseId, role, read) => (role === 'per-slot' ? { ...read, replies: [read.replies[0], read.replies[1], '\x1b]5;3;rgb:3434/3434/3434\x1b\\'] } : null);
    const [item] = build({ mutate: wrong }).cases;
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.equal(item.conclusion.outcome, 'supported');
    assert.equal(item.divergence.kind, 'pinned-supported-effect-unsupported');
    assert.equal(item.observed.groups['per-slot'].matches, false);
});

test('a pinned index-0 answered with a bad value is inconclusive, recorded beside an unsupported effect', () => {
    const [item] = build({ mutate: onlyPinned([osc5Frame(0, 'rgb:1/2')]) }).cases;
    assert.equal(item.conclusion.outcome, 'inconclusive');
    assert.equal(item.pinned.termless.observation.reason, 'invalid-reply');
    assert.equal(item.effectContract.outcome, 'unsupported');
    assert.equal(item.divergence.kind, 'pinned-inconclusive-effect-unsupported');
});

test('a silent pinned request is inconclusive with the pinned no-response result on both paths', () => {
    const [item] = build({ mutate: onlyPinned([]) }).cases;
    assert.equal(item.conclusion.reason, 'no-response');
    assert.equal(item.pinned.termless.response, '');
    assert.equal(item.pinned.terminal.response, '\x1b[?62;4;22c', 'the terminal path saw only the DA barrier');
    assert.equal(item.pinned.terminal.observation.reason, 'no-response');
    assert.equal(item.effectContract.outcome, 'unsupported');
});

test('refuses tampered evidence: restoration, binding, adapter identity, pinned results, reports and frames', () => {
    let evidence = syntheticOsc5Evidence();
    evidence.frames.at(-1).sha256 = '0'.repeat(64);
    assert.throws(() => validateOsc5Evidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /Changed special-color snapshot evidence/);
    const recolored = syntheticOsc5Evidence({ finishBinding: paletteBinding({ background: 0xff010203 }) });
    assert.throws(() => validateOsc5Evidence(recolored.frames, recolored.run, recolored.runId, recolored.adapter), /fixture was not restored|palette or theme/);
    evidence = syntheticOsc5Evidence();
    evidence.adapter.id = 'other';
    assert.throws(() => validateOsc5Evidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /Invalid special-color adapter identity/);
    evidence = syntheticOsc5Evidence();
    evidence.adapter.derivedFromAdapterId = 'other';
    assert.throws(() => validateOsc5Evidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /Invalid special-color adapter identity/);
    evidence = syntheticOsc5Evidence();
    evidence.run.probeIds = ['other'];
    assert.throws(() => validateOsc5Evidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter), /does not cover the contracted cases/);
    refuses(({ run }) => { run.cases[0].pinned.termless.observation.reason = 'no-response'; }, /does not match its observed evidence/);
    refuses(({ run }) => { run.cases[0].pinned.terminal.response = 'edited'; }, /does not match its observed evidence/);
});
