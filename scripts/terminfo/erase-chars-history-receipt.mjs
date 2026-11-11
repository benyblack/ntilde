// Receipt binding for the erase-character / saved-lines qualification (erase.character, erase.screen.scrollback). The graded evidence
// stays provisional until the shared cleanup receipt exists, and the check re-derives the whole report from retained bytes without
// the collector. A run carrying this evidence must also carry its cursor, attribute-rectangle, erase-attributes/margins, saved-state/reset, scrolling/history, charsets, line-editing,
// erase-in-display, character-editing, erase-in-line, SGR closure, SGR selective-reset, SGR-underline-color, SGR-underline,
// SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors predecessors: these conclusions sit beside theirs, never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { eraseCharsHistorySuiteId, eraseCharsHistoryContractsVersion } from './erase-chars-history-contracts.mjs';
import { adaptEraseCharsHistoryCollector, eraseCharsHistoryAdapterId } from './erase-chars-history-grading-collector.mjs';
import { adaptSavedResetCollector } from './saved-reset-grading-collector.mjs';
import { adaptEraseAttrsRegionCollector } from './erase-attrs-region-grading-collector.mjs';
import { adaptAttrRectCollector } from './attr-rect-grading-collector.mjs';
import { adaptCursorQualCollector } from './cursor-qual-grading-collector.mjs';
import { adaptEraseLineCollector } from './erase-line-grading-collector.mjs';
import { adaptCharEditCollector } from './char-edit-grading-collector.mjs';
import { adaptEraseDisplayCollector } from './erase-display-grading-collector.mjs';
import { adaptLineEditCollector } from './line-edit-grading-collector.mjs';
import { adaptCharsetsCollector } from './charsets-grading-collector.mjs';
import { adaptScrollbackCollector } from './scrollback-grading-collector.mjs';
import { adaptSgrResetCollector } from './sgr-reset-grading-collector.mjs';
import { adaptSgrClosureCollector } from './sgr-closure-grading-collector.mjs';
import { adaptSgrUnderlineColorCollector } from './sgr-underline-color-grading-collector.mjs';
import { adaptSgrUnderlineCollector } from './sgr-underline-grading-collector.mjs';
import { adaptSgrTruecolorCollector } from './sgr-truecolor-grading-collector.mjs';
import { adaptSgrPaletteCollector } from './sgr-palette-grading-collector.mjs';
import { adaptSgrAttrsCollector } from './sgr-attrs-grading-collector.mjs';
import { adaptSgrRgbCollector } from './sgr-rgb-grading-collector.mjs';
import { adaptSgrColorsCollector } from './sgr-colors-grading-collector.mjs';
import { adaptGradingCollector } from './grading-collector.mjs';
import { buildEraseCharsHistoryGradingReport } from './erase-chars-history-grading.mjs';
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['echh-observations.json', 'echh-run.json', 'echh-snapshots.jsonl', 'collector-echh.mjs'];
/** The earlier suites whose evidence and adapter declarations must accompany this one: the context carries `<name>AdapterId`, the receipt `<name>Evidence`. */
const predecessors = Object.freeze(['cursorQual', 'attrRect', 'eraseAttrsRegion', 'savedReset', 'scrollback', 'charsets', 'lineEdit', 'eraseDisplay', 'charEdit', 'eraseLine', 'sgrClosure', 'sgrReset',
    'sgrUnderlineColor', 'sgrUnderline', 'sgrTruecolor', 'sgrColors', 'sgrRgb', 'sgrAttrs', 'sgrPalette']);

/** Whether a retained artifact path exists. */
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

/** Any retained erase-character/saved-lines artifact or declaration requires the v32 suffix, and a
 * policy-v29 or v30 run always carries them, because the current worker collects this suite
 * unconditionally after the cursor suite. */
export function includesEraseCharsHistoryEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v29', 'readonly-owned-source-graph-v30', 'readonly-owned-source-graph-v31', 'readonly-owned-source-graph-v32', 'readonly-owned-source-graph-v33', 'readonly-owned-source-graph-v34', 'readonly-owned-source-graph-v35', 'readonly-owned-source-graph-v36', 'readonly-owned-source-graph-v37', 'readonly-owned-source-graph-v38', 'readonly-owned-source-graph-v39', 'readonly-owned-source-graph-v40', 'readonly-owned-source-graph-v41', 'readonly-owned-source-graph-v42'].includes(context.sourceSnapshotPolicy)
        || context.eraseCharsHistoryAdapterId !== undefined || context.eraseCharsHistorySuiteId !== undefined
        || context.eraseCharsHistoryBundleSha256 !== undefined || context.eraseCharsHistoryContractsVersion !== undefined
        || evidence.eraseCharsHistoryEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Every retained artifact must be a regular file inside the output directory: a symlink could point at valid evidence
 * that later disappears, leaving a run that can no longer be rederived. */
function requireRegularFiles(output) {
    for (const file of files) {
        if (!lstatSync(join(output, file)).isFile()) throw new Error(`erase-character/saved-lines artifact is not a regular file: ${file}`);
    }
}

/** The outcome counts of the case conclusions. */
export function eraseCharsHistoryCounts(report) {
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return counts;
}

