import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { checkEraseLineEvidence } from './erase-line-receipt.mjs';
import { checkScrollbackEvidence } from './scrollback-receipt.mjs';
import { checkSavedResetEvidence, savedResetCounts } from './saved-reset-receipt.mjs';
import { checkEraseAttrsRegionEvidence, eraseAttrsRegionCounts } from './erase-attrs-region-receipt.mjs';
import { adaptEraseAttrsRegionCollector, eraseAttrsRegionAdapterId } from './erase-attrs-region-grading-collector.mjs';
import { eraseAttrsRegionSuiteId, eraseAttrsRegionContractsVersion } from './erase-attrs-region-contracts.mjs';
import { buildEraseAttrsRegionGradingReport } from './erase-attrs-region-grading.mjs';
import { syntheticEraseAttrsRegionEvidence } from './fixtures/erase-attrs-region-evidence.mjs';
import { checkEraseCharsHistoryEvidence, includesEraseCharsHistoryEvidence, eraseCharsHistoryCounts } from './erase-chars-history-receipt.mjs';
import { adaptEraseCharsHistoryCollector, eraseCharsHistoryAdapterId } from './erase-chars-history-grading-collector.mjs';
import { eraseCharsHistorySuiteId, eraseCharsHistoryContractsVersion } from './erase-chars-history-contracts.mjs';
import { buildEraseCharsHistoryGradingReport } from './erase-chars-history-grading.mjs';
import { syntheticEraseCharsHistoryEvidence } from './fixtures/erase-chars-history-evidence.mjs';
import { adaptSavedResetCollector, savedResetAdapterId } from './saved-reset-grading-collector.mjs';
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
import { adaptEraseLineCollector, eraseLineAdapterId } from './erase-line-grading-collector.mjs';
import { adaptCharEditCollector, charEditAdapterId } from './char-edit-grading-collector.mjs';
import { adaptEraseDisplayCollector, eraseDisplayAdapterId } from './erase-display-grading-collector.mjs';
import { adaptLineEditCollector, lineEditAdapterId } from './line-edit-grading-collector.mjs';
import { adaptCharsetsCollector, charsetsAdapterId } from './charsets-grading-collector.mjs';
import { charsetsSuiteId, charsetsContractsVersion } from './charsets-contracts.mjs';
import { buildCharsetsGradingReport } from './charsets-grading.mjs';
import { syntheticCharsetsEvidence } from './fixtures/charsets-evidence.mjs';
import { lineEditSuiteId, lineEditContractsVersion } from './line-edit-contracts.mjs';
import { buildLineEditGradingReport } from './line-edit-grading.mjs';
import { syntheticLineEditEvidence } from './fixtures/line-edit-evidence.mjs';
import { eraseDisplaySuiteId, eraseDisplayContractsVersion } from './erase-display-contracts.mjs';
import { buildEraseDisplayGradingReport } from './erase-display-grading.mjs';
import { syntheticEraseDisplayEvidence } from './fixtures/erase-display-evidence.mjs';
import { charEditSuiteId, charEditContractsVersion } from './char-edit-contracts.mjs';
import { buildCharEditGradingReport } from './char-edit-grading.mjs';
import { syntheticCharEditEvidence } from './fixtures/char-edit-evidence.mjs';
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
import { scrollbackSuiteId, scrollbackContractsVersion } from './scrollback-contracts.mjs';
import { buildScrollbackGradingReport } from './scrollback-grading.mjs';
import { adaptScrollbackCollector, scrollbackAdapterId } from './scrollback-grading-collector.mjs';
import { syntheticScrollbackEvidence } from './fixtures/scrollback-evidence.mjs';
import { adaptCollector } from './collector.mjs';
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
// The retained layered derivation is checked against the REAL pinned bundle (the scrolling/history layer pins the verbatim upstream
// section by digest), derived through every earlier layer exactly as the runner does. Skipped when the collector cache is absent.
const bundleUrl = new URL('../../artifacts/terminfo/tool/node_modules/terminfo.dev/dist/terminfo.bundle.mjs', import.meta.url);
const haveBundle = existsSync(bundleUrl);
const maybe = haveBundle ? test : (name, run) => test(name, { skip: 'collector cache not installed' }, run);
let syntheticOwnedAdapter = '', syntheticFirstExtension = '', syntheticColorsExtension = '', syntheticRgbExtension = '', syntheticAttrsExtension = '',
    syntheticPaletteExtension = '', syntheticTruecolorExtension = '', syntheticUnderlineExtension = '', syntheticUnderlineColorExtension = '',
    syntheticResetExtension = '', syntheticClosureExtension = '', syntheticEraseExtension = '', syntheticCharEditExtension = '', syntheticEraseDisplayExtension = '', syntheticLineEditExtension = '', syntheticCharsetsExtension = '';
