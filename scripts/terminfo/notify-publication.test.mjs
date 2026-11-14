import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { notifyPublication } from './notify-publication.mjs';
import { buildNotifyReport } from './notify-grading.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-14-notify/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

test('retained run independently rederives the complete receipt and published projection', () => {
    const manifestBytes = read('manifest.json');
    assert.equal(hash(manifestBytes), '4aa9aab3458c5fff96ceb6f93a7f071fabec15c555bede00c9dd50872783f63a');
    const manifest = JSON.parse(manifestBytes);
    assert.equal(Object.keys(manifest.archives).length, 3, 'the final run and two retained superseded attempts');
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const root = mkdtempSync(join(tmpdir(), 'nt-notify-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        // The retained report is rebuilt from the retained paired journal and worker run log, byte for byte.
        const frames = readFileSync(join(root, 'ntf-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)), retained = readFileSync(join(root, 'ntf-observations.json'));
        const rebuilt = buildNotifyReport(frames, JSON.parse(readFileSync(join(root, 'ntf-run.json'))), JSON.parse(retained).runId, JSON.parse(retained).adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), retained), 0);
        const projected = notifyPublication(root);
        // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.deepEqual(projected.report.cases.map(item => [item.caseId, item.pinned.qualifiedOutcome, item.effectContract.outcome, item.stock.outcome]), [
            ['extensions.osc777-notify', 'inconclusive', 'supported', 'inconclusive'], ['extensions.osc99-kitty-notify', 'supported', 'supported', 'supported'],
        ]);
        const [osc777, osc99] = projected.report.cases;
        assert.equal(osc777.pinned.termlessFunction, false); assert.equal(osc777.pinned.termless, null);
        assert.equal(osc777.pinned.terminal.observation.outcome, 'inconclusive'); assert.deepEqual(osc777.pinned.writes, ['\x1b[1;1H\x1b[2K', '\x1b]777;notify;test;body\x07']);
        assert.equal(osc99.pinned.verdictSource.startsWith('stock collector'), true);
        assert.deepEqual([osc99.pinned.queryWitness.id, osc99.pinned.queryWitness.expectedEvidence, osc99.pinned.queryWitness.contractMatched], ['q07.extensions.osc99-kitty-notify', 'positive-reply', true]);
        assert.equal(Buffer.from(osc99.pinned.queryWitness.receivedHex, 'hex').toString('latin1'), '\x1b]99;i=ntilde-query-audit:p=?;o=always:p=title,body\x1b\\');
        assert.ok(projected.report.cases.every(item => item.effectContract.preconditionsMet === true && item.effectContract.controlsHeld === true));
        assert.deepEqual(projected.report.focusActions.map(item => item.action), ['refocus', 'defocus', 'refocus']);
        assert.ok(projected.report.focusActions.every(item => item.attempts.length >= 1 && item.appPid > 0));
        assert.deepEqual(projected.summary.stockCollector.counts, { inconclusive: 166, supported: 88, unsupported: 1 });
        assert.equal(schemaVersion, 59); assert.equal(fixtureRemoved, true);
        assert.equal(JSON.parse(readFileSync(join(root, 'context.json'))).ownedQueryPolicy, 'owned-exact-query-paired-raw-state-v3');
    } finally { rmSync(root, { recursive: true, force: true }); }
});
