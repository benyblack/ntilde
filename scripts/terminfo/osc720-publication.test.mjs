import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { osc720Publication } from './osc720-publication.mjs';
import { buildOsc720Report } from './osc720-grading.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-17-osc720/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('retained run independently rederives the complete receipt and published projection', () => {
    const manifestBytes = read('manifest.json');
    assert.equal(hash(manifestBytes), '0a887e9e51bdc21298b84791d79fe1218e195f558f594505c2d49f8c873e168e');
    const manifest = JSON.parse(manifestBytes);
    assert.equal(Object.keys(manifest.archives).length, 11, 'the final run, five retained failed attempts and five earlier runs (four complete, one with a failed control)');
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const root = mkdtempSync(join(tmpdir(), 'nt-osc720-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        // The retained report is rebuilt from the retained paired journal and worker run log, byte for byte.
        const frames = readFileSync(join(root, 'ovu-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)), retained = readFileSync(join(root, 'ovu-observations.json'));
        const rebuilt = buildOsc720Report(frames, JSON.parse(readFileSync(join(root, 'ovu-run.json'))), JSON.parse(retained).runId, JSON.parse(retained).adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), retained), 0);
        const projected = osc720Publication(root);
        // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        const [item] = projected.report.cases;
        assert.deepEqual(projected.report.cases.map(entry => [entry.caseId, entry.pinned.qualifiedOutcome, entry.effectContract.outcome]), [['extensions.osc720-scroll-up', 'inconclusive', 'unsupported']]);
        assert.equal(item.pinned.terminal.observation.evidence, 'consumed');
        assert.equal(item.pinned.termlessReplay.observation.outcome, 'unsupported', 'the unchanged pinned termless function over the mapped, unchanged viewport');
        assert.equal(item.stock.outcome, 'inconclusive');
        assert.equal(item.effectContract.controlsHeld, true);
        assert.deepEqual(item.effectContract.protocolExperiments.map(entry => entry.observed), Array(6).fill('none'));
        assert.deepEqual(item.effectContract.controlExperiments.map(entry => [entry.name, entry.observed]), [['unmarked', 'none'], ['ed3-control', 'erase-saved'], ['wheel-control', 'view-up']]);
        // The history every experiment started from, the wheel's view move and the ED 3 erase are the retained observed quantities.
        const byName = Object.fromEntries(item.experiments.map(entry => [entry.name, entry]));
        for (const entry of item.experiments) assert.equal(entry.observations[0].scrollbackLines, 30, entry.name);
        assert.equal(byName['wheel-control'].observations[1].scrollOffset > 0, true);
        assert.equal(byName['ed3-control'].observations[1].scrollbackLines, 0);
        assert.deepEqual(projected.report.wheelActions.map(entry => [entry.action, entry.notches.length]), [['scroll-up', 1], ['scroll-down', 1], ['scroll-down', 1], ['scroll-up', 1], ['scroll-down', 1], ['scroll-down', 1]]);
        assert.deepEqual(projected.summary.stockCollector.counts, { inconclusive: 160, supported: 94, unsupported: 1 });
        assert.equal(schemaVersion, 74); assert.equal(fixtureRemoved, true);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
