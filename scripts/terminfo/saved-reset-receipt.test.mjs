import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { checkEraseLineEvidence } from './erase-line-receipt.mjs';
import { checkCharEditEvidence } from './char-edit-receipt.mjs';
import { adaptCharEditCollector, charEditAdapterId, charEditSeams } from './char-edit-grading-collector.mjs';
import { charEditSuiteId, charEditContractsVersion } from './char-edit-contracts.mjs';
import { buildCharEditGradingReport } from './char-edit-grading.mjs';
import { syntheticCharEditEvidence } from './fixtures/char-edit-evidence.mjs';
import { checkSavedResetEvidence, includesSavedResetEvidence, savedResetCounts } from './saved-reset-receipt.mjs';
import { adaptSavedResetCollector, savedResetAdapterId, savedResetSeams } from './saved-reset-grading-collector.mjs';
import { savedResetSuiteId, savedResetContractsVersion } from './saved-reset-contracts.mjs';
import { buildSavedResetGradingReport } from './saved-reset-grading.mjs';
import { syntheticSavedResetEvidence } from './fixtures/saved-reset-evidence.mjs';
import { checkSgrResetEvidence } from './sgr-reset-receipt.mjs';
import { checkSgrClosureEvidence } from './sgr-closure-receipt.mjs';
import { adaptSgrClosureCollector, sgrClosureAdapterId, sgrClosureSeams } from './sgr-closure-grading-collector.mjs';
import { sgrClosureSuiteId, sgrClosureContractsVersion } from './sgr-closure-contracts.mjs';
import { buildSgrClosureGradingReport } from './sgr-closure-grading.mjs';
import { syntheticSgrClosureEvidence } from './fixtures/sgr-closure-evidence.mjs';
import { checkSgrUnderlineColorEvidence } from './sgr-underline-color-receipt.mjs';
import { checkSgrUnderlineEvidence, includesSgrUnderlineEvidence } from './sgr-underline-receipt.mjs';
import { completeOwnedReceipt } from './receipt.mjs';
import { adaptSgrResetCollector, sgrResetAdapterId, sgrResetSeams } from './sgr-reset-grading-collector.mjs';
import { adaptEraseLineCollector, eraseLineAdapterId, eraseLineSeams } from './erase-line-grading-collector.mjs';
import { adaptSgrUnderlineColorCollector, sgrUnderlineColorAdapterId, sgrUnderlineColorSeams } from './sgr-underline-color-grading-collector.mjs';
import { adaptSgrUnderlineCollector, sgrUnderlineAdapterId, sgrUnderlineSeams } from './sgr-underline-grading-collector.mjs';
import { adaptSgrTruecolorCollector, sgrTruecolorAdapterId, sgrTruecolorSeams } from './sgr-truecolor-grading-collector.mjs';
import { adaptSgrPaletteCollector, sgrPaletteAdapterId, sgrPaletteSeams } from './sgr-palette-grading-collector.mjs';
import { adaptSgrAttrsCollector, sgrAttrsAdapterId } from './sgr-attrs-grading-collector.mjs';
import { adaptSgrRgbCollector, sgrRgbAdapterId } from './sgr-rgb-grading-collector.mjs';
import { adaptSgrColorsCollector } from './sgr-colors-grading-collector.mjs';
import { adaptGradingCollector } from './grading-collector.mjs';
import { sgrTruecolorSuiteId, sgrTruecolorContractsVersion } from './sgr-truecolor-contracts.mjs';
import { buildSgrTruecolorGradingReport } from './sgr-truecolor-grading.mjs';
import { sgrUnderlineSuiteId, sgrUnderlineContractsVersion } from './sgr-underline-contracts.mjs';
import { buildSgrUnderlineGradingReport } from './sgr-underline-grading.mjs';
import { sgrUnderlineColorSuiteId, sgrUnderlineColorContractsVersion } from './sgr-underline-color-contracts.mjs';
import { buildSgrUnderlineColorGradingReport } from './sgr-underline-color-grading.mjs';
import { syntheticSgrUnderlineColorEvidence } from './fixtures/sgr-underline-color-evidence.mjs';
import { syntheticSgrResetEvidence } from './fixtures/sgr-reset-evidence.mjs';
import { sgrResetSuiteId, sgrResetContractsVersion } from './sgr-reset-contracts.mjs';
import { buildSgrResetGradingReport } from './sgr-reset-grading.mjs';
import { eraseLineSuiteId, eraseLineContractsVersion } from './erase-line-contracts.mjs';
import { buildEraseLineGradingReport } from './erase-line-grading.mjs';
import { syntheticEraseLineEvidence } from './fixtures/erase-line-evidence.mjs';
import { syntheticSgrUnderlineEvidence } from './fixtures/sgr-underline-evidence.mjs';
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
const editSeams = charEditSeams.filter(seam => !seam.startsWith('export '));
const savedSeams = savedResetSeams.filter(seam => !seam.startsWith('export '));
// The reset layer's complete measuredReset body already carries the colors layer's two measuredReset seams once.
const colorSeamsWithoutReset = colorSeams.filter(seam => seam !== 'function measuredReset(ctx, id, setup, reset, set, preserve) {'
    && seam !== 'measured setup attributes clear on Y and unrelated attributes remain set');
