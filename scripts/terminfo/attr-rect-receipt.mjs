// Receipt binding for the attribute-rectangle qualification (editing.deccara, editing.decrara, editing.decsace). The graded
// evidence stays provisional until the shared cleanup receipt exists, and the check re-derives the whole report from retained
// bytes without the collector. A run carrying this evidence must also carry its saved-state/reset, scrolling/history, charsets,
// line-editing, erase-in-display, character-editing, erase-in-line, SGR closure, SGR selective-reset, SGR-underline-color,
// SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors predecessors: these conclusions sit beside
// theirs, never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { attrRectSuiteId, attrRectContractsVersion } from './attr-rect-contracts.mjs';
import { adaptAttrRectCollector, attrRectAdapterId } from './attr-rect-grading-collector.mjs';
import { adaptEraseLineCollector } from './erase-line-grading-collector.mjs';
import { adaptCharEditCollector } from './char-edit-grading-collector.mjs';
import { adaptEraseDisplayCollector } from './erase-display-grading-collector.mjs';
import { adaptLineEditCollector } from './line-edit-grading-collector.mjs';
import { adaptCharsetsCollector } from './charsets-grading-collector.mjs';
import { adaptScrollbackCollector } from './scrollback-grading-collector.mjs';
import { adaptSavedResetCollector } from './saved-reset-grading-collector.mjs';
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
import { buildAttrRectGradingReport } from './attr-rect-grading.mjs';
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['atrc-observations.json', 'atrc-run.json', 'atrc-snapshots.jsonl', 'collector-atrc.mjs'];

/** Whether a retained artifact path exists. */
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

/** Any retained attribute-rectangle artifact or declaration requires the v32 suffix, and a
 * policy-v26 run always carries them, because the current worker collects this suite
 * unconditionally after the saved-state/reset suite. */
export function includesAttrRectEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v26'].includes(context.sourceSnapshotPolicy)
        || context.attrRectAdapterId !== undefined || context.attrRectSuiteId !== undefined
        || context.attrRectBundleSha256 !== undefined || context.attrRectContractsVersion !== undefined
        || evidence.attrRectEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Every retained artifact must be a regular file inside the output directory: a symlink could point at valid evidence
 * that later disappears, leaving a run that can no longer be rederived. */
function requireRegularFiles(output) {
    for (const file of files) {
        if (!lstatSync(join(output, file)).isFile()) throw new Error(`attribute-rectangle artifact is not a regular file: ${file}`);
    }
}

/** The outcome counts of the case conclusions and of the separately identified effect contracts. */
export function attrRectCounts(report) {
    const counts = {}, effectCounts = {};
    for (const item of report.cases) {
        counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
        effectCounts[item.effectContract.outcome] = (effectCounts[item.effectContract.outcome] ?? 0) + 1;
    }
    return { counts, effectCounts };
}

/** Full rederivation: journal, run log, layered adapter bytes and conclusions. */
export function checkAttrRectEvidence(output, evidence, runId, context) {
    if (context.attrRectAdapterId !== attrRectAdapterId || context.attrRectSuiteId !== attrRectSuiteId) {
        throw new Error('Missing or changed attribute-rectangle adapter declaration.');
    }
    if (context.savedResetAdapterId === undefined || evidence.savedResetEvidence === undefined
        || context.scrollbackAdapterId === undefined || evidence.scrollbackEvidence === undefined
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
        throw new Error('attribute-rectangle evidence requires its saved-state/reset, scrolling/history, charsets, line-editing, erase-in-display, character-editing, erase-in-line, SGR closure, SGR selective-reset, SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors predecessors.');
    }
    const binding = evidence.attrRectEvidence;
    if (binding === undefined) throw new Error('Missing attribute-rectangle suite receipt binding.');
    if (!binding || binding.runId !== runId || binding.suiteId !== attrRectSuiteId
        || binding.file !== 'atrc-observations.json' || binding.runFile !== 'atrc-run.json'
        || binding.provisionalUntil !== 'cell-receipt.json'
        || !/^[a-f\d]{64}$/.test(binding.sha256 ?? '') || !/^[a-f\d]{64}$/.test(binding.runSha256 ?? '')
        || !/^[a-f\d]{64}$/.test(binding.adapterBundleSha256 ?? '')
        || binding.contractsVersion !== attrRectContractsVersion
        || context.attrRectContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid attribute-rectangle suite receipt binding.');
    }
    requireRegularFiles(output);
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('attribute-rectangle observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('attribute-rectangle run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-atrc.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.attrRectBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('attribute-rectangle adapter bytes changed.');
    }
    // The retained extension must be exactly the layered derivation of the retained saved-state/reset extension over the retained scrolling/history extension over the charsets and line-editing extensions over the erase-in-display and character-editing extensions over the erase-in-line extension over the SGR
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
    const savedResetBytes = Buffer.from(adaptSavedResetCollector(scrollbackBytes.toString()));
    const derivedBytes = Buffer.from(adaptAttrRectCollector(savedResetBytes.toString()));
    if (Buffer.compare(derivedBytes, adapterBytes) !== 0) {
        throw new Error('attribute-rectangle adapter is not the retained layered derivation.');
    }
    if (hash(sgrColorsBytes) !== context.sgrColorsBundleSha256 || hash(sgrRgbBytes) !== context.sgrRgbBundleSha256
        || hash(sgrAttrsBytes) !== context.sgrAttrsBundleSha256 || hash(sgrPaletteBytes) !== context.sgrPaletteBundleSha256
        || hash(sgrTruecolorBytes) !== context.sgrTruecolorBundleSha256 || hash(sgrUnderlineBytes) !== context.sgrUnderlineBundleSha256
        || hash(sgrUnderlineColorBytes) !== context.sgrUnderlineColorBundleSha256 || hash(sgrResetBytes) !== context.sgrResetBundleSha256
        || hash(sgrClosureBytes) !== context.sgrClosureBundleSha256 || hash(eraseLineBytes) !== context.eraseLineBundleSha256 || hash(charEditBytes) !== context.charEditBundleSha256
        || hash(eraseDisplayBytes) !== context.eraseDisplayBundleSha256 || hash(lineEditBytes) !== context.lineEditBundleSha256
        || hash(charsetsBytes) !== context.charsetsBundleSha256 || hash(scrollbackBytes) !== context.scrollbackBundleSha256
        || hash(savedResetBytes) !== context.savedResetBundleSha256) {
        throw new Error('attribute-rectangle adapter is not derived from the retained saved-state/reset, scrolling/history, charsets, line-editing, erase-in-display, character-editing and earlier extensions.');
    }
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256
        || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || report.contractsVersion !== binding.contractsVersion) {
        throw new Error('attribute-rectangle report adapter or contract identity differs from the receipt.');
    }
    const journalText = readFileSync(join(output, 'atrc-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('attribute-rectangle journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildAttrRectGradingReport(frames, run, runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('attribute-rectangle observations do not match retained evidence.');
    if (JSON.stringify(frames) !== JSON.stringify(report.snapshots)) throw new Error('attribute-rectangle journal does not match the report.');
    const { counts, effectCounts } = attrRectCounts(report);
    if (JSON.stringify(counts) !== JSON.stringify(binding.counts) || JSON.stringify(effectCounts) !== JSON.stringify(binding.effectCounts)) {
        throw new Error('attribute-rectangle receipt counts do not match the report.');
    }
}
