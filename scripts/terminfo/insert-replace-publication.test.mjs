import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { insertReplacePublication } from './insert-replace-publication.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-12-insert-replace/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('the retained owned run rederives its complete receipt, predecessor suites and publication', () => {
    const manifestBytes = read('manifest.json');
    assert.equal(hash(manifestBytes), '358c602533c5df4da807a7104df66d08278e53b339f738e081e460846121f559');
    const manifest = JSON.parse(manifestBytes);
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const root = mkdtempSync(join(tmpdir(), 'nt-irm-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        const projected = insertReplacePublication(root, { appRevision: manifest.appRevision, scriptsRevision: manifest.scriptsRevision });
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        const item = projected.report.cases[0];
        assert.equal(item.caseId, 'modes.insert-replace');
        assert.equal(item.pinned.observation.outcome, 'supported');
        assert.equal(item.terminalPinned.observation.outcome, 'inconclusive');
        assert.equal(item.contractResult.outcome, 'supported');
        assert.equal(item.stock.outcome, 'inconclusive');
        assert.deepEqual([item.observed.seed.text, item.observed.inserted.text, item.observed.replaced.text], ['ABC     ', 'XABC    ', 'XYBC    ']);
        assert.deepEqual([item.observed.edgeBefore.text, item.observed.edgeInserted.text], ['WXY', 'QRW']);
        assert.equal(item.observed.attributes.beforeD.bold, true); assert.deepEqual(item.observed.attributes.beforeD, item.observed.attributes.afterD);
        assert.deepEqual(item.observed.attributes.writtenX, item.observed.attributes.penX);
        assert.equal(schemaVersion, 54); assert.equal(fixtureRemoved, true);
        assert.equal(original.oscTitleEvidence.suiteId, 'ntilde-owned-osc-title-graded-v1');
    } finally { rmSync(root, { recursive: true, force: true }); }
});
