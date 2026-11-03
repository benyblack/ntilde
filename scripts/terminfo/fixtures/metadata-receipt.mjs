// Synthetic v8 receipt fixtures, never runtime qualification.
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync, unlinkSync } from 'node:fs';
import { join } from 'node:path';
import { ownedSavedReceiptFixture, runId } from './saved-receipt.mjs';
import { syntheticMetadataJournal } from './metadata-journal.mjs';
import { buildMetadataReport } from '../metadata.mjs';
import { metadataSuiteId, ownedMetadataPolicy } from '../metadata-cases.mjs';
import { ownedSourceFiles, legacyV8SourceFiles, sourceSnapshotPolicy } from '../owned-sources.mjs';
import { snapshotHash } from '../agent-observer.mjs';
export { runId };
export function ownedMetadataReceiptFixture(action) {
    return ownedSavedReceiptFixture(f => {
        const frames = syntheticMetadataJournal(runId), report = buildMetadataReport(frames, runId);
        Object.assign(f.context, { metadataSuiteId, ownedMetadataPolicy });
        for (const [key, file] of Object.entries(legacyV8SourceFiles)) {
            const data = readFileSync(new URL(file === 'terminfo-owned.mjs' ? '../../terminfo-owned.mjs' : '../' + file, import.meta.url));
            writeFileSync(join(f.output, 'sources', file), data); f.context[key] = createHash('sha256').update(data).digest('hex');
        }
        const bundle = Buffer.from('// Synthetic test-only collector bundle.\n');
        writeFileSync(join(f.output, 'collector-adapter.mjs'), bundle);
        writeFileSync(join(f.output, 'package-lock.json'), readFileSync(new URL('../collector-package-lock.json', import.meta.url)));
        Object.assign(f.context, { retainedSources: 'sources', retainedAdapterBundle: 'collector-adapter.mjs',
            adapterBundleSha256: createHash('sha256').update(bundle).digest('hex') });
        f.proof.proofs[0].metadata = { policy: ownedMetadataPolicy, paneId: frames[0].paneId, geometry: { rows: 12, cols: 60 } };
        const settings = { ...JSON.parse(readFileSync(join(f.output, 'initial-settings.json'))), Osc9NotificationsEnabled: true, Osc9ProgressReportingEnabled: true,
            AgentAccessObserveEnabled: true, TabStripOrientation: 'Horizontal' };
        f.write('initial-settings.json', settings);
        f.context.initialSettingsSha256 = createHash('sha256').update(readFileSync(join(f.output, 'initial-settings.json'))).digest('hex');
        f.write('context.json', f.context); f.write('ownership-proof.json', f.proof); f.write('metadata-observations.json', report);
        writeFileSync(join(f.output, 'metadata-snapshots.jsonl'), frames.map(value => JSON.stringify(value)).join('\n'));
        f.evidence.metadataEvidence = { runId, suiteId: metadataSuiteId, file: 'metadata-observations.json', sha256: snapshotHash(report), counts: { passed: 10, inconclusive: 2, failed: 1 } };
        action({ ...f, frames, metadataReport: report });
    });
}

export function ownedSnapshotReceiptFixture(action) {
    return ownedMetadataReceiptFixture(f => {
        for (const [key, file] of Object.entries(ownedSourceFiles)) {
            const data = readFileSync(new URL(file === 'terminfo-owned-runner.mjs' ? '../../' + file : '../' + file, import.meta.url));
            writeFileSync(join(f.output, 'sources', file), data); f.context[key] = createHash('sha256').update(data).digest('hex');
        }
        unlinkSync(join(f.output, 'sources', 'terminfo-owned.mjs'));
        const manifest = { schemaVersion: 1, policy: sourceSnapshotPolicy,
            hashes: Object.fromEntries(Object.keys(ownedSourceFiles).map(key => [key, f.context[key]])) };
        f.write('source-snapshot.json', manifest);
        Object.assign(f.context, { sourceSnapshotPolicy, sourceSnapshotManifestSha256: snapshotHash(manifest),
            sourceSnapshotRoot: join(f.output, 'already-removed-source-tree') });
        f.proof.sourceSnapshot = { policy: sourceSnapshotPolicy, root: f.context.sourceSnapshotRoot, removedAt: new Date().toISOString() };
        f.write('context.json', f.context); f.write('ownership-proof.json', f.proof);
        action({ ...f, manifest });
    });
}
