// Receipt binding for the SGR truecolor qualification. The graded
// evidence stays provisional until the shared cleanup receipt exists, and the
// check re-derives the whole report from retained bytes without the collector.
// A run carrying SGR-truecolor evidence must also carry its SGR-palette, SGR-attributes,
// SGR-RGB and SGR-colors predecessors: these conclusions sit beside theirs, never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { sgrTruecolorSuiteId, sgrTruecolorContractsVersion } from './sgr-truecolor-contracts.mjs';
import { adaptSgrTruecolorCollector, sgrTruecolorAdapterId } from './sgr-truecolor-grading-collector.mjs';
import { adaptSgrPaletteCollector } from './sgr-palette-grading-collector.mjs';
import { adaptSgrAttrsCollector } from './sgr-attrs-grading-collector.mjs';
import { adaptSgrRgbCollector } from './sgr-rgb-grading-collector.mjs';
import { adaptSgrColorsCollector } from './sgr-colors-grading-collector.mjs';
import { adaptGradingCollector } from './grading-collector.mjs';
import { buildSgrTruecolorGradingReport } from './sgr-truecolor-grading.mjs';
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['sgtc-observations.json', 'sgtc-run.json', 'sgtc-snapshots.jsonl', 'collector-sgtc.mjs'];

/** Whether a retained artifact path exists. */
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

/** Any retained SGR-truecolor artifact or declaration requires the v18 suffix — and a
 * policy-v12 run always carries them, because the current worker collects this
 * suite unconditionally after the SGR-palette suite. */
export function includesSgrTruecolorEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v12', 'readonly-owned-source-graph-v13', 'readonly-owned-source-graph-v14', 'readonly-owned-source-graph-v15', 'readonly-owned-source-graph-v16', 'readonly-owned-source-graph-v17', 'readonly-owned-source-graph-v18', 'readonly-owned-source-graph-v19', 'readonly-owned-source-graph-v20', 'readonly-owned-source-graph-v21', 'readonly-owned-source-graph-v22', 'readonly-owned-source-graph-v23', 'readonly-owned-source-graph-v24', 'readonly-owned-source-graph-v25', 'readonly-owned-source-graph-v26', 'readonly-owned-source-graph-v27'].includes(context.sourceSnapshotPolicy)
        || context.sgrTruecolorAdapterId !== undefined || context.sgrTruecolorSuiteId !== undefined
        || context.sgrTruecolorBundleSha256 !== undefined || context.sgrTruecolorContractsVersion !== undefined
        || evidence.sgrTruecolorEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Full rederivation: journal, run log, layered adapter bytes and conclusions. */
export function checkSgrTruecolorEvidence(output, evidence, runId, context) {
    if (context.sgrTruecolorAdapterId !== sgrTruecolorAdapterId || context.sgrTruecolorSuiteId !== sgrTruecolorSuiteId) {
        throw new Error('Missing or changed SGR-truecolor adapter declaration.');
    }
    if (context.sgrColorsAdapterId === undefined || evidence.sgrColorsEvidence === undefined
        || context.sgrRgbAdapterId === undefined || evidence.sgrRgbEvidence === undefined
        || context.sgrAttrsAdapterId === undefined || evidence.sgrAttrsEvidence === undefined
        || context.sgrPaletteAdapterId === undefined || evidence.sgrPaletteEvidence === undefined) {
        throw new Error('SGR-truecolor evidence requires its SGR-colors, SGR-RGB, SGR-attributes and SGR-palette predecessors.');
    }
    const binding = evidence.sgrTruecolorEvidence;
    if (binding === undefined) throw new Error('Missing SGR-truecolor suite receipt binding.');
    if (!binding || binding.runId !== runId || binding.suiteId !== sgrTruecolorSuiteId
        || binding.file !== 'sgtc-observations.json' || binding.runFile !== 'sgtc-run.json'
        || binding.provisionalUntil !== 'cell-receipt.json'
        || !/^[a-f\d]{64}$/.test(binding.sha256 ?? '') || !/^[a-f\d]{64}$/.test(binding.runSha256 ?? '')
        || !/^[a-f\d]{64}$/.test(binding.adapterBundleSha256 ?? '')
        || binding.contractsVersion !== sgrTruecolorContractsVersion
        || context.sgrTruecolorContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid SGR-truecolor suite receipt binding.');
    }
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('SGR-truecolor observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('SGR-truecolor run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-sgtc.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.sgrTruecolorBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('SGR-truecolor adapter bytes changed.');
    }
    // The retained extension must be exactly the layered derivation of the
    // retained SGR-palette extension over the retained SGR-attributes extension over the retained SGR-RGB extension over the
    // retained SGR-colors extension over the retained first grading extension over
    // the retained owned adapter.
    const asciiSgrBytes = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    const sgrColorsBytes = Buffer.from(adaptSgrColorsCollector(asciiSgrBytes.toString()));
    const sgrRgbBytes = Buffer.from(adaptSgrRgbCollector(sgrColorsBytes.toString()));
    const sgrAttrsBytes = Buffer.from(adaptSgrAttrsCollector(sgrRgbBytes.toString()));
    const sgrPaletteBytes = Buffer.from(adaptSgrPaletteCollector(sgrAttrsBytes.toString()));
    const derivedBytes = Buffer.from(adaptSgrTruecolorCollector(sgrPaletteBytes.toString()));
    if (Buffer.compare(derivedBytes, adapterBytes) !== 0) {
        throw new Error('SGR-truecolor adapter is not the retained layered derivation.');
    }
    if (hash(sgrColorsBytes) !== context.sgrColorsBundleSha256 || hash(sgrRgbBytes) !== context.sgrRgbBundleSha256
        || hash(sgrAttrsBytes) !== context.sgrAttrsBundleSha256 || hash(sgrPaletteBytes) !== context.sgrPaletteBundleSha256) {
        throw new Error('SGR-truecolor adapter is not derived from the retained SGR-colors, SGR-RGB, SGR-attributes and SGR-palette extensions.');
    }
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256
        || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || report.contractsVersion !== binding.contractsVersion) {
        throw new Error('SGR-truecolor report adapter or contract identity differs from the receipt.');
    }
    const journalText = readFileSync(join(output, 'sgtc-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('SGR-truecolor journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildSgrTruecolorGradingReport(frames, run, runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('SGR-truecolor observations do not match retained evidence.');
    if (JSON.stringify(frames) !== JSON.stringify(report.snapshots)) throw new Error('SGR-truecolor journal does not match the report.');
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    if (JSON.stringify(counts) !== JSON.stringify(binding.counts)) throw new Error('SGR-truecolor receipt counts do not match the report.');
}
