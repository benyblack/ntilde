import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { checkSgrRgbEvidence, includesSgrRgbEvidence } from './sgr-rgb-receipt.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
import { adaptSgrRgbCollector, sgrRgbAdapterId } from './sgr-rgb-grading-collector.mjs';
import { adaptSgrColorsCollector } from './sgr-colors-grading-collector.mjs';
import { adaptGradingCollector } from './grading-collector.mjs';
import { sgrRgbSuiteId, sgrRgbContractsVersion } from './sgr-rgb-contracts.mjs';
import { buildSgrRgbGradingReport } from './sgr-rgb-grading.mjs';
import { sgrColorsSuiteId, sgrColorsContractsVersion } from './sgr-colors-contracts.mjs';
import { buildSgrColorsGradingReport } from './sgr-colors-grading.mjs';
import { syntheticSgrRgbEvidence } from './fixtures/sgr-rgb-evidence.mjs';
import { syntheticSgrColorsEvidence } from './fixtures/sgr-colors-evidence.mjs';
import { syntheticGradingEvidence } from './fixtures/ascii-sgr-evidence.mjs';
import { buildAsciiSgrGradingReport } from './ascii-sgr-grading.mjs';
import { asciiSgrSuiteId, asciiSgrContractsVersion } from './ascii-sgr-contracts.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const runId = 'e'.repeat(32);
// Deterministic synthetic adapters: the full four-layer derivation chain (owned
// adapter -> first grading extension -> SGR-colors extension -> SGR-RGB
// extension) over a synthetic source carrying every seam of all layers.
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
// The RGB-layer seams are added by the SGR-colors derivation's appended block,
// never the base source, so each appears exactly once in the layered chain.
const template = lines => lines.map((seam, index) => `const seam${index} = \`${seam}\`;`).join('\n');
const syntheticOwnedAdapter = template([...firstSeams, ...colorSeams])
    + '\nexport async function collectNtildeRun(assertion) {\n  return collectProbeRun({ ntildeOwner: assertion });\n}\n';
const syntheticFirstExtension = adaptGradingCollector(syntheticOwnedAdapter);
const syntheticColorsExtension = adaptSgrColorsCollector(syntheticFirstExtension);