const syntheticOwnedAdapter = template([...firstSeams, ...colorSeamsWithoutReset, ...attributeSeams, ...paletteSeams, ...truecolorSeams, ...underlineSeams, ...underlineColorSeams, ...resetSeams, ...closureSeams, ...eraseSeams, ...editSeams, ...savedSeams])
    + '\nexport async function collectNtildeRun(assertion) {\n  return collectProbeRun({ ntildeOwner: assertion });\n}\n';
const syntheticFirstExtension = adaptGradingCollector(syntheticOwnedAdapter);
const syntheticColorsExtension = adaptSgrColorsCollector(syntheticFirstExtension);
const syntheticRgbExtension = adaptSgrRgbCollector(syntheticColorsExtension);
const syntheticAttrsExtension = adaptSgrAttrsCollector(syntheticRgbExtension);
const syntheticPaletteExtension = adaptSgrPaletteCollector(syntheticAttrsExtension);
const syntheticTruecolorExtension = adaptSgrTruecolorCollector(syntheticPaletteExtension);
const syntheticUnderlineExtension = adaptSgrUnderlineCollector(syntheticTruecolorExtension);
const syntheticUnderlineColorExtension = adaptSgrUnderlineColorCollector(syntheticUnderlineExtension);
const syntheticResetExtension = adaptSgrResetCollector(syntheticUnderlineColorExtension);
const syntheticClosureExtension = adaptSgrClosureCollector(syntheticResetExtension);
const syntheticEraseExtension = adaptEraseLineCollector(syntheticClosureExtension);
const syntheticCharExtension = adaptCharEditCollector(syntheticEraseExtension);

