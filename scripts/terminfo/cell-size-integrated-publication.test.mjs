import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { cellSizePublication } from './cell-size-publication.mjs';
import { buildCellSizeReport } from './cell-size-grading.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-17-osc-cell-size-integrated/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const bytes = name => readFileSync(join(dir, name));
const json = (root, name) => JSON.parse(readFileSync(join(root, name)));
const manifest = () => {
    const data = bytes('manifest.json');
    assert.equal(hash(data), 'e66305b7cbb0330516fbba1745a1da88e27890d1b8f0b5195f3e4a27e9abee62');
    return JSON.parse(data);
};

test('retained OSC cell-size run independently rederives its complete receipt, paired report and publication', () => {
    const mapping = manifest(), root = mkdtempSync(join(tmpdir(), 'nt-cell-size-publication-'));
    try {
        assert.equal(Object.keys(mapping.archives).length, 1);
        for (const [name, digest] of Object.entries(mapping.archives)) assert.equal(hash(bytes(name)), digest, name);
        assert.equal(hash(bytes('build-identities.json')), mapping.buildIdentitiesSha256);
        extractOwnedArchive(dir, mapping, root, join(dir, 'owned-run.zip'));
        const original = json(root, 'cell-receipt.json'), context = json(root, 'context.json'), proof = json(root, 'ownership-proof.json');
        assert.equal(original.schemaVersion, 71);
        assert.equal(original.fixtureRemoved, true);
        assert.equal(original.animationEvidence.suiteId, 'ntilde-owned-kitty-animation-v1');
        assert.equal(Object.keys(json(root, 'source-snapshot.json').hashes).length, 432);
        assert.equal(proof.completed, true);
        assert.ok(Date.parse(original.completedAt) >= Date.parse(proof.cleanedUpAt));
        assert.equal(context.runId, mapping.measurement.runId);
        assert.equal(context.sourceSnapshotPolicy, 'readonly-owned-source-graph-v64');
        const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
        rmSync(join(root, 'cell-receipt.json'));
        const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
        assert.deepEqual({ ...derived, completedAt }, original);
        const retained = readFileSync(join(root, 'cell-size-observations.json'));
        const report = JSON.parse(retained);
        const frames = readFileSync(join(root, 'cell-size-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
        const rebuilt = buildCellSizeReport(frames, json(root, 'cell-size-run.json'), context.runId, report.adapter);
        assert.equal(Buffer.compare(Buffer.from(JSON.stringify(rebuilt, null, 2) + '\n'), retained), 0);
        assert.equal(frames.length, 22);
        assert.equal(report.cases.reduce((count, item) => count + item.observed.exchanges.length, 0), 19);
        const projected = cellSizePublication(root, mapping.measurement);
        projected.summary.receipt = original;
        assert.deepEqual(projected.summary, json(dir, 'summary.json'));
        assert.deepEqual(projected.report, json(dir, 'report.json'));
        assert.deepEqual(projected.report.cases.map(item => [item.caseId, item.pinned.termless.observation.outcome,
            item.pinned.terminal.observation.outcome, item.effectContract.outcome, item.stock.outcome, item.stock.reason]), [
            ['extensions.osc1337-cellsize', 'inconclusive', 'inconclusive', 'supported', 'inconclusive', 'invalid-reply'],
            ['extensions.osc776-cell-size', 'inconclusive', 'inconclusive', 'supported', 'inconclusive', 'no-response'],
        ]);
        for (const item of report.cases) {
            assert.ok(item.effectContract.validity.every(control => control.held));
            assert.ok(item.effectContract.required.every(control => control.held));
            assert.ok(item.observed.exchanges.every(exchange => exchange.matches));
        }
        assert.deepEqual(frames[0].geometry, { viewRows: 40, viewCols: 140, cellWidthDip: 8.5, cellHeightDip: 18.5,
            fontAscentDip: 14.280059814453125, renderScaling: 2, backingScale: 2 });
        const exchange = (index, id) => report.cases[index].observed.exchanges.find(item => item.id === id).report.replies;
        assert.deepEqual(exchange(0, 'documented-bel'), ['\x1b]1337;ReportCellSize=18.5;8.5;2\x1b\\']);
        assert.deepEqual(exchange(1, 'documented-bel'), ['\x1b]776;17;37;29\x07']);
        assert.deepEqual(exchange(1, 'documented-st'), ['\x1b]776;17;37;29\x1b\\']);
        const identities = json(dir, 'build-identities.json');
        assert.equal(identities.appRevision, mapping.measurement.appRevision);
        assert.equal(identities.scriptsRevision, mapping.measurement.scriptsRevision);
        assert.equal(context.executableSha256, identities.files.Ntilde);
        assert.equal(context.assemblySha256, identities.files['Ntilde.dll']);
        assert.equal(hash(readFileSync(join(root, 'sources/clipboard-pane-session.cs'))), context.privateClipboardInput8SourceSha256);
        assert.equal(hash(readFileSync(join(root, 'sources/core-icon-window.cs'))), context.coreIconWindowSourceSha256);
        assert.deepEqual(projected.summary.stockCollector.counts, { inconclusive: 161, supported: 93, unsupported: 1 });
        assert.equal(context.collectionScope.id, 'owned-osc633-without-notification-suites-v4');
        assert.deepEqual(context.collectionScope.omittedSuites, ['osc9', 'notify']);
    } finally { rmSync(root, { recursive: true, force: true }); }
});

