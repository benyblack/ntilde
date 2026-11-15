import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { kclPublication } from './kcl-publication.mjs';
import { buildKclGradingReport } from './kcl-grading.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-15-kitty-color-control/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('retained run independently rederives the complete receipt and published projection', () => {
    const manifestBytes = read('manifest.json');
    assert.equal(hash(manifestBytes), 'a4d8ac5e5e2e44e41f14a6353913dc93f8588910170d953dd864285dfcf412f0');
    const manifest = JSON.parse(manifestBytes);
    assert.equal(Object.keys(manifest.archives).length, 1, 'the single final run; no superseded attempt');
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const root = mkdtempSync(join(tmpdir(), 'nt-kcl-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        // The retained report is rebuilt from the retained paired journal and worker run log, byte for byte.
        const frames = readFileSync(join(root, 'kcl-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)), retained = readFileSync(join(root, 'kcl-observations.json'));
        const rebuilt = buildKclGradingReport(frames, JSON.parse(readFileSync(join(root, 'kcl-run.json'))), JSON.parse(retained).runId, JSON.parse(retained).adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), retained), 0);
        const projected = kclPublication(root);
        // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.equal(schemaVersion, 65); assert.equal(fixtureRemoved, true);
        const context = JSON.parse(readFileSync(join(root, 'context.json')));
        assert.equal(context.ownedQueryPolicy, 'owned-exact-query-paired-raw-state-v5');
        assert.equal(context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v59');
        // Both pinned paths replay the unchanged pinned code over the journaled reply of their own exchange; the stock collector and the exact-query witness agree.
        const [item] = projected.report.cases;
        assert.deepEqual([item.caseId, item.pinned.termless.observation.outcome, item.pinned.terminal.observation.outcome, item.effectContract.outcome, item.stock.outcome],
            ['extensions.osc21-kitty-color', 'supported', 'supported', 'supported', 'supported']);
        assert.equal(item.divergence, undefined);
        assert.deepEqual(item.queryWitness, { id: 'q07.extensions.osc21-kitty-color', expectedEvidence: 'positive-reply', expectedPattern: item.queryWitness.expectedPattern,
            receivedHex: item.queryWitness.receivedHex, contractMatched: true });
        assert.match(Buffer.from(item.queryWitness.receivedHex, 'hex').toString('latin1'), /^\x1b\]21;foreground=rgb:[0-9a-f]{2}\/[0-9a-f]{2}\/[0-9a-f]{2}\x1b\\$/);
        const groups = Object.entries(item.observed.groups);
        assert.deepEqual(Object.fromEntries(['control', 'protocol', 'policy'].map(kind => [kind, groups.filter(([, group]) => group.kind === kind).length])), {"control": 6, "protocol": 31, "policy": 12});
        // Every control and protocol group matched its demand and left the screen unchanged; policy groups record Ntilde's choices, two of which differ from kitty's text.
        assert.equal(groups.filter(([, group]) => group.kind !== 'policy').every(([, group]) => group.matches && group.unchanged), true);
        assert.equal(groups.every(([, group]) => group.unchanged), true);
        assert.deepEqual(groups.filter(([, group]) => group.kind === 'policy' && !group.matches).map(([role]) => role), ["encoding-sharp-4", "encoding-rgbi-range"]);
        assert.deepEqual(projected.summary.stockCollector.counts, { inconclusive: 161, supported: 93, unsupported: 1 });
    } finally { rmSync(root, { recursive: true, force: true }); }
});