if (haveBundle) {
    syntheticOwnedAdapter = adaptCollector(readFileSync(bundleUrl).toString(), new URL('./ownership.mjs', import.meta.url).pathname);
    syntheticFirstExtension = adaptGradingCollector(syntheticOwnedAdapter);
    syntheticColorsExtension = adaptSgrColorsCollector(syntheticFirstExtension);
    syntheticRgbExtension = adaptSgrRgbCollector(syntheticColorsExtension);
    syntheticAttrsExtension = adaptSgrAttrsCollector(syntheticRgbExtension);
    syntheticPaletteExtension = adaptSgrPaletteCollector(syntheticAttrsExtension);
    syntheticTruecolorExtension = adaptSgrTruecolorCollector(syntheticPaletteExtension);
    syntheticUnderlineExtension = adaptSgrUnderlineCollector(syntheticTruecolorExtension);
    syntheticUnderlineColorExtension = adaptSgrUnderlineColorCollector(syntheticUnderlineExtension);
    syntheticResetExtension = adaptSgrResetCollector(syntheticUnderlineColorExtension);
    syntheticClosureExtension = adaptSgrClosureCollector(syntheticResetExtension);
    syntheticEraseExtension = adaptEraseLineCollector(syntheticClosureExtension);
    syntheticCharEditExtension = adaptCharEditCollector(syntheticEraseExtension);
    syntheticEraseDisplayExtension = adaptEraseDisplayCollector(syntheticCharEditExtension);
    syntheticLineEditExtension = adaptLineEditCollector(syntheticEraseDisplayExtension);
    syntheticCharsetsExtension = adaptCharsetsCollector(syntheticLineEditExtension);
}

