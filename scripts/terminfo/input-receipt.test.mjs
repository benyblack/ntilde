import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, unlinkSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { ownedInputReceiptFixture as fixture } from './fixtures/input-receipt.mjs';
import { capturedV8SourceFiles, capturedV9SourceFiles, capturedV8SourcePolicy } from './owned-sources.mjs';
import { inputSources, checkInputEvidence } from './input-receipt.mjs';
import { inputSuiteId, ownedInputPolicy } from './input-cases.mjs';
import { syntheticInputJournal, inputRunId } from './fixtures/input-journal.mjs';
import { buildInputReport } from './input-state.mjs';
import { snapshotHash } from './agent-observer.mjs';
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
    f.write('cell-observations.json', { snapshots: [{ paneId: frames[0].paneId, screen: { rows: 12, cols: 120 } }] });
    const binding = { runId: inputRunId, suiteId: inputSuiteId, file: 'input-observations.json', sha256: snapshotHash(report), counts: { passed: 39 } };
    assert.doesNotThrow(() => checkInputEvidence(f.output, binding, inputRunId, f.context, f.proof));
    actions[0].observed.app.eventDispatched = false;
    writeFileSync(join(f.output, 'input-actions.jsonl'), actions.map(x => JSON.stringify(x)).join('\n'));
    assert.throws(() => checkInputEvidence(f.output, binding, inputRunId, f.context, f.proof));
}));
