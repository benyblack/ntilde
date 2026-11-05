import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { checkAsciiSgrEvidence, includesAsciiSgrEvidence } from './ascii-sgr-receipt.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
import { adaptGradingCollector, gradingAdapterId } from './grading-collector.mjs';
import { asciiSgrSuiteId, asciiSgrContractsVersion } from './ascii-sgr-contracts.mjs';
import { buildAsciiSgrGradingReport } from './ascii-sgr-grading.mjs';
import { syntheticGradingEvidence } from './fixtures/ascii-sgr-evidence.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const runId = 'c'.repeat(32);
// A synthetic owned adapter carrying every derivation seam; the receipt check
// re-derives the grading extension from exactly these bytes.
const syntheticSeams = [
    'probe("text.basic", (ctx) => {\n        ctx.feed("\\x1B[2J\\x1B[H");',
    '"Hello appears after a measured empty control"',
    'probe("text.overwrite", (ctx) => {\n        ctx.feed("AB");',
    '"C overwrites A while B remains"',
    'sgrProbe("sgr.bold", "\\x1B[1m", (cell) => cell.bold === true)',
    'sgrProbe("sgr.italic", "\\x1B[3m", (cell) => cell.italic === true)',
    'var init_src = __esm(() => {',
];
const syntheticOwnedAdapter = syntheticSeams.map((seam, index) => `const seam${index} = \`${seam}\`;`).join('\n')
    + '\nexport async function collectNtildeRun(assertion) {\n'
    + '  return collectProbeRun({ ntildeOwner: assertion, out: process.stdout });\n}\n';

function fixture(action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-grading-receipt-'));
    const scratch = join(output, 'removed-fixture');
    const ownedAdapterBytes = Buffer.from(syntheticOwnedAdapter);
    const adapterBytes = Buffer.from(adaptGradingCollector(ownedAdapterBytes));
    const evidence = syntheticGradingEvidence(runId, hash(adapterBytes));
    const report = buildAsciiSgrGradingReport(evidence.frames, evidence.run, runId, evidence.adapter);
    const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value) + '\n');
    const context = { runId, gradingAdapterId, asciiSgrSuiteId, gradingBundleSha256: evidence.adapter.bundleSha256,
        gradingContractsVersion: asciiSgrContractsVersion, upstreamBundleSha256: evidence.adapter.upstreamBundleSha256 };
    try {
        write('grading-observations.json', report);
        write('grading-run.json', evidence.run);
        writeFileSync(join(output, 'grading-snapshots.jsonl'), evidence.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        writeFileSync(join(output, 'collector-grading.mjs'), adapterBytes);
        writeFileSync(join(output, 'collector-adapter.mjs'), ownedAdapterBytes);
        write('ownership-proof.json', { completed: true, cleanedUpAt: '2026-11-05T00:00:00Z', cleanupError: null });
        write('context.json', context);
        write('raw.json', { observations: [] });
        const binding = { runId, suiteId: asciiSgrSuiteId, file: 'grading-observations.json',
            sha256: hash(readFileSync(join(output, 'grading-observations.json'))),
            runFile: 'grading-run.json', runSha256: hash(readFileSync(join(output, 'grading-run.json'))),
            adapterBundleSha256: evidence.adapter.bundleSha256, contractsVersion: asciiSgrContractsVersion,
            counts: { supported: 4 }, provisionalUntil: 'cell-receipt.json' };
        action({ output, scratch, evidence, report, context, binding, write, adapterBytes });
    } finally { rmSync(output, { recursive: true, force: true }); }
}

test('the graded receipt binding re-derives the whole report from retained bytes', () => fixture(({ output, evidence, binding, context }) => {
    assert.equal(includesAsciiSgrEvidence(output, {}, {}), true);
    assert.doesNotThrow(() => checkAsciiSgrEvidence(output, { gradingEvidence: binding }, runId, context));
}));

test('the shared finalizer publishes receipt schema 13 only with complete graded evidence', () => fixture(({ output, scratch, binding, write }) => {
    for (const prefix of ['cell', 'sgr', 'rgb', 'rendition', 'cursor']) write(`${prefix}-observations.json`, { runId });
    const entry = file => ({ runId, suiteId: `independent-${file}-fixture`, file,
        sha256: hash(readFileSync(join(output, file))), counts: {} });
    const evidence = { ...entry('cell-observations.json'), sgrEvidence: entry('sgr-observations.json'),
        rgbEvidence: entry('rgb-observations.json'), renditionEvidence: entry('rendition-observations.json'),
        cursorEvidence: entry('cursor-observations.json'), gradingEvidence: binding };
    const receipt = completeOwnedReceipt(output, scratch, evidence);
    assert.equal(receipt.schemaVersion, 13);
    assert.equal(receipt.gradingEvidence, binding);
}));

