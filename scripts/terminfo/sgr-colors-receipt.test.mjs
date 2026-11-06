import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { checkSgrColorsEvidence, includesSgrColorsEvidence } from './sgr-colors-receipt.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
import { adaptSgrColorsCollector, sgrColorsAdapterId } from './sgr-colors-grading-collector.mjs';
import { adaptGradingCollector } from './grading-collector.mjs';
import { sgrColorsSuiteId, sgrColorsContractsVersion } from './sgr-colors-contracts.mjs';
import { buildSgrColorsGradingReport } from './sgr-colors-grading.mjs';
import { syntheticSgrColorsEvidence } from './fixtures/sgr-colors-evidence.mjs';
import { syntheticGradingEvidence } from './fixtures/ascii-sgr-evidence.mjs';
import { buildAsciiSgrGradingReport } from './ascii-sgr-grading.mjs';
import { asciiSgrSuiteId, asciiSgrContractsVersion } from './ascii-sgr-contracts.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const runId = 'e'.repeat(32);
// Deterministic synthetic adapters: the full three-layer derivation chain
// (owned adapter -> first grading extension -> SGR-colors extension) over a
// synthetic source that carries every seam of both layers.
const firstSeams = [
    'probe("text.basic", (ctx) => {\n        ctx.feed("\\x1B[2J\\x1B[H");',
    '"Hello appears after a measured empty control"',
    'probe("text.overwrite", (ctx) => {\n        ctx.feed("AB");',
    '"C overwrites A while B remains"',
    'sgrProbe("sgr.bold", "\\x1B[1m", (cell) => cell.bold === true)',
    'sgrProbe("sgr.italic", "\\x1B[3m", (cell) => cell.italic === true)',
    'var init_src = __esm(() => {',
];
const colorSeams = [
    'namedColorProbe("sgr.fg.standard", "fg", 31, 34)',
    'namedColorProbe("sgr.bg.standard", "bg", 41, 44)',
    'resetProbe("sgr.selective-reset.bold", "\\x1B[1;2;3m", "\\x1B[22m", ["bold", "dim"], ["italic"])',
    'resetProbe("sgr.selective-reset.italic", "\\x1B[1;3m", "\\x1B[23m", ["italic"], ["bold"])',
    'resetProbe("sgr.reset", "\\x1B[1;3;4m", "\\x1B[0m", ["bold", "italic", "underline"])',
    'function measuredReset(ctx, id, setup, reset, set, preserve) {',
    'function namedColorProbe(id, channel, firstCode, secondCode) {',
    'the two named colors select distinct exposed ',
    'measured setup attributes clear on Y and unrelated attributes remain set',
];
const template = lines => lines.map((seam, index) => `const seam${index} = \`${seam}\`;`).join('\n');
const syntheticOwnedAdapter = template([...firstSeams, ...colorSeams])
    + '\nexport async function collectNtildeRun(assertion) {\n  return collectProbeRun({ ntildeOwner: assertion });\n}\n';
const syntheticFirstExtension = adaptGradingCollector(syntheticOwnedAdapter);

