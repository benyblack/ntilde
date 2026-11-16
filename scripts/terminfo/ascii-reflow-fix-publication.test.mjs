import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { completeOwnedReceipt } from './receipt.mjs';
import { asciiReflowPublication } from './ascii-reflow-publication.mjs';
import { extractOwnedArchive } from '../terminfo-core-results.mjs';

const dir = fileURLToPath(new URL('../../docs/qa/terminfo/2026-11-16-ascii-reflow-fix/', import.meta.url));
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const read = name => readFileSync(join(dir, name));

for (const [stage, archive, effect, displaced] of [
    ['before', 'before-fix.zip', 'unsupported', 38], ['after', 'owned-run.zip', 'supported', 44],
]) {
    test(`the ${stage}-fix live run rederives its complete receipt and exact reflow result`, () => {
        const manifestBytes = read('manifest.json');
        assert.equal(hash(manifestBytes), '932f7afcbb2a27e108702a55a72544093b8c23d4f9d016e00e90f46ae59df3c4');
        const manifest = JSON.parse(manifestBytes);
        for (const [name, digest] of Object.entries(manifest.archives)) assert.equal(hash(read(name)), digest, name);
        const root = mkdtempSync(join(tmpdir(), 'nt-ascii-reflow-fix-'));
        try {
            extractOwnedArchive(dir, { archives: { 'owned-run.zip': manifest.archives[archive] } }, root, join(dir, archive));
            for (const [file, digest] of Object.entries(manifest.sameReflowInputs))
                assert.equal(hash(readFileSync(join(root, 'sources', file))), digest, `unchanged reflow input: ${file}`);
            const original = JSON.parse(readFileSync(join(root, 'cell-receipt.json')));
            const { schemaVersion, ownershipProofSha256, contextSha256, rawSha256, fixtureRemoved, completedAt, ...evidence } = original;
            rmSync(join(root, 'cell-receipt.json'));
            const derived = completeOwnedReceipt(root, join(root, 'removed-fixture'), evidence);
            assert.deepEqual({ ...derived, completedAt }, original);
            const identity = manifest.runs[stage];
            const projected = asciiReflowPublication(root, identity);
            projected.summary.receipt = original;
            assert.equal(projected.summary.ownedRunId, identity.runId);
            assert.deepEqual(projected.summary, JSON.parse(read(`${stage}-summary.json`)));
            assert.deepEqual(projected.report, JSON.parse(read(`${stage}-report.json`)));
            assert.deepEqual(projected.report.cases.map(item => [item.caseId, item.pinned.observation.outcome, item.effectContract.outcome]), [
                ['extensions.reflow', 'inconclusive', effect],
            ]);
            assert.equal(projected.report.cases[0].stock.outcome, 'inconclusive');
            const observed = projected.report.cases[0].observed;
            assert.deepEqual(observed.width.initial.geometry, { rows: 40, cols: 140 });
            assert.deepEqual(observed.width.shrunk.geometry, { rows: 40, cols: 133 });
            for (const value of Object.values(observed.width)) assert.equal(value.matchesModel, true);
            assert.deepEqual(observed.history.shrunk.geometry, { rows: 36, cols: 133 });
            assert.equal(observed.history.shrunk.history, displaced);
            assert.equal(observed.history.shrunk.matchesModel, stage === 'after');
            assert.deepEqual(observed.history.shrunk.cursor, [35, 5]);
            assert.equal(observed.history.initial.matchesModel, true);
            assert.equal(observed.history.restored.matchesModel, true);
            assert.equal(observed.history.restored.history, 22);
            assert.deepEqual(observed.history.restored.cursor, [17, 138]);
            assert.equal(fixtureRemoved, true);
            assert.equal(schemaVersion, identity.receiptSchemaVersion);
        } finally { rmSync(root, { recursive: true, force: true }); }
    });
}

test('the active publication selects only the fresh fixed run and retains the historical negative separately', () => {
    assert.deepEqual(JSON.parse(read('summary.json')), JSON.parse(read('after-summary.json')));
    assert.deepEqual(JSON.parse(read('report.json')), JSON.parse(read('after-report.json')));
    const historical = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-12-ascii-reflow/report.json', import.meta.url)));
    assert.equal(historical.cases[0].effectContract.outcome, 'unsupported');
});

test('retained build identities match both measured App binaries and bind the exact source correction', () => {
    const manifest = JSON.parse(read('manifest.json'));
    const builds = JSON.parse(read('build-identities.json'));
    assert.equal(hash(read('build-identities.json')), manifest.buildIdentitiesSha256);
    for (const stage of ['before', 'after']) {
        const summary = JSON.parse(read(`${stage}-summary.json`));
        assert.equal(summary.appRevision, builds[stage].appRevision);
        assert.equal(summary.context.executableSha256, builds[stage].files.Ntilde);
        assert.equal(summary.context.assemblySha256, builds[stage].files['Ntilde.dll']);
    }
    assert.notEqual(builds.before.files['Ntilde.VT.dll'], builds.after.files['Ntilde.VT.dll']);
    const root = mkdtempSync(join(tmpdir(), 'nt-reflow-fix-source-'));
    try {
        extractOwnedArchive(dir, { archives: { 'owned-run.zip': manifest.archives['production-sources.zip'] } }, root, join(dir, 'production-sources.zip'));
        const before = readFileSync(join(root, 'before/TerminalBuffer.AccessAndSnapshot.cs'), 'utf8');
        const after = readFileSync(join(root, 'after/TerminalBuffer.AccessAndSnapshot.cs'), 'utf8');
        const old = '            row.WrappedColumns = null;\r\n            row.WrittenColumns = 0;';
        assert.equal(before.split(old).length, 2);
        assert.equal(before.replace(old, old.replace('row.WrappedColumns = null;', 'row.IsWrapped = false;')), after);
    } finally { rmSync(root, { recursive: true, force: true }); }
});
