import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { capturedV10SourceFiles } from './owned-sources.mjs';
import { queryReceiptFixture } from './fixtures/query-receipt.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
test('complete schema11 query suffix preserves all15 original prefix bindings and exact negative/silent facts', () => queryReceiptFixture(f => {
    const receipt = f.finish(); assert.equal(receipt.schemaVersion, 11); assert.deepEqual(receipt.queryEvidence.counts, { matched: 40 });
    for (const key of Object.keys(f.evidence).filter(key => key !== 'queryEvidence')) assert.deepEqual(receipt[key], f.old[key]);
    assert.equal(f.report.observations.filter(x => x.expectedEvidence === 'negative-reply').length, 21);
    assert.equal(f.report.observations.filter(x => x.expectedEvidence === 'silence').length, 16);
    assert.deepEqual(f.report.readerCounts, [48, 42]); assert.equal(f.report.priorChallenges.length, 110);
}));
test('semantic exact-reply mismatch can complete without acquiring a feature-support grade', () => queryReceiptFixture(f => {
    assert.deepEqual(f.finish().queryEvidence.counts, { matched: 39, mismatched: 1 });
    assert.equal(f.report.observations.find(x => x.id === 'q07.device.xtwinops-18').contractMatched, false);
}, { mutateReply: (id, value) => id === 'q07.device.xtwinops-18' ? '' : value }));
for (const [label, mutate] of [
    ['missing initial journal', f => rmSync(join(f.output, 'query-initial.json'))],
    ['missing action journal', f => rmSync(join(f.output, 'query-actions.jsonl'))],
    ['failed raw reader', f => { f.proof.proofs[0].queries.seal.status.failed = true; f.write('ownership-proof.json', f.proof); }],
    ['foreign original pane', f => { f.proof.proofs[0].queries.binding.paneId = '11111111-1111-1111-1111-111111111111'; f.write('ownership-proof.json', f.proof); }],
    ['partial prior nonce list', f => { f.report.priorChallenges.pop(); f.write('query-observations.json', f.report); }],
    ['retained failure', f => f.write('query-raw-failure.json', { failed: true })],
]) test('schema11 refuses ' + label, () => queryReceiptFixture(f => { mutate(f); assert.throws(f.finish); }));
test('coherent mismatched-result promotion cannot replace independently derived exact bytes', () => queryReceiptFixture(f => {
    const observation = f.report.observations.find(x => x.id === 'q07.device.xtwinops-18');
    observation.contractMatched = true; observation.observedEvidence = 'positive-reply';
    const bytes = JSON.stringify(f.report, null, 2) + '\n'; writeFileSync(join(f.output, 'query-observations.json'), bytes);
    f.evidence.queryEvidence.sha256 = hash(bytes); f.evidence.queryEvidence.counts = { matched: 40 };
    assert.throws(f.finish);
}, { mutateReply: (id, value) => id === 'q07.device.xtwinops-18' ? '' : value }));

test('coherent query stripping cannot downgrade the retained importing graph into schema10', () => queryReceiptFixture(f => {
    delete f.evidence.queryEvidence; delete f.context.sourceImportsSourceSha256; delete f.context.querySuiteId; delete f.context.ownedQueryPolicy; delete f.proof.proofs[0].queries;
    for (const key of Object.keys(f.context)) if (key.startsWith('query') && key.endsWith('SourceSha256')) delete f.context[key];
    for (const file of ['query-observations.json', 'query-initial.json', 'query-snapshots.jsonl', 'query-actions.jsonl']) rmSync(join(f.output, file));
    for (const file of ['queries.mjs', 'query-capture.mjs', 'query-collector.mjs', 'query-receipt.mjs', 'query-witness-cases.mjs', 'query-witness-frame.mjs', 'source-imports.mjs']) rmSync(join(f.output, 'sources', file));
    const bytes = JSON.stringify(capturedV10SourceFiles, null, 2) + '\n'; writeFileSync(join(f.output, 'sources', 'owned-source-inventory.json'), bytes);
    f.context.sourceInventorySha256 = hash(bytes); f.context.sourceSnapshotPolicy = 'readonly-owned-source-graph-v3';
    f.proof.sourceSnapshot.policy = f.context.sourceSnapshotPolicy;
    f.manifest.policy = f.context.sourceSnapshotPolicy; f.manifest.hashes = Object.fromEntries(Object.keys(capturedV10SourceFiles).map(key => [key, f.context[key]]));
    f.write('source-snapshot.json', f.manifest); f.context.sourceSnapshotManifestSha256 = hash(readFileSync(join(f.output, 'source-snapshot.json')));
    f.write('context.json', f.context); f.write('ownership-proof.json', f.proof);
    assert.throws(f.finish, /outside its versioned scope|Invalid/);
}));

