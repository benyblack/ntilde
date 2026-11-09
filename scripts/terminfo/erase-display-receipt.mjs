// Receipt binding for the erase-in-display qualification. The graded
// evidence stays provisional until the shared cleanup receipt exists, and the
// check re-derives the whole report from retained bytes without the collector.
// A run carrying erase-in-display evidence must also carry its erase-in-line, SGR closure, SGR selective-reset, SGR-underline-color,
// SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors
// predecessors: these conclusions sit beside theirs, never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { eraseDisplaySuiteId, eraseDisplayContractsVersion } from './erase-display-contracts.mjs';
import { adaptEraseDisplayCollector, eraseDisplayAdapterId } from './erase-display-grading-collector.mjs';
import { adaptEraseLineCollector } from './erase-line-grading-collector.mjs';
import { adaptCharEditCollector } from './char-edit-grading-collector.mjs';
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
import { buildEraseDisplayGradingReport } from './erase-display-grading.mjs';
/** The predecessor layers in derivation order: declaration key and collector adapter. Each is required evidence of this run. */
const derivationChain = Object.freeze([['sgrColors', adaptSgrColorsCollector], ['sgrRgb', adaptSgrRgbCollector], ['sgrAttrs', adaptSgrAttrsCollector],
    ['sgrPalette', adaptSgrPaletteCollector], ['sgrTruecolor', adaptSgrTruecolorCollector], ['sgrUnderline', adaptSgrUnderlineCollector],
    ['sgrUnderlineColor', adaptSgrUnderlineColorCollector], ['sgrReset', adaptSgrResetCollector], ['sgrClosure', adaptSgrClosureCollector],
    ['eraseLine', adaptEraseLineCollector], ['charEdit', adaptCharEditCollector]]);
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['erdi-observations.json', 'erdi-run.json', 'erdi-snapshots.jsonl', 'collector-erdi.mjs'];

/** Whether a retained artifact path exists. */
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

/** Any retained erase-in-display artifact or declaration requires the v27 suffix, and a
 * policy-v21 run always carries them, because the current worker collects this suite
 * unconditionally after the character-editing suite. */
export function includesEraseDisplayEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v21', 'readonly-owned-source-graph-v22'].includes(context.sourceSnapshotPolicy)
        || context.eraseDisplayAdapterId !== undefined || context.eraseDisplaySuiteId !== undefined
        || context.eraseDisplayBundleSha256 !== undefined || context.eraseDisplayContractsVersion !== undefined
        || evidence.eraseDisplayEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Every retained artifact must be a regular file inside the output directory: a symlink could point at valid evidence
 * that later disappears, leaving a run that can no longer be rederived. */
function requireRegularFiles(output) {
    for (const file of files) {
        if (!lstatSync(join(output, file)).isFile()) throw new Error(`erase-in-display artifact is not a regular file: ${file}`);
    }
}

/** Full rederivation: journal, run log, layered adapter bytes and conclusions. */
export function checkEraseDisplayEvidence(output, evidence, runId, context) {
    if (context.eraseDisplayAdapterId !== eraseDisplayAdapterId || context.eraseDisplaySuiteId !== eraseDisplaySuiteId) {
        throw new Error('Missing or changed erase-in-display adapter declaration.');
    }
    if (derivationChain.some(([key]) => context[`${key}AdapterId`] === undefined || evidence[`${key}Evidence`] === undefined)) {
        throw new Error('erase-in-display evidence requires its character-editing, erase-in-line, SGR closure, SGR selective-reset, SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors predecessors.');
    }
    const binding = evidence.eraseDisplayEvidence;
    if (binding === undefined) throw new Error('Missing erase-in-display suite receipt binding.');
    if (!binding || binding.runId !== runId || binding.suiteId !== eraseDisplaySuiteId
        || binding.file !== 'erdi-observations.json' || binding.runFile !== 'erdi-run.json'
        || binding.provisionalUntil !== 'cell-receipt.json'
        || !/^[a-f\d]{64}$/.test(binding.sha256 ?? '') || !/^[a-f\d]{64}$/.test(binding.runSha256 ?? '')
        || !/^[a-f\d]{64}$/.test(binding.adapterBundleSha256 ?? '')
        || binding.contractsVersion !== eraseDisplayContractsVersion
        || context.eraseDisplayContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid erase-in-display suite receipt binding.');
    }
    requireRegularFiles(output);
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('erase-in-display observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('erase-in-display run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-erdi.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.eraseDisplayBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('erase-in-display adapter bytes changed.');
    }
    // The retained extension must be exactly the layered derivation of the retained
    // erase-in-line extension over the SGR closure extension over the SGR selective-reset, SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette,
    // SGR-attributes, SGR-RGB and SGR-colors extensions over the retained first grading
    // extension over the retained owned adapter.
    // Each layer is derived from the previous one's bytes; every declared layer digest must match its re-derived bytes.
    let layerBytes = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    const drifted = [];
    for (const [key, adapt] of derivationChain) {
        layerBytes = Buffer.from(adapt(layerBytes.toString()));
        if (hash(layerBytes) !== context[`${key}BundleSha256`]) drifted.push(key);
    }
    const derivedBytes = Buffer.from(adaptEraseDisplayCollector(layerBytes.toString()));
    if (Buffer.compare(derivedBytes, adapterBytes) !== 0) {
        throw new Error('erase-in-display adapter is not the retained layered derivation.');
    }
    if (drifted.length > 0) {
        throw new Error('erase-in-display adapter is not derived from the retained character-editing, erase-in-line, SGR closure and earlier extensions.');
    }
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256
        || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || report.contractsVersion !== binding.contractsVersion) {
        throw new Error('erase-in-display report adapter or contract identity differs from the receipt.');
    }
    const journalText = readFileSync(join(output, 'erdi-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('erase-in-display journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildEraseDisplayGradingReport(frames, run, runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('erase-in-display observations do not match retained evidence.');
    if (JSON.stringify(frames) !== JSON.stringify(report.snapshots)) throw new Error('erase-in-display journal does not match the report.');
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    if (JSON.stringify(counts) !== JSON.stringify(binding.counts)) throw new Error('erase-in-display receipt counts do not match the report.');
}
