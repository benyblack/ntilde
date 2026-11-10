import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildRectChecksumGradingReport, replayRectChecksumPinned, validateRectChecksumEvidence } from './rect-checksum-grading.mjs';
import { rectChecksumContracts } from './rect-checksum-contracts.mjs';
import { syntheticRectChecksumEvidence } from './fixtures/rect-checksum-evidence.mjs';

const contract = rectChecksumContracts[0];
const build = evidence => buildRectChecksumGradingReport(evidence.frames, evidence.run, evidence.runId, evidence.adapter);
/** The index of the frame, and of the run-log read, of a role. */
const frameOf = role => 2 + contract.roles.indexOf(role);
const readOf = role => contract.roles.indexOf(role);

test('a complete synthetic run rederives one supported conclusion with its replies and the agreeing pinned grade', () => {
    const report = build(syntheticRectChecksumEvidence());
    assert.equal(report.cases.length, 1);
    const [item] = report.cases;
    assert.equal(item.conclusion.outcome, 'supported');
    assert.equal(item.conclusion.evidence, 'owned-stored-cells-and-exact-replies');
    assert.equal(item.pinned.observation.outcome, 'supported');
    assert.equal(item.pinned.response, '\x1bP1!~FEB1\x1b\\');
    assert.equal(item.divergence, undefined);
    assert.equal(item.reads[readOf('pinned')].reply, '\x1bP1!~FEB1\x1b\\');
    assert.equal(item.reads[readOf('identity-colon')].reply, null);
    assert.equal(item.reads[readOf('seed')].reply, undefined, 'a cells read carries no reply');
    assert.equal(Object.values(item.observed.requests).every(request => request.matches && request.unchanged), true);
    assert.equal(report.snapshots.length, 3 + contract.roles.length);
});

test('the pinned replay mirrors checksumResult for a framed, an unframed and an absent reply', () => {
    const at = raw => contract.roles.map((role, index) => (index === contract.pinnedRead && raw !== null ? { raw } : null));
    assert.deepEqual(replayRectChecksumPinned(contract, at('\x1bP1!~0000\x1b\\')).observation, { outcome: 'supported', evidence: 'query' });
    assert.deepEqual(replayRectChecksumPinned(contract, at('\x1bP2!~0000\x1b\\')).observation, { outcome: 'inconclusive', evidence: 'query', reason: 'invalid-reply' });
    const absent = replayRectChecksumPinned(contract, at(null));
    assert.deepEqual(absent.observation, { outcome: 'inconclusive', evidence: 'query', reason: 'no-response' });
    assert.equal(absent.response, '');
    assert.equal(absent.assertions, undefined);
});

test('a silent terminal is pinned-inconclusive and contract-unsupported, recorded as a measured mismatch', () => {
    const evidence = syntheticRectChecksumEvidence('a'.repeat(32), 'f'.repeat(64), undefined, (role, read) => (read.reply ? { ...read, reply: null } : null));
    const [item] = build(evidence).cases;
    assert.equal(item.pinned.observation.reason, 'no-response');
    assert.equal(item.conclusion.outcome, 'unsupported');
    assert.deepEqual(item.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'unsupported', kind: 'contract-measured-mismatch' });
});

for (const [label, mutate, pattern] of [
    ['a journaled reply that differs from the worker record', e => { e.frames[frameOf('pinned')].report.reply = { raw: '\x1bP1!~FEB2\x1b\\' }; }, /reply differs from its read frame/],
    ['a worker record that differs from the journal', e => { e.run.cases[0].reads[readOf('attr-bold')].report.reply = null; }, /reply differs from its read frame/],
    ['a report on a cells read', e => { e.frames[frameOf('seed')].report = { barrier: e.frames[frameOf('seed')].barrier, reply: null }; }, /report on a cells read/],
    ['a report read without its report', e => { delete e.frames[frameOf('terminal')].report; delete e.run.cases[0].reads[readOf('terminal')].report; }, /rectangle checksum/],
    ['a report on the start frame', e => { e.frames[0].report = { barrier: '\x1b[?62;4;22c', reply: null }; }, /Unexpected Rectangle-checksum report frame/],
    ['a pinned result that differs from its reply', e => { e.run.cases[0].pinned.observation = { outcome: 'inconclusive', evidence: 'query', reason: 'no-response' }; }, /does not match its observed evidence/],
    ['a pinned result with another evidence kind', e => { e.run.cases[0].pinned.observation.evidence = 'parser-state'; }, /Missing pinned/],
    ['a changed adapter identity', e => { e.adapter.id = 'other'; }, /adapter identity/],
    ['a changed predecessor', e => { e.adapter.derivedFromAdapterId = 'ntilde-owned-rectangle-editing-grading-v1'; }, /adapter identity/],
    ['a truncated journal', e => { e.frames.pop(); }, /Incomplete Rectangle-checksum snapshot sequence/],
    ['changed feeds', e => { e.run.cases[0].feeds[2] = '\x1b[1;1;1;1;1;6*y'; }, /feeds do not match/],
]) {
    test('the report builder refuses ' + label, () => {
        const evidence = syntheticRectChecksumEvidence();
        mutate(evidence);
        assert.throws(() => build(evidence), pattern);
    });
}

test('the evidence check alone binds the run identity and the shared theme', () => {
    const evidence = syntheticRectChecksumEvidence();
    assert.doesNotThrow(() => validateRectChecksumEvidence(evidence.frames, evidence.run, evidence.runId, evidence.adapter));
    assert.throws(() => validateRectChecksumEvidence(evidence.frames, evidence.run, 'b'.repeat(32), evidence.adapter), /not bound to this run/);
});
