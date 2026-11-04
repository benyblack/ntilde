// Synthetic14-suite receipt: the input suite has a strict pre-fixture geometry refusal.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { ownedSnapshotReceiptFixture } from './metadata-receipt.mjs';
import { capturedV9SourceFiles, sourceSnapshotPolicy } from '../owned-sources.mjs';
import { inputSuiteId, ownedInputPolicy } from '../input-cases.mjs';
import { snapshotHash } from '../agent-observer.mjs';
export function ownedInputReceiptFixture(action) {
    return ownedSnapshotReceiptFixture(f => {
        for (const [key, file] of Object.entries(capturedV9SourceFiles)) {
            const bytes = readFileSync(new URL(file === 'terminfo-owned-runner.mjs' ? '../../' + file : '../' + file, import.meta.url));
            writeFileSync(join(f.output, 'sources', file), bytes); f.context[key] = createHash('sha256').update(bytes).digest('hex');
        }
        const manifest = { schemaVersion: 1, policy: sourceSnapshotPolicy,
            hashes: Object.fromEntries(Object.keys(capturedV9SourceFiles).map(key => [key, f.context[key]])) };
        f.write('source-snapshot.json', manifest);
        Object.assign(f.context, { inputSuiteId, ownedInputPolicy, sourceSnapshotPolicy, sourceSnapshotManifestSha256: snapshotHash(manifest) });
        f.proof.sourceSnapshot.policy = sourceSnapshotPolicy;
        f.proof.proofs[0].input = { policy: ownedInputPolicy, actions: [] };
        const report = { schemaVersion: 1, runId: f.context.runId, suiteId: inputSuiteId, status: 'unavailable',
            reason: 'inputGeometryUnavailable', fixtureStarted: false, plannedExperiments: 39 };
        f.write('input-unavailable.json', report);
        writeFileSync(join(f.output, 'input-snapshots.jsonl'), ''); writeFileSync(join(f.output, 'input-actions.jsonl'), '');
        f.evidence.inputEvidence = { runId: f.context.runId, suiteId: inputSuiteId, file: 'input-unavailable.json', sha256: snapshotHash(report),
            status: 'unavailable', reason: 'inputGeometryUnavailable', counts: {} };
        f.write('context.json', f.context); f.write('ownership-proof.json', f.proof);
        action({ ...f, inputReport: report, manifest });
    });
}
