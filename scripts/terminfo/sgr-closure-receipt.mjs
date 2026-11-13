// Receipt binding for the SGR closure qualification. The graded
// evidence stays provisional until the shared cleanup receipt exists, and the
// check re-derives the whole report from retained bytes without the collector.
// A run carrying SGR closure evidence must also carry its SGR selective-reset, SGR-underline-color,
// SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors
// predecessors: these conclusions sit beside theirs, never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { sgrClosureSuiteId, sgrClosureContractsVersion } from './sgr-closure-contracts.mjs';
import { adaptSgrClosureCollector, sgrClosureAdapterId } from './sgr-closure-grading-collector.mjs';
import { adaptSgrResetCollector } from './sgr-reset-grading-collector.mjs';
import { adaptSgrUnderlineColorCollector } from './sgr-underline-color-grading-collector.mjs';
import { adaptSgrUnderlineCollector } from './sgr-underline-grading-collector.mjs';
import { adaptSgrTruecolorCollector } from './sgr-truecolor-grading-collector.mjs';
import { adaptSgrPaletteCollector } from './sgr-palette-grading-collector.mjs';
import { adaptSgrAttrsCollector } from './sgr-attrs-grading-collector.mjs';
import { adaptSgrRgbCollector } from './sgr-rgb-grading-collector.mjs';
import { adaptSgrColorsCollector } from './sgr-colors-grading-collector.mjs';
import { adaptGradingCollector } from './grading-collector.mjs';
import { buildSgrClosureGradingReport } from './sgr-closure-grading.mjs';
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['sgcl-observations.json', 'sgcl-run.json', 'sgcl-snapshots.jsonl', 'collector-sgcl.mjs'];

/** Whether a retained artifact path exists. */
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

/** Any retained selective-reset artifact or declaration requires the v24 suffix, and a
 * policy-v18 run always carries them, because the current worker collects this suite
 * unconditionally after the SGR-underline-color suite. */
export function includesSgrClosureEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v18', 'readonly-owned-source-graph-v19', 'readonly-owned-source-graph-v20', 'readonly-owned-source-graph-v21', 'readonly-owned-source-graph-v22', 'readonly-owned-source-graph-v23', 'readonly-owned-source-graph-v24', 'readonly-owned-source-graph-v25', 'readonly-owned-source-graph-v26', 'readonly-owned-source-graph-v27', 'readonly-owned-source-graph-v28', 'readonly-owned-source-graph-v29', 'readonly-owned-source-graph-v30', 'readonly-owned-source-graph-v31', 'readonly-owned-source-graph-v32', 'readonly-owned-source-graph-v33', 'readonly-owned-source-graph-v34', 'readonly-owned-source-graph-v35', 'readonly-owned-source-graph-v36', 'readonly-owned-source-graph-v37', 'readonly-owned-source-graph-v38', 'readonly-owned-source-graph-v39', 'readonly-owned-source-graph-v40', 'readonly-owned-source-graph-v41', 'readonly-owned-source-graph-v42', 'readonly-owned-source-graph-v43', 'readonly-owned-source-graph-v44', 'readonly-owned-source-graph-v45', 'readonly-owned-source-graph-v46', 'readonly-owned-source-graph-v47', 'readonly-owned-source-graph-v48', 'readonly-owned-source-graph-v49', 'readonly-owned-source-graph-v50', 'readonly-owned-source-graph-v51'].includes(context.sourceSnapshotPolicy)
        || context.sgrClosureAdapterId !== undefined || context.sgrClosureSuiteId !== undefined
        || context.sgrClosureBundleSha256 !== undefined || context.sgrClosureContractsVersion !== undefined
        || evidence.sgrClosureEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Every retained artifact must be a regular file inside the output directory: a symlink could point at valid evidence
 * that later disappears, leaving a run that can no longer be rederived. */
function requireRegularFiles(output) {
    for (const file of files) {
        if (!lstatSync(join(output, file)).isFile()) throw new Error(`SGR closure artifact is not a regular file: ${file}`);
    }
}

