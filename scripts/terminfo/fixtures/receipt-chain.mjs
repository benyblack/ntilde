// Shared synthetic layered-derivation chain and receipt fixture for the terminfo receipt tests: the owned adapter and every grading
// extension through erase-in-line, with each layer's synthetic evidence, retained files and receipt bindings. A suite layered on top
// (see erase-display-receipt.test.mjs) adds its own files and declarations to the fixture it is given.
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { adaptSgrClosureCollector, sgrClosureAdapterId, sgrClosureSeams } from '../sgr-closure-grading-collector.mjs';
import { sgrClosureSuiteId, sgrClosureContractsVersion } from '../sgr-closure-contracts.mjs';
import { buildSgrClosureGradingReport } from '../sgr-closure-grading.mjs';
import { syntheticSgrClosureEvidence } from './sgr-closure-evidence.mjs';
import { adaptSgrResetCollector, sgrResetAdapterId, sgrResetSeams } from '../sgr-reset-grading-collector.mjs';
import { adaptEraseLineCollector, eraseLineAdapterId, eraseLineSeams } from '../erase-line-grading-collector.mjs';
import { eraseDisplaySeams } from '../erase-display-grading-collector.mjs';
import { adaptSgrUnderlineColorCollector, sgrUnderlineColorAdapterId, sgrUnderlineColorSeams } from '../sgr-underline-color-grading-collector.mjs';
import { adaptSgrUnderlineCollector, sgrUnderlineAdapterId, sgrUnderlineSeams } from '../sgr-underline-grading-collector.mjs';
import { adaptSgrTruecolorCollector, sgrTruecolorAdapterId, sgrTruecolorSeams } from '../sgr-truecolor-grading-collector.mjs';
import { adaptSgrPaletteCollector, sgrPaletteAdapterId, sgrPaletteSeams } from '../sgr-palette-grading-collector.mjs';
import { adaptSgrAttrsCollector, sgrAttrsAdapterId } from '../sgr-attrs-grading-collector.mjs';
import { adaptSgrRgbCollector, sgrRgbAdapterId } from '../sgr-rgb-grading-collector.mjs';
import { adaptSgrColorsCollector } from '../sgr-colors-grading-collector.mjs';
import { adaptGradingCollector } from '../grading-collector.mjs';
import { sgrTruecolorSuiteId, sgrTruecolorContractsVersion } from '../sgr-truecolor-contracts.mjs';
import { buildSgrTruecolorGradingReport } from '../sgr-truecolor-grading.mjs';
import { sgrUnderlineSuiteId, sgrUnderlineContractsVersion } from '../sgr-underline-contracts.mjs';
import { buildSgrUnderlineGradingReport } from '../sgr-underline-grading.mjs';
import { sgrUnderlineColorSuiteId, sgrUnderlineColorContractsVersion } from '../sgr-underline-color-contracts.mjs';
import { buildSgrUnderlineColorGradingReport } from '../sgr-underline-color-grading.mjs';
import { syntheticSgrUnderlineColorEvidence } from './sgr-underline-color-evidence.mjs';
import { syntheticSgrResetEvidence } from './sgr-reset-evidence.mjs';
import { sgrResetSuiteId, sgrResetContractsVersion } from '../sgr-reset-contracts.mjs';
import { buildSgrResetGradingReport } from '../sgr-reset-grading.mjs';
import { eraseLineSuiteId, eraseLineContractsVersion } from '../erase-line-contracts.mjs';
import { buildEraseLineGradingReport } from '../erase-line-grading.mjs';
import { syntheticEraseLineEvidence } from './erase-line-evidence.mjs';
import { syntheticSgrUnderlineEvidence } from './sgr-underline-evidence.mjs';
import { sgrPaletteSuiteId, sgrPaletteContractsVersion } from '../sgr-palette-contracts.mjs';
import { buildSgrPaletteGradingReport } from '../sgr-palette-grading.mjs';
import { syntheticSgrPaletteEvidence } from './sgr-palette-evidence.mjs';
import { sgrAttrsSuiteId, sgrAttrsContractsVersion } from '../sgr-attrs-contracts.mjs';
import { buildSgrAttrsGradingReport } from '../sgr-attrs-grading.mjs';
import { sgrRgbSuiteId, sgrRgbContractsVersion } from '../sgr-rgb-contracts.mjs';
import { buildSgrRgbGradingReport } from '../sgr-rgb-grading.mjs';
import { sgrColorsSuiteId, sgrColorsContractsVersion } from '../sgr-colors-contracts.mjs';
import { buildSgrColorsGradingReport } from '../sgr-colors-grading.mjs';
import { syntheticSgrTruecolorEvidence } from './sgr-truecolor-evidence.mjs';
import { syntheticSgrAttrsEvidence } from './sgr-attrs-evidence.mjs';
import { syntheticSgrRgbEvidence } from './sgr-rgb-evidence.mjs';
import { syntheticSgrColorsEvidence } from './sgr-colors-evidence.mjs';
import { syntheticGradingEvidence } from './ascii-sgr-evidence.mjs';
import { buildAsciiSgrGradingReport } from '../ascii-sgr-grading.mjs';
import { asciiSgrSuiteId, asciiSgrContractsVersion } from '../ascii-sgr-contracts.mjs';