for (const [name, mutate] of [
    ['changed observation bytes', (f) => f.write('grading-observations.json', { changed: true })],
    ['changed run log bytes', (f) => f.write('grading-run.json', { changed: true })],
    ['changed adapter bytes', (f) => writeFileSync(join(f.output, 'collector-grading.mjs'), Buffer.from('tampered adapter'))],
    ['foreign adapter derivation', (f) => writeFileSync(join(f.output, 'collector-adapter.mjs'), Buffer.from('not the owned adapter'))],
    ['changed journal', (f) => writeFileSync(join(f.output, 'grading-snapshots.jsonl'), '\n')],
    ['changed conclusion counts', (f) => { f.binding.counts = { supported: 3 }; }],
    ['dropped run binding digest', (f) => { delete f.binding.runSha256; }],
    ['wrong run identity', (f) => { f.binding.runId = 'd'.repeat(32); }],
    ['wrong contracts version', (f) => { f.binding.contractsVersion = 2; }],
    ['declared contracts drift', (f) => { f.context.gradingContractsVersion = 2; f.write('context.json', f.context); }],
    ['missing adapter declaration', (f) => { delete f.context.gradingAdapterId; f.write('context.json', f.context); }],
    ['report adapter digest drift', (f) => {
        f.report.adapter.bundleSha256 = 'a'.repeat(64);
        f.write('grading-observations.json', f.report);
        f.binding.sha256 = hash(readFileSync(join(f.output, 'grading-observations.json')));
    }],
]) test('graded receipt refuses ' + name, () => fixture(f => {
    mutate(f);
    assert.throws(() => checkAsciiSgrEvidence(f.output, { gradingEvidence: f.binding }, runId, f.context),
        /graded|Graded|Invalid|Missing|bytes|report|journal|counts|identity|derivation|contracts|differs/i);
    assert.equal(existsSync(join(f.output, 'cell-receipt.json')), false);
}));

test('retained sources alone never imply grading evidence', () => fixture(({ output }) => {
    const sources = join(output, 'sources');
    mkdirSync(sources);
    writeFileSync(join(sources, 'grading-collector.mjs'), '// retained');
    for (const file of ['grading-observations.json', 'grading-run.json', 'grading-snapshots.jsonl', 'collector-grading.mjs'])
        rmSync(join(output, file));
    assert.equal(includesAsciiSgrEvidence(output, {}, {}), false);
    assert.equal(includesAsciiSgrEvidence(output, {}, { gradingAdapterId }), true);
    rmSync(sources, { recursive: true });
    assert.equal(includesAsciiSgrEvidence(output, { gradingEvidence: {} }, {}), true);
}));

test('historical v12 receipts stay valid without any grading artifacts', async () => {
    // The frozen reset publication still validates byte-for-byte; this batch
    // adds a new suffix rather than rewriting any older binding.
    const publication = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-04-reset-observations.json', import.meta.url)));
    assert.equal(publication.receipt.schemaVersion, 8);
    assert.equal(publication.receipt.gradingEvidence, undefined);
    const older = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-03-metadata.json', import.meta.url)));
    assert.ok(older.receipt.schemaVersion < 13);
    assert.equal(older.receipt.gradingEvidence, undefined);
    // The grown v13 inventory carries its own policy; a v12-era run (policy v5,
    // 103 sources) still resolves to its own frozen file set.
    const { retainedInventory } = await import('./metadata-receipt.mjs');
    const { capturedV12SourceFiles, capturedV13SourceFiles, sourceSnapshotPolicy } = await import('./owned-sources.mjs');
    assert.equal(sourceSnapshotPolicy, 'readonly-owned-source-graph-v6');
    assert.equal(retainedInventory(null, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v5' }), capturedV12SourceFiles);
    assert.equal(retainedInventory(null, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v6' }), capturedV13SourceFiles);
    assert.equal(Object.keys(capturedV12SourceFiles).length, 103);
    assert.equal(Object.keys(capturedV13SourceFiles).length, 108);
});
