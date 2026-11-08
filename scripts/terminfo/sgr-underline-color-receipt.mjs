// Receipt binding for the SGR underline-style qualification. The graded
// evidence stays provisional until the shared cleanup receipt exists, and the
// check re-derives the whole report from retained bytes without the collector.
// A run carrying SGR-underline-color evidence must also carry its SGR-truecolor, SGR-palette,
// SGR-attributes, SGR-RGB and SGR-colors predecessors: these conclusions sit beside
// theirs, never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { sgrUnderlineColorSuiteId, sgrUnderlineColorContractsVersion } from './sgr-underline-color-contracts.mjs';
import { adaptSgrUnderlineColorCollector, sgrUnderlineColorAdapterId } from './sgr-underline-color-grading-collector.mjs';
import { adaptSgrUnderlineCollector } from './sgr-underline-grading-collector.mjs';
import { adaptSgrTruecolorCollector } from './sgr-truecolor-grading-collector.mjs';
import { adaptSgrPaletteCollector } from './sgr-palette-grading-collector.mjs';
import { adaptSgrAttrsCollector } from './sgr-attrs-grading-collector.mjs';
import { adaptSgrRgbCollector } from './sgr-rgb-grading-collector.mjs';
import { adaptSgrColorsCollector } from './sgr-colors-grading-collector.mjs';
import { adaptGradingCollector } from './grading-collector.mjs';
import { buildSgrUnderlineColorGradingReport } from './sgr-underline-color-grading.mjs';
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['sguc-observations.json', 'sguc-run.json', 'sguc-snapshots.jsonl', 'collector-sguc.mjs'];

/** Whether a retained artifact path exists. */
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

/** Any retained SGR-underline-color artifact or declaration requires the v20 suffix — and a
 * policy-v15 run always carries them, because the current worker collects this
 * suite unconditionally after the SGR-underline suite. */
export function includesSgrUnderlineColorEvidence(output, evidence, context) {
    return context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v15'
        || context.sgrUnderlineColorAdapterId !== undefined || context.sgrUnderlineColorSuiteId !== undefined
        || context.sgrUnderlineColorBundleSha256 !== undefined || context.sgrUnderlineColorContractsVersion !== undefined
        || evidence.sgrUnderlineColorEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Full rederivation: journal, run log, layered adapter bytes and conclusions. */
export function checkSgrUnderlineColorEvidence(output, evidence, runId, context) {
    if (context.sgrUnderlineColorAdapterId !== sgrUnderlineColorAdapterId || context.sgrUnderlineColorSuiteId !== sgrUnderlineColorSuiteId) {
        throw new Error('Missing or changed SGR-underline-color adapter declaration.');
    }
    if (context.sgrUnderlineAdapterId === undefined || evidence.sgrUnderlineEvidence === undefined
        || context.sgrTruecolorAdapterId === undefined || evidence.sgrTruecolorEvidence === undefined
        || context.sgrColorsAdapterId === undefined || evidence.sgrColorsEvidence === undefined
        || context.sgrRgbAdapterId === undefined || evidence.sgrRgbEvidence === undefined
        || context.sgrAttrsAdapterId === undefined || evidence.sgrAttrsEvidence === undefined
        || context.sgrPaletteAdapterId === undefined || evidence.sgrPaletteEvidence === undefined) {
        throw new Error('SGR-underline-color evidence requires its SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors predecessors.');
    }
    const binding = evidence.sgrUnderlineColorEvidence;
    if (binding === undefined) throw new Error('Missing SGR-underline-color suite receipt binding.');
    if (!binding || binding.runId !== runId || binding.suiteId !== sgrUnderlineColorSuiteId
        || binding.file !== 'sguc-observations.json' || binding.runFile !== 'sguc-run.json'
        || binding.provisionalUntil !== 'cell-receipt.json'
        || !/^[a-f\d]{64}$/.test(binding.sha256 ?? '') || !/^[a-f\d]{64}$/.test(binding.runSha256 ?? '')
        || !/^[a-f\d]{64}$/.test(binding.adapterBundleSha256 ?? '')
        || binding.contractsVersion !== sgrUnderlineColorContractsVersion
        || context.sgrUnderlineColorContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid SGR-underline-color suite receipt binding.');
    }
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('SGR-underline-color observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('SGR-underline-color run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-sguc.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.sgrUnderlineColorBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('SGR-underline-color adapter bytes changed.');
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
    const sgrUnderlineBytes = Buffer.from(adaptSgrUnderlineCollector(sgrTruecolorBytes.toString()));
    const derivedBytes = Buffer.from(adaptSgrUnderlineColorCollector(sgrUnderlineBytes.toString()));
    if (Buffer.compare(derivedBytes, adapterBytes) !== 0) {
        throw new Error('SGR-underline-color adapter is not the retained layered derivation.');
    }
    if (hash(sgrColorsBytes) !== context.sgrColorsBundleSha256 || hash(sgrRgbBytes) !== context.sgrRgbBundleSha256
        || hash(sgrAttrsBytes) !== context.sgrAttrsBundleSha256 || hash(sgrPaletteBytes) !== context.sgrPaletteBundleSha256
        || hash(sgrTruecolorBytes) !== context.sgrTruecolorBundleSha256 || hash(sgrUnderlineBytes) !== context.sgrUnderlineBundleSha256) {
        throw new Error('SGR-underline-color adapter is not derived from the retained SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors extensions.');
    }
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256
        || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || report.contractsVersion !== binding.contractsVersion) {
        throw new Error('SGR-underline-color report adapter or contract identity differs from the receipt.');
    }
    const journalText = readFileSync(join(output, 'sguc-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('SGR-underline-color journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildSgrUnderlineColorGradingReport(frames, run, runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('SGR-underline-color observations do not match retained evidence.');
    if (JSON.stringify(frames) !== JSON.stringify(report.snapshots)) throw new Error('SGR-underline-color journal does not match the report.');
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    if (JSON.stringify(counts) !== JSON.stringify(binding.counts)) throw new Error('SGR-underline-color receipt counts do not match the report.');
}