export const hash = bytes => createHash('sha256').update(bytes).digest('hex');
export const runId = 'e'.repeat(32);
// Deterministic synthetic adapters: the full nine-layer derivation chain (owned
// adapter -> first grading extension -> SGR-colors -> SGR-RGB -> SGR-attributes ->
// SGR-palette -> SGR-truecolor -> SGR-underline -> SGR-underline-color) over a synthetic source carrying every pinned seam of the layers.
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
const underlineSeams = sgrUnderlineSeams.filter(seam => !seam.startsWith('export '));
const underlineColorSeams = sgrUnderlineColorSeams.filter(seam => !seam.startsWith('export '));
// A seam that is a substring of another (the closure layer's complete sgrProbe body contains the attribute layer's smaller seams) is carried once, inside the larger one.
const template = lines => [...new Set(lines)].filter(line => ![...new Set(lines)].some(other => other !== line && other.includes(line))).map((seam, index) => `const seam${index} = \`${seam}\`;`).join('\n');
const resetSeams = sgrResetSeams.filter(seam => !seam.startsWith('export '));
const closureSeams = sgrClosureSeams.filter(seam => !seam.startsWith('export '));
const eraseSeams = eraseLineSeams.filter(seam => !seam.startsWith('export '));
// The suite layered on top (erase-in-display) derives from this chain, so its pinned seams are part of the synthetic source too.
const displaySeams = eraseDisplaySeams.filter(seam => !seam.startsWith('export '));
// The reset layer's complete measuredReset body already carries the colors layer's two measuredReset seams once.
const colorSeamsWithoutReset = colorSeams.filter(seam => seam !== 'function measuredReset(ctx, id, setup, reset, set, preserve) {'
    && seam !== 'measured setup attributes clear on Y and unrelated attributes remain set');
export const syntheticOwnedAdapter = template([...firstSeams, ...colorSeamsWithoutReset, ...attributeSeams, ...paletteSeams, ...truecolorSeams, ...underlineSeams, ...underlineColorSeams, ...resetSeams, ...closureSeams, ...eraseSeams, ...displaySeams])
    + '\nexport async function collectNtildeRun(assertion) {\n  return collectProbeRun({ ntildeOwner: assertion });\n}\n';
const syntheticFirstExtension = adaptGradingCollector(syntheticOwnedAdapter);
const syntheticColorsExtension = adaptSgrColorsCollector(syntheticFirstExtension);
const syntheticRgbExtension = adaptSgrRgbCollector(syntheticColorsExtension);
const syntheticAttrsExtension = adaptSgrAttrsCollector(syntheticRgbExtension);
export const syntheticPaletteExtension = adaptSgrPaletteCollector(syntheticAttrsExtension);
const syntheticTruecolorExtension = adaptSgrTruecolorCollector(syntheticPaletteExtension);
export const syntheticUnderlineExtension = adaptSgrUnderlineCollector(syntheticTruecolorExtension);
export const syntheticUnderlineColorExtension = adaptSgrUnderlineColorCollector(syntheticUnderlineExtension);
export const syntheticResetExtension = adaptSgrResetCollector(syntheticUnderlineColorExtension);
export const syntheticClosureExtension = adaptSgrClosureCollector(syntheticResetExtension);


