import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { checkSgrTruecolorEvidence, includesSgrTruecolorEvidence } from './sgr-truecolor-receipt.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
import { adaptSgrTruecolorCollector, sgrTruecolorAdapterId, sgrTruecolorSeams } from './sgr-truecolor-grading-collector.mjs';
import { adaptSgrPaletteCollector, sgrPaletteAdapterId, sgrPaletteSeams } from './sgr-palette-grading-collector.mjs';
import { adaptSgrAttrsCollector, sgrAttrsAdapterId } from './sgr-attrs-grading-collector.mjs';
import { adaptSgrRgbCollector, sgrRgbAdapterId } from './sgr-rgb-grading-collector.mjs';
import { adaptSgrColorsCollector } from './sgr-colors-grading-collector.mjs';
import { adaptGradingCollector } from './grading-collector.mjs';
import { sgrTruecolorSuiteId, sgrTruecolorContractsVersion } from './sgr-truecolor-contracts.mjs';
import { buildSgrTruecolorGradingReport } from './sgr-truecolor-grading.mjs';
import { sgrPaletteSuiteId, sgrPaletteContractsVersion } from './sgr-palette-contracts.mjs';
import { buildSgrPaletteGradingReport } from './sgr-palette-grading.mjs';
import { syntheticSgrPaletteEvidence } from './fixtures/sgr-palette-evidence.mjs';
import { sgrAttrsSuiteId, sgrAttrsContractsVersion } from './sgr-attrs-contracts.mjs';
import { buildSgrAttrsGradingReport } from './sgr-attrs-grading.mjs';
import { sgrRgbSuiteId, sgrRgbContractsVersion } from './sgr-rgb-contracts.mjs';
import { buildSgrRgbGradingReport } from './sgr-rgb-grading.mjs';
import { sgrColorsSuiteId, sgrColorsContractsVersion } from './sgr-colors-contracts.mjs';
import { buildSgrColorsGradingReport } from './sgr-colors-grading.mjs';
import { syntheticSgrTruecolorEvidence } from './fixtures/sgr-truecolor-evidence.mjs';
import { syntheticSgrAttrsEvidence } from './fixtures/sgr-attrs-evidence.mjs';
import { syntheticSgrRgbEvidence } from './fixtures/sgr-rgb-evidence.mjs';
import { syntheticSgrColorsEvidence } from './fixtures/sgr-colors-evidence.mjs';
import { syntheticGradingEvidence } from './fixtures/ascii-sgr-evidence.mjs';
import { buildAsciiSgrGradingReport } from './ascii-sgr-grading.mjs';
import { asciiSgrSuiteId, asciiSgrContractsVersion } from './ascii-sgr-contracts.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const runId = 'e'.repeat(32);
// Deterministic synthetic adapters: the full seven-layer derivation chain (owned
// adapter -> first grading extension -> SGR-colors -> SGR-RGB -> SGR-attributes ->
// SGR-palette -> SGR-truecolor) over a synthetic source carrying every pinned seam of the layers.
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
    // 'the two named colors select distinct exposed ' is supplied once by the palette layer's full note seam below.
    'measured setup attributes clear on Y and unrelated attributes remain set',
];
const attributeSeams = [
    'sgrProbe("sgr.faint", "\\x1B[2m", (cell) => cell.dim === true)',
    'sgrProbe("sgr.underline.single", "\\x1B[4m", (cell) => !!cell.underline)',
    'sgrProbe("sgr.inverse", "\\x1B[7m", (cell) => cell.inverse === true)',
    'sgrProbe("sgr.hidden", "\\x1B[8m", (cell) => cell.hidden === true)',
    'sgrProbe("sgr.strikethrough", "\\x1B[9m", (cell) => cell.strikethrough === true)',
    'function sgrProbe(id, sequence, check, noObservableWhen) {',
    '      ctx.feed(sequence + "X");\n      const cell = ctx.getCell(0, 0);',
    'const measured = cell.char === "X" && check(cell) === true;',
    '`${id}: attribute applied to the rendered X cell`',
    '"No rendered X cell to evaluate"',
    'Cell readback cannot distinguish unsupported styling from unreported attributes or collapsed styles; a negative needs rendering review',
];
// The seams that live in the pinned bundle itself; exports of earlier layers are appended by their own derivations.
const paletteSeams = sgrPaletteSeams.filter(seam => !seam.startsWith('export '));
const truecolorSeams = sgrTruecolorSeams.filter(seam => !seam.startsWith('export '));
const template = lines => [...new Set(lines)].map((seam, index) => `const seam${index} = \`${seam}\`;`).join('\n');
const syntheticOwnedAdapter = template([...firstSeams, ...colorSeams, ...attributeSeams, ...paletteSeams, ...truecolorSeams])
    + '\nexport async function collectNtildeRun(assertion) {\n  return collectProbeRun({ ntildeOwner: assertion });\n}\n';
