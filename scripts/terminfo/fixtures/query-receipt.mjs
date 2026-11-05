// Synthetic query suffix after the immutable real 15-suite v10 prefix; never runtime evidence.
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { createHash } from 'node:crypto';
import { historicalArchive } from './captured-sources.mjs';
import { queryWorld } from './query-world.mjs';
import { capturedV11SourceFiles } from '../owned-sources.mjs';
import { querySuiteId, ownedQueryPolicy } from '../queries.mjs';
import { completeOwnedReceipt } from '../receipt.mjs';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
export async function queryReceiptFixture(action, worldOptions = {}) {
    const output = mkdtempSync(join(tmpdir(), 'nt-query-receipt-'));
    const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value, null, 2) + '\n');
    try {
        const archived = historicalArchive('2026-11-04-image-observations');
        for (const [file, bytes] of archived) { const target = join(output, file); mkdirSync(dirname(target), { recursive: true }); writeFileSync(target, bytes); }
        const old = JSON.parse(archived.get('cell-receipt.json'));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = old;
        rmSync(join(output, 'cell-receipt.json'));
        const context = JSON.parse(archived.get('context.json')), proof = JSON.parse(archived.get('ownership-proof.json'));
        const settings = JSON.parse(archived.get('initial-settings.json'));
        const image = JSON.parse(archived.get('image-observations.json')), input = JSON.parse(archived.get('input-observations.json'));
        const original = proof.proofs[0], last = image.snapshots.at(-1);
        const priorChallenges = [...input.snapshots, ...image.snapshots].filter(frame => frame.capture).map(frame => frame.capture.nonces.join(','));
        const binding = { runId: context.runId, ownerPid: original.app.pid, workerPid: original.worker.pid, uid: original.worker.uid,
            appExecutable: original.app.executable, workerExecutable: original.worker.executable, tty: original.tty,
            profileName: settings.Profiles[0].Name, paneId: last.paneId };
        const world = queryWorld({ binding, initialScreen: last.screen, cursorVisible: last.screen.cursorVisible,
            processFacts: original, atStart: Date.parse(image.seal.ownership.at) + 1, priorChallenges, ...worldOptions });
        await world.collect(); const report = world.observer.report;
        const frozenSources = historicalArchive('2026-11-05-query-observations');
        for (const [key, file] of Object.entries(capturedV11SourceFiles)) {
            const bytes = frozenSources.get('sources/' + file);
            writeFileSync(join(output, 'sources', file), bytes); context[key] = hash(bytes);
        }
        const policy = 'readonly-owned-source-graph-v4', manifest = { schemaVersion: 1, policy,
            hashes: Object.fromEntries(Object.keys(capturedV11SourceFiles).map(key => [key, context[key]])) };
        write('source-snapshot.json', manifest);
        Object.assign(context, { sourceSnapshotPolicy: policy, sourceSnapshotManifestSha256: hash(readFileSync(join(output, 'source-snapshot.json'))), querySuiteId, ownedQueryPolicy });
        proof.sourceSnapshot.policy = policy;
        original.queries = { policy: ownedQueryPolicy, binding, actions: report.actions, seal: report.seal };
        write('context.json', context); write('ownership-proof.json', proof); write('query-observations.json', report); write('query-initial.json', report.initial);
        writeFileSync(join(output, 'query-snapshots.jsonl'), report.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        writeFileSync(join(output, 'query-actions.jsonl'), report.actions.map(item => JSON.stringify(item)).join('\n') + '\n');
        const counts = {};
        for (const item of report.observations) { const key = item.contractMatched ? 'matched' : 'mismatched'; counts[key] = (counts[key] ?? 0) + 1; }
        evidence.queryEvidence = { runId: context.runId, suiteId: querySuiteId, file: 'query-observations.json', sha256: hash(readFileSync(join(output, 'query-observations.json'))), counts };
        await action({ output, write, evidence, context, proof, report, manifest, old,
            finish: () => completeOwnedReceipt(output, join(output, 'already-removed-fixture'), evidence) });
    } finally { rmSync(output, { recursive: true, force: true }); }
}