function unavailablePrefix(f) {
    for (const [prefix, bindingKey, policyKey, planned] of [['input', 'inputEvidence', 'input', { plannedExperiments: 39 }],
        ['image', 'staticImageEvidence', 'staticImages', { plannedExperiments: 5, plannedStages: 23 }]]) {
        const old = f.evidence[bindingKey];
        const report = { schemaVersion: 1, runId: old.runId, suiteId: old.suiteId, status: 'unavailable', reason: 'screenDetailsUnavailable', fixtureStarted: false, ...planned };
        const file = prefix + '-unavailable.json'; f.write(file, report); rmSync(join(f.output, prefix + '-observations.json'));
        for (const suffix of ['snapshots', 'actions']) writeFileSync(join(f.output, prefix + '-' + suffix + '.jsonl'), '');
        f.evidence[bindingKey] = { runId: old.runId, suiteId: old.suiteId, file, sha256: hash(readFileSync(join(f.output, file))), status: 'unavailable', reason: report.reason, counts: {} };
        const marker = f.proof.proofs[0][policyKey]; f.proof.proofs[0][policyKey] = { policy: marker.policy, actions: [] };
    }
    for (const dir of ['image-inputs', 'image-pixels']) rmSync(join(f.output, dir), { recursive: true });
    const original = f.evidence.queryEvidence;
    const report = { schemaVersion: 1, runId: original.runId, suiteId: original.suiteId, status: 'unavailable', reason: 'queryPrerequisiteUnavailable',
        fixtureStarted: false, plannedActions: 44, plannedContracts: 40, observedActions: 0, observedContracts: 0, observedFrames: 0,
        prerequisites: ['inputEvidence', 'staticImageEvidence'].map(bindingKey => ({ bindingKey, ...f.evidence[bindingKey] })) };
    f.write('query-unavailable.json', report); rmSync(join(f.output, 'query-observations.json')); rmSync(join(f.output, 'query-initial.json'));
    for (const suffix of ['snapshots', 'actions']) writeFileSync(join(f.output, 'query-' + suffix + '.jsonl'), '');
    f.evidence.queryEvidence = { runId: original.runId, suiteId: original.suiteId, file: 'query-unavailable.json', sha256: hash(readFileSync(join(f.output, 'query-unavailable.json'))), status: 'unavailable', reason: report.reason, counts: {} };
    f.proof.proofs[0].queries = { policy: f.proof.proofs[0].queries.policy, actions: [] }; f.write('ownership-proof.json', f.proof);
    return report;
}
test('independently typed initial prefix refusal preserves all16 bindings without fabricated query observations', () => queryReceiptFixture(f => {
    const report = unavailablePrefix(f), receipt = f.finish(); assert.equal(receipt.schemaVersion, 11);
    assert.deepEqual(receipt.queryEvidence.counts, {}); assert.equal(receipt.queryEvidence.status, 'unavailable');
    assert.equal(report.observedContracts, 0); assert.equal(report.prerequisites.length, 2);
}));
for (const mode of ['forged prerequisite', 'started initial']) test('query unavailable cannot conceal ' + mode, () => queryReceiptFixture(f => {
    const report = unavailablePrefix(f);
    if (mode === 'started initial') f.write('query-initial.json', f.report.initial);
    else { report.prerequisites[0].sha256 = 'a'.repeat(64); f.write('query-unavailable.json', report); f.evidence.queryEvidence.sha256 = hash(readFileSync(join(f.output, 'query-unavailable.json'))); }
    assert.throws(f.finish);
}));

test('schema11 independently refuses a coherently rehashed partial prior challenge list', () => queryReceiptFixture(f => {
    f.report.priorChallenges.pop(); const bytes = JSON.stringify(f.report, null, 2) + '\n';
    writeFileSync(join(f.output, 'query-observations.json'), bytes); f.evidence.queryEvidence.sha256 = hash(bytes);
    assert.throws(f.finish);
}));

// The matched case (the exact FFB2 reply under the setting) is the v33 private-clipboard receipt fixture.
test('a run whose initial settings turn DECRQCRA on owes the reply, so retained silence is refused', () => queryReceiptFixture(f => {
    const settings = JSON.parse(readFileSync(join(f.output, 'initial-settings.json'))); settings.AllowRectangleChecksumReport = true;
    f.write('initial-settings.json', settings); f.context.initialSettingsSha256 = hash(readFileSync(join(f.output, 'initial-settings.json'))); f.write('context.json', f.context);
    assert.equal(f.report.observations.find(x => x.id === 'q07.editing.decrqcra').observedEvidence, 'silence');
    assert.throws(f.finish);
}));

test('a v2 query policy run owes the DECRQSS s reply; the receipt re-derives it from the archived policy alone', () => queryReceiptFixture(f => {
    assert.equal(f.context.ownedQueryPolicy, 'owned-exact-query-paired-raw-state-v2'); assert.equal(f.report.policy, f.context.ownedQueryPolicy);
    assert.equal(f.report.observations.filter(x => x.expectedEvidence === 'negative-reply').length, 20);
    assert.equal(f.report.observations.find(x => x.id === 'q12.decrqss.horizontal-margins').expectedEvidence, 'positive-reply');
    assert.deepEqual(f.finish().queryEvidence.counts, { matched: 40 });
}, { marginsReport: true }));
test('a v2 run whose switch reply stayed negative is a recorded mismatch, not a refusal', () => queryReceiptFixture(f => {
    assert.deepEqual(f.finish().queryEvidence.counts, { matched: 39, mismatched: 1 });
}, { marginsReport: true, mutateReply: (id, value) => id === 'q12.decrqss.horizontal-margins' ? '\x1bP0$r\x1b\\' : value }));
test('rewriting the archived query policy cannot move a run between the negative and positive expectation', async () => {
    for (const [options, forged] of [[{}, 'owned-exact-query-paired-raw-state-v2'], [{ marginsReport: true }, 'owned-exact-query-paired-raw-state-v1']]) {
        await queryReceiptFixture(f => {
            f.context.ownedQueryPolicy = forged; f.proof.proofs[0].queries.policy = forged;
            f.write('context.json', f.context); f.write('ownership-proof.json', f.proof);
            assert.throws(f.finish);
        }, options);
    }
    await queryReceiptFixture(f => {
        f.context.ownedQueryPolicy = 'owned-exact-query-paired-raw-state-v3'; f.proof.proofs[0].queries.policy = f.context.ownedQueryPolicy;
        f.write('context.json', f.context); f.write('ownership-proof.json', f.proof);
        assert.throws(f.finish);
    });
});