const syntheticFirstExtension = adaptGradingCollector(syntheticOwnedAdapter);
const syntheticColorsExtension = adaptSgrColorsCollector(syntheticFirstExtension);
const syntheticRgbExtension = adaptSgrRgbCollector(syntheticColorsExtension);
const syntheticAttrsExtension = adaptSgrAttrsCollector(syntheticRgbExtension);
const syntheticPaletteExtension = adaptSgrPaletteCollector(syntheticAttrsExtension);

function fixture(action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-sgtc-receipt-'));
    const scratch = join(output, 'removed-fixture');
    const ownedAdapterBytes = Buffer.from(syntheticOwnedAdapter);
    const firstExtensionBytes = Buffer.from(syntheticFirstExtension);
    const colorsBytes = Buffer.from(syntheticColorsExtension);
    const rgbBytes = Buffer.from(syntheticRgbExtension);
    const attrsBytes = Buffer.from(syntheticAttrsExtension);
    const paletteBytes = Buffer.from(syntheticPaletteExtension);
    const truecolorBytes = Buffer.from(adaptSgrTruecolorCollector(syntheticPaletteExtension));
    const upstream = 'aa5f2bf9956fb16870b581157e6bac8c5f11242d6eeee7975be1d92b61d5e1f6';
    const grading = syntheticGradingEvidence(runId, hash(firstExtensionBytes));
    grading.adapter = { id: 'ntilde-owned-ascii-sgr-grading-v1', derivedFromAdapterId: 'ntilde-owned-macos-v1',
        bundleSha256: hash(firstExtensionBytes), upstreamBundleSha256: upstream };
    const gradingReport = buildAsciiSgrGradingReport(grading.frames, grading.run, grading.runId, grading.adapter);
    const colors = syntheticSgrColorsEvidence(runId, hash(colorsBytes));
    const colorsReport = buildSgrColorsGradingReport(colors.frames, colors.run, runId, colors.adapter);
    const rgbEvidence = syntheticSgrRgbEvidence(runId, hash(rgbBytes));
    const rgbReport = buildSgrRgbGradingReport(rgbEvidence.frames, rgbEvidence.run, runId, rgbEvidence.adapter);
    const attrsEvidence = syntheticSgrAttrsEvidence(runId, hash(attrsBytes));
    const attrsReport = buildSgrAttrsGradingReport(attrsEvidence.frames, attrsEvidence.run, runId, attrsEvidence.adapter);
    const paletteEvidence = syntheticSgrPaletteEvidence(runId, hash(paletteBytes));
    const paletteReport = buildSgrPaletteGradingReport(paletteEvidence.frames, paletteEvidence.run, runId, paletteEvidence.adapter);
    const evidence = syntheticSgrTruecolorEvidence(runId, hash(truecolorBytes));
    const report = buildSgrTruecolorGradingReport(evidence.frames, evidence.run, runId, evidence.adapter);
    const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value) + '\n');
    const journal = (file, frames) => writeFileSync(join(output, file), frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
    const context = { runId, sourceSnapshotPolicy: 'readonly-owned-source-graph-v12',
        gradingAdapterId: 'ntilde-owned-ascii-sgr-grading-v1', asciiSgrSuiteId,
        gradingBundleSha256: hash(firstExtensionBytes), gradingContractsVersion: asciiSgrContractsVersion,
        sgrColorsAdapterId: 'ntilde-owned-sgr-colors-grading-v1', sgrColorsSuiteId, sgrColorsBundleSha256: hash(colorsBytes),
        sgrColorsContractsVersion, sgrRgbAdapterId, sgrRgbSuiteId, sgrRgbBundleSha256: hash(rgbBytes), sgrRgbContractsVersion,
        sgrAttrsAdapterId, sgrAttrsSuiteId, sgrAttrsBundleSha256: hash(attrsBytes), sgrAttrsContractsVersion,
        sgrPaletteAdapterId, sgrPaletteSuiteId, sgrPaletteBundleSha256: hash(paletteBytes), sgrPaletteContractsVersion,
        sgrTruecolorAdapterId, sgrTruecolorSuiteId, sgrTruecolorBundleSha256: hash(truecolorBytes), sgrTruecolorContractsVersion,
        upstreamBundleSha256: upstream };
    try {
        write('grading-observations.json', gradingReport); write('grading-run.json', grading.run);
        journal('grading-snapshots.jsonl', grading.frames);
        write('sgcolors-observations.json', colorsReport); write('sgcolors-run.json', colors.run);
        journal('sgcolors-snapshots.jsonl', colors.frames);
        write('sgrgb-observations.json', rgbReport); write('sgrgb-run.json', rgbEvidence.run);
        journal('sgrgb-snapshots.jsonl', rgbEvidence.frames);
        write('sgattr-observations.json', attrsReport); write('sgattr-run.json', attrsEvidence.run);
        journal('sgattr-snapshots.jsonl', attrsEvidence.frames);
        write('sgpal-observations.json', paletteReport); write('sgpal-run.json', paletteEvidence.run);
        journal('sgpal-snapshots.jsonl', paletteEvidence.frames);
        write('sgtc-observations.json', report); write('sgtc-run.json', evidence.run);
        journal('sgtc-snapshots.jsonl', evidence.frames);
        writeFileSync(join(output, 'collector-adapter.mjs'), ownedAdapterBytes);
        writeFileSync(join(output, 'collector-grading.mjs'), firstExtensionBytes);
        writeFileSync(join(output, 'collector-sgcolors.mjs'), colorsBytes);
        writeFileSync(join(output, 'collector-sgrgb.mjs'), rgbBytes);
        writeFileSync(join(output, 'collector-sgattr.mjs'), attrsBytes);
        writeFileSync(join(output, 'collector-sgpal.mjs'), paletteBytes);
        writeFileSync(join(output, 'collector-sgtc.mjs'), truecolorBytes);
        write('ownership-proof.json', { completed: true, cleanedUpAt: '2026-11-08T00:00:00Z', cleanupError: null });
        write('context.json', context);
        write('raw.json', { observations: [] });
        const bind = (suiteId, prefix, bytes, version, counts) => ({ runId, suiteId, file: `${prefix}-observations.json`,
            sha256: hash(readFileSync(join(output, `${prefix}-observations.json`))),
            runFile: `${prefix}-run.json`, runSha256: hash(readFileSync(join(output, `${prefix}-run.json`))),
            adapterBundleSha256: hash(bytes), contractsVersion: version, counts, provisionalUntil: 'cell-receipt.json' });
        const binding = bind(sgrTruecolorSuiteId, 'sgtc', truecolorBytes, sgrTruecolorContractsVersion, { supported: 2 });
        const paletteBinding = bind(sgrPaletteSuiteId, 'sgpal', paletteBytes, sgrPaletteContractsVersion, { supported: 6 });
        const attrsBinding = bind(sgrAttrsSuiteId, 'sgattr', attrsBytes, sgrAttrsContractsVersion, { supported: 5 });
        const rgbBinding = bind(sgrRgbSuiteId, 'sgrgb', rgbBytes, sgrRgbContractsVersion, { supported: 2 });
        const colorsBinding = bind(sgrColorsSuiteId, 'sgcolors', colorsBytes, sgrColorsContractsVersion, { supported: 5 });
        const gradingBinding = bind(gradingReport.suiteId, 'grading', firstExtensionBytes, gradingReport.contractsVersion, { supported: 4 });
        action({ output, scratch, evidence, report, context, binding, paletteBinding, attrsBinding, rgbBinding, colorsBinding, gradingBinding, write });
    } finally { rmSync(output, { recursive: true, force: true }); }
}