function fixture(action) {
    const output = mkdtempSync(join(tmpdir(), 'nt-echh-receipt-'));
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
    const charEditBytes = Buffer.from(syntheticCharEditExtension);
    const eraseDisplayBytes = Buffer.from(syntheticEraseDisplayExtension);
    const lineEditBytes = Buffer.from(syntheticLineEditExtension);
    const charsetsBytes = Buffer.from(syntheticCharsetsExtension);
    const syntheticScrollbackExtension = adaptScrollbackCollector(syntheticCharsetsExtension);
    const scrollbackBytes = Buffer.from(syntheticScrollbackExtension);
    const syntheticSavedExtension = adaptSavedResetCollector(syntheticScrollbackExtension);
    const savedBytes = Buffer.from(syntheticSavedExtension);
    const syntheticEraseAttrsExtension = adaptEraseAttrsRegionCollector(syntheticSavedExtension);
    const eraseAttrsBytes = Buffer.from(syntheticEraseAttrsExtension);
    const echBytes = Buffer.from(adaptEraseCharsHistoryCollector(syntheticEraseAttrsExtension));
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
    const eraseEvidence = syntheticEraseLineEvidence(runId, hash(eraseBytes));
    const eraseReport = buildEraseLineGradingReport(eraseEvidence.frames, eraseEvidence.run, runId, eraseEvidence.adapter);
    const charEditEvidence = syntheticCharEditEvidence(runId, hash(charEditBytes));
    const charEditReport = buildCharEditGradingReport(charEditEvidence.frames, charEditEvidence.run, runId, charEditEvidence.adapter);
    const eraseDisplayEvidence = syntheticEraseDisplayEvidence(runId, hash(eraseDisplayBytes));
    const eraseDisplayReport = buildEraseDisplayGradingReport(eraseDisplayEvidence.frames, eraseDisplayEvidence.run, runId, eraseDisplayEvidence.adapter);
    const lineEditEvidence = syntheticLineEditEvidence(runId, hash(lineEditBytes));
    const lineEditReport = buildLineEditGradingReport(lineEditEvidence.frames, lineEditEvidence.run, runId, lineEditEvidence.adapter);
    const charsetsEvidence = syntheticCharsetsEvidence(runId, hash(charsetsBytes));
    const charsetsReport = buildCharsetsGradingReport(charsetsEvidence.frames, charsetsEvidence.run, runId, charsetsEvidence.adapter);
    const evidence = syntheticScrollbackEvidence(runId, hash(scrollbackBytes));
    const report = buildScrollbackGradingReport(evidence.frames, evidence.run, runId, evidence.adapter);
    const savedEvidence = syntheticSavedResetEvidence(runId, hash(savedBytes));
    const savedReport = buildSavedResetGradingReport(savedEvidence.frames, savedEvidence.run, runId, savedEvidence.adapter);
    const eraseAttrsEvidence = syntheticEraseAttrsRegionEvidence(runId, hash(eraseAttrsBytes));
    const eraseAttrsReport = buildEraseAttrsRegionGradingReport(eraseAttrsEvidence.frames, eraseAttrsEvidence.run, runId, eraseAttrsEvidence.adapter);
    const echEvidence = syntheticEraseCharsHistoryEvidence(runId, hash(echBytes));
    const echReport = buildEraseCharsHistoryGradingReport(echEvidence.frames, echEvidence.run, runId, echEvidence.adapter);
    const write = (file, value) => writeFileSync(join(output, file), JSON.stringify(value) + '\n');
    const journal = (file, frames) => writeFileSync(join(output, file), frames.map(frame => JSON.stringify(frame)).join('\n') + '\n');
    const context = { runId, sourceSnapshotPolicy: 'readonly-owned-source-graph-v27',
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
        charsetsAdapterId, charsetsSuiteId, charsetsBundleSha256: hash(charsetsBytes), charsetsContractsVersion,
        lineEditAdapterId, lineEditSuiteId, lineEditBundleSha256: hash(lineEditBytes), lineEditContractsVersion,
        eraseDisplayAdapterId, eraseDisplaySuiteId, eraseDisplayBundleSha256: hash(eraseDisplayBytes), eraseDisplayContractsVersion,
        charEditAdapterId, charEditSuiteId, charEditBundleSha256: hash(charEditBytes), charEditContractsVersion,
        scrollbackAdapterId, scrollbackSuiteId, scrollbackBundleSha256: hash(scrollbackBytes), scrollbackContractsVersion,
        savedResetAdapterId, savedResetSuiteId, savedResetBundleSha256: hash(savedBytes), savedResetContractsVersion,
        eraseAttrsRegionAdapterId, eraseAttrsRegionSuiteId, eraseAttrsRegionBundleSha256: hash(eraseAttrsBytes), eraseAttrsRegionContractsVersion,
        eraseCharsHistoryAdapterId, eraseCharsHistorySuiteId, eraseCharsHistoryBundleSha256: hash(echBytes), eraseCharsHistoryContractsVersion,
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
        write('erln-observations.json', eraseReport); write('erln-run.json', eraseEvidence.run);
        journal('erln-snapshots.jsonl', eraseEvidence.frames);
        write('ched-observations.json', charEditReport); write('ched-run.json', charEditEvidence.run);
        journal('ched-snapshots.jsonl', charEditEvidence.frames);
        write('erdi-observations.json', eraseDisplayReport); write('erdi-run.json', eraseDisplayEvidence.run);
        journal('erdi-snapshots.jsonl', eraseDisplayEvidence.frames);
        write('ledt-observations.json', lineEditReport); write('ledt-run.json', lineEditEvidence.run);
        journal('ledt-snapshots.jsonl', lineEditEvidence.frames);
        write('chrs-observations.json', charsetsReport); write('chrs-run.json', charsetsEvidence.run);
        journal('chrs-snapshots.jsonl', charsetsEvidence.frames);
        write('scrl-observations.json', report); write('scrl-run.json', evidence.run);
        journal('scrl-snapshots.jsonl', evidence.frames);
        write('svrs-observations.json', savedReport); write('svrs-run.json', savedEvidence.run);
        journal('svrs-snapshots.jsonl', savedEvidence.frames);
        write('erar-observations.json', eraseAttrsReport); write('erar-run.json', eraseAttrsEvidence.run);
        journal('erar-snapshots.jsonl', eraseAttrsEvidence.frames);
        write('echh-observations.json', echReport); write('echh-run.json', echEvidence.run);
        journal('echh-snapshots.jsonl', echEvidence.frames);
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
        writeFileSync(join(output, 'collector-ched.mjs'), charEditBytes);
        writeFileSync(join(output, 'collector-svrs.mjs'), savedBytes);
        writeFileSync(join(output, 'collector-erar.mjs'), eraseAttrsBytes);
        writeFileSync(join(output, 'collector-echh.mjs'), echBytes);
        writeFileSync(join(output, 'collector-erdi.mjs'), eraseDisplayBytes);
        writeFileSync(join(output, 'collector-ledt.mjs'), lineEditBytes);
        writeFileSync(join(output, 'collector-chrs.mjs'), charsetsBytes);
        writeFileSync(join(output, 'collector-scrl.mjs'), scrollbackBytes);
        write('ownership-proof.json', { completed: true, cleanedUpAt: '2026-11-08T00:00:00Z', cleanupError: null });
        write('context.json', context);
        write('raw.json', { observations: [] });
        const bind = (suiteId, prefix, bytes, version, counts) => ({ runId, suiteId, file: `${prefix}-observations.json`,
            sha256: hash(readFileSync(join(output, `${prefix}-observations.json`))),
            runFile: `${prefix}-run.json`, runSha256: hash(readFileSync(join(output, `${prefix}-run.json`))),
            adapterBundleSha256: hash(bytes), contractsVersion: version, counts, provisionalUntil: 'cell-receipt.json' });
        const resetBinding = bind(sgrResetSuiteId, 'sgsr', resetBytes, sgrResetContractsVersion, { supported: 2 });
        const closureBinding = bind(sgrClosureSuiteId, 'sgcl', closureBytes, sgrClosureContractsVersion, { supported: 3 });
        const eraseBinding = bind(eraseLineSuiteId, 'erln', eraseBytes, eraseLineContractsVersion, { supported: 3 });
        const charEditBinding = bind(charEditSuiteId, 'ched', charEditBytes, charEditContractsVersion, { supported: 3 });
        const eraseDisplayBinding = bind(eraseDisplaySuiteId, 'erdi', eraseDisplayBytes, eraseDisplayContractsVersion, { supported: 3 });
        const lineEditBinding = bind(lineEditSuiteId, 'ledt', lineEditBytes, lineEditContractsVersion, { supported: 2 });
        const charsetsBinding = bind(charsetsSuiteId, 'chrs', charsetsBytes, charsetsContractsVersion, { supported: 4 });
        const savedResetBinding = { ...bind(savedResetSuiteId, 'svrs', savedBytes, savedResetContractsVersion, {}), ...savedResetCounts(savedReport) };
        const eraseAttrsBinding = { ...bind(eraseAttrsRegionSuiteId, 'erar', eraseAttrsBytes, eraseAttrsRegionContractsVersion, {}), ...eraseAttrsRegionCounts(eraseAttrsReport) };
        const echBinding = bind(eraseCharsHistorySuiteId, 'echh', echBytes, eraseCharsHistoryContractsVersion, eraseCharsHistoryCounts(echReport));
        const binding = bind(scrollbackSuiteId, 'scrl', scrollbackBytes, scrollbackContractsVersion, { supported: 9 });
        const underlineColorBinding = bind(sgrUnderlineColorSuiteId, 'sguc', underlineColorBytes, sgrUnderlineColorContractsVersion, { supported: 3 });
        const underlineBinding = bind(sgrUnderlineSuiteId, 'sgus', underlineBytes, sgrUnderlineContractsVersion, { supported: 4 });
        const truecolorBinding = bind(sgrTruecolorSuiteId, 'sgtc', truecolorBytes, sgrTruecolorContractsVersion, { supported: 2 });
        const paletteBinding = bind(sgrPaletteSuiteId, 'sgpal', paletteBytes, sgrPaletteContractsVersion, { supported: 6 });
        const attrsBinding = bind(sgrAttrsSuiteId, 'sgattr', attrsBytes, sgrAttrsContractsVersion, { supported: 5 });
        const rgbBinding = bind(sgrRgbSuiteId, 'sgrgb', rgbBytes, sgrRgbContractsVersion, { supported: 2 });
        const colorsBinding = bind(sgrColorsSuiteId, 'sgcolors', colorsBytes, sgrColorsContractsVersion, { supported: 5 });
        const gradingBinding = bind(gradingReport.suiteId, 'grading', firstExtensionBytes, gradingReport.contractsVersion, { supported: 4 });
        action({ output, scratch, evidence, report, context, binding, savedResetBinding, savedResetReport: savedReport, echBinding, echReport, eraseAttrsBinding, eraseAttrsReport, eraseAttrsText: syntheticEraseAttrsExtension, savedText: syntheticSavedExtension, scrollbackText: syntheticScrollbackExtension, charsetsBinding, lineEditBinding, eraseDisplayBinding, charEditBinding, eraseBinding, closureBinding, resetBinding, underlineColorBinding, underlineBinding, truecolorBinding, paletteBinding, attrsBinding, rgbBinding, colorsBinding, gradingBinding, write });
    } finally { rmSync(output, { recursive: true, force: true }); }
}

