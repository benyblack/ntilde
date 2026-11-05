// Synthetic image suffix after immutable real14-suite input evidence; never runtime qualification.
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { historicalArchive } from './captured-sources.mjs';
import { syntheticImageJournal } from './static-images-journal.mjs';
import { capturedV10SourceFiles } from '../owned-sources.mjs';
import { ownedStaticImagePolicy, buildStaticImageReport } from '../static-images.mjs';
import { staticImageSuiteId } from '../static-images-cases.mjs';
import { pngHash } from '../pixels-capture.mjs';
import { completeOwnedReceipt } from '../receipt.mjs';
export function staticImageReceiptFixture(action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-image-receipt-'));
    const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value, null, 2) + '\n');
    try {
        const archived = historicalArchive('2026-11-04-input-observations');
        for (const [file, bytes] of archived) { const target = join(output, file); mkdirSync(dirname(target), { recursive: true }); writeFileSync(target, bytes); }
        const old = JSON.parse(archived.get('cell-receipt.json'));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = old;
        rmSync(join(output, 'cell-receipt.json'));
        const context = JSON.parse(archived.get('context.json')), proof = JSON.parse(archived.get('ownership-proof.json'));
        const frozenSources = historicalArchive('2026-11-04-image-observations');
        for (const [key, file] of Object.entries(capturedV10SourceFiles)) {
            const bytes = frozenSources.get('sources/' + file);
            if (!bytes) throw new Error('Missing frozen v10 source: ' + file);
            writeFileSync(join(output, 'sources', file), bytes); context[key] = pngHash(bytes);
        }
        const policy = 'readonly-owned-source-graph-v3', manifest = { schemaVersion: 1, policy,
            hashes: Object.fromEntries(Object.keys(capturedV10SourceFiles).map(key => [key, context[key]])) };
        write('source-snapshot.json', manifest);
        Object.assign(context, { sourceSnapshotPolicy: policy, sourceSnapshotManifestSha256: pngHash(readFileSync(join(output, 'source-snapshot.json'))),
            staticImageSuiteId, ownedStaticImagePolicy }); proof.sourceSnapshot.policy = policy;
        const input = JSON.parse(archived.get('input-observations.json'));
        const { frames, actions, seal } = syntheticImageJournal(output, { initial: input.snapshots[0].screen, paneId: input.snapshots[0].paneId, runId: context.runId });
        const ownership = { app: proof.proofs[0].app, worker: proof.proofs[0].worker, tty: proof.proofs[0].tty,
            terminalOutput: proof.proofs[0].terminalOutput, at: proof.cleanedUpAt };
        for (const frame of frames) frame.ownership = structuredClone(ownership);
        for (const item of actions) item.ownership = structuredClone(ownership);
        seal.ownership = structuredClone(ownership);
        const prior = input.snapshots.filter(frame => frame.capture).map(frame => frame.capture.nonces.join(','));
        const report = buildStaticImageReport(frames, actions, seal, context.runId, output, prior);
        proof.proofs[0].staticImages = { policy: ownedStaticImagePolicy, paneId: frames[0].paneId,
            geometry: { rows: frames[0].screen.rows, cols: frames[0].screen.cols }, actions, seal };
        write('context.json', context); write('ownership-proof.json', proof); write('image-observations.json', report);
        writeFileSync(join(output, 'image-snapshots.jsonl'), frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        writeFileSync(join(output, 'image-actions.jsonl'), actions.map(item => JSON.stringify(item)).join('\n') + '\n');
        evidence.staticImageEvidence = { runId: context.runId, suiteId: staticImageSuiteId, file: 'image-observations.json',
            sha256: pngHash(readFileSync(join(output, 'image-observations.json'))), counts: { passed: 23 } };
        action({ output, write, evidence, context, proof, report, manifest, old,
            finish: () => completeOwnedReceipt(output, join(output, 'already-removed-fixture'), evidence) });
    } finally { rmSync(output, { recursive: true, force: true }); }
}
