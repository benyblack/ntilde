// Receipt binding for the saved-state / reset qualification (modes.alt-screen.exit, modes.altscreen-47,
// modes.altscreen-1047, reset.sgr, reset.decaln). The graded evidence stays provisional until the shared cleanup receipt
// exists, and the check re-derives the whole report from retained bytes without the collector. A run carrying this
// evidence must also carry its scrolling/history, charsets, line-editing, erase-in-display, character-editing, erase-in-line, SGR closure, SGR selective-reset, SGR-underline-color, SGR-underline,
// SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors predecessors: these conclusions sit beside theirs,
// never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { savedResetSuiteId, savedResetContractsVersion } from './saved-reset-contracts.mjs';
import { adaptSavedResetCollector, savedResetAdapterId } from './saved-reset-grading-collector.mjs';
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
import { buildSavedResetGradingReport } from './saved-reset-grading.mjs';
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['svrs-observations.json', 'svrs-run.json', 'svrs-snapshots.jsonl', 'collector-svrs.mjs'];

/** Whether a retained artifact path exists. */
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

/** Any retained saved-state/reset artifact or declaration requires the v31 suffix, and a
 * policy-v25 or v26 run always carries them, because the current worker collects this suite
 * unconditionally after the scrolling/history suite. */
export function includesSavedResetEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v25', 'readonly-owned-source-graph-v26', 'readonly-owned-source-graph-v27', 'readonly-owned-source-graph-v28', 'readonly-owned-source-graph-v29', 'readonly-owned-source-graph-v30', 'readonly-owned-source-graph-v31', 'readonly-owned-source-graph-v32', 'readonly-owned-source-graph-v33', 'readonly-owned-source-graph-v34', 'readonly-owned-source-graph-v35', 'readonly-owned-source-graph-v36', 'readonly-owned-source-graph-v37'].includes(context.sourceSnapshotPolicy)
        || context.savedResetAdapterId !== undefined || context.savedResetSuiteId !== undefined
        || context.savedResetBundleSha256 !== undefined || context.savedResetContractsVersion !== undefined
        || evidence.savedResetEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Every retained artifact must be a regular file inside the output directory: a symlink could point at valid evidence
 * that later disappears, leaving a run that can no longer be rederived. */
function requireRegularFiles(output) {
    for (const file of files) {
        if (!lstatSync(join(output, file)).isFile()) throw new Error(`saved-state/reset artifact is not a regular file: ${file}`);
    }
}

/** The outcome counts of the case conclusions and of the separately identified effect contracts. */
export function savedResetCounts(report) {
    const counts = {}, effectCounts = {};
    for (const item of report.cases) {
        counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
        effectCounts[item.effectContract.outcome] = (effectCounts[item.effectContract.outcome] ?? 0) + 1;
    }
    return { counts, effectCounts };
}