const predecessors = f => ({ eraseCharsHistoryEvidence: f.echBinding, eraseAttrsRegionEvidence: f.eraseAttrsBinding, savedResetEvidence: f.savedResetBinding, scrollbackEvidence: f.binding, charsetsEvidence: f.charsetsBinding, lineEditEvidence: f.lineEditBinding, eraseDisplayEvidence: f.eraseDisplayBinding, charEditEvidence: f.charEditBinding, eraseLineEvidence: f.eraseBinding, sgrClosureEvidence: f.closureBinding, sgrResetEvidence: f.resetBinding, sgrUnderlineColorEvidence: f.underlineColorBinding, sgrUnderlineEvidence: f.underlineBinding, sgrTruecolorEvidence: f.truecolorBinding, sgrPaletteEvidence: f.paletteBinding, sgrAttrsEvidence: f.attrsBinding, sgrRgbEvidence: f.rgbBinding, sgrColorsEvidence: f.colorsBinding });

const reasons = /Erase-character|erase-character|observations|Saved-state|saved-state|Invalid|Missing|bytes|report|journal|counts|identity|derivation|contracts|differs|predecessor|ENOENT/i;
const ownFiles = ['echh-observations.json', 'echh-run.json', 'echh-snapshots.jsonl', 'collector-echh.mjs'];

