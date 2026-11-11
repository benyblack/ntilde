// Receipt binding for the attribute-rectangle qualification (editing.deccara, editing.decrara, editing.decsace). The graded
// evidence stays provisional until the shared cleanup receipt exists, and the check re-derives the whole report from retained
// bytes without the collector. A run carrying this evidence must also carry its erase-attributes/margins, saved-state/reset, scrolling/history, charsets,
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
import { adaptEraseAttrsRegionCollector } from './erase-attrs-region-grading-collector.mjs';
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
/** The predecessor layers in derivation order: declaration key and collector adapter. Each is required evidence of this run. */
const derivationChain = Object.freeze([['sgrColors', adaptSgrColorsCollector], ['sgrRgb', adaptSgrRgbCollector], ['sgrAttrs', adaptSgrAttrsCollector],
    ['sgrPalette', adaptSgrPaletteCollector], ['sgrTruecolor', adaptSgrTruecolorCollector], ['sgrUnderline', adaptSgrUnderlineCollector],
    ['sgrUnderlineColor', adaptSgrUnderlineColorCollector], ['sgrReset', adaptSgrResetCollector], ['sgrClosure', adaptSgrClosureCollector],
    ['eraseLine', adaptEraseLineCollector], ['charEdit', adaptCharEditCollector], ['eraseDisplay', adaptEraseDisplayCollector],
    ['lineEdit', adaptLineEditCollector], ['charsets', adaptCharsetsCollector], ['scrollback', adaptScrollbackCollector],
    ['savedReset', adaptSavedResetCollector], ['eraseAttrsRegion', adaptEraseAttrsRegionCollector]]);
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
    return ['readonly-owned-source-graph-v27', 'readonly-owned-source-graph-v28', 'readonly-owned-source-graph-v29', 'readonly-owned-source-graph-v30', 'readonly-owned-source-graph-v31', 'readonly-owned-source-graph-v32', 'readonly-owned-source-graph-v33', 'readonly-owned-source-graph-v34', 'readonly-owned-source-graph-v35', 'readonly-owned-source-graph-v36', 'readonly-owned-source-graph-v37', 'readonly-owned-source-graph-v38'].includes(context.sourceSnapshotPolicy)
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
    if (derivationChain.some(([key]) => context[`${key}AdapterId`] === undefined || evidence[`${key}Evidence`] === undefined)) {
        throw new Error('attribute-rectangle evidence requires its erase-attributes/margins, saved-state/reset, scrolling/history, charsets, line-editing, erase-in-display, character-editing, erase-in-line, SGR closure, SGR selective-reset, SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors predecessors.');
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
    // The retained extension must be exactly the layered derivation of every retained predecessor extension over the retained first grading
    // extension over the retained owned adapter. Each layer is derived from the previous one's bytes; every declared layer digest must match
    // its re-derived bytes.
    let layerBytes = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    const drifted = [];
    for (const [key, adapt] of derivationChain) {
        layerBytes = Buffer.from(adapt(layerBytes.toString()));
        if (hash(layerBytes) !== context[`${key}BundleSha256`]) drifted.push(key);
    }
    const derivedBytes = Buffer.from(adaptAttrRectCollector(layerBytes.toString()));
    if (Buffer.compare(derivedBytes, adapterBytes) !== 0) {
        throw new Error('attribute-rectangle adapter is not the retained layered derivation.');
    }
    if (drifted.length > 0) {
        throw new Error('attribute-rectangle adapter is not derived from the retained erase-attributes/margins, saved-state/reset, scrolling/history and earlier extensions.');
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