/** Full rederivation: journal, run log, layered adapter bytes and conclusions. */
export function checkSgrClosureEvidence(output, evidence, runId, context) {
    if (context.sgrClosureAdapterId !== sgrClosureAdapterId || context.sgrClosureSuiteId !== sgrClosureSuiteId) {
        throw new Error('Missing or changed SGR closure adapter declaration.');
    }
    if (context.sgrResetAdapterId === undefined || evidence.sgrResetEvidence === undefined
        || context.sgrUnderlineColorAdapterId === undefined || evidence.sgrUnderlineColorEvidence === undefined
        || context.sgrUnderlineAdapterId === undefined || evidence.sgrUnderlineEvidence === undefined
        || context.sgrTruecolorAdapterId === undefined || evidence.sgrTruecolorEvidence === undefined
        || context.sgrColorsAdapterId === undefined || evidence.sgrColorsEvidence === undefined
        || context.sgrRgbAdapterId === undefined || evidence.sgrRgbEvidence === undefined
        || context.sgrAttrsAdapterId === undefined || evidence.sgrAttrsEvidence === undefined
        || context.sgrPaletteAdapterId === undefined || evidence.sgrPaletteEvidence === undefined) {
        throw new Error('SGR closure evidence requires its SGR selective-reset, SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors predecessors.');
    }
    const binding = evidence.sgrClosureEvidence;
    if (binding === undefined) throw new Error('Missing SGR closure suite receipt binding.');
    if (!binding || binding.runId !== runId || binding.suiteId !== sgrClosureSuiteId
        || binding.file !== 'sgcl-observations.json' || binding.runFile !== 'sgcl-run.json'
        || binding.provisionalUntil !== 'cell-receipt.json'
        || !/^[a-f\d]{64}$/.test(binding.sha256 ?? '') || !/^[a-f\d]{64}$/.test(binding.runSha256 ?? '')
        || !/^[a-f\d]{64}$/.test(binding.adapterBundleSha256 ?? '')
        || binding.contractsVersion !== sgrClosureContractsVersion
        || context.sgrClosureContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid SGR closure suite receipt binding.');
    }
    requireRegularFiles(output);
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('SGR closure observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('SGR closure run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-sgcl.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.sgrClosureBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('SGR closure adapter bytes changed.');
    }
    // The retained extension must be exactly the layered derivation of the retained
    // SGR selective-reset extension over the SGR-underline-color extension over the SGR-underline, SGR-truecolor, SGR-palette,
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
    const derivedBytes = Buffer.from(adaptSgrClosureCollector(sgrResetBytes.toString()));
    if (Buffer.compare(derivedBytes, adapterBytes) !== 0) {
        throw new Error('SGR closure adapter is not the retained layered derivation.');
    }
    if (hash(sgrColorsBytes) !== context.sgrColorsBundleSha256 || hash(sgrRgbBytes) !== context.sgrRgbBundleSha256
        || hash(sgrAttrsBytes) !== context.sgrAttrsBundleSha256 || hash(sgrPaletteBytes) !== context.sgrPaletteBundleSha256
        || hash(sgrTruecolorBytes) !== context.sgrTruecolorBundleSha256 || hash(sgrUnderlineBytes) !== context.sgrUnderlineBundleSha256
        || hash(sgrUnderlineColorBytes) !== context.sgrUnderlineColorBundleSha256 || hash(sgrResetBytes) !== context.sgrResetBundleSha256) {
        throw new Error('SGR closure adapter is not derived from the retained SGR selective-reset and earlier extensions.');
    }
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256
        || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || report.contractsVersion !== binding.contractsVersion) {
        throw new Error('SGR closure report adapter or contract identity differs from the receipt.');
    }
    const journalText = readFileSync(join(output, 'sgcl-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('SGR closure journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildSgrClosureGradingReport(frames, run, runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('SGR closure observations do not match retained evidence.');
    if (JSON.stringify(frames) !== JSON.stringify(report.snapshots)) throw new Error('SGR closure journal does not match the report.');
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    if (JSON.stringify(counts) !== JSON.stringify(binding.counts)) throw new Error('SGR closure receipt counts do not match the report.');
}