maybe('the erase-character/saved-lines receipt binding re-derives the whole report from retained bytes', () => fixture(f => {
    assert.equal(includesEraseCharsHistoryEvidence(f.output, {}, f.context), true);
    assert.doesNotThrow(() => checkEraseCharsHistoryEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkEraseAttrsRegionEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSavedResetEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkScrollbackEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkEraseLineEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrClosureEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrResetEvidence(f.output, predecessors(f), runId, f.context));
    assert.doesNotThrow(() => checkSgrUnderlineColorEvidence(f.output, predecessors(f), runId, f.context));
    assert.deepEqual(f.echBinding.counts, { supported: 2 });
}));

maybe('the shared finalizer publishes receipt schema 33 beside every predecessor suite', () => fixture(f => {
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
    assert.equal(receipt.schemaVersion, 33);
    assert.equal(receipt.eraseAttrsRegionEvidence, f.eraseAttrsBinding);
    assert.equal(receipt.eraseCharsHistoryEvidence, f.echBinding);
    assert.equal(receipt.savedResetEvidence, f.savedResetBinding);
    assert.equal(receipt.scrollbackEvidence, f.binding);
    assert.equal(receipt.charsetsEvidence, f.charsetsBinding);
    assert.equal(receipt.sgrColorsEvidence, f.colorsBinding);
}));