function fixture(action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-svrs-receipt-'));
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
    const eraseBytes = Buffer.from(syntheticEraseExtension);
    const charBytes = Buffer.from(syntheticCharExtension);
    const savedBytes = Buffer.from(adaptSavedResetCollector(syntheticCharExtension));
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
    const eraseReport = buildEraseLineGradingReport(evidence.frames, evidence.run, runId, evidence.adapter);
    const charEvidence = syntheticCharEditEvidence(runId, hash(charBytes));
    const charReport = buildCharEditGradingReport(charEvidence.frames, charEvidence.run, runId, charEvidence.adapter);
    const savedEvidence = syntheticSavedResetEvidence(runId, hash(savedBytes));
    const report = buildSavedResetGradingReport(savedEvidence.frames, savedEvidence.run, runId, savedEvidence.adapter);
    const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value) + '\n');
    const journal = (file, frames) => writeFileSync(join(output, file), frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
    const context = { runId, sourceSnapshotPolicy: 'readonly-owned-source-graph-v21',
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
        charEditAdapterId, charEditSuiteId, charEditBundleSha256: hash(charBytes), charEditContractsVersion,
        savedResetAdapterId, savedResetSuiteId, savedResetBundleSha256: hash(savedBytes), savedResetContractsVersion,
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
        write('erln-observations.json', eraseReport); write('erln-run.json', evidence.run);
        journal('erln-snapshots.jsonl', evidence.frames);
        write('ched-observations.json', charReport); write('ched-run.json', charEvidence.run);
        journal('ched-snapshots.jsonl', charEvidence.frames);
        write('svrs-observations.json', report); write('svrs-run.json', savedEvidence.run);
        journal('svrs-snapshots.jsonl', savedEvidence.frames);
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
        writeFileSync(join(output, 'collector-ched.mjs'), charBytes);
        writeFileSync(join(output, 'collector-svrs.mjs'), savedBytes);
        write('ownership-proof.json', { completed: true, cleanedUpAt: '2026-11-08T00:00:00Z', cleanupError: null });
        write('context.json', context);
        write('raw.json', { observations: [] });
        const bind = (suiteId, prefix, bytes, version, counts) => ({ runId, suiteId, file: `${prefix}-observations.json`,
            sha256: hash(readFileSync(join(output, `${prefix}-observations.json`))),
            runFile: `${prefix}-run.json`, runSha256: hash(readFileSync(join(output, `${prefix}-run.json`))),
            adapterBundleSha256: hash(bytes), contractsVersion: version, counts, provisionalUntil: 'cell-receipt.json' });
        const resetBinding = bind(sgrResetSuiteId, 'sgsr', resetBytes, sgrResetContractsVersion, { supported: 2 });
        const closureBinding = bind(sgrClosureSuiteId, 'sgcl', closureBytes, sgrClosureContractsVersion, { supported: 3 });
        const charBinding = bind(charEditSuiteId, 'ched', charBytes, charEditContractsVersion, { supported: 3 });
        const eraseBinding = bind(eraseLineSuiteId, 'erln', eraseBytes, eraseLineContractsVersion, { supported: 3 });
        const binding = { ...bind(savedResetSuiteId, 'svrs', savedBytes, savedResetContractsVersion, {}), ...savedResetCounts(report) };
        const underlineColorBinding = bind(sgrUnderlineColorSuiteId, 'sguc', underlineColorBytes, sgrUnderlineColorContractsVersion, { supported: 3 });
        const underlineBinding = bind(sgrUnderlineSuiteId, 'sgus', underlineBytes, sgrUnderlineContractsVersion, { supported: 4 });
        const truecolorBinding = bind(sgrTruecolorSuiteId, 'sgtc', truecolorBytes, sgrTruecolorContractsVersion, { supported: 2 });
        const paletteBinding = bind(sgrPaletteSuiteId, 'sgpal', paletteBytes, sgrPaletteContractsVersion, { supported: 6 });
        const attrsBinding = bind(sgrAttrsSuiteId, 'sgattr', attrsBytes, sgrAttrsContractsVersion, { supported: 5 });
        const rgbBinding = bind(sgrRgbSuiteId, 'sgrgb', rgbBytes, sgrRgbContractsVersion, { supported: 2 });
        const colorsBinding = bind(sgrColorsSuiteId, 'sgcolors', colorsBytes, sgrColorsContractsVersion, { supported: 5 });
        const gradingBinding = bind(gradingReport.suiteId, 'grading', firstExtensionBytes, gradingReport.contractsVersion, { supported: 4 });
        action({ output, scratch, evidence, report, context, binding, eraseBinding, charBinding, closureBinding, resetBinding, underlineColorBinding, underlineBinding, truecolorBinding, paletteBinding, attrsBinding, rgbBinding, colorsBinding, gradingBinding, write });
    } finally { rmSync(output, { recursive: true, force: true }); }
}

const predecessors = f => ({ savedResetEvidence: f.binding, charEditEvidence: f.charBinding, eraseLineEvidence: f.eraseBinding, sgrClosureEvidence: f.closureBinding, sgrResetEvidence: f.resetBinding, sgrUnderlineColorEvidence: f.underlineColorBinding, sgrUnderlineEvidence: f.underlineBinding, sgrTruecolorEvidence: f.truecolorBinding, sgrPaletteEvidence: f.paletteBinding, sgrAttrsEvidence: f.attrsBinding, sgrRgbEvidence: f.rgbBinding, sgrColorsEvidence: f.colorsBinding });

const reasons = /Saved-state|saved-state|Erase-in-line|SGR closure|erase-in-line|SGR selective-reset|SGR-underline|Invalid|Missing|bytes|report|journal|counts|identity|derivation|contracts|differs|predecessor|ENOENT/i;

test('the saved-state/reset receipt binding re-derives the whole report from retained bytes', () => fixture(f => {
    assert.equal(includesSavedResetEvidence(f.output, {}, f.context), true);
    assert.doesNotThrow(() => checkSavedResetEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkCharEditEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkEraseLineEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrClosureEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrResetEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrUnderlineColorEvidence(f.output, predecessors(f), runId, f.context));
}));

test('the shared finalizer publishes receipt schema 27 beside every predecessor suite', () => fixture(f => {
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
    assert.equal(receipt.schemaVersion, 27);
    assert.equal(receipt.savedResetEvidence, f.binding);
    assert.equal(receipt.charEditEvidence, f.charBinding);
    assert.equal(receipt.eraseLineEvidence, f.eraseBinding);
    assert.equal(receipt.sgrClosureEvidence, f.closureBinding);
    assert.equal(receipt.sgrResetEvidence, f.resetBinding);
    assert.equal(receipt.sgrUnderlineColorEvidence, f.underlineColorBinding);
    assert.equal(receipt.sgrUnderlineEvidence, f.underlineBinding);
}));

