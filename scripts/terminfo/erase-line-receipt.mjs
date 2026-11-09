// Receipt binding for the erase-in-line qualification. The graded
// evidence stays provisional until the shared cleanup receipt exists, and the
// check re-derives the whole report from retained bytes without the collector.
// A run carrying erase-in-line evidence must also carry its SGR selective-reset, SGR-underline-color,
// SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors
// predecessors: these conclusions sit beside theirs, never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { eraseLineSuiteId, eraseLineContractsVersion } from './erase-line-contracts.mjs';
import { adaptEraseLineCollector, eraseLineAdapterId } from './erase-line-grading-collector.mjs';
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
import { buildEraseLineGradingReport } from './erase-line-grading.mjs';
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['erln-observations.json', 'erln-run.json', 'erln-snapshots.jsonl', 'collector-erln.mjs'];

/** Whether a retained artifact path exists. */
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

/** Any retained erase-in-line artifact or declaration requires the v25 suffix, and a
 * policy-v19 or later run always carries them, because the current worker collects this suite
 * unconditionally after the SGR closure suite. */
export function includesEraseLineEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v19', 'readonly-owned-source-graph-v20', 'readonly-owned-source-graph-v21', 'readonly-owned-source-graph-v22'].includes(context.sourceSnapshotPolicy)
        || context.eraseLineAdapterId !== undefined || context.eraseLineSuiteId !== undefined
        || context.eraseLineBundleSha256 !== undefined || context.eraseLineContractsVersion !== undefined
        || evidence.eraseLineEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Every retained artifact must be a regular file inside the output directory: a symlink could point at valid evidence
 * that later disappears, leaving a run that can no longer be rederived. */
function requireRegularFiles(output) {
    for (const file of files) {
        if (!lstatSync(join(output, file)).isFile()) throw new Error(`erase-in-line artifact is not a regular file: ${file}`);
    }
}

/** Full rederivation: journal, run log, layered adapter bytes and conclusions. */
export function checkEraseLineEvidence(output, evidence, runId, context) {
    if (context.eraseLineAdapterId !== eraseLineAdapterId || context.eraseLineSuiteId !== eraseLineSuiteId) {
        throw new Error('Missing or changed erase-in-line adapter declaration.');
    }
    if (context.sgrClosureAdapterId === undefined || evidence.sgrClosureEvidence === undefined
        || context.sgrResetAdapterId === undefined || evidence.sgrResetEvidence === undefined
        || context.sgrUnderlineColorAdapterId === undefined || evidence.sgrUnderlineColorEvidence === undefined
        || context.sgrUnderlineAdapterId === undefined || evidence.sgrUnderlineEvidence === undefined
        || context.sgrTruecolorAdapterId === undefined || evidence.sgrTruecolorEvidence === undefined
        || context.sgrColorsAdapterId === undefined || evidence.sgrColorsEvidence === undefined
        || context.sgrRgbAdapterId === undefined || evidence.sgrRgbEvidence === undefined
        || context.sgrAttrsAdapterId === undefined || evidence.sgrAttrsEvidence === undefined
        || context.sgrPaletteAdapterId === undefined || evidence.sgrPaletteEvidence === undefined) {
        throw new Error('erase-in-line evidence requires its SGR closure, SGR selective-reset, SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors predecessors.');
    }
    const binding = evidence.eraseLineEvidence;
    if (binding === undefined) throw new Error('Missing erase-in-line suite receipt binding.');
    if (!binding || binding.runId !== runId || binding.suiteId !== eraseLineSuiteId
        || binding.file !== 'erln-observations.json' || binding.runFile !== 'erln-run.json'
        || binding.provisionalUntil !== 'cell-receipt.json'
        || !/^[a-f\d]{64}$/.test(binding.sha256 ?? '') || !/^[a-f\d]{64}$/.test(binding.runSha256 ?? '')
        || !/^[a-f\d]{64}$/.test(binding.adapterBundleSha256 ?? '')
        || binding.contractsVersion !== eraseLineContractsVersion
        || context.eraseLineContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid erase-in-line suite receipt binding.');
    }
    requireRegularFiles(output);
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('erase-in-line observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('erase-in-line run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-erln.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.eraseLineBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('erase-in-line adapter bytes changed.');
    }
    // The retained extension must be exactly the layered derivation of the retained
    // SGR closure extension over the SGR selective-reset, SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette,
    // SGR-attributes, SGR-RGB and SGR-colors extensions over the retained first grading
    // extension over the retained owned adapter.
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
    const derivedBytes = Buffer.from(adaptEraseLineCollector(sgrClosureBytes.toString()));
    if (Buffer.compare(derivedBytes, adapterBytes) !== 0) {
        throw new Error('erase-in-line adapter is not the retained layered derivation.');
    }
    if (hash(sgrColorsBytes) !== context.sgrColorsBundleSha256 || hash(sgrRgbBytes) !== context.sgrRgbBundleSha256
        || hash(sgrAttrsBytes) !== context.sgrAttrsBundleSha256 || hash(sgrPaletteBytes) !== context.sgrPaletteBundleSha256
        || hash(sgrTruecolorBytes) !== context.sgrTruecolorBundleSha256 || hash(sgrUnderlineBytes) !== context.sgrUnderlineBundleSha256
        || hash(sgrUnderlineColorBytes) !== context.sgrUnderlineColorBundleSha256 || hash(sgrResetBytes) !== context.sgrResetBundleSha256
        || hash(sgrClosureBytes) !== context.sgrClosureBundleSha256) {
        throw new Error('erase-in-line adapter is not derived from the retained SGR closure and earlier extensions.');
    }
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256
        || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || report.contractsVersion !== binding.contractsVersion) {
        throw new Error('erase-in-line report adapter or contract identity differs from the receipt.');
    }
    const journalText = readFileSync(join(output, 'erln-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('erase-in-line journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildEraseLineGradingReport(frames, run, runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('erase-in-line observations do not match retained evidence.');
    if (JSON.stringify(frames) !== JSON.stringify(report.snapshots)) throw new Error('erase-in-line journal does not match the report.');
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    if (JSON.stringify(counts) !== JSON.stringify(binding.counts)) throw new Error('erase-in-line receipt counts do not match the report.');
}
