// Frozen real-App evidence; synthetic tests do not replace this measurement.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { buildMetadataReport } from './metadata.mjs';
import { capturedV8SourceFiles, capturedV8SourcePolicy } from './owned-sources.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
test('published captured-source metadata preserves exact observations, all source pins and cleanup', () => {
    const data = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-04-metadata-snapshot.json', import.meta.url)));
    assert.equal(data.appBuildSource, '1f4403f222ee0095d9b2d8a486009fd38254ed71');
    assert.equal(data.adapterSource, data.appBuildSource);
    assert.deepEqual(buildMetadataReport(data.metadataReport.snapshots, data.context.runId), data.metadataReport);
    assert.deepEqual(data.receipt.metadataEvidence.counts, { passed: 10, inconclusive: 2, failed: 1 });
    assert.equal(data.metadataReport.observations.at(-1).result, 'passed');
    assert.ok(data.metadataReport.observations.every(item => item.control.passed));
    assert.equal(data.metadataReport.snapshots[39].before.metadata.oscTitle, 'MD-base-4fd48c78');
    assert.equal(data.sourceSnapshot.policy, capturedV8SourcePolicy);
    assert.equal(Object.keys(data.sourceSnapshot.hashes).length, 52);
    for (const [key, file] of Object.entries(capturedV8SourceFiles)) {
        assert.equal(data.sourceSnapshot.hashes[key], data.context[key]);
        assert.equal(data.archive.fileSha256['sources/' + file], data.context[key]);
    }
    assert.equal(data.archive.fileSha256['source-snapshot.json'], data.context.sourceSnapshotManifestSha256);
    assert.equal(data.sourceSnapshotCleanup.policy, capturedV8SourcePolicy);
    assert.equal(data.sourceSnapshotCleanup.root, data.context.sourceSnapshotRoot);
    assert.ok(Date.parse(data.sourceSnapshotCleanup.removedAt) <= Date.parse(data.receipt.completedAt));
    assert.equal(data.receipt.schemaVersion, 8); assert.equal(data.receipt.fixtureRemoved, true);
    assert.equal(data.archive.files, 166); assert.equal(Object.keys(data.archive.fileSha256).length, 166);
    const report = JSON.stringify(data.metadataReport, null, 2) + '\n';
    assert.equal(Buffer.byteLength(report), 396053);
    assert.equal(hash(report), '2f168b8ed5a8c146510647c6024f4046199d9cac43bf802335cf55350651e74a');
    assert.equal(data.receipt.metadataEvidence.sha256, hash(report));
    const archive = readFileSync(new URL('../../docs/qa/terminfo/' + data.archive.file, import.meta.url));
    assert.equal(archive.length, 602025);
    assert.equal(hash(archive), '688361762a0c99cbb95ac92527c11d3b7d2e0947f8bf0e637edf305334204863');
    assert.equal(data.archive.sha256, hash(archive));
    assert.deepEqual(data.stockCollector.counts, { supported: 78, inconclusive: 177 });
    assert.equal(data.stockCollector.suiteComplete, false);
});