/** Full rederivation: journal, run log, layered adapter bytes and conclusions. */
export function checkEraseCharsHistoryEvidence(output, evidence, runId, context) {
    if (context.eraseCharsHistoryAdapterId !== eraseCharsHistoryAdapterId || context.eraseCharsHistorySuiteId !== eraseCharsHistorySuiteId) {
        throw new Error('Missing or changed erase-character/saved-lines adapter declaration.');
    }
    if (predecessors.some(name => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) {
        throw new Error('erase-character/saved-lines evidence requires its cursor, attribute-rectangle, erase-attributes/margins, saved-state/reset, scrolling/history, charsets, line-editing, erase-in-display, character-editing, erase-in-line, SGR closure, SGR selective-reset, SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors predecessors.');
    }
    const binding = evidence.eraseCharsHistoryEvidence;
    if (binding === undefined) throw new Error('Missing erase-character/saved-lines suite receipt binding.');
    if (!binding || binding.runId !== runId || binding.suiteId !== eraseCharsHistorySuiteId
        || binding.file !== 'echh-observations.json' || binding.runFile !== 'echh-run.json'
        || binding.provisionalUntil !== 'cell-receipt.json'
        || !/^[a-f\d]{64}$/.test(binding.sha256 ?? '') || !/^[a-f\d]{64}$/.test(binding.runSha256 ?? '')
        || !/^[a-f\d]{64}$/.test(binding.adapterBundleSha256 ?? '')
        || binding.contractsVersion !== eraseCharsHistoryContractsVersion
        || context.eraseCharsHistoryContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid erase-character/saved-lines suite receipt binding.');
    }
    requireRegularFiles(output);
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('erase-character/saved-lines observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('erase-character/saved-lines run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-echh.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.eraseCharsHistoryBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('erase-character/saved-lines adapter bytes changed.');
    }
    // The retained extension must be exactly the layered derivation of the retained cursor extension over the attribute-rectangle extension over the erase-attributes/margins and saved-state/reset and scrolling/history,
    // charsets, line-editing, erase-in-display and character-editing extensions over the erase-in-line extension over the SGR closure, SGR
    // selective-reset, SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors extensions over
    // the retained first grading extension over the retained owned adapter.
    const asciiSgrBytes = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    const sgrColorsBytes = Buffer.from(adaptSgrColorsCollector(asciiSgrBytes.toString()));
    const sgrRgbBytes = Buffer.from(adaptSgrRgbCollector(sgrColorsBytes.toString()));
    const sgrAttrsBytes = Buffer.from(adaptSgrAttrsCollector(sgrRgbBytes.toString()));
    const sgrPaletteBytes = Buffer.from(adaptSgrPaletteCollector(sgrAttrsBytes.toString()));
    const sgrTruecolorBytes = Buffer.from(adaptSgrTruecolorCollector(sgrPaletteBytes.toString()));
    const sgrUnderlineBytes = Buffer.from(adaptSgrUnderlineCollector(sgrTruecolorBytes.toString()));
    const sgrUnderlineColorBytes = Buffer.from(adaptSgrUnderlineColorCollector(sgrUnderlineBytes.toString()));
    const sgrResetBytes = Buffer.from(adaptSgrResetCollector(sgrUnderlineColorBytes.toString()));
    const sgrClosureBytes = Buffer.from(adaptSgrClosureCollector(sgrResetBytes.toString()));
    const eraseLineBytes = Buffer.from(adaptEraseLineCollector(sgrClosureBytes.toString()));
    const charEditBytes = Buffer.from(adaptCharEditCollector(eraseLineBytes.toString()));
    const eraseDisplayBytes = Buffer.from(adaptEraseDisplayCollector(charEditBytes.toString()));
    const lineEditBytes = Buffer.from(adaptLineEditCollector(eraseDisplayBytes.toString()));
    const charsetsBytes = Buffer.from(adaptCharsetsCollector(lineEditBytes.toString()));
    const scrollbackBytes = Buffer.from(adaptScrollbackCollector(charsetsBytes.toString()));
    const savedResetBytes = Buffer.from(adaptSavedResetCollector(scrollbackBytes.toString()));
    const eraseAttrsRegionBytes = Buffer.from(adaptEraseAttrsRegionCollector(savedResetBytes.toString()));
    const attrRectBytes = Buffer.from(adaptAttrRectCollector(eraseAttrsRegionBytes.toString()));
    const cursorQualBytes = Buffer.from(adaptCursorQualCollector(attrRectBytes.toString()));
    const derivedBytes = Buffer.from(adaptEraseCharsHistoryCollector(cursorQualBytes.toString()));
    if (Buffer.compare(derivedBytes, adapterBytes) !== 0) {
        throw new Error('erase-character/saved-lines adapter is not the retained layered derivation.');
    }
    if (hash(sgrColorsBytes) !== context.sgrColorsBundleSha256 || hash(sgrRgbBytes) !== context.sgrRgbBundleSha256
        || hash(sgrAttrsBytes) !== context.sgrAttrsBundleSha256 || hash(sgrPaletteBytes) !== context.sgrPaletteBundleSha256
        || hash(sgrTruecolorBytes) !== context.sgrTruecolorBundleSha256 || hash(sgrUnderlineBytes) !== context.sgrUnderlineBundleSha256
        || hash(sgrUnderlineColorBytes) !== context.sgrUnderlineColorBundleSha256 || hash(sgrResetBytes) !== context.sgrResetBundleSha256
        || hash(sgrClosureBytes) !== context.sgrClosureBundleSha256 || hash(eraseLineBytes) !== context.eraseLineBundleSha256 || hash(charEditBytes) !== context.charEditBundleSha256
        || hash(eraseDisplayBytes) !== context.eraseDisplayBundleSha256 || hash(lineEditBytes) !== context.lineEditBundleSha256
        || hash(charsetsBytes) !== context.charsetsBundleSha256 || hash(scrollbackBytes) !== context.scrollbackBundleSha256
        || hash(savedResetBytes) !== context.savedResetBundleSha256 || hash(eraseAttrsRegionBytes) !== context.eraseAttrsRegionBundleSha256 || hash(attrRectBytes) !== context.attrRectBundleSha256 || hash(cursorQualBytes) !== context.cursorQualBundleSha256) {
        throw new Error('erase-character/saved-lines adapter is not derived from the retained saved-state/reset, scrolling/history, charsets, line-editing, erase-in-display, character-editing and earlier extensions.');
    }
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256
        || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || report.contractsVersion !== binding.contractsVersion) {
        throw new Error('erase-character/saved-lines report adapter or contract identity differs from the receipt.');
    }
    const journalText = readFileSync(join(output, 'echh-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('erase-character/saved-lines journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildEraseCharsHistoryGradingReport(frames, run, runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('erase-character/saved-lines observations do not match retained evidence.');
    if (JSON.stringify(frames) !== JSON.stringify(report.snapshots)) throw new Error('erase-character/saved-lines journal does not match the report.');
    if (JSON.stringify(eraseCharsHistoryCounts(report)) !== JSON.stringify(binding.counts)) {
        throw new Error('erase-character/saved-lines receipt counts do not match the report.');
    }
}
