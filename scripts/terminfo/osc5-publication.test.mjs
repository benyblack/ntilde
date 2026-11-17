import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { osc5Publication } from './osc5-publication.mjs';
import { buildOsc5GradingReport } from './osc5-grading.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-17-osc5-special-colors/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('retained run independently rederives the complete receipt and published projection', () => {
    const manifestBytes = read('manifest.json');
    assert.equal(hash(manifestBytes), '7991c411846088023ff0e08d870a63fe846aee73344da2f4cd62140f48ebbc84');
    const manifest = JSON.parse(manifestBytes);
    assert.equal(Object.keys(manifest.archives).length, 1, 'the single final run; no superseded attempt');
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const root = mkdtempSync(join(tmpdir(), 'nt-osc5-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        // The retained report is rebuilt from the retained paired journal and worker run log, byte for byte.
        const frames = readFileSync(join(root, 'osc5-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)), retained = readFileSync(join(root, 'osc5-observations.json'));
        const rebuilt = buildOsc5GradingReport(frames, JSON.parse(readFileSync(join(root, 'osc5-run.json'))), JSON.parse(retained).runId, JSON.parse(retained).adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), retained), 0);
        const projected = osc5Publication(root);
        // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.equal(schemaVersion, 70); assert.equal(fixtureRemoved, true);
        const context = JSON.parse(readFileSync(join(root, 'context.json')));
        assert.equal(context.ownedQueryPolicy, 'owned-exact-query-paired-raw-state-v6');
        assert.equal(context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v63');
        // Both pinned paths replay the unchanged pinned code over the journaled reply of their own exchange; the stock collector and the exact-query witness agree.
        const [item] = projected.report.cases;
        assert.deepEqual([item.caseId, item.pinned.termless.observation.outcome, item.pinned.terminal.observation.outcome, item.effectContract.outcome, item.stock.outcome],
            ['extensions.osc5-special-color', 'supported', 'supported', 'supported', 'supported']);
        assert.equal(item.divergence, undefined);
        assert.deepEqual(item.queryWitness, { id: 'q07.extensions.osc5-special-color', expectedEvidence: 'positive-reply', expectedPattern: item.queryWitness.expectedPattern,
            receivedHex: item.queryWitness.receivedHex, contractMatched: true });
        assert.equal(item.queryWitness.byteLength, undefined, 'the shape-only witness records no byte length');
        assert.match(Buffer.from(item.queryWitness.receivedHex, 'hex').toString('latin1'), /^\x1b\]5;0;rgb:[0-9a-f]{1,4}\/[0-9a-f]{1,4}\/[0-9a-f]{1,4}(?:\x07|\x1b\\)$/i);
        const groups = Object.entries(item.observed.groups);
        assert.deepEqual(Object.fromEntries(['control', 'protocol', 'policy'].map(kind => [kind, groups.filter(([, group]) => group.kind === kind).length])), { control: 3, protocol: 15, policy: 5 });
        // Every control and protocol group matched its demand and left the screen unchanged; policy groups record Ntilde's choices.
        assert.equal(groups.filter(([, group]) => group.kind !== 'policy').every(([, group]) => group.matches && group.unchanged), true);
        assert.equal(groups.every(([, group]) => group.unchanged), true);
        assert.deepEqual(groups.filter(([, group]) => group.kind === 'policy' && !group.matches).map(([role]) => role), [], 'every policy group matched: the oversize drop and the silence both agree with the recorded policy');
        // The isolation set/read pairs leave the other service's measured default in force on both directions.
        for (const role of ['cross-dynamic-set', 'cross-dynamic-read', 'cross-palette-set', 'cross-palette-read', 'cross-kitty-set', 'cross-kitty-read']) {
            assert.equal(item.observed.groups[role].matches, true, role);
        }
        assert.deepEqual(projected.summary.stockCollector.counts, { inconclusive: 160, supported: 94, unsupported: 1 });
    } finally { rmSync(root, { recursive: true, force: true }); }
});
