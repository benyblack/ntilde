// Receipt binding for the SGR selective-reset qualification. The graded
// evidence stays provisional until the shared cleanup receipt exists, and the
// check re-derives the whole report from retained bytes without the collector.
// A run carrying SGR selective-reset evidence must also carry its SGR-underline-color,
// SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors
// predecessors: these conclusions sit beside theirs, never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { sgrResetSuiteId, sgrResetContractsVersion } from './sgr-reset-contracts.mjs';
import { adaptSgrResetCollector, sgrResetAdapterId } from './sgr-reset-grading-collector.mjs';
import { adaptSgrUnderlineColorCollector } from './sgr-underline-color-grading-collector.mjs';
import { adaptSgrUnderlineCollector } from './sgr-underline-grading-collector.mjs';
import { adaptSgrTruecolorCollector } from './sgr-truecolor-grading-collector.mjs';
import { adaptSgrPaletteCollector } from './sgr-palette-grading-collector.mjs';
import { adaptSgrAttrsCollector } from './sgr-attrs-grading-collector.mjs';
import { adaptSgrRgbCollector } from './sgr-rgb-grading-collector.mjs';
import { adaptSgrColorsCollector } from './sgr-colors-grading-collector.mjs';
import { adaptGradingCollector } from './grading-collector.mjs';
import { buildSgrResetGradingReport } from './sgr-reset-grading.mjs';
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['sgsr-observations.json', 'sgsr-run.json', 'sgsr-snapshots.jsonl', 'collector-sgsr.mjs'];

/** Whether a retained artifact path exists. */
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

/** Any retained selective-reset artifact or declaration requires the v23 suffix, and a
 * policy-v17 or v18 run always carries them, because the current worker collects this suite
 * unconditionally after the SGR-underline-color suite. */
export function includesSgrResetEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v17', 'readonly-owned-source-graph-v18', 'readonly-owned-source-graph-v19', 'readonly-owned-source-graph-v20', 'readonly-owned-source-graph-v21', 'readonly-owned-source-graph-v22', 'readonly-owned-source-graph-v23', 'readonly-owned-source-graph-v24', 'readonly-owned-source-graph-v25', 'readonly-owned-source-graph-v26'].includes(context.sourceSnapshotPolicy)
        || context.sgrResetAdapterId !== undefined || context.sgrResetSuiteId !== undefined
        || context.sgrResetBundleSha256 !== undefined || context.sgrResetContractsVersion !== undefined
        || evidence.sgrResetEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Every retained artifact must be a regular file inside the output directory: a symlink could point at valid evidence
 * that later disappears, leaving a run that can no longer be rederived. */
function requireRegularFiles(output) {
    for (const file of files) {
        if (!lstatSync(join(output, file)).isFile()) throw new Error(`SGR selective-reset artifact is not a regular file: ${file}`);
    }
}

/** Full rederivation: journal, run log, layered adapter bytes and conclusions. */
export function checkSgrResetEvidence(output, evidence, runId, context) {
    if (context.sgrResetAdapterId !== sgrResetAdapterId || context.sgrResetSuiteId !== sgrResetSuiteId) {
        throw new Error('Missing or changed SGR selective-reset adapter declaration.');
    }
    if (context.sgrUnderlineColorAdapterId === undefined || evidence.sgrUnderlineColorEvidence === undefined
        || context.sgrUnderlineAdapterId === undefined || evidence.sgrUnderlineEvidence === undefined
        || context.sgrTruecolorAdapterId === undefined || evidence.sgrTruecolorEvidence === undefined
        || context.sgrColorsAdapterId === undefined || evidence.sgrColorsEvidence === undefined
        || context.sgrRgbAdapterId === undefined || evidence.sgrRgbEvidence === undefined
        || context.sgrAttrsAdapterId === undefined || evidence.sgrAttrsEvidence === undefined
        || context.sgrPaletteAdapterId === undefined || evidence.sgrPaletteEvidence === undefined) {
        throw new Error('SGR selective-reset evidence requires its SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors predecessors.');
    }
    const binding = evidence.sgrResetEvidence;
    if (binding === undefined) throw new Error('Missing SGR selective-reset suite receipt binding.');
    if (!binding || binding.runId !== runId || binding.suiteId !== sgrResetSuiteId
        || binding.file !== 'sgsr-observations.json' || binding.runFile !== 'sgsr-run.json'
        || binding.provisionalUntil !== 'cell-receipt.json'
        || !/^[a-f\d]{64}$/.test(binding.sha256 ?? '') || !/^[a-f\d]{64}$/.test(binding.runSha256 ?? '')
        || !/^[a-f\d]{64}$/.test(binding.adapterBundleSha256 ?? '')
        || binding.contractsVersion !== sgrResetContractsVersion
        || context.sgrResetContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid SGR selective-reset suite receipt binding.');
    }
    requireRegularFiles(output);
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('SGR selective-reset observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('SGR selective-reset run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-sgsr.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.sgrResetBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('SGR selective-reset adapter bytes changed.');
    }
    // The retained extension must be exactly the layered derivation of the retained
    // SGR-underline-color extension over the SGR-underline, SGR-truecolor, SGR-palette,
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
    const derivedBytes = Buffer.from(adaptSgrResetCollector(sgrUnderlineColorBytes.toString()));
    if (Buffer.compare(derivedBytes, adapterBytes) !== 0) {
        throw new Error('SGR selective-reset adapter is not the retained layered derivation.');
    }
    if (hash(sgrColorsBytes) !== context.sgrColorsBundleSha256 || hash(sgrRgbBytes) !== context.sgrRgbBundleSha256
        || hash(sgrAttrsBytes) !== context.sgrAttrsBundleSha256 || hash(sgrPaletteBytes) !== context.sgrPaletteBundleSha256
        || hash(sgrTruecolorBytes) !== context.sgrTruecolorBundleSha256 || hash(sgrUnderlineBytes) !== context.sgrUnderlineBundleSha256
        || hash(sgrUnderlineColorBytes) !== context.sgrUnderlineColorBundleSha256) {
        throw new Error('SGR selective-reset adapter is not derived from the retained SGR-underline-color and earlier extensions.');
    }
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256
        || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || report.contractsVersion !== binding.contractsVersion) {
        throw new Error('SGR selective-reset report adapter or contract identity differs from the receipt.');
    }
    const journalText = readFileSync(join(output, 'sgsr-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('SGR selective-reset journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildSgrResetGradingReport(frames, run, runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('SGR selective-reset observations do not match retained evidence.');
    if (JSON.stringify(frames) !== JSON.stringify(report.snapshots)) throw new Error('SGR selective-reset journal does not match the report.');
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    if (JSON.stringify(counts) !== JSON.stringify(binding.counts)) throw new Error('SGR selective-reset receipt counts do not match the report.');
}
