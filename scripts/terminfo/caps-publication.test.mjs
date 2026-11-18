import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { capsPublication, measuredAppRevision, measuredScriptsRevision } from './caps-publication.mjs';
import { buildCapsGradingReport } from './caps-grading.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const publication = { directory: '2026-11-17-osc1337-capabilities', manifestHash: '9f0aa9849233ae9f84dce53c8c72aecbb45d542bc68feac04b087f372f8900d6', revision: measuredScriptsRevision, schema: 77, policy: 70 };

test(`${publication.directory} independently rederives the complete receipt and published projection`, () => {
    const dir = fileURLToPath(new URL(`../../docs/qa/terminfo/${publication.directory}/`, import.meta.url));
    const read = name => readFileSync(join(dir, name));
    const revisions = { appRevision: publication.revision, scriptsRevision: publication.revision };
    const manifestBytes = read('manifest.json');
    assert.equal(hash(manifestBytes), publication.manifestHash);
    const manifest = JSON.parse(manifestBytes);
    assert.equal(Object.keys(manifest.archives).length, 1, 'the single final run; the three superseded attempts are retained in the qualification page, not here');
    for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
    const root = mkdtempSync(join(tmpdir(), 'nt-caps-publication-'));
    try {
        extractOwnedArchive(dir, manifest, root, join(dir, 'owned-run.zip'));
        const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        // The retained report is rebuilt from the retained paired journal and worker run log, byte for byte.
        const frames = readFileSync(join(root, 'caps-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line)), retained = readFileSync(join(root, 'caps-observations.json'));
        const rebuilt = buildCapsGradingReport(frames, JSON.parse(readFileSync(join(root, 'caps-run.json'))), JSON.parse(retained).runId, JSON.parse(retained).adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), retained), 0);
        // Every projected file must remain bound to this receipt; the stock and owned run IDs legitimately differ.
        for (const [file, mutate] of [
            ['context.json', value => { value.runId = 'b'.repeat(32); }],
            ['raw.json', value => { value.runId = 'b'.repeat(32); }],
            ['caps-observations.json', value => { value.runId = 'b'.repeat(32); }],
            ['query-observations.json', value => { value.runId = 'b'.repeat(32); }],
            ['summary.json', value => { value.counts.supported++; }],
            ['summary.json', value => { value.runId = 'b'.repeat(32); }],
            ['cell-receipt.json', value => { value.rawSha256 = '0'.repeat(64); }],
        ]) {
            const path = join(root, file), bytes = readFileSync(path), value = JSON.parse(bytes);
            mutate(value); writeFileSync(path, JSON.stringify(value));
            assert.throws(() => capsPublication(root, revisions), /publication run or receipt binding/, file);
            writeFileSync(path, bytes);
        }
        const projected = capsPublication(root, revisions);
        // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.equal(schemaVersion, publication.schema); assert.equal(fixtureRemoved, true);
        const context = JSON.parse(readFileSync(join(root, 'context.json')));
        assert.equal(context.ownedQueryPolicy, 'owned-exact-query-paired-raw-state-v7');
        assert.equal(context.sourceSnapshotPolicy, `readonly-owned-source-graph-v${publication.policy}`);
        // Both pinned paths replay the unchanged pinned code over the journaled reply of their own exchange; the stock collector and the exact-query witness agree.
        const [item] = projected.report.cases;
        assert.deepEqual([item.caseId, item.pinned.termless.observation.outcome, item.pinned.terminal.observation.outcome, item.effectContract.outcome, item.stock.outcome],
            ['extensions.osc1337-capabilities', 'supported', 'supported', 'supported', 'supported']);
        assert.equal(item.divergence, undefined);
        // The exact-query witness (query policy v7) demands the declared string byte for byte: the direct pane's
        // host accepts OSC 52 writes under the run's default initial settings, so the reply includes Cw.
        assert.deepEqual(item.queryWitness, { id: 'q07.extensions.osc1337-capabilities', expectedEvidence: 'positive-reply',
            receivedHex: item.queryWitness.receivedHex, contractMatched: true });
        assert.equal(Buffer.from(item.queryWitness.receivedHex, 'hex').toString('latin1'), '\x1b]1337;Capabilities=T3CwLrMSc7UTs3BFGsGoSyHNoP\x1b\\');
        const groups = Object.entries(item.observed.groups);
        assert.deepEqual(Object.fromEntries(['protocol', 'policy'].map(kind => [kind, groups.filter(([, group]) => group.kind === kind).length])), { protocol: 4, policy: 4 });
        // Every protocol group matched its demand and left the screen unchanged; every policy group stayed silent as recorded.
        assert.equal(groups.every(([, group]) => group.unchanged), true);
        assert.equal(groups.every(([, group]) => group.matches), true);
        assert.deepEqual(projected.summary.stockCollector.counts, { inconclusive: 159, supported: 95, unsupported: 1 });
        assert.equal(measuredScriptsRevision, measuredAppRevision);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
