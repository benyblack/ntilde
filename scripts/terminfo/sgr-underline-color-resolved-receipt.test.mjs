import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { checkUnderlineResolvedEvidence, includesUnderlineResolvedEvidence } from './sgr-underline-color-resolved-receipt.mjs';
import { buildUnderlineResolvedReport } from './sgr-underline-color-resolved-grading.mjs';
import { underlineResolvedSuiteId, underlineResolvedContractsVersion } from './sgr-underline-color-resolved-contracts.mjs';
import { syntheticUnderlineResolvedEvidence, underlineCells } from './fixtures/underline-color-resolved-evidence.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const runId = 'e'.repeat(32);
const files = ['sgur-observations.json', 'sgur-run.json', 'sgur-snapshots.jsonl'];

function fixture(action, options = {}) {
    const output = mkdtempSync(join(tmpdir(), 'nt-sgur-receipt-'));
    try {
        const evidence = syntheticUnderlineResolvedEvidence(runId, options);
        const report = buildUnderlineResolvedReport(evidence.frames, evidence.run, runId);
        const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value) + '\n');
        write('sgur-observations.json', report); write('sgur-run.json', evidence.run);
        writeFileSync(join(output, 'sgur-snapshots.jsonl'), evidence.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        const counts = {};
        for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
        const binding = { runId, suiteId: underlineResolvedSuiteId, file: 'sgur-observations.json',
            sha256: hash(readFileSync(join(output, 'sgur-observations.json'))), runFile: 'sgur-run.json',
            runSha256: hash(readFileSync(join(output, 'sgur-run.json'))), contractsVersion: underlineResolvedContractsVersion,
            counts, provisionalUntil: 'cell-receipt.json' };
        // The pinned underline-color predecessor is re-derived by its own checker in the shared finalizer; here only its presence is required.
        const context = { runId, underlineResolvedSuiteId, underlineResolvedContractsVersion, sgrUnderlineColorAdapterId: 'predecessor' };
        action({ output, report, evidence, binding, context, write, predecessors: { sgrUnderlineColorEvidence: {} } });
    } finally { rmSync(output, { recursive: true, force: true }); }
}
const check = f => checkUnderlineResolvedEvidence(f.output, { underlineResolvedEvidence: f.binding, ...f.predecessors }, runId, f.context);

test('the receipt binding re-derives the whole report from retained bytes', () => fixture(f => {
    assert.equal(includesUnderlineResolvedEvidence(f.output, {}, f.context), true);
    assert.doesNotThrow(() => check(f));
    assert.deepEqual(f.binding.counts, { supported: 3 });
}));

test('conclusive inconclusive verdicts bind and re-derive too', () => fixture(f => {
    assert.deepEqual(f.binding.counts, { inconclusive: 3 });
    assert.doesNotThrow(() => check(f));
}, { cellsOf: contract => underlineCells(contract).map(cell => ({ ...cell, underlineStyle: 0, underlineColorIndex: -1, underlineArgb: null })) }));

for (const [name, mutate] of [
    ['changed observation bytes', f => f.write('sgur-observations.json', { changed: true })],
    ['changed run log bytes', f => f.write('sgur-run.json', { changed: true })],
    ['changed journal', f => writeFileSync(join(f.output, 'sgur-snapshots.jsonl'), '\n')],
    ['changed conclusion counts', f => { f.binding.counts = { supported: 1 }; }],
    ['wrong run identity', f => { f.binding.runId = 'f'.repeat(32); }],
    ['wrong suite', f => { f.binding.suiteId = 'ntilde-owned-truecolor-literal-finding-v1'; }],
    ['wrong contracts version', f => { f.binding.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.underlineResolvedContractsVersion = 2; }],
    ['missing suite declaration', f => { delete f.context.underlineResolvedSuiteId; }],
    ['missing predecessor declaration', f => { delete f.context.sgrUnderlineColorAdapterId; }],
    ['missing predecessor evidence', f => { f.predecessors = {}; }],
    ['dropped provisional marker', f => { delete f.binding.provisionalUntil; }],
    ['artifact removal downgrade', f => { for (const file of files) rmSync(join(f.output, file)); }],
]) test('underline-resolved receipt refuses ' + name, () => fixture(f => {
    mutate(f);
    assert.throws(() => check(f), /underline-resolved|Underline-resolved|Invalid|Missing|bytes|ENOENT/i);
    assert.equal(existsSync(join(f.output, 'cell-receipt.json')), false);
}));

test('policy v16 requires the binding even without artifacts; earlier policies do not', () => fixture(f => {
    for (const file of files) rmSync(join(f.output, file));
    assert.equal(includesUnderlineResolvedEvidence(f.output, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v16' }), true);
    assert.throws(() => checkUnderlineResolvedEvidence(f.output, {}, runId, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v16' }),
        /Missing or changed underline-resolved suite declaration/);
    for (const policy of ['readonly-owned-source-graph-v14', 'readonly-owned-source-graph-v15'])
        assert.equal(includesUnderlineResolvedEvidence('/nonexistent', {}, { sourceSnapshotPolicy: policy }), false);
    assert.equal(includesUnderlineResolvedEvidence('/nonexistent', {}, { underlineResolvedContractsVersion: 1 }), true);
}));

test('the frozen underline-color publication stays valid without any resolved-underline artifacts', () => {
    const publication = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-underline-color/summary.json', import.meta.url)));
    assert.equal(publication.receipt.schemaVersion, 21);
    assert.equal(publication.receipt.underlineResolvedEvidence, undefined);
    assert.equal(includesUnderlineResolvedEvidence('/nonexistent', {}, publication.context), false);
});