const predecessors = f => ({ sgrTruecolorEvidence: f.binding, sgrPaletteEvidence: f.paletteBinding, sgrAttrsEvidence: f.attrsBinding, sgrRgbEvidence: f.rgbBinding, sgrColorsEvidence: f.colorsBinding });

test('the SGR-truecolor receipt binding re-derives the whole report from retained bytes', () => fixture(f => {
    assert.equal(includesSgrTruecolorEvidence(f.output, {}, f.context), true);
    assert.doesNotThrow(() => checkSgrTruecolorEvidence(f.output, predecessors(f), runId, f.context));
}));

test('the shared finalizer publishes receipt schema 18 beside every predecessor suite', () => fixture(f => {
    const { output, scratch, write, context } = f;
    const { sourceSnapshotPolicy, ...lightContext } = context;
    void sourceSnapshotPolicy;
    write('context.json', lightContext);
    for (const prefix of ['cell', 'sgr', 'rgb', 'rendition', 'cursor']) write(`${prefix}-observations.json`, { runId });
    const entry = file => ({ runId, suiteId: `independent-${file}-fixture`, file,
        sha256: hash(readFileSync(join(output, file))), counts: {} });
    const evidence = { ...entry('cell-observations.json'), sgrEvidence: entry('sgr-observations.json'),
        rgbEvidence: entry('rgb-observations.json'), renditionEvidence: entry('rendition-observations.json'),
        cursorEvidence: entry('cursor-observations.json'), gradingEvidence: f.gradingBinding, ...predecessors(f) };
    const receipt = completeOwnedReceipt(output, scratch, evidence);
    assert.equal(receipt.schemaVersion, 18);
    assert.equal(receipt.sgrPaletteEvidence, f.paletteBinding);
    assert.equal(receipt.sgrTruecolorEvidence, f.binding);
    assert.equal(receipt.sgrAttrsEvidence, f.attrsBinding);
}));

