import test from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync, rmSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { staticImageReceiptFixture as fixture } from './fixtures/static-images-receipt.mjs';
import { includesStaticImageEvidence } from './static-images-receipt.mjs';
import { buildStaticImageReport } from './static-images.mjs';
import { pngHash } from './pixels-capture.mjs';
test('full15-suite synthetic finalizer requires fixed67-source schema10 and preserves all14 archived prefix bindings', () => fixture(f => {
    const receipt = f.finish(); assert.equal(receipt.schemaVersion, 10); assert.equal(receipt.fixtureRemoved, true);
    assert.deepEqual(receipt.staticImageEvidence.counts, { passed: 23 });
    for (const key of Object.keys(f.evidence).filter(key => key !== 'staticImageEvidence')) assert.deepEqual(receipt[key], f.old[key]);
    assert.equal(Object.keys(f.manifest.hashes).length, 67); assert.equal(f.report.snapshots.length, 31);
}));
test('schema10 can complete a valid controlled semantic pixel failure while preserving its successful ACK separately', () => fixture(f => {
    const frame = f.report.snapshots.find(value => value.caseId === 'kitty.numbered-lifecycle' && value.stageId === 'place');
    const blank = f.report.snapshots[1].pixels[0], bytes = readFileSync(join(f.output, blank.file));
    for (let side = 0; side < 2; side++) { const file = frame.pixels[side].file; writeFileSync(join(f.output, file), bytes); frame.pixels[side] = { ...blank, file }; }
    const derived = buildStaticImageReport(f.report.snapshots, f.report.actions, f.report.seal, f.context.runId, f.output, f.report.priorChallenges);
    const observation = derived.observations.find(value => value.featureId === 'kitty.numbered-lifecycle.place');
    assert.equal(observation.reply.passed, true); assert.equal(observation.result, 'failed');
    f.write('image-observations.json', derived);
    writeFileSync(join(f.output, 'image-snapshots.jsonl'), derived.snapshots.map(value => JSON.stringify(value)).join('\n') + '\n');
    Object.assign(f.evidence.staticImageEvidence, { sha256: pngHash(readFileSync(join(f.output, 'image-observations.json'))), counts: { passed: 22, failed: 1 } });
    const receipt = f.finish(); assert.equal(receipt.schemaVersion, 10); assert.deepEqual(receipt.staticImageEvidence.counts, { passed: 22, failed: 1 });
}));
test('only a pre-fixture typed refusal can complete an explicitly unavailable image binding', () => fixture(f => {
    for (const file of ['image-inputs', 'image-pixels', 'image-observations.json']) rmSync(join(f.output, file), { recursive: true });
    writeFileSync(join(f.output, 'image-snapshots.jsonl'), ''); writeFileSync(join(f.output, 'image-actions.jsonl'), '');
    const marker = f.proof.proofs[0].staticImages;
    delete marker.paneId; delete marker.geometry; delete marker.seal; marker.actions = []; f.write('ownership-proof.json', f.proof);
    const report = { schemaVersion: 1, runId: f.context.runId, suiteId: f.context.staticImageSuiteId,
        status: 'unavailable', reason: 'imageGeometryUnavailable', fixtureStarted: false, plannedExperiments: 5, plannedStages: 23 };
    f.write('image-unavailable.json', report);
    Object.assign(f.evidence.staticImageEvidence, { file: 'image-unavailable.json', sha256: pngHash(readFileSync(join(f.output, 'image-unavailable.json'))),
        status: 'unavailable', reason: report.reason, counts: {} });
    assert.equal(f.finish().schemaVersion, 10);
}));
for (const [name, mutate] of [
    ['missing entire suite', f => { delete f.evidence.staticImageEvidence; }],
    ['changed journal', f => writeFileSync(join(f.output, 'image-actions.jsonl'), '')],
    ['extra source image', f => writeFileSync(join(f.output, 'image-inputs', 'foreign.png'), 'x')],
    ['extra retained PNG', f => writeFileSync(join(f.output, 'image-pixels', 'foreign.png'), 'x')],
    ['failed partial observation', f => f.write('image-failed-observation.json', { error: 'partial' })],
    ['changed cleanup ordering', f => { f.proof.cleanedUpAt = '2026-11-04T00:00:00.000Z'; f.write('ownership-proof.json', f.proof); }],
    ['wrong stock pane', f => { f.proof.proofs[0].staticImages.paneId = '11111111-1111-1111-1111-111111111111'; f.write('ownership-proof.json', f.proof); }],
]) test('full receipt refuses ' + name, () => fixture(f => { mutate(f); assert.throws(f.finish); }));
test('coherent source-policy/field stripping cannot downgrade retained image evidence to v9', () => fixture(f => {
    delete f.evidence.staticImageEvidence; delete f.context.staticImageSuiteId; delete f.context.ownedStaticImagePolicy;
    delete f.proof.proofs[0].staticImages;
    f.context.sourceSnapshotPolicy = f.proof.sourceSnapshot.policy = 'readonly-owned-source-graph-v2';
    for (const key of Object.keys(f.context).filter(key => key.startsWith('staticImage'))) delete f.context[key];
    f.write('context.json', f.context); f.write('ownership-proof.json', f.proof);
    assert.equal(includesStaticImageEvidence(f.output, f.evidence, f.context, f.proof), true); assert.throws(f.finish);
    for (const file of ['image-snapshots.jsonl', 'image-actions.jsonl', 'image-observations.json', 'image-inputs', 'image-pixels']) rmSync(join(f.output, file), { recursive: true });
    assert.equal(includesStaticImageEvidence(f.output, f.evidence, f.context, f.proof), true);
    for (const file of ['static-images.mjs', 'static-images-cases.mjs', 'static-images-inputs.mjs', 'static-images-oracle.mjs', 'static-images-capture.mjs', 'static-images-collector.mjs', 'static-images-receipt.mjs']) rmSync(join(f.output, 'sources', file));
    // Worker/runner/receipt imports remain an independent downgrade witness.
    assert.match(readFileSync(join(f.output, 'sources', 'worker.mjs'), 'utf8'), /static-images-collector/);
    assert.equal(includesStaticImageEvidence(f.output, f.evidence, f.context, f.proof), true); assert.throws(f.finish);
}));