/** Full rederivation: journal, run log, layered adapter bytes and conclusions. */
export function checkSavedResetEvidence(output, evidence, runId, context) {
    if (context.savedResetAdapterId !== savedResetAdapterId || context.savedResetSuiteId !== savedResetSuiteId) {
        throw new Error('Missing or changed saved-state/reset adapter declaration.');
    }
    if (context.scrollbackAdapterId === undefined || evidence.scrollbackEvidence === undefined
        || context.charsetsAdapterId === undefined || evidence.charsetsEvidence === undefined
        || context.lineEditAdapterId === undefined || evidence.lineEditEvidence === undefined
        || context.eraseDisplayAdapterId === undefined || evidence.eraseDisplayEvidence === undefined
        || context.charEditAdapterId === undefined || evidence.charEditEvidence === undefined
        || context.eraseLineAdapterId === undefined || evidence.eraseLineEvidence === undefined
        || context.sgrClosureAdapterId === undefined || evidence.sgrClosureEvidence === undefined
        || context.sgrResetAdapterId === undefined || evidence.sgrResetEvidence === undefined
        || context.sgrUnderlineColorAdapterId === undefined || evidence.sgrUnderlineColorEvidence === undefined
        || context.sgrUnderlineAdapterId === undefined || evidence.sgrUnderlineEvidence === undefined
        || context.sgrTruecolorAdapterId === undefined || evidence.sgrTruecolorEvidence === undefined
        || context.sgrColorsAdapterId === undefined || evidence.sgrColorsEvidence === undefined
        || context.sgrRgbAdapterId === undefined || evidence.sgrRgbEvidence === undefined
        || context.sgrAttrsAdapterId === undefined || evidence.sgrAttrsEvidence === undefined
        || context.sgrPaletteAdapterId === undefined || evidence.sgrPaletteEvidence === undefined) {
        throw new Error('saved-state/reset evidence requires its scrolling/history, charsets, line-editing, erase-in-display, character-editing, erase-in-line, SGR closure, SGR selective-reset, SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors predecessors.');
    }
    const binding = evidence.savedResetEvidence;
    if (binding === undefined) throw new Error('Missing saved-state/reset suite receipt binding.');
    if (!binding || binding.runId !== runId || binding.suiteId !== savedResetSuiteId
        || binding.file !== 'svrs-observations.json' || binding.runFile !== 'svrs-run.json'
        || binding.provisionalUntil !== 'cell-receipt.json'
        || !/^[a-f\d]{64}$/.test(binding.sha256 ?? '') || !/^[a-f\d]{64}$/.test(binding.runSha256 ?? '')
        || !/^[a-f\d]{64}$/.test(binding.adapterBundleSha256 ?? '')
        || binding.contractsVersion !== savedResetContractsVersion
        || context.savedResetContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid saved-state/reset suite receipt binding.');
    }
    requireRegularFiles(output);
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('saved-state/reset observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('saved-state/reset run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-svrs.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.savedResetBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('saved-state/reset adapter bytes changed.');
    }
    // The retained extension must be exactly the layered derivation of the retained scrolling/history extension over the charsets and line-editing extensions over the erase-in-display and character-editing extensions over the erase-in-line extension over the SGR
    // closure, SGR selective-reset, SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and
    // SGR-colors extensions over the retained first grading extension over the retained owned adapter.
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
    const derivedBytes = Buffer.from(adaptSavedResetCollector(scrollbackBytes.toString()));
    if (Buffer.compare(derivedBytes, adapterBytes) !== 0) {
        throw new Error('saved-state/reset adapter is not the retained layered derivation.');
    }
    if (hash(sgrColorsBytes) !== context.sgrColorsBundleSha256 || hash(sgrRgbBytes) !== context.sgrRgbBundleSha256
        || hash(sgrAttrsBytes) !== context.sgrAttrsBundleSha256 || hash(sgrPaletteBytes) !== context.sgrPaletteBundleSha256
        || hash(sgrTruecolorBytes) !== context.sgrTruecolorBundleSha256 || hash(sgrUnderlineBytes) !== context.sgrUnderlineBundleSha256
        || hash(sgrUnderlineColorBytes) !== context.sgrUnderlineColorBundleSha256 || hash(sgrResetBytes) !== context.sgrResetBundleSha256
        || hash(sgrClosureBytes) !== context.sgrClosureBundleSha256 || hash(eraseLineBytes) !== context.eraseLineBundleSha256 || hash(charEditBytes) !== context.charEditBundleSha256
        || hash(eraseDisplayBytes) !== context.eraseDisplayBundleSha256 || hash(lineEditBytes) !== context.lineEditBundleSha256
        || hash(charsetsBytes) !== context.charsetsBundleSha256 || hash(scrollbackBytes) !== context.scrollbackBundleSha256) {
        throw new Error('saved-state/reset adapter is not derived from the retained scrolling/history, charsets, line-editing, erase-in-display, character-editing and earlier extensions.');
    }
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256
        || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || report.contractsVersion !== binding.contractsVersion) {
        throw new Error('saved-state/reset report adapter or contract identity differs from the receipt.');
    }
    const journalText = readFileSync(join(output, 'svrs-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('saved-state/reset journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildSavedResetGradingReport(frames, run, runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('saved-state/reset observations do not match retained evidence.');
    if (JSON.stringify(frames) !== JSON.stringify(report.snapshots)) throw new Error('saved-state/reset journal does not match the report.');
    const { counts, effectCounts } = savedResetCounts(report);
    if (JSON.stringify(counts) !== JSON.stringify(binding.counts) || JSON.stringify(effectCounts) !== JSON.stringify(binding.effectCounts)) {
        throw new Error('saved-state/reset receipt counts do not match the report.');
    }
}
