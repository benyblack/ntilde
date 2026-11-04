import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, unlinkSync, mkdtempSync, mkdirSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { createHash } from 'node:crypto';
import { ownedInputReceiptFixture as fixture } from './fixtures/input-receipt.mjs';
import { capturedV8SourceFiles, capturedV9SourceFiles, capturedV8SourcePolicy } from './owned-sources.mjs';
import { inputSources, checkInputEvidence } from './input-receipt.mjs';
import { inputSuiteId, ownedInputPolicy } from './input-cases.mjs';
import { syntheticInputJournal, inputRunId } from './fixtures/input-journal.mjs';
import { buildInputReport } from './input-state.mjs';
import { snapshotHash } from './agent-observer.mjs';
import { historicalArchive } from './fixtures/captured-sources.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
test('v9 requires the complete14-suite chain and fixed60-file captured scope even for a strict initial refusal', () => fixture(f => {
    const receipt = f.finish(); assert.equal(receipt.schemaVersion, 9); assert.equal(receipt.fixtureRemoved, true);
    assert.equal(Object.keys(f.manifest.hashes).length, 60); assert.equal(Object.keys(capturedV8SourceFiles).length, 52);
    assert.equal(Object.keys(capturedV9SourceFiles).length, 60); assert.deepEqual(receipt.inputEvidence.counts, {});
}));
for (const [name, mutate] of [
    ['missing binding', f => delete f.evidence.inputEvidence],
    ['missing prior metadata chain', f => delete f.evidence.metadataEvidence],
    ['missing source', f => unlinkSync(join(f.output, 'sources', 'input-bytes.mjs'))],
    ['changed source', f => writeFileSync(join(f.output, 'sources', 'input-action.mjs'), '// substituted')],
    ['actions before refusal', f => f.proof.proofs[0].input.actions.push({ index: 0 })],
    ['bound pane before refusal', f => f.proof.proofs[0].input.paneId = '11111111-1111-1111-1111-111111111111'],
    ['started fixture refusal', f => { f.inputReport.fixtureStarted = true; f.write('input-unavailable.json', f.inputReport); f.evidence.inputEvidence.sha256 = snapshotHash(f.inputReport); }],
    ['failed raw attempt', f => f.write('input-raw-failure.json', { error: 'late input' })],
    ['failed parent observation', f => f.write('input-failed-observation.json', { error: 'ownership lost' })],
    ['simultaneous report and refusal', f => f.write('input-observations.json', {})],
]) test(name + ' cannot finalize v9 or downgrade', () => fixture(f => {
    mutate(f); f.write('context.json', f.context); f.write('ownership-proof.json', f.proof);
    assert.throws(f.finish);
}));
test('coherent stripping cannot downgrade retained new runner/worker imports to a v8 receipt', () => fixture(f => {
    for (const [key, file] of Object.entries(inputSources)) { delete f.context[key]; unlinkSync(join(f.output, 'sources', file)); }
    for (const file of ['input-unavailable.json', 'input-snapshots.jsonl', 'input-actions.jsonl']) unlinkSync(join(f.output, file));
    delete f.evidence.inputEvidence; delete f.context.inputSuiteId; delete f.context.ownedInputPolicy; delete f.proof.proofs[0].input;
    f.context.sourceSnapshotPolicy = capturedV8SourcePolicy; f.proof.sourceSnapshot.policy = capturedV8SourcePolicy;
    const inventory = Buffer.from(JSON.stringify(capturedV8SourceFiles, null, 2) + '\n');
    writeFileSync(join(f.output, 'sources', 'owned-source-inventory.json'), inventory);
    f.context.sourceInventorySha256 = createHash('sha256').update(inventory).digest('hex');
    f.manifest.policy = capturedV8SourcePolicy;
    f.manifest.hashes = Object.fromEntries(Object.keys(capturedV8SourceFiles).map(key => [key, f.context[key]]));
    f.write('source-snapshot.json', f.manifest); f.context.sourceSnapshotManifestSha256 = snapshotHash(f.manifest);
    f.write('context.json', f.context); f.write('ownership-proof.json', f.proof);
    assert.throws(f.finish, /dependency|incomplete/);
}));
test('complete positive input report is re-derived from paired state, action facts and original raw bytes', () => fixture(f => {
    const { frames, actions, seal } = syntheticInputJournal(inputRunId), report = buildInputReport(frames, actions, seal, inputRunId);
    unlinkSync(join(f.output, 'input-unavailable.json')); f.write('input-observations.json', report);
    writeFileSync(join(f.output, 'input-snapshots.jsonl'), frames.map(x => JSON.stringify(x)).join('\n'));
    writeFileSync(join(f.output, 'input-actions.jsonl'), actions.map(x => JSON.stringify(x)).join('\n'));
    f.proof.proofs[0].input = { policy: ownedInputPolicy, paneId: frames[0].paneId, geometry: { rows: 12, cols: 120 }, actions, seal };
    f.proof.proofs[0].app = actions[0].observed.before.app; f.proof.proofs[0].worker = actions[0].observed.before.worker;
    f.write('cell-observations.json', { snapshots: [{ paneId: frames[0].paneId, screen: { rows: 12, cols: 120 } }] });
    const binding = { runId: inputRunId, suiteId: inputSuiteId, file: 'input-observations.json', sha256: snapshotHash(report), counts: { passed: 39 } };
    assert.doesNotThrow(() => checkInputEvidence(f.output, binding, inputRunId, f.context, f.proof));
    actions[0].observed.app.eventDispatched = false;
    writeFileSync(join(f.output, 'input-actions.jsonl'), actions.map(x => JSON.stringify(x)).join('\n'));
    assert.throws(() => checkInputEvidence(f.output, binding, inputRunId, f.context, f.proof));
}));