for (const [name, mutate] of [
    ['changed observation bytes', f => f.write('sgtc-observations.json', { changed: true })],
    ['changed run log bytes', f => f.write('sgtc-run.json', { changed: true })],
    ['changed adapter bytes', f => writeFileSync(join(f.output, 'collector-sgtc.mjs'), Buffer.from('tampered'))],
    ['declared SGR-palette layer digest drift', f => {
        const renamed = Buffer.from(syntheticPaletteExtension.replace('ntilde-owned-sgr-palette-grading-v1', 'ntilde-owned-renamed-v1'));
        f.context.sgrPaletteBundleSha256 = hash(renamed);
        f.write('context.json', f.context);
    }],
    ['foreign layered derivation', f => {
        writeFileSync(join(f.output, 'collector-adapter.mjs'), Buffer.concat([Buffer.from(syntheticOwnedAdapter), Buffer.from('// doctored\n')]));
    }],
    ['declared SGR-attributes layer digest drift', f => {
        const renamed = Buffer.from(syntheticAttrsExtension.replace('ntilde-owned-sgr-attributes-grading-v1', 'ntilde-owned-renamed-v1'));
        f.context.sgrAttrsBundleSha256 = hash(renamed);
        f.write('context.json', f.context);
    }],
    ['declared SGR-RGB layer digest drift', f => {
        const renamed = Buffer.from(syntheticRgbExtension.replace('ntilde-owned-sgr-rgb-grading-v1', 'ntilde-owned-renamed-v1'));
        f.context.sgrRgbBundleSha256 = hash(renamed);
        f.write('context.json', f.context);
    }],
    ['changed journal', f => writeFileSync(join(f.output, 'sgtc-snapshots.jsonl'), '\n')],
    ['changed conclusion counts', f => { f.binding.counts = { supported: 1 }; }],
    ['wrong run identity', f => { f.binding.runId = 'f'.repeat(32); }],
    ['wrong contracts version', f => { f.binding.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.sgrTruecolorContractsVersion = 2; f.write('context.json', f.context); }],
    ['missing adapter declaration', f => { delete f.context.sgrTruecolorAdapterId; f.write('context.json', f.context); }],
    ['missing SGR-palette predecessor declaration', f => { delete f.context.sgrPaletteAdapterId; f.write('context.json', f.context); }],
    ['missing SGR-palette predecessor evidence', f => { delete f.paletteBinding; }],
    ['missing SGR-attributes predecessor declaration', f => { delete f.context.sgrAttrsAdapterId; f.write('context.json', f.context); }],
    ['missing SGR-attributes predecessor evidence', f => { delete f.attrsBinding; }],
    ['missing SGR-RGB predecessor evidence', f => { delete f.rgbBinding; }],
    ['missing SGR-colors predecessor evidence', f => { delete f.colorsBinding; }],
    ['report adapter digest drift', f => {
        f.report.adapter.bundleSha256 = 'a'.repeat(64);
        f.write('sgtc-observations.json', f.report);
        f.binding.sha256 = hash(readFileSync(join(f.output, 'sgtc-observations.json')));
    }],
    ['truecolor artifact removal downgrade', f => {
        for (const file of ['sgtc-observations.json', 'sgtc-run.json', 'sgtc-snapshots.jsonl', 'collector-sgtc.mjs'])
            rmSync(join(f.output, file));
    }],
]) test('SGR-truecolor receipt refuses ' + name, () => fixture(f => {
    mutate(f);
    assert.throws(() => checkSgrTruecolorEvidence(f.output, predecessors(f), runId, f.context),
        /SGR-truecolor|Invalid|Missing|bytes|report|journal|counts|identity|derivation|contracts|differs|predecessor|ENOENT/i);
    assert.equal(existsSync(join(f.output, 'cell-receipt.json')), false);
}));

