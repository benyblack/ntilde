import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { checkAsciiSgrEvidence, includesAsciiSgrEvidence } from './ascii-sgr-receipt.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
import { gradingAdapterId } from './grading-collector.mjs';
import { asciiSgrSuiteId, asciiSgrContractsVersion } from './ascii-sgr-contracts.mjs';
import { buildAsciiSgrGradingReport } from './ascii-sgr-grading.mjs';
import { syntheticGradingEvidence } from './fixtures/ascii-sgr-evidence.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const runId = 'c'.repeat(32);

function fixture(action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-grading-receipt-'));
    const scratch = join(output, 'removed-fixture');
    const adapterBytes = Buffer.from('// derived grading adapter bytes');
    const evidence = syntheticGradingEvidence(runId, hash(adapterBytes));
    const report = buildAsciiSgrGradingReport(evidence.frames, evidence.run, runId, evidence.adapter);
    const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value) + '\n');
    const context = { runId, gradingAdapterId, asciiSgrSuiteId, gradingBundleSha256: evidence.adapter.bundleSha256 };
    try {
        write('grading-observations.json', report);
        write('grading-run.json', evidence.run);
        writeFileSync(join(output, 'grading-snapshots.jsonl'), evidence.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        writeFileSync(join(output, 'collector-grading.mjs'), adapterBytes);
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

test('the graded receipt binding re-derives the whole report from retained bytes', () => fixture(({ output, evidence, binding }) => {
    assert.equal(includesAsciiSgrEvidence(output, {}, {}), true);
    assert.doesNotThrow(() => checkAsciiSgrEvidence(output, { gradingEvidence: binding }, runId,
        { gradingAdapterId, asciiSgrSuiteId, gradingBundleSha256: evidence.adapter.bundleSha256 }));
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
    ['changed adapter bytes', (f) => writeFileSync(join(f.output, 'collector-grading.mjs'), 'f'.repeat(64))],
    ['changed journal', (f) => writeFileSync(join(f.output, 'grading-snapshots.jsonl'), '\n')],
    ['changed conclusion counts', (f) => { f.binding.counts = { supported: 3 }; }],
    ['dropped run binding digest', (f) => { delete f.binding.runSha256; }],
    ['wrong run identity', (f) => { f.binding.runId = 'd'.repeat(32); }],
    ['missing adapter declaration', (f) => { delete f.context.gradingAdapterId; f.write('context.json', f.context); }],
]) test('graded receipt refuses ' + name, () => fixture(f => {
    mutate(f);
    assert.throws(() => checkAsciiSgrEvidence(f.output, { gradingEvidence: f.binding }, runId, f.context),
        /graded|Graded|Invalid|Missing|bytes|report|journal|counts|identity/i);
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

test('historical v12 receipts stay valid without any grading artifacts', () => {
    // The frozen reset publication still validates byte-for-byte; this batch
    // adds a new suffix rather than rewriting any older binding.
    const publication = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-04-reset-observations.json', import.meta.url)));
    assert.equal(publication.receipt.schemaVersion, 8);
    assert.equal(publication.receipt.gradingEvidence, undefined);
    const older = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-03-metadata.json', import.meta.url)));
    assert.ok(older.receipt.schemaVersion < 13);
    assert.equal(older.receipt.gradingEvidence, undefined);
});
