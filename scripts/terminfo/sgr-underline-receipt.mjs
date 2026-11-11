// Receipt binding for the SGR underline-style qualification. The graded
// evidence stays provisional until the shared cleanup receipt exists, and the
// check re-derives the whole report from retained bytes without the collector.
// A run carrying SGR-underline evidence must also carry its SGR-truecolor, SGR-palette,
// SGR-attributes, SGR-RGB and SGR-colors predecessors: these conclusions sit beside
// theirs, never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { sgrUnderlineSuiteId, sgrUnderlineContractsVersion } from './sgr-underline-contracts.mjs';
import { adaptSgrUnderlineCollector, sgrUnderlineAdapterId } from './sgr-underline-grading-collector.mjs';
import { adaptSgrTruecolorCollector } from './sgr-truecolor-grading-collector.mjs';
import { adaptSgrPaletteCollector } from './sgr-palette-grading-collector.mjs';
import { adaptSgrAttrsCollector } from './sgr-attrs-grading-collector.mjs';
import { adaptSgrRgbCollector } from './sgr-rgb-grading-collector.mjs';
import { adaptSgrColorsCollector } from './sgr-colors-grading-collector.mjs';
import { adaptGradingCollector } from './grading-collector.mjs';
import { buildSgrUnderlineGradingReport } from './sgr-underline-grading.mjs';
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['sgus-observations.json', 'sgus-run.json', 'sgus-snapshots.jsonl', 'collector-sgus.mjs'];

/** Whether a retained artifact path exists. */
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

/** Any retained SGR-underline artifact or declaration requires the v20 suffix — and a
 * policy-v14 run always carries them, because the current worker collects this
 * suite unconditionally after the truecolor-finding suite. */
export function includesSgrUnderlineEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v14', 'readonly-owned-source-graph-v15', 'readonly-owned-source-graph-v16', 'readonly-owned-source-graph-v17', 'readonly-owned-source-graph-v18', 'readonly-owned-source-graph-v19', 'readonly-owned-source-graph-v20', 'readonly-owned-source-graph-v21', 'readonly-owned-source-graph-v22', 'readonly-owned-source-graph-v23', 'readonly-owned-source-graph-v24', 'readonly-owned-source-graph-v25', 'readonly-owned-source-graph-v26', 'readonly-owned-source-graph-v27', 'readonly-owned-source-graph-v28', 'readonly-owned-source-graph-v29', 'readonly-owned-source-graph-v30', 'readonly-owned-source-graph-v31', 'readonly-owned-source-graph-v32', 'readonly-owned-source-graph-v33', 'readonly-owned-source-graph-v34', 'readonly-owned-source-graph-v35', 'readonly-owned-source-graph-v36', 'readonly-owned-source-graph-v37', 'readonly-owned-source-graph-v38'].includes(context.sourceSnapshotPolicy)
        || context.sgrUnderlineAdapterId !== undefined || context.sgrUnderlineSuiteId !== undefined
        || context.sgrUnderlineBundleSha256 !== undefined || context.sgrUnderlineContractsVersion !== undefined
        || evidence.sgrUnderlineEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Full rederivation: journal, run log, layered adapter bytes and conclusions. */
export function checkSgrUnderlineEvidence(output, evidence, runId, context) {
    if (context.sgrUnderlineAdapterId !== sgrUnderlineAdapterId || context.sgrUnderlineSuiteId !== sgrUnderlineSuiteId) {
        throw new Error('Missing or changed SGR-underline adapter declaration.');
    }
    if (context.sgrTruecolorAdapterId === undefined || evidence.sgrTruecolorEvidence === undefined
        || context.sgrColorsAdapterId === undefined || evidence.sgrColorsEvidence === undefined
        || context.sgrRgbAdapterId === undefined || evidence.sgrRgbEvidence === undefined
        || context.sgrAttrsAdapterId === undefined || evidence.sgrAttrsEvidence === undefined
        || context.sgrPaletteAdapterId === undefined || evidence.sgrPaletteEvidence === undefined) {
        throw new Error('SGR-underline evidence requires its SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors predecessors.');
    }
    const binding = evidence.sgrUnderlineEvidence;
    if (binding === undefined) throw new Error('Missing SGR-underline suite receipt binding.');
    if (!binding || binding.runId !== runId || binding.suiteId !== sgrUnderlineSuiteId
        || binding.file !== 'sgus-observations.json' || binding.runFile !== 'sgus-run.json'
        || binding.provisionalUntil !== 'cell-receipt.json'
        || !/^[a-f\d]{64}$/.test(binding.sha256 ?? '') || !/^[a-f\d]{64}$/.test(binding.runSha256 ?? '')
        || !/^[a-f\d]{64}$/.test(binding.adapterBundleSha256 ?? '')
        || binding.contractsVersion !== sgrUnderlineContractsVersion
        || context.sgrUnderlineContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid SGR-underline suite receipt binding.');
    }
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('SGR-underline observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('SGR-underline run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-sgus.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.sgrUnderlineBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('SGR-underline adapter bytes changed.');
    }
    // The retained extension must be exactly the layered derivation of the
    // retained SGR-truecolor extension over the SGR-palette extension over the SGR-attributes
    // extension over the SGR-RGB extension over the SGR-colors extension over the retained first
    // grading extension over the retained owned adapter.
    const asciiSgrBytes = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    const sgrColorsBytes = Buffer.from(adaptSgrColorsCollector(asciiSgrBytes.toString()));
    const sgrRgbBytes = Buffer.from(adaptSgrRgbCollector(sgrColorsBytes.toString()));
    const sgrAttrsBytes = Buffer.from(adaptSgrAttrsCollector(sgrRgbBytes.toString()));
    const sgrPaletteBytes = Buffer.from(adaptSgrPaletteCollector(sgrAttrsBytes.toString()));
    const sgrTruecolorBytes = Buffer.from(adaptSgrTruecolorCollector(sgrPaletteBytes.toString()));
    const derivedBytes = Buffer.from(adaptSgrUnderlineCollector(sgrTruecolorBytes.toString()));
    if (Buffer.compare(derivedBytes, adapterBytes) !== 0) {
        throw new Error('SGR-underline adapter is not the retained layered derivation.');
    }
    if (hash(sgrColorsBytes) !== context.sgrColorsBundleSha256 || hash(sgrRgbBytes) !== context.sgrRgbBundleSha256
        || hash(sgrAttrsBytes) !== context.sgrAttrsBundleSha256 || hash(sgrPaletteBytes) !== context.sgrPaletteBundleSha256
        || hash(sgrTruecolorBytes) !== context.sgrTruecolorBundleSha256) {
        throw new Error('SGR-underline adapter is not derived from the retained SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors extensions.');
    }
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256
        || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || report.contractsVersion !== binding.contractsVersion) {
        throw new Error('SGR-underline report adapter or contract identity differs from the receipt.');
    }
    const journalText = readFileSync(join(output, 'sgus-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('SGR-underline journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildSgrUnderlineGradingReport(frames, run, runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('SGR-underline observations do not match retained evidence.');
    if (JSON.stringify(frames) !== JSON.stringify(report.snapshots)) throw new Error('SGR-underline journal does not match the report.');
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    if (JSON.stringify(counts) !== JSON.stringify(binding.counts)) throw new Error('SGR-underline receipt counts do not match the report.');
}
