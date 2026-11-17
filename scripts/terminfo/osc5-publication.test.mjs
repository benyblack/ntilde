import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { osc5Publication } from './osc5-publication.mjs';
import { buildOsc5GradingReport } from './osc5-grading.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const publications = [
    { directory: '2026-11-17-osc5-special-colors-stack-isolation', manifestHash: 'cace6fd43bb0ca338022b161d8c7b5152d43555cf7f98786bbbd7c252b6e5635', revision: 'dc930315fa4bc7133d676ddc577b609d9c4f528e', schema: 71, policy: 64 },
    { directory: '2026-11-17-osc5-special-colors', manifestHash: '7991c411846088023ff0e08d870a63fe846aee73344da2f4cd62140f48ebbc84', revision: '9c922a4a3857fb109ba1ef7978a35ae013a8e63f', schema: 70, policy: 63 },
    { directory: '2026-11-17-osc5-special-colors-reviewed', manifestHash: 'ba2da13503cc0ad3479f7be4f959443593268e1ce12ad1cbbbd26efffcce75ea', revision: 'e52b99c9ac2624bb071d8079ef2195a183ce4fa4', schema: 71, policy: 64 },
];
for (const publication of publications) {
    const dir = fileURLToPath(new URL(`../../docs/qa/terminfo/${publication.directory}/`, import.meta.url));
    const read = name => readFileSync(join(dir, name));
    const revisions = { appRevision: publication.revision, scriptsRevision: publication.revision };
test(`${publication.directory} independently rederives the complete receipt and published projection`, () => {
    const manifestBytes = read('manifest.json');
    assert.equal(hash(manifestBytes), publication.manifestHash);
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
        // Every projected file must remain bound to this receipt; the stock and owned run IDs legitimately differ.
        for (const [file, mutate] of [
            ['context.json', value => { value.runId = 'b'.repeat(32); }],
            ['raw.json', value => { value.runId = 'b'.repeat(32); }],
            ['osc5-observations.json', value => { value.runId = 'b'.repeat(32); }],
            ['query-observations.json', value => { value.runId = 'b'.repeat(32); }],
            ['summary.json', value => { value.counts.supported++; }],
            ['summary.json', value => { value.runId = 'b'.repeat(32); }],
            ['cell-receipt.json', value => { value.rawSha256 = '0'.repeat(64); }],
        ]) {
            const path = join(root, file), bytes = readFileSync(path), value = JSON.parse(bytes);
            mutate(value); writeFileSync(path, JSON.stringify(value));
            assert.throws(() => osc5Publication(root, revisions), /publication run or receipt binding/, file);
            writeFileSync(path, bytes);
        }
        const projected = osc5Publication(root, revisions);
        // A newly finalized receipt has a new timestamp; publication preserves the original receipt byte-for-byte.
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, JSON.parse(read('summary.json')));
        assert.deepEqual(projected.report, JSON.parse(read('report.json')));
        assert.equal(schemaVersion, publication.schema); assert.equal(fixtureRemoved, true);
        const context = JSON.parse(readFileSync(join(root, 'context.json')));
        assert.equal(context.ownedQueryPolicy, 'owned-exact-query-paired-raw-state-v6');
        assert.equal(context.sourceSnapshotPolicy, `readonly-owned-source-graph-v${publication.policy}`);
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
}

test('the failed native-render-timer startup is retained without a grade', () => {
    const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-17-osc5-special-colors-stack-isolation/', import.meta.url));
    const manifest = JSON.parse(readFileSync(join(dir, 'manifest.json')));
    assert.equal(manifest.attempts.length, 1);
    const [attempt] = manifest.attempts;
    assert.equal(attempt.status, 'refused-before-collection');
    assert.equal(hash(readFileSync(join(dir, attempt.file))), attempt.sha256);
    const root = mkdtempSync(join(tmpdir(), 'nt-osc5-refused-'));
    try {
        extractOwnedArchive(dir, { archives: { 'owned-run.zip': attempt.sha256 } }, root, join(dir, attempt.file));
        assert.equal(JSON.parse(readFileSync(join(root, 'context.json'))).runId, attempt.ownedRunId);
        assert.equal(JSON.parse(readFileSync(join(root, 'failure.json'))).error, attempt.error);
        const proof = JSON.parse(readFileSync(join(root, 'ownership-proof.json')));
        assert.equal(proof.completed, false);
        assert.ok(proof.cleanedUpAt);
        assert.match(readFileSync(join(root, 'app-output.log'), 'utf8'), /not able to start the RenderTimer.*-6661/);
        assert.equal(readFileSync(join(root, 'cell-snapshots.jsonl')).length, 0);
        assert.equal(existsSync(join(root, 'cell-receipt.json')), false);
        assert.equal(existsSync(join(root, 'summary.json')), false);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
