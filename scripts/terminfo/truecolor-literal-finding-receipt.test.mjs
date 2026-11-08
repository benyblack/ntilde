import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { checkTruecolorFindingEvidence, includesTruecolorFindingEvidence } from './truecolor-literal-finding-receipt.mjs';
import { buildTruecolorFindingReport } from './truecolor-literal-finding-grading.mjs';
import { truecolorFindingSuiteId, truecolorFindingContractsVersion } from './truecolor-literal-finding-contracts.mjs';
import { syntheticTruecolorFindingEvidence, findingCells } from './fixtures/truecolor-literal-finding-evidence.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const runId = 'e'.repeat(32);

function fixture(action, options = {}) {
    const output = mkdtempSync(join(tmpdir(), 'nt-sgtf-receipt-'));
    try {
        const evidence = syntheticTruecolorFindingEvidence(runId, options);
        const report = buildTruecolorFindingReport(evidence.frames, evidence.run, runId);
        const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value) + '\n');
        write('sgtf-observations.json', report); write('sgtf-run.json', evidence.run);
        writeFileSync(join(output, 'sgtf-snapshots.jsonl'), evidence.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        const counts = {};
        for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
        const binding = { runId, suiteId: truecolorFindingSuiteId, file: 'sgtf-observations.json',
            sha256: hash(readFileSync(join(output, 'sgtf-observations.json'))), runFile: 'sgtf-run.json',
            runSha256: hash(readFileSync(join(output, 'sgtf-run.json'))), contractsVersion: truecolorFindingContractsVersion,
            counts, provisionalUntil: 'cell-receipt.json' };
        // The truecolor predecessor is re-derived by its own checker in the shared finalizer; here only its presence is required.
        const context = { runId, truecolorFindingSuiteId, truecolorFindingContractsVersion, sgrTruecolorAdapterId: 'predecessor' };
        action({ output, report, evidence, binding, context, write, predecessors: { sgrTruecolorEvidence: {} } });
    } finally { rmSync(output, { recursive: true, force: true }); }
}
const check = f => checkTruecolorFindingEvidence(f.output, { truecolorFindingEvidence: f.binding, ...f.predecessors }, runId, f.context);

test('the finding receipt binding re-derives the whole report from retained bytes', () => fixture(f => {
    assert.equal(includesTruecolorFindingEvidence(f.output, {}, f.context), true);
    assert.doesNotThrow(() => check(f));
    assert.deepEqual(f.binding.counts, { supported: 10 });
}));

test('conclusive unsupported findings (the pre-fix run) bind and re-derive too', () => fixture(f => {
    assert.deepEqual(f.binding.counts, { unsupported: 10 });
    assert.doesNotThrow(() => check(f));
}, { cellsOf: contract => findingCells(contract, { aliased: true }) }));

for (const [name, mutate] of [
    ['changed observation bytes', f => f.write('sgtf-observations.json', { changed: true })],
    ['changed run log bytes', f => f.write('sgtf-run.json', { changed: true })],
    ['changed journal', f => writeFileSync(join(f.output, 'sgtf-snapshots.jsonl'), '\n')],
    ['changed conclusion counts', f => { f.binding.counts = { supported: 1 }; }],
    ['wrong run identity', f => { f.binding.runId = 'f'.repeat(32); }],
    ['wrong suite', f => { f.binding.suiteId = 'ntilde-owned-sgr-truecolor-graded-v1'; }],
    ['wrong contracts version', f => { f.binding.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.truecolorFindingContractsVersion = 2; }],
    ['missing suite declaration', f => { delete f.context.truecolorFindingSuiteId; }],
    ['missing predecessor declaration', f => { delete f.context.sgrTruecolorAdapterId; }],
    ['missing predecessor evidence', f => { f.predecessors = {}; }],
    ['dropped provisional marker', f => { delete f.binding.provisionalUntil; }],
    ['artifact removal downgrade', f => { for (const file of ['sgtf-observations.json', 'sgtf-run.json', 'sgtf-snapshots.jsonl']) rmSync(join(f.output, file)); }],
]) test('finding receipt refuses ' + name, () => fixture(f => {
    mutate(f);
    assert.throws(() => check(f), /truecolor-finding|Truecolor-finding|Invalid|Missing|bytes|ENOENT/i);
    assert.equal(existsSync(join(f.output, 'cell-receipt.json')), false);
}));

test('policy v13 requires the finding binding even without artifacts; earlier policies do not', () => fixture(f => {
    for (const file of ['sgtf-observations.json', 'sgtf-run.json', 'sgtf-snapshots.jsonl']) rmSync(join(f.output, file));
    assert.equal(includesTruecolorFindingEvidence(f.output, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v13' }), true);
    assert.throws(() => checkTruecolorFindingEvidence(f.output, {}, runId, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v13' }),
        /Missing or changed truecolor-finding suite declaration/);
    for (const policy of ['readonly-owned-source-graph-v11', 'readonly-owned-source-graph-v12'])
        assert.equal(includesTruecolorFindingEvidence('/nonexistent', {}, { sourceSnapshotPolicy: policy }), false);
    assert.equal(includesTruecolorFindingEvidence('/nonexistent', {}, { truecolorFindingContractsVersion: 1 }), true);
}));

test('historical receipts stay valid without any finding artifacts', () => {
    const publication = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-truecolor/summary.json', import.meta.url)));
    assert.equal(publication.receipt.schemaVersion, 18);
    assert.equal(publication.receipt.truecolorFindingEvidence, undefined);
    assert.equal(includesTruecolorFindingEvidence('/nonexistent', {}, publication.context), false);
});
