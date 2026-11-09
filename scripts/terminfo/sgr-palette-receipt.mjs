// Receipt binding for the SGR palette-color qualification. The graded
// evidence stays provisional until the shared cleanup receipt exists, and the
// check re-derives the whole report from retained bytes without the collector.
// A run carrying SGR-palette evidence must also carry its SGR-attributes, SGR-RGB
// and SGR-colors predecessors: these conclusions sit beside theirs, never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { sgrPaletteSuiteId, sgrPaletteContractsVersion } from './sgr-palette-contracts.mjs';
import { adaptSgrPaletteCollector, sgrPaletteAdapterId } from './sgr-palette-grading-collector.mjs';
import { adaptSgrAttrsCollector } from './sgr-attrs-grading-collector.mjs';
import { adaptSgrRgbCollector } from './sgr-rgb-grading-collector.mjs';
import { adaptSgrColorsCollector } from './sgr-colors-grading-collector.mjs';
import { adaptGradingCollector } from './grading-collector.mjs';
import { buildSgrPaletteGradingReport } from './sgr-palette-grading.mjs';
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['sgpal-observations.json', 'sgpal-run.json', 'sgpal-snapshots.jsonl', 'collector-sgpal.mjs'];

/** Whether a retained artifact path exists. */
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

/** Any retained SGR-palette artifact or declaration requires the v17 suffix — and a
 * policy-v11 run always carries them, because the current worker collects this
 * suite unconditionally after the SGR-RGB suite. */
export function includesSgrPaletteEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v11', 'readonly-owned-source-graph-v12', 'readonly-owned-source-graph-v13', 'readonly-owned-source-graph-v14', 'readonly-owned-source-graph-v15', 'readonly-owned-source-graph-v16', 'readonly-owned-source-graph-v17', 'readonly-owned-source-graph-v18', 'readonly-owned-source-graph-v19', 'readonly-owned-source-graph-v20', 'readonly-owned-source-graph-v21', 'readonly-owned-source-graph-v22', 'readonly-owned-source-graph-v23', 'readonly-owned-source-graph-v24', 'readonly-owned-source-graph-v25', 'readonly-owned-source-graph-v26', 'readonly-owned-source-graph-v27', 'readonly-owned-source-graph-v28', 'readonly-owned-source-graph-v29', 'readonly-owned-source-graph-v30', 'readonly-owned-source-graph-v31'].includes(context.sourceSnapshotPolicy)
        || context.sgrPaletteAdapterId !== undefined || context.sgrPaletteSuiteId !== undefined
        || context.sgrPaletteBundleSha256 !== undefined || evidence.sgrPaletteEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Full rederivation: journal, run log, layered adapter bytes and conclusions. */
export function checkSgrPaletteEvidence(output, evidence, runId, context) {
    if (context.sgrPaletteAdapterId !== sgrPaletteAdapterId || context.sgrPaletteSuiteId !== sgrPaletteSuiteId) {
        throw new Error('Missing or changed SGR-palette adapter declaration.');
    }
    if (context.sgrColorsAdapterId === undefined || evidence.sgrColorsEvidence === undefined
        || context.sgrRgbAdapterId === undefined || evidence.sgrRgbEvidence === undefined
        || context.sgrAttrsAdapterId === undefined || evidence.sgrAttrsEvidence === undefined) {
        throw new Error('SGR-palette evidence requires its SGR-colors, SGR-RGB and SGR-attributes predecessors.');
    }
    const binding = evidence.sgrPaletteEvidence;
    if (binding === undefined) throw new Error('Missing SGR-palette suite receipt binding.');
    if (!binding || binding.runId !== runId || binding.suiteId !== sgrPaletteSuiteId
        || binding.file !== 'sgpal-observations.json' || binding.runFile !== 'sgpal-run.json'
        || binding.provisionalUntil !== 'cell-receipt.json'
        || !/^[a-f\d]{64}$/.test(binding.sha256 ?? '') || !/^[a-f\d]{64}$/.test(binding.runSha256 ?? '')
        || !/^[a-f\d]{64}$/.test(binding.adapterBundleSha256 ?? '')
        || binding.contractsVersion !== sgrPaletteContractsVersion
        || context.sgrPaletteContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid SGR-palette suite receipt binding.');
    }
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('SGR-palette observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('SGR-palette run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-sgpal.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.sgrPaletteBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('SGR-palette adapter bytes changed.');
    }
    // The retained extension must be exactly the layered derivation of the
    // retained SGR-attributes extension over the retained SGR-RGB extension over the
    // retained SGR-colors extension over the retained first grading extension over
    // the retained owned adapter.
    const asciiSgrBytes = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    const sgrColorsBytes = Buffer.from(adaptSgrColorsCollector(asciiSgrBytes.toString()));
    const sgrRgbBytes = Buffer.from(adaptSgrRgbCollector(sgrColorsBytes.toString()));
    const sgrAttrsBytes = Buffer.from(adaptSgrAttrsCollector(sgrRgbBytes.toString()));
    const derivedBytes = Buffer.from(adaptSgrPaletteCollector(sgrAttrsBytes.toString()));
    if (Buffer.compare(derivedBytes, adapterBytes) !== 0) {
        throw new Error('SGR-palette adapter is not the retained layered derivation.');
    }
    if (hash(sgrColorsBytes) !== context.sgrColorsBundleSha256 || hash(sgrRgbBytes) !== context.sgrRgbBundleSha256
        || hash(sgrAttrsBytes) !== context.sgrAttrsBundleSha256) {
        throw new Error('SGR-palette adapter is not derived from the retained SGR-colors, SGR-RGB and SGR-attributes extensions.');
    }
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256
        || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || report.contractsVersion !== binding.contractsVersion) {
        throw new Error('SGR-palette report adapter or contract identity differs from the receipt.');
    }
    const journalText = readFileSync(join(output, 'sgpal-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('SGR-palette journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildSgrPaletteGradingReport(frames, run, runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('SGR-palette observations do not match retained evidence.');
    if (JSON.stringify(frames) !== JSON.stringify(report.snapshots)) throw new Error('SGR-palette journal does not match the report.');
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    if (JSON.stringify(counts) !== JSON.stringify(binding.counts)) throw new Error('SGR-palette receipt counts do not match the report.');
}