const drift = (extensionKey, bundleKey, from) => f => {
    const renamed = Buffer.from(f[extensionKey].replace(from, 'ntilde-owned-renamed-v1'));
    f.context[bundleKey] = hash(renamed);
    f.write('context.json', f.context);
};
for (const [name, mutate] of [
    ['changed observation bytes', f => f.write('echh-observations.json', { changed: true })],
    ['changed run log bytes', f => f.write('echh-run.json', { changed: true })],
    ['changed adapter bytes', f => writeFileSync(join(f.output, 'collector-echh.mjs'), Buffer.from('tampered'))],
    ['foreign layered derivation', f => writeFileSync(join(f.output, 'collector-adapter.mjs'), Buffer.concat([Buffer.from(syntheticOwnedAdapter), Buffer.from('// doctored\n')]))],
    ['changed journal', f => writeFileSync(join(f.output, 'echh-snapshots.jsonl'), '\n')],
    ['changed conclusion counts', f => { f.echBinding.counts = { supported: 1 }; }],
    ['wrong run identity', f => { f.echBinding.runId = 'f'.repeat(32); }],
    ['wrong suite identity', f => { f.echBinding.suiteId = 'ntilde-owned-other-v1'; }],
    ['wrong contracts version', f => { f.echBinding.contractsVersion = 2; }],
    ['declared contracts drift', f => { f.context.eraseCharsHistoryContractsVersion = 2; f.write('context.json', f.context); }],
    ['missing adapter declaration', f => { delete f.context.eraseCharsHistoryAdapterId; f.write('context.json', f.context); }],
    ['missing suite declaration', f => { delete f.context.eraseCharsHistorySuiteId; f.write('context.json', f.context); }],
    ['missing erase-attributes/margins predecessor evidence', f => { delete f.eraseAttrsBinding; }],
    ['missing erase-attributes/margins predecessor declaration', f => { delete f.context.eraseAttrsRegionAdapterId; f.write('context.json', f.context); }],
    ['declared erase-attributes/margins layer digest drift', drift('eraseAttrsText', 'eraseAttrsRegionBundleSha256', 'ntilde-owned-erase-attrs-region-grading-v1')],
    ['missing saved-state/reset predecessor evidence', f => { delete f.savedResetBinding; }],
    ['missing saved-state/reset predecessor declaration', f => { delete f.context.savedResetAdapterId; f.write('context.json', f.context); }],
    ['declared saved-state/reset layer digest drift', drift('savedText', 'savedResetBundleSha256', 'ntilde-owned-saved-reset-grading-v1')],
    ['missing scrolling/history predecessor evidence', f => { delete f.binding; }],
    ['missing scrolling/history predecessor declaration', f => { delete f.context.scrollbackAdapterId; f.write('context.json', f.context); }],
    ['declared scrolling/history layer digest drift', drift('scrollbackText', 'scrollbackBundleSha256', 'ntilde-owned-scrollback-grading-v1')],
    ['missing charsets predecessor evidence', f => { delete f.charsetsBinding; }],
    ['missing line-editing predecessor evidence', f => { delete f.lineEditBinding; }],
    ['missing erase-in-display predecessor evidence', f => { delete f.eraseDisplayBinding; }],
    ['missing character-editing predecessor evidence', f => { delete f.charEditBinding; }],
    ['missing character-editing predecessor declaration', f => { delete f.context.charEditAdapterId; f.write('context.json', f.context); }],
    ['missing erase-in-line predecessor evidence', f => { delete f.eraseBinding; }],
    ['missing SGR closure predecessor evidence', f => { delete f.closureBinding; }],
    ['missing SGR selective-reset predecessor evidence', f => { delete f.resetBinding; }],
    ['missing SGR-underline-color predecessor evidence', f => { delete f.underlineColorBinding; }],
    ['missing SGR-underline predecessor evidence', f => { delete f.underlineBinding; }],
    ['missing SGR-truecolor predecessor evidence', f => { delete f.truecolorBinding; }],
    ['missing SGR-palette predecessor evidence', f => { delete f.paletteBinding; }],
    ['missing SGR-attributes predecessor evidence', f => { delete f.attrsBinding; }],
    ['missing SGR-RGB predecessor evidence', f => { delete f.rgbBinding; }],
    ['missing SGR-colors predecessor evidence', f => { delete f.colorsBinding; }],
    ['report adapter digest drift', f => {
        f.echReport.adapter.bundleSha256 = 'a'.repeat(64);
        f.write('echh-observations.json', f.echReport);
        f.echBinding.sha256 = hash(readFileSync(join(f.output, 'echh-observations.json')));
    }],
    ['report identity drift', f => {
        f.echReport.cases[0].conclusion.outcome = 'inconclusive';
        f.write('echh-observations.json', f.echReport);
        f.echBinding.sha256 = hash(readFileSync(join(f.output, 'echh-observations.json')));
    }],
    ['erase-character/saved-lines artifact removal downgrade', f => { for (const file of ownFiles) rmSync(join(f.output, file)); }],
]) maybe('Erase-character/saved-lines receipt refuses ' + name, () => fixture(f => {
    mutate(f);
    assert.throws(() => checkEraseCharsHistoryEvidence(f.output, predecessors(f), runId, f.context), reasons);
    assert.equal(existsSync(join(f.output, 'cell-receipt.json')), false);
}));