export function fixture(action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-receipt-chain-'));
    const scratch = join(output, 'removed-fixture');
    const ownedAdapterBytes = Buffer.from(syntheticOwnedAdapter);
    const firstExtensionBytes = Buffer.from(syntheticFirstExtension);
    const colorsBytes = Buffer.from(syntheticColorsExtension);
    const rgbBytes = Buffer.from(syntheticRgbExtension);
    const attrsBytes = Buffer.from(syntheticAttrsExtension);
    const paletteBytes = Buffer.from(syntheticPaletteExtension);
    const truecolorBytes = Buffer.from(syntheticTruecolorExtension);
    const underlineBytes = Buffer.from(syntheticUnderlineExtension);
    const underlineColorBytes = Buffer.from(syntheticUnderlineColorExtension);
    const resetBytes = Buffer.from(syntheticResetExtension);
    const closureBytes = Buffer.from(syntheticClosureExtension);
    const eraseBytes = Buffer.from(adaptEraseLineCollector(syntheticClosureExtension));
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
    const truecolorEvidence = syntheticSgrTruecolorEvidence(runId, hash(truecolorBytes));
    const truecolorReport = buildSgrTruecolorGradingReport(truecolorEvidence.frames, truecolorEvidence.run, runId, truecolorEvidence.adapter);
    const underlineEvidence = syntheticSgrUnderlineEvidence(runId, hash(underlineBytes));
    const underlineReport = buildSgrUnderlineGradingReport(underlineEvidence.frames, underlineEvidence.run, runId, underlineEvidence.adapter);
    const ucEvidence = syntheticSgrUnderlineColorEvidence(runId, hash(underlineColorBytes));
    const ucReport = buildSgrUnderlineColorGradingReport(ucEvidence.frames, ucEvidence.run, runId, ucEvidence.adapter);
    const resetEvidence = syntheticSgrResetEvidence(runId, hash(resetBytes));
    const resetReport = buildSgrResetGradingReport(resetEvidence.frames, resetEvidence.run, runId, resetEvidence.adapter);
    const closureEvidence = syntheticSgrClosureEvidence(runId, hash(closureBytes));
    const closureReport = buildSgrClosureGradingReport(closureEvidence.frames, closureEvidence.run, runId, closureEvidence.adapter);
    const evidence = syntheticEraseLineEvidence(runId, hash(eraseBytes));
    const report = buildEraseLineGradingReport(evidence.frames, evidence.run, runId, evidence.adapter);
    const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value) + '\n');
    const journal = (file, frames) => writeFileSync(join(output, file), frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
    const context = { runId, sourceSnapshotPolicy: 'readonly-owned-source-graph-v19',
        gradingAdapterId: 'ntilde-owned-ascii-sgr-grading-v1', asciiSgrSuiteId,
        gradingBundleSha256: hash(firstExtensionBytes), gradingContractsVersion: asciiSgrContractsVersion,
        sgrColorsAdapterId: 'ntilde-owned-sgr-colors-grading-v1', sgrColorsSuiteId, sgrColorsBundleSha256: hash(colorsBytes),
        sgrColorsContractsVersion, sgrRgbAdapterId, sgrRgbSuiteId, sgrRgbBundleSha256: hash(rgbBytes), sgrRgbContractsVersion,
        sgrAttrsAdapterId, sgrAttrsSuiteId, sgrAttrsBundleSha256: hash(attrsBytes), sgrAttrsContractsVersion,
        sgrPaletteAdapterId, sgrPaletteSuiteId, sgrPaletteBundleSha256: hash(paletteBytes), sgrPaletteContractsVersion,
        sgrTruecolorAdapterId, sgrTruecolorSuiteId, sgrTruecolorBundleSha256: hash(truecolorBytes), sgrTruecolorContractsVersion,
        sgrUnderlineAdapterId, sgrUnderlineSuiteId, sgrUnderlineBundleSha256: hash(underlineBytes), sgrUnderlineContractsVersion,
        sgrUnderlineColorAdapterId, sgrUnderlineColorSuiteId, sgrUnderlineColorBundleSha256: hash(underlineColorBytes), sgrUnderlineColorContractsVersion,
        sgrResetAdapterId, sgrResetSuiteId, sgrResetBundleSha256: hash(resetBytes), sgrResetContractsVersion,
        sgrClosureAdapterId, sgrClosureSuiteId, sgrClosureBundleSha256: hash(closureBytes), sgrClosureContractsVersion,
        eraseLineAdapterId, eraseLineSuiteId, eraseLineBundleSha256: hash(eraseBytes), eraseLineContractsVersion,
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
        write('sgtc-observations.json', truecolorReport); write('sgtc-run.json', truecolorEvidence.run);
        journal('sgtc-snapshots.jsonl', truecolorEvidence.frames);
        write('sgus-observations.json', underlineReport); write('sgus-run.json', underlineEvidence.run);
        journal('sgus-snapshots.jsonl', underlineEvidence.frames);
        write('sguc-observations.json', ucReport); write('sguc-run.json', ucEvidence.run);
        journal('sguc-snapshots.jsonl', ucEvidence.frames);
        write('sgsr-observations.json', resetReport); write('sgsr-run.json', resetEvidence.run);
        journal('sgsr-snapshots.jsonl', resetEvidence.frames);
        write('sgcl-observations.json', closureReport); write('sgcl-run.json', closureEvidence.run);
        journal('sgcl-snapshots.jsonl', closureEvidence.frames);
        write('erln-observations.json', report); write('erln-run.json', evidence.run);
        journal('erln-snapshots.jsonl', evidence.frames);
        writeFileSync(join(output, 'collector-adapter.mjs'), ownedAdapterBytes);
        writeFileSync(join(output, 'collector-grading.mjs'), firstExtensionBytes);
        writeFileSync(join(output, 'collector-sgcolors.mjs'), colorsBytes);
        writeFileSync(join(output, 'collector-sgrgb.mjs'), rgbBytes);
        writeFileSync(join(output, 'collector-sgattr.mjs'), attrsBytes);
        writeFileSync(join(output, 'collector-sgpal.mjs'), paletteBytes);
        writeFileSync(join(output, 'collector-sgtc.mjs'), truecolorBytes);
        writeFileSync(join(output, 'collector-sgus.mjs'), underlineBytes);
        writeFileSync(join(output, 'collector-sguc.mjs'), underlineColorBytes);
        writeFileSync(join(output, 'collector-sgsr.mjs'), resetBytes);
        writeFileSync(join(output, 'collector-sgcl.mjs'), closureBytes);
        writeFileSync(join(output, 'collector-erln.mjs'), eraseBytes);
        write('ownership-proof.json', { completed: true, cleanedUpAt: '2026-11-08T00:00:00Z', cleanupError: null });
        write('context.json', context);
        write('raw.json', { observations: [] });
        const bind = (suiteId, prefix, bytes, version, counts) => ({ runId, suiteId, file: `${prefix}-observations.json`,
            sha256: hash(readFileSync(join(output, `${prefix}-observations.json`))),
            runFile: `${prefix}-run.json`, runSha256: hash(readFileSync(join(output, `${prefix}-run.json`))),
            adapterBundleSha256: hash(bytes), contractsVersion: version, counts, provisionalUntil: 'cell-receipt.json' });
        const resetBinding = bind(sgrResetSuiteId, 'sgsr', resetBytes, sgrResetContractsVersion, { supported: 2 });
        const closureBinding = bind(sgrClosureSuiteId, 'sgcl', closureBytes, sgrClosureContractsVersion, { supported: 3 });
        const binding = bind(eraseLineSuiteId, 'erln', eraseBytes, eraseLineContractsVersion, { supported: 3 });
        const underlineColorBinding = bind(sgrUnderlineColorSuiteId, 'sguc', underlineColorBytes, sgrUnderlineColorContractsVersion, { supported: 3 });
        const underlineBinding = bind(sgrUnderlineSuiteId, 'sgus', underlineBytes, sgrUnderlineContractsVersion, { supported: 4 });
        const truecolorBinding = bind(sgrTruecolorSuiteId, 'sgtc', truecolorBytes, sgrTruecolorContractsVersion, { supported: 2 });
        const paletteBinding = bind(sgrPaletteSuiteId, 'sgpal', paletteBytes, sgrPaletteContractsVersion, { supported: 6 });
        const attrsBinding = bind(sgrAttrsSuiteId, 'sgattr', attrsBytes, sgrAttrsContractsVersion, { supported: 5 });
        const rgbBinding = bind(sgrRgbSuiteId, 'sgrgb', rgbBytes, sgrRgbContractsVersion, { supported: 2 });
        const colorsBinding = bind(sgrColorsSuiteId, 'sgcolors', colorsBytes, sgrColorsContractsVersion, { supported: 5 });
        const gradingBinding = bind(gradingReport.suiteId, 'grading', firstExtensionBytes, gradingReport.contractsVersion, { supported: 4 });
        action({ output, scratch, evidence, report, context, binding, closureBinding, resetBinding, underlineColorBinding, underlineBinding, truecolorBinding, paletteBinding, attrsBinding, rgbBinding, colorsBinding, gradingBinding, write });
    } finally { rmSync(output, { recursive: true, force: true }); }
}