function fixture(action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-sgrcolors-receipt-'));
    const scratch = join(output, 'removed-fixture');
    const ownedAdapterBytes = Buffer.from(syntheticOwnedAdapter);
    const firstExtensionBytes = Buffer.from(syntheticFirstExtension);
    const sgrColorsBytes = Buffer.from(adaptSgrColorsCollector(syntheticFirstExtension));
    const grading = syntheticGradingEvidence(runId, hash(firstExtensionBytes));
    grading.adapter = { id: 'ntilde-owned-ascii-sgr-grading-v1', derivedFromAdapterId: 'ntilde-owned-macos-v1',
        bundleSha256: hash(firstExtensionBytes), upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    const gradingReport = buildAsciiSgrGradingReport(grading.frames, grading.run, grading.runId, grading.adapter);
    const evidence = syntheticSgrColorsEvidence(runId, hash(sgrColorsBytes));
    const report = buildSgrColorsGradingReport(evidence.frames, evidence.run, runId, evidence.adapter);
    const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value) + '\n');
    const context = { runId, sourceSnapshotPolicy: 'readonly-owned-source-graph-v7',
        gradingAdapterId: 'ntilde-owned-ascii-sgr-grading-v1', asciiSgrSuiteId,
        gradingBundleSha256: hash(firstExtensionBytes), gradingContractsVersion: asciiSgrContractsVersion,
        sgrColorsAdapterId, sgrColorsSuiteId, sgrColorsBundleSha256: hash(sgrColorsBytes),
        sgrColorsContractsVersion, upstreamBundleSha256: evidence.adapter.upstreamBundleSha256 };
    try {
        write('grading-observations.json', gradingReport);
        write('grading-run.json', grading.run);
        writeFileSync(join(output, 'grading-snapshots.jsonl'), grading.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        writeFileSync(join(output, 'collector-adapter.mjs'), ownedAdapterBytes);
        writeFileSync(join(output, 'collector-grading.mjs'), firstExtensionBytes);
        write('sgcolors-observations.json', report);
        write('sgcolors-run.json', evidence.run);
        writeFileSync(join(output, 'sgcolors-snapshots.jsonl'), evidence.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        writeFileSync(join(output, 'collector-sgcolors.mjs'), sgrColorsBytes);
        write('ownership-proof.json', { completed: true, cleanedUpAt: '2026-11-06T00:00:00Z', cleanupError: null });
        write('context.json', context);
        write('raw.json', { observations: [] });
        const binding = { runId, suiteId: sgrColorsSuiteId, file: 'sgcolors-observations.json',
            sha256: hash(readFileSync(join(output, 'sgcolors-observations.json'))),
            runFile: 'sgcolors-run.json', runSha256: hash(readFileSync(join(output, 'sgcolors-run.json'))),
            adapterBundleSha256: hash(sgrColorsBytes), contractsVersion: sgrColorsContractsVersion,
            counts: { supported: 5 }, provisionalUntil: 'cell-receipt.json' };
        const gradingBinding = { runId, suiteId: gradingReport.suiteId, file: 'grading-observations.json',
            sha256: hash(readFileSync(join(output, 'grading-observations.json'))),
            runFile: 'grading-run.json', runSha256: hash(readFileSync(join(output, 'grading-run.json'))),
            adapterBundleSha256: hash(firstExtensionBytes), contractsVersion: gradingReport.contractsVersion,
            counts: { supported: 4 }, provisionalUntil: 'cell-receipt.json' };
        action({ output, scratch, evidence, report, context, binding, gradingBinding, write, sgrColorsBytes, firstExtensionBytes });
    } finally { rmSync(output, { recursive: true, force: true }); }
}

test('the SGR-colors receipt binding re-derives the whole report from retained bytes', () => fixture(({ output, binding, context }) => {
    assert.equal(includesSgrColorsEvidence(output, {}, context), true);
    assert.doesNotThrow(() => checkSgrColorsEvidence(output, { sgrColorsEvidence: binding }, runId, context));
}));

test('the shared finalizer publishes receipt schema 14 with both grading suites', () => fixture(({ output, scratch, binding, gradingBinding, write, context }) => {
    // A policy-free context: the private-clipboard suffix is not under test here.
    const lightContext = { runId, gradingAdapterId: context.gradingAdapterId, asciiSgrSuiteId: context.asciiSgrSuiteId,
        gradingBundleSha256: context.gradingBundleSha256, gradingContractsVersion: context.gradingContractsVersion,
        sgrColorsAdapterId: context.sgrColorsAdapterId, sgrColorsSuiteId: context.sgrColorsSuiteId,
        sgrColorsBundleSha256: context.sgrColorsBundleSha256, sgrColorsContractsVersion: context.sgrColorsContractsVersion,
        upstreamBundleSha256: context.upstreamBundleSha256 };
    write('context.json', lightContext);
    for (const prefix of ['cell', 'sgr', 'rgb', 'rendition', 'cursor']) write(`${prefix}-observations.json`, { runId });
    const entry = file => ({ runId, suiteId: `independent-${file}-fixture`, file,
        sha256: hash(readFileSync(join(output, file))), counts: {} });
    const evidence = { ...entry('cell-observations.json'), sgrEvidence: entry('sgr-observations.json'),
        rgbEvidence: entry('rgb-observations.json'), renditionEvidence: entry('rendition-observations.json'),
        cursorEvidence: entry('cursor-observations.json'), gradingEvidence: gradingBinding, sgrColorsEvidence: binding };
    void context;
    const receipt = completeOwnedReceipt(output, scratch, evidence);
    assert.equal(receipt.schemaVersion, 14);
    assert.equal(receipt.sgrColorsEvidence, binding);
}));

for (const [name, mutate] of [
    ['changed observation bytes', f => f.write('sgcolors-observations.json', { changed: true })],
    ['changed run log bytes', f => f.write('sgcolors-run.json', { changed: true })],
    ['changed adapter bytes', f => writeFileSync(join(f.output, 'collector-sgcolors.mjs'), Buffer.from('tampered'))],
    ['foreign layered derivation', f => {
        // A doctored owned adapter with every digest still declared for the
        // retained extension bytes: the derivation comparison must catch it.
        writeFileSync(join(f.output, 'collector-adapter.mjs'), Buffer.concat([Buffer.from(syntheticOwnedAdapter), Buffer.from('// doctored\n')]));
    }],
    ['changed journal', f => writeFileSync(join(f.output, 'sgcolors-snapshots.jsonl'), '\n')],
    ['changed conclusion counts', f => { f.binding.counts = { supported: 4 }; }],
    ['dropped run binding digest', f => { delete f.binding.runSha256; }],
    ['wrong run identity', f => { f.binding.runId = 'f'.repeat(32); }],
    ['wrong contracts version', f => { f.binding.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.sgrColorsContractsVersion = 2; f.write('context.json', f.context); }],
    ['missing adapter declaration', f => { delete f.context.sgrColorsAdapterId; f.write('context.json', f.context); }],
    ['report adapter digest drift', f => {
        f.report.adapter.bundleSha256 = 'a'.repeat(64);
        f.write('sgcolors-observations.json', f.report);
        f.binding.sha256 = hash(readFileSync(join(f.output, 'sgcolors-observations.json')));
    }],
    ['grading-suite artifact removal downgrade', f => {
        for (const file of ['sgcolors-observations.json', 'sgcolors-run.json', 'sgcolors-snapshots.jsonl', 'collector-sgcolors.mjs'])
            rmSync(join(f.output, file));
    }],
]) test('SGR-colors receipt refuses ' + name, () => fixture(f => {
    mutate(f);
    assert.throws(() => checkSgrColorsEvidence(f.output, { sgrColorsEvidence: f.binding }, runId, f.context),
        /SGR-colors|Invalid|Missing|bytes|report|journal|counts|identity|derivation|contracts|differs|seam changed|ENOENT/i);
    assert.equal(existsSync(join(f.output, 'cell-receipt.json')), false);
}));

test('policy v7 requires the SGR-colors binding even without artifacts', () => fixture(({ output, gradingBinding }) => {
    for (const file of ['sgcolors-observations.json', 'sgcolors-run.json', 'sgcolors-snapshots.jsonl', 'collector-sgcolors.mjs'])
        rmSync(join(output, file));
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v7' };
    assert.equal(includesSgrColorsEvidence(output, {}, context), true);
    // The finalizer path routes such a run into the v14 suffix, whose check
    // demands the grading and SGR-colors declarations the deletion removed.
    assert.throws(() => checkSgrColorsEvidence(output, { gradingEvidence: gradingBinding }, runId, context),
        /Missing or changed SGR-colors adapter declaration/);
}));

test('historical receipts stay valid without any SGR-colors artifacts', async () => {
    const publication = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-05-ascii-sgr-qualification.json', import.meta.url)));
    assert.equal(publication.receipt.schemaVersion, 13);
    assert.equal(publication.receipt.sgrColorsEvidence, undefined);
    const { includesSgrColorsEvidence: includes } = await import('./sgr-colors-receipt.mjs');
    assert.equal(includes('/nonexistent', {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v6' }), false);
    assert.equal(includes('/nonexistent', {}, {}), false);
    const older = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-04-reset-observations.json', import.meta.url)));
    assert.equal(older.receipt.schemaVersion, 8);
});