for (const [name, mutate] of [
    ['changed observation bytes', f => f.write('svrs-observations.json', { changed: true })],
    ['changed run log bytes', f => f.write('svrs-run.json', { changed: true })],
    ['changed adapter bytes', f => writeFileSync(join(f.output, 'collector-svrs.mjs'), Buffer.from('tampered'))],
    ['declared SGR-underline-color layer digest drift', f => {
        const renamed = Buffer.from(syntheticUnderlineColorExtension.replace('ntilde-owned-sgr-underline-color-grading-v1', 'ntilde-owned-renamed-v1'));
        f.context.sgrUnderlineColorBundleSha256 = hash(renamed);
        f.write('context.json', f.context);
    }],
    ['declared SGR-underline layer digest drift', f => {
        const renamed = Buffer.from(syntheticUnderlineExtension.replace('ntilde-owned-sgr-underline-grading-v1', 'ntilde-owned-renamed-v1'));
        f.context.sgrUnderlineBundleSha256 = hash(renamed);
        f.write('context.json', f.context);
    }],
    ['declared SGR-palette layer digest drift', f => {
        const renamed = Buffer.from(syntheticPaletteExtension.replace('ntilde-owned-sgr-palette-grading-v1', 'ntilde-owned-renamed-v1'));
        f.context.sgrPaletteBundleSha256 = hash(renamed);
        f.write('context.json', f.context);
    }],
    ['foreign layered derivation', f => {
        writeFileSync(join(f.output, 'collector-adapter.mjs'), Buffer.concat([Buffer.from(syntheticOwnedAdapter), Buffer.from('// doctored\n')]));
    }],
    ['changed journal', f => writeFileSync(join(f.output, 'svrs-snapshots.jsonl'), '\n')],
    ['changed conclusion counts', f => { f.binding.counts = { supported: 1 }; }],
    ['wrong run identity', f => { f.binding.runId = 'f'.repeat(32); }],
    ['wrong suite identity', f => { f.binding.suiteId = 'ntilde-owned-other-v1'; }],
    ['wrong contracts version', f => { f.binding.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.savedResetContractsVersion = 2; f.write('context.json', f.context); }],
    ['missing adapter declaration', f => { delete f.context.savedResetAdapterId; f.write('context.json', f.context); }],
    ['missing suite declaration', f => { delete f.context.savedResetSuiteId; f.write('context.json', f.context); }],
    ['missing character-editing predecessor declaration', f => { delete f.context.charEditAdapterId; f.write('context.json', f.context); }],
    ['missing character-editing predecessor evidence', f => { delete f.charBinding; }],
    ['declared character-editing layer digest drift', f => {
        const renamed = Buffer.from(syntheticCharExtension.replace('ntilde-owned-char-edit-grading-v1', 'ntilde-owned-renamed-v1'));
        f.context.charEditBundleSha256 = hash(renamed);
        f.write('context.json', f.context);
    }],
    ['missing erase-in-line predecessor declaration', f => { delete f.context.eraseLineAdapterId; f.write('context.json', f.context); }],
    ['missing erase-in-line predecessor evidence', f => { delete f.eraseBinding; }],
    ['declared erase-in-line layer digest drift', f => {
        const renamed = Buffer.from(syntheticEraseExtension.replace('ntilde-owned-erase-line-grading-v1', 'ntilde-owned-renamed-v1'));
        f.context.eraseLineBundleSha256 = hash(renamed);
        f.write('context.json', f.context);
    }],
    ['changed effect counts', f => { f.binding.effectCounts = { supported: 4, unsupported: 1 }; }],
    ['missing effect counts', f => { delete f.binding.effectCounts; }],
    ['missing SGR selective-reset predecessor declaration', f => { delete f.context.sgrResetAdapterId; f.write('context.json', f.context); }],
    ['missing SGR closure predecessor evidence', f => { delete f.closureBinding; }],
    ['missing SGR closure predecessor declaration', f => { delete f.context.sgrClosureAdapterId; f.write('context.json', f.context); }],
    ['declared SGR closure layer digest drift', f => {
        const renamed = Buffer.from(syntheticClosureExtension.replace('ntilde-owned-sgr-closure-grading-v1', 'ntilde-owned-renamed-v1'));
        f.context.sgrClosureBundleSha256 = hash(renamed);
        f.write('context.json', f.context);
    }],
    ['missing SGR selective-reset predecessor evidence', f => { delete f.resetBinding; }],
    ['declared SGR selective-reset layer digest drift', f => {
        const renamed = Buffer.from(syntheticResetExtension.replace('ntilde-owned-sgr-selective-reset-grading-v1', 'ntilde-owned-renamed-v1'));
        f.context.sgrResetBundleSha256 = hash(renamed);
        f.write('context.json', f.context);
    }],
    ['missing SGR-underline-color predecessor declaration', f => { delete f.context.sgrUnderlineColorAdapterId; f.write('context.json', f.context); }],
    ['missing SGR-underline-color predecessor evidence', f => { delete f.underlineColorBinding; }],
    ['missing SGR-underline predecessor evidence', f => { delete f.underlineBinding; }],
    ['missing SGR-truecolor predecessor evidence', f => { delete f.truecolorBinding; }],
    ['missing SGR-palette predecessor evidence', f => { delete f.paletteBinding; }],
    ['missing SGR-attributes predecessor evidence', f => { delete f.attrsBinding; }],
    ['missing SGR-RGB predecessor evidence', f => { delete f.rgbBinding; }],
    ['missing SGR-colors predecessor evidence', f => { delete f.colorsBinding; }],
    ['report adapter digest drift', f => {
        f.report.adapter.bundleSha256 = 'a'.repeat(64);
        f.write('svrs-observations.json', f.report);
        f.binding.sha256 = hash(readFileSync(join(f.output, 'svrs-observations.json')));
    }],
    ['report identity drift', f => {
        f.report.cases[0].conclusion.outcome = 'unsupported';
        f.write('svrs-observations.json', f.report);
        f.binding.sha256 = hash(readFileSync(join(f.output, 'svrs-observations.json')));
    }],
    ['erase-in-line artifact removal downgrade', f => {
        for (const file of ['svrs-observations.json', 'svrs-run.json', 'svrs-snapshots.jsonl', 'collector-svrs.mjs'])
            rmSync(join(f.output, file));
    }],
]) test('Saved-state/reset receipt refuses ' + name, () => fixture(f => {
    mutate(f);
    assert.throws(() => checkSavedResetEvidence(f.output, predecessors(f), runId, f.context), reasons);
    assert.equal(existsSync(join(f.output, 'cell-receipt.json')), false);
}));