// Unconditional (no collector cache needed): the policy alone admits the suffix, whatever the output directory holds, so a regression that
// dropped policy-only admission cannot pass CI on a runner without the cache.
test('policy v27 alone admits the suffix and refuses it without a declaration, with an empty output directory', () => {
    const output = mkdtempSync(join(tmpdir(), 'nt-echh-policy-'));
    try {
        const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v27' };
        assert.equal(includesEraseCharsHistoryEvidence(output, {}, context), true);
        assert.throws(() => checkEraseCharsHistoryEvidence(output, {}, runId, context), /Missing or changed erase-character.saved-lines adapter declaration/);
        assert.equal(includesEraseCharsHistoryEvidence(output, {}, { sourceSnapshotPolicy: 'readonly-owned-source-graph-v26' }), false);
        assert.equal(includesEraseCharsHistoryEvidence(output, {}, {}), false);
    } finally { rmSync(output, { recursive: true, force: true }); }
});

maybe('policy v27 requires the erase-character/saved-lines binding even without artifacts', () => fixture(f => {
    for (const file of ownFiles) rmSync(join(f.output, file));
    const context = { sourceSnapshotPolicy: 'readonly-owned-source-graph-v27' };
    assert.equal(includesEraseCharsHistoryEvidence(f.output, {}, context), true);
    assert.throws(() => checkEraseCharsHistoryEvidence(f.output, predecessors(f), runId, context), /Missing or changed erase-character.saved-lines adapter declaration/);
}));

maybe('a retained contracts-version declaration alone admits the suffix so incomplete evidence is rejected', () => fixture(f => {
    for (const file of ownFiles) rmSync(join(f.output, file));
    const context = { eraseCharsHistoryContractsVersion: 1 };
    assert.equal(includesEraseCharsHistoryEvidence(f.output, {}, context), true);
    assert.throws(() => checkEraseCharsHistoryEvidence(f.output, {}, runId, context), /Missing or changed erase-character.saved-lines adapter declaration/);
}));

maybe('a symlinked retained artifact is refused even when it points at valid evidence', () => fixture(f => {
    for (const file of ownFiles) {
        const target = join(f.scratch, 'elsewhere-' + file);
        mkdirSync(f.scratch, { recursive: true });
        writeFileSync(target, readFileSync(join(f.output, file)));
        rmSync(join(f.output, file));
        symlinkSync(target, join(f.output, file));
        assert.throws(() => checkEraseCharsHistoryEvidence(f.output, predecessors(f), runId, f.context), /not a regular file/, file);
        rmSync(join(f.output, file));
        writeFileSync(join(f.output, file), readFileSync(target));
    }
    assert.doesNotThrow(() => checkEraseCharsHistoryEvidence(f.output, predecessors(f), runId, f.context));
}));

maybe('historical receipts stay valid without any erase-character/saved-lines artifacts (policies v10-v26)', () => {
    const publication = JSON.parse(readFileSync(new URL('../../docs/qa/terminfo/2026-11-08-sgr-attributes-qualification.json', import.meta.url)));
    assert.equal(publication.receipt.schemaVersion, 16);
    assert.equal(publication.receipt.eraseCharsHistoryEvidence, undefined);
    for (const version of [10, 11, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26]) {
        assert.equal(includesEraseCharsHistoryEvidence('/nonexistent', {}, { sourceSnapshotPolicy: `readonly-owned-source-graph-v${version}` }), false, `v${version}`);
    }
    assert.equal(includesEraseCharsHistoryEvidence('/nonexistent', {}, {}), false);
    assert.equal(typeof savedResetCounts, 'function');
});