test('full v9 finalizer checks a synthetic positive input suffix after all13 frozen prefix bindings', () => {
    // This assembled test fixture is never published as a runtime measurement.
    const output = mkdtempSync(join(tmpdir(), 'nt-v9-positive-'));
    const hash = bytes => createHash('sha256').update(bytes).digest('hex');
    const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value, null, 2) + '\n');
    try {
        for (const [file, bytes] of historicalArchive('2026-11-04-reset-observations')) {
            const target = join(output, file); mkdirSync(dirname(target), { recursive: true }); writeFileSync(target, bytes);
        }
        const context = JSON.parse(readFileSync(join(output, 'context.json'))), proof = JSON.parse(readFileSync(join(output, 'ownership-proof.json')));
        const old = JSON.parse(readFileSync(join(output, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = old;
        unlinkSync(join(output, 'cell-receipt.json'));
        for (const [key, file] of Object.entries(capturedV9SourceFiles)) {
            const bytes = readFileSync(new URL(file === 'terminfo-owned-runner.mjs' ? '../' + file : './' + file, import.meta.url));
            writeFileSync(join(output, 'sources', file), bytes); context[key] = hash(bytes);
        }
        const policy = 'readonly-owned-source-graph-v2', manifest = { schemaVersion: 1, policy,
            hashes: Object.fromEntries(Object.keys(capturedV9SourceFiles).map(key => [key, context[key]])) };
        write('source-snapshot.json', manifest);
        Object.assign(context, { sourceSnapshotPolicy: policy, sourceSnapshotManifestSha256: hash(readFileSync(join(output, 'source-snapshot.json'))), inputSuiteId, ownedInputPolicy });
        proof.sourceSnapshot.policy = policy;
        const initial = JSON.parse(readFileSync(join(output, 'metadata-observations.json'))).snapshots.at(-1).before.screen;
        const paneId = proof.proofs[0].metadata.paneId, { frames, actions, seal } = syntheticInputJournal(context.runId, { paneId, initial });
        const profileName = JSON.parse(readFileSync(join(output, 'initial-settings.json'))).Profiles[0].Name;
        for (const action of actions) for (const side of ['before', 'after']) {
            action.observed[side].pane.profileName = profileName;
            action.observed[side].app = structuredClone(proof.proofs[0].app);
            action.observed[side].worker = structuredClone(proof.proofs[0].worker);
        }
        const report = buildInputReport(frames, actions, seal, context.runId);
        proof.proofs[0].input = { policy: ownedInputPolicy, paneId, geometry: { rows: initial.rows, cols: initial.cols }, actions, seal };
        write('input-observations.json', report); write('context.json', context); write('ownership-proof.json', proof);
        writeFileSync(join(output, 'input-snapshots.jsonl'), frames.map(x => JSON.stringify(x)).join('\n'));
        writeFileSync(join(output, 'input-actions.jsonl'), actions.map(x => JSON.stringify(x)).join('\n'));
        evidence.inputEvidence = { runId: context.runId, suiteId: inputSuiteId, file: 'input-observations.json', sha256: hash(readFileSync(join(output, 'input-observations.json'))), counts: { passed: 39 } };
        const receipt = completeOwnedReceipt(output, join(output, 'already-removed-fixture'), evidence);
        assert.equal(receipt.schemaVersion, 9); assert.deepEqual(receipt.inputEvidence.counts, { passed: 39 });
        assert.deepEqual(receipt.metadataEvidence, old.metadataEvidence); assert.deepEqual(receipt.savedEvidence, old.savedEvidence);
    } finally { rmSync(output, { recursive: true, force: true }); }
});