test('policy v21 requires the saved-state/reset binding even without artifacts', () => fixture(f => {
    for (const file of ['svrs-observations.json', 'svrs-run.json', 'svrs-snapshots.jsonl', 'collector-svrs.mjs'])
        rmSync(join(f.output, file));
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v21' };
    assert.equal(includesSavedResetEvidence(f.output, {}, context), true);
    assert.throws(() => checkSavedResetEvidence(f.output, predecessors(f), runId, context), /Missing or changed saved-state\/reset adapter declaration/);
}));

test('a retained contracts-version declaration alone admits the suffix so incomplete evidence is rejected', () => fixture(f => {
    for (const file of ['svrs-observations.json', 'svrs-run.json', 'svrs-snapshots.jsonl', 'collector-svrs.mjs']) rmSync(join(f.output, file));
    const context = { savedResetContractsVersion: 1 };
    assert.equal(includesSavedResetEvidence(f.output, {}, context), true);
    assert.throws(() => checkSavedResetEvidence(f.output, {}, runId, context), /Missing or changed saved-state\/reset adapter declaration/);
}));

test('a symlinked retained artifact is refused even when it points at valid evidence', () => fixture(f => {
    for (const file of ['svrs-observations.json', 'svrs-run.json', 'svrs-snapshots.jsonl', 'collector-svrs.mjs']) {
        const target = join(f.scratch, 'elsewhere-' + file);
        mkdirSync(f.scratch, { recursive: true });
        writeFileSync(target, readFileSync(join(f.output, file)));
        rmSync(join(f.output, file));
        symlinkSync(target, join(f.output, file));
        assert.throws(() => checkSavedResetEvidence(f.output, predecessors(f), runId, f.context), /not a regular file/, file);
        rmSync(join(f.output, file));
        writeFileSync(join(f.output, file), readFileSync(target));
    }
    assert.doesNotThrow(() => checkSavedResetEvidence(f.output, predecessors(f), runId, f.context));
}));

test('historical receipts stay valid without any saved-state/reset artifacts (policies v10-v20)', () => {
    const publication = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-attributes-qualification.json', import.meta.url)));
    assert.equal(publication.receipt.schemaVersion, 16);
    assert.equal(publication.receipt.savedResetEvidence, undefined);
    for (const version of [10, 11, 13, 14, 15, 16, 17, 18, 19, 20]) {
        assert.equal(includesSavedResetEvidence('/nonexistent', {}, { sourceSnapshotPolicy: `readonly-owned-source-graph-v${version}` }), false, `v${version}`);
    }
    assert.equal(includesSavedResetEvidence('/nonexistent', {}, {}), false);
});
