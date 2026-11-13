import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { osc9Publication } from './osc9-publication.mjs';
import { buildOsc9Report } from './osc9-grading.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-13-osc9/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('retained run independently rederives the complete receipt and published projection', () => {
    const manifestBytes = read('manifest.json');
    assert.equal(hash(manifestBytes), '89f023293c1517ed4c559b607d12440dde4e55dd79f11c34d6ca3dba703655ec');
    const manifest = JSON.parse(manifestBytes);
    assert.equal(Object.keys(manifest.archives).length, 12, 'the final run and eleven retained superseded attempts');
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const root = mkdtempSync(join(tmpdir(), 'nt-osc9-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        // The retained report is rebuilt from the retained paired journal and worker run log, byte for byte.
        const frames = readFileSync(join(root, 'osn-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)), retained = readFileSync(join(root, 'osn-observations.json'));
        const rebuilt = buildOsc9Report(frames, JSON.parse(readFileSync(join(root, 'osn-run.json'))), JSON.parse(retained).runId, JSON.parse(retained).adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), retained), 0);
        const projected = osc9Publication(root);
        // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.deepEqual(projected.report.cases.map(item => [item.caseId, item.pinned.qualifiedOutcome, item.effectContract.outcome]), [
            ['extensions.osc9-progress', 'inconclusive', 'supported'], ['extensions.notifications', 'inconclusive', 'supported'],
        ]);
        const [progress, notifications] = projected.report.cases;
        assert.equal(progress.pinned.termless, null); assert.equal(progress.pinned.termlessFunction, false);
        assert.equal(notifications.pinned.termless.observation.evidence, 'legacy');
        assert.ok(projected.report.cases.every(item => item.stock.outcome === 'inconclusive' && item.pinned.terminal.observation.outcome === 'inconclusive'));
        assert.equal(notifications.effectContract.preconditionsMet, true);
        assert.deepEqual(projected.report.focusActions.map(item => item.action), ['refocus', 'defocus', 'refocus']);
        assert.ok(projected.report.focusActions.every(item => item.attempts.length >= 1 && item.appPid > 0));
        assert.deepEqual(projected.summary.stockCollector.counts, { inconclusive: 167, supported: 87, unsupported: 1 });
        assert.equal(schemaVersion, 57); assert.equal(fixtureRemoved, true);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