test('policy v12 requires the SGR-truecolor binding even without artifacts', () => fixture(f => {
    for (const file of ['sgtc-observations.json', 'sgtc-run.json', 'sgtc-snapshots.jsonl', 'collector-sgtc.mjs'])
        rmSync(join(f.output, file));
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v12' };
    assert.equal(includesSgrTruecolorEvidence(f.output, {}, context), true);
    assert.throws(() => checkSgrTruecolorEvidence(f.output, { sgrPaletteEvidence: f.paletteBinding, sgrAttrsEvidence: f.attrsBinding, sgrRgbEvidence: f.rgbBinding, sgrColorsEvidence: f.colorsBinding }, runId, context),
        /Missing or changed SGR-truecolor adapter declaration/);
}));

test('historical receipts stay valid without any SGR-truecolor artifacts', () => {
    const publication = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-attributes-qualification.json', import.meta.url)));
    assert.equal(publication.receipt.schemaVersion, 16);
    assert.equal(publication.receipt.sgrTruecolorEvidence, undefined);
    assert.equal(includesSgrTruecolorEvidence('/nonexistent', {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v10' }), false);
    assert.equal(includesSgrTruecolorEvidence('/nonexistent', {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v11' }), false);
    assert.equal(includesSgrTruecolorEvidence('/nonexistent', {}, {}), false);
});
