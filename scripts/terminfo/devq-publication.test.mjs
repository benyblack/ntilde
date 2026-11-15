import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { devqPublication } from './devq-publication.mjs';
import { buildDevqGradingReport } from './devq-grading.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-15-device-queries/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('retained run independently rederives the complete receipt and published projection', () => {
    const manifestBytes = read('manifest.json');
    assert.equal(hash(manifestBytes), '03089768aa747048f3081acd3e9495032803e60bb97f0478644cce2b3d4d1fff');
    const manifest = JSON.parse(manifestBytes);
    assert.equal(Object.keys(manifest.archives).length, 4, 'the final run and the retained superseded attempts');
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const root = mkdtempSync(join(tmpdir(), 'nt-devq-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        // The retained report is rebuilt from the retained paired journal and worker run log, byte for byte.
        const frames = readFileSync(join(root, 'dvq-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)), retained = readFileSync(join(root, 'dvq-observations.json'));
        const rebuilt = buildDevqGradingReport(frames, JSON.parse(readFileSync(join(root, 'dvq-run.json'))), JSON.parse(retained).runId, JSON.parse(retained).adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), retained), 0);
        const projected = devqPublication(root);
        // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.equal(schemaVersion, 60); assert.equal(fixtureRemoved, true);
        const settings = JSON.parse(readFileSync(join(root, 'initial-settings.json')));
        assert.equal(settings.AllowTitleReport, true, 'the measured run turned the off-by-default title reports on');
        assert.equal(JSON.parse(readFileSync(join(root, 'context.json'))).ownedQueryPolicy, 'owned-exact-query-paired-raw-state-v4');
        // The four pinned verdicts follow the unchanged pinned code over the journaled raw replies; XTREPORTCOLORS' xterm reply has a ? marker the pinned pattern cannot match.
        assert.deepEqual(projected.report.cases.map(item => [item.caseId, item.pinned.termless.observation.outcome, item.pinned.terminal.observation.outcome, item.effectContract.outcome, item.stock.outcome]), [
            ['device.xtgetxres', 'supported', 'supported', 'supported', 'supported'],
            ['device.xtreportcolors', 'inconclusive', 'inconclusive', 'supported', 'inconclusive'],
            ['device.xtwinops-20', 'supported', 'supported', 'supported', 'supported'],
            ['device.xtwinops-21', 'supported', 'supported', 'supported', 'supported'],
        ]);
        const colors = projected.report.cases[1];
        assert.equal(colors.pinned.termless.observation.reason, 'invalid-reply');
        assert.deepEqual(colors.pinned.termless.response, '\x1b[?0;0#Q');
        assert.deepEqual(colors.divergence, { pinnedOutcome: 'inconclusive', contractOutcome: 'supported', kind: 'pinned-inconclusive-effect-supported' });
        for (const item of projected.report.cases) {
            assert.equal(Object.values(item.observed.groups).every(group => group.matches && group.unchanged), true, item.caseId);
            assert.equal(item.observed.seed.cells, true);
        }
        const xres = projected.report.cases[0];
        assert.deepEqual(xres.pinned.termless.response, '\x1bP1+R7465726d4e616d65=787465726D2D323536636F6C6F72\x1b\\');
        assert.deepEqual(projected.report.cases.map(item => [item.queryWitness.id, item.queryWitness.contractMatched]), [
            ['q07.device.xtgetxres', true], ['q07.device.xtreportcolors', true], ['q07.device.xtwinops-20', false], ['q07.device.xtwinops-21', false]]);
        assert.equal(projected.report.termFeatures.stock.outcome, 'inconclusive');
        assert.deepEqual(projected.summary.stockCollector.counts, { inconclusive: 163, supported: 91, unsupported: 1 });
    } finally { rmSync(root, { recursive: true, force: true }); }
});