function fixture(action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-sgrrgb-receipt-'));
    const scratch = join(output, 'removed-fixture');
    const ownedAdapterBytes = Buffer.from(syntheticOwnedAdapter);
    const firstExtensionBytes = Buffer.from(syntheticFirstExtension);
    const colorsBytes = Buffer.from(syntheticColorsExtension);
    const rgbBytes = Buffer.from(adaptSgrRgbCollector(syntheticColorsExtension));
    const grading = syntheticGradingEvidence(runId, hash(firstExtensionBytes));
    grading.adapter = { id: 'ntilde-owned-ascii-sgr-grading-v1', derivedFromAdapterId: 'ntilde-owned-macos-v1',
        bundleSha256: hash(firstExtensionBytes), upstreamBundleSha256: 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6' };
    const gradingReport = buildAsciiSgrGradingReport(grading.frames, grading.run, grading.runId, grading.adapter);
    const colors = syntheticSgrColorsEvidence(runId, hash(colorsBytes));
    const colorsReport = buildSgrColorsGradingReport(colors.frames, colors.run, runId, colors.adapter);
    const evidence = syntheticSgrRgbEvidence(runId, hash(rgbBytes));
    const report = buildSgrRgbGradingReport(evidence.frames, evidence.run, runId, evidence.adapter);
    const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value) + '\n');
    const context = { runId, sourceSnapshotPolicy: 'readonly-owned-source-graph-v9',
        gradingAdapterId: 'ntilde-owned-ascii-sgr-grading-v1', asciiSgrSuiteId,
        gradingBundleSha256: hash(firstExtensionBytes), gradingContractsVersion: asciiSgrContractsVersion,
        sgrColorsAdapterId: 'ntilde-owned-sgr-colors-grading-v1', sgrColorsSuiteId, sgrColorsBundleSha256: hash(colorsBytes),
        sgrColorsContractsVersion, sgrRgbAdapterId, sgrRgbSuiteId, sgrRgbBundleSha256: hash(rgbBytes),
        sgrRgbContractsVersion, upstreamBundleSha256: evidence.adapter.upstreamBundleSha256 };
    try {
        write('grading-observations.json', gradingReport);
        write('grading-run.json', grading.run);
        writeFileSync(join(output, 'grading-snapshots.jsonl'), grading.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        write('sgcolors-observations.json', colorsReport);
        write('sgcolors-run.json', colors.run);
        writeFileSync(join(output, 'sgcolors-snapshots.jsonl'), colors.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        write('sgrgb-observations.json', report);
        write('sgrgb-run.json', evidence.run);
        writeFileSync(join(output, 'sgrgb-snapshots.jsonl'), evidence.frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
        writeFileSync(join(output, 'collector-adapter.mjs'), ownedAdapterBytes);
        writeFileSync(join(output, 'collector-grading.mjs'), firstExtensionBytes);
        writeFileSync(join(output, 'collector-sgcolors.mjs'), colorsBytes);
        writeFileSync(join(output, 'collector-sgrgb.mjs'), rgbBytes);
        write('ownership-proof.json', { completed: true, cleanedUpAt: '2026-11-07T00:00:00Z', cleanupError: null });
        write('context.json', context);
        write('raw.json', { observations: [] });
        const binding = { runId, suiteId: sgrRgbSuiteId, file: 'sgrgb-observations.json',
            sha256: hash(readFileSync(join(output, 'sgrgb-observations.json'))),
            runFile: 'sgrgb-run.json', runSha256: hash(readFileSync(join(output, 'sgrgb-run.json'))),
            adapterBundleSha256: hash(rgbBytes), contractsVersion: sgrRgbContractsVersion,
            counts: { supported: 2 }, provisionalUntil: 'cell-receipt.json' };
        const colorsBinding = { runId, suiteId: sgrColorsSuiteId, file: 'sgcolors-observations.json',
            sha256: hash(readFileSync(join(output, 'sgcolors-observations.json'))),
            runFile: 'sgcolors-run.json', runSha256: hash(readFileSync(join(output, 'sgcolors-run.json'))),
            adapterBundleSha256: hash(colorsBytes), contractsVersion: sgrColorsContractsVersion,
            counts: { supported: 5 }, provisionalUntil: 'cell-receipt.json' };
        const gradingBinding = { runId, suiteId: gradingReport.suiteId, file: 'grading-observations.json',
            sha256: hash(readFileSync(join(output, 'grading-observations.json'))),
            runFile: 'grading-run.json', runSha256: hash(readFileSync(join(output, 'grading-run.json'))),
            adapterBundleSha256: hash(firstExtensionBytes), contractsVersion: gradingReport.contractsVersion,
            counts: { supported: 4 }, provisionalUntil: 'cell-receipt.json' };
        action({ output, scratch, evidence, report, context, binding, colorsBinding, gradingBinding, write,
            rgbBytes, colorsBytes, firstExtensionBytes });
    } finally { rmSync(output, { recursive: true, force: true }); }
}

test('the SGR-RGB receipt binding re-derives the whole report from retained bytes', () => fixture(({ output, binding, context, colorsBinding }) => {
    assert.equal(includesSgrRgbEvidence(output, {}, context), true);
    assert.doesNotThrow(() => checkSgrRgbEvidence(output, { sgrRgbEvidence: binding, sgrColorsEvidence: colorsBinding }, runId, context));
}));

test('the shared finalizer publishes receipt schema 15 with all three grading suites', () => fixture(({ output, scratch, binding, colorsBinding, gradingBinding, write, context }) => {
    // A policy-free context: the private-clipboard suffix is not under test here.
    const lightContext = { runId, gradingAdapterId: context.gradingAdapterId, asciiSgrSuiteId: context.asciiSgrSuiteId,
        gradingBundleSha256: context.gradingBundleSha256, gradingContractsVersion: context.gradingContractsVersion,
        sgrColorsAdapterId: context.sgrColorsAdapterId, sgrColorsSuiteId: context.sgrColorsSuiteId,
        sgrColorsBundleSha256: context.sgrColorsBundleSha256, sgrColorsContractsVersion: context.sgrColorsContractsVersion,
        sgrRgbAdapterId: context.sgrRgbAdapterId, sgrRgbSuiteId: context.sgrRgbSuiteId,
        sgrRgbBundleSha256: context.sgrRgbBundleSha256, sgrRgbContractsVersion: context.sgrRgbContractsVersion,
        upstreamBundleSha256: context.upstreamBundleSha256 };
    write('context.json', lightContext);
    for (const prefix of ['cell', 'sgr', 'rgb', 'rendition', 'cursor']) write(`${prefix}-observations.json`, { runId });
    const entry = file => ({ runId, suiteId: `independent-${file}-fixture`, file,
        sha256: hash(readFileSync(join(output, file))), counts: {} });
    const evidence = { ...entry('cell-observations.json'), sgrEvidence: entry('sgr-observations.json'),
        rgbEvidence: entry('rgb-observations.json'), renditionEvidence: entry('rendition-observations.json'),
        cursorEvidence: entry('cursor-observations.json'), gradingEvidence: gradingBinding,
        sgrColorsEvidence: colorsBinding, sgrRgbEvidence: binding };
    void context;
    const receipt = completeOwnedReceipt(output, scratch, evidence);
    assert.equal(receipt.schemaVersion, 15);
    assert.equal(receipt.sgrRgbEvidence, binding);
}));

for (const [name, mutate] of [
    ['changed observation bytes', f => f.write('sgrgb-observations.json', { changed: true })],
    ['changed run log bytes', f => f.write('sgrgb-run.json', { changed: true })],
    ['changed adapter bytes', f => writeFileSync(join(f.output, 'collector-sgrgb.mjs'), Buffer.from('tampered'))],
    ['foreign layered derivation', f => {
        writeFileSync(join(f.output, 'collector-adapter.mjs'), Buffer.concat([Buffer.from(syntheticOwnedAdapter), Buffer.from('// doctored\n')]));
    }],
    ['declared SGR-colors layer digest drift', f => {
        // The retained RGB bytes derive from the true chain; declaring a doctored
        // SGR-colors digest must not be accepted as their origin.
        const renamed = Buffer.from(syntheticColorsExtension.replace('ntilde-owned-sgr-colors-grading-v1', 'ntilde-owned-renamed-v1'));
        f.context.sgrColorsBundleSha256 = hash(renamed);
        f.write('context.json', f.context);
    }],
    ['changed journal', f => writeFileSync(join(f.output, 'sgrgb-snapshots.jsonl'), '\n')],
    ['changed conclusion counts', f => { f.binding.counts = { supported: 1 }; }],
    ['wrong run identity', f => { f.binding.runId = 'f'.repeat(32); }],
    ['wrong contracts version', f => { f.binding.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.sgrRgbContractsVersion = 2; f.write('context.json', f.context); }],
    ['missing adapter declaration', f => { delete f.context.sgrRgbAdapterId; f.write('context.json', f.context); }],
    ['missing SGR-colors predecessor', f => {
        delete f.context.sgrColorsAdapterId;
        delete f.context.sgrColorsSuiteId;
        f.write('context.json', f.context);
    }],
    ['missing SGR-colors predecessor evidence', f => { delete f.colorsBinding; }],
    ['report adapter digest drift', f => {
        f.report.adapter.bundleSha256 = 'a'.repeat(64);
        f.write('sgrgb-observations.json', f.report);
        f.binding.sha256 = hash(readFileSync(join(f.output, 'sgrgb-observations.json')));
    }],
    ['RGB artifact removal downgrade', f => {
        for (const file of ['sgrgb-observations.json', 'sgrgb-run.json', 'sgrgb-snapshots.jsonl', 'collector-sgrgb.mjs'])
            rmSync(join(f.output, file));
    }],
]) test('SGR-RGB receipt refuses ' + name, () => fixture(f => {
    mutate(f);
    assert.throws(() => checkSgrRgbEvidence(f.output, { sgrRgbEvidence: f.binding, sgrColorsEvidence: f.colorsBinding }, runId, f.context),
        /SGR-RGB|Invalid|Missing|bytes|report|journal|counts|identity|derivation|contracts|differs|predecessor|ENOENT/i);
    assert.equal(existsSync(join(f.output, 'cell-receipt.json')), false);
}));

test('policy v9 requires the SGR-RGB binding even without artifacts', () => fixture(({ output, colorsBinding }) => {
    for (const file of ['sgrgb-observations.json', 'sgrgb-run.json', 'sgrgb-snapshots.jsonl', 'collector-sgrgb.mjs'])
        rmSync(join(output, file));
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v9' };
    assert.equal(includesSgrRgbEvidence(output, {}, context), true);
    assert.throws(() => checkSgrRgbEvidence(output, { sgrColorsEvidence: colorsBinding }, runId, context),
        /Missing or changed SGR-RGB adapter declaration/);
}));

test('historical receipts stay valid without any SGR-RGB artifacts', () => {
    const publication = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-06-sgr-colors-qualification.json', import.meta.url)));
    assert.equal(publication.receipt.schemaVersion, 14);
    assert.equal(publication.receipt.sgrRgbEvidence, undefined);
    assert.equal(includesSgrRgbEvidence('/nonexistent', {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v8' }), false);
    assert.equal(includesSgrRgbEvidence('/nonexistent', {}, {}), false);
});
