// Receipt binding for the character-editing qualification. The graded
// evidence stays provisional until the shared cleanup receipt exists, and the
// check re-derives the whole report from retained bytes without the collector.
// A run carrying character-editing evidence must also carry its erase-in-line, SGR closure, SGR selective-reset,
// SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors
// predecessors: these conclusions sit beside theirs, never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { charEditSuiteId, charEditContractsVersion } from './char-edit-contracts.mjs';
import { adaptCharEditCollector, charEditAdapterId } from './char-edit-grading-collector.mjs';
import { adaptEraseLineCollector } from './erase-line-grading-collector.mjs';
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
import { buildCharEditGradingReport } from './char-edit-grading.mjs';
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['ched-observations.json', 'ched-run.json', 'ched-snapshots.jsonl', 'collector-ched.mjs'];

/** Whether a retained artifact path exists. */
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

/** Any retained character-editing artifact or declaration requires the v26 suffix, and a
 * policy-v20 or v21 run always carries them, because the current worker collects this suite
 * unconditionally after the SGR closure suite. */
export function includesCharEditEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v20', 'readonly-owned-source-graph-v21', 'readonly-owned-source-graph-v22', 'readonly-owned-source-graph-v23', 'readonly-owned-source-graph-v24', 'readonly-owned-source-graph-v25', 'readonly-owned-source-graph-v26', 'readonly-owned-source-graph-v27', 'readonly-owned-source-graph-v28', 'readonly-owned-source-graph-v29', 'readonly-owned-source-graph-v30', 'readonly-owned-source-graph-v31', 'readonly-owned-source-graph-v32', 'readonly-owned-source-graph-v33', 'readonly-owned-source-graph-v34', 'readonly-owned-source-graph-v35', 'readonly-owned-source-graph-v36', 'readonly-owned-source-graph-v37', 'readonly-owned-source-graph-v38', 'readonly-owned-source-graph-v39', 'readonly-owned-source-graph-v40', 'readonly-owned-source-graph-v41', 'readonly-owned-source-graph-v42', 'readonly-owned-source-graph-v43'].includes(context.sourceSnapshotPolicy)
        || context.charEditAdapterId !== undefined || context.charEditSuiteId !== undefined
        || context.charEditBundleSha256 !== undefined || context.charEditContractsVersion !== undefined
        || evidence.charEditEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Every retained artifact must be a regular file inside the output directory: a symlink could point at valid evidence
 * that later disappears, leaving a run that can no longer be rederived. */
function requireRegularFiles(output) {
    for (const file of files) {
        if (!lstatSync(join(output, file)).isFile()) throw new Error(`character-editing artifact is not a regular file: ${file}`);
    }
}

/** Full rederivation: journal, run log, layered adapter bytes and conclusions. */
export function checkCharEditEvidence(output, evidence, runId, context) {
    if (context.charEditAdapterId !== charEditAdapterId || context.charEditSuiteId !== charEditSuiteId) {
        throw new Error('Missing or changed character-editing adapter declaration.');
    }
    if (context.eraseLineAdapterId === undefined || evidence.eraseLineEvidence === undefined
        || context.sgrClosureAdapterId === undefined || evidence.sgrClosureEvidence === undefined
        || context.sgrResetAdapterId === undefined || evidence.sgrResetEvidence === undefined
        || context.sgrUnderlineColorAdapterId === undefined || evidence.sgrUnderlineColorEvidence === undefined
        || context.sgrUnderlineAdapterId === undefined || evidence.sgrUnderlineEvidence === undefined
        || context.sgrTruecolorAdapterId === undefined || evidence.sgrTruecolorEvidence === undefined
        || context.sgrColorsAdapterId === undefined || evidence.sgrColorsEvidence === undefined
        || context.sgrRgbAdapterId === undefined || evidence.sgrRgbEvidence === undefined
        || context.sgrAttrsAdapterId === undefined || evidence.sgrAttrsEvidence === undefined
        || context.sgrPaletteAdapterId === undefined || evidence.sgrPaletteEvidence === undefined) {
        throw new Error('character-editing evidence requires its erase-in-line, SGR closure, SGR selective-reset, SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors predecessors.');
    }
    const binding = evidence.charEditEvidence;
    if (binding === undefined) throw new Error('Missing character-editing suite receipt binding.');
    if (!binding || binding.runId !== runId || binding.suiteId !== charEditSuiteId
        || binding.file !== 'ched-observations.json' || binding.runFile !== 'ched-run.json'
        || binding.provisionalUntil !== 'cell-receipt.json'
        || !/^[a-f\d]{64}$/.test(binding.sha256 ?? '') || !/^[a-f\d]{64}$/.test(binding.runSha256 ?? '')
        || !/^[a-f\d]{64}$/.test(binding.adapterBundleSha256 ?? '')
        || binding.contractsVersion !== charEditContractsVersion
        || context.charEditContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid character-editing suite receipt binding.');
    }
    requireRegularFiles(output);
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('character-editing observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('character-editing run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-ched.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.charEditBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('character-editing adapter bytes changed.');
    }
    // The retained extension must be exactly the layered derivation of the retained
    // erase-in-line extension over the SGR closure extension over the SGR selective-reset, SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette,
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
    const eraseLineBytes = Buffer.from(adaptEraseLineCollector(sgrClosureBytes.toString()));
    const derivedBytes = Buffer.from(adaptCharEditCollector(eraseLineBytes.toString()));
    if (Buffer.compare(derivedBytes, adapterBytes) !== 0) {
        throw new Error('character-editing adapter is not the retained layered derivation.');
    }
    if (hash(sgrColorsBytes) !== context.sgrColorsBundleSha256 || hash(sgrRgbBytes) !== context.sgrRgbBundleSha256
        || hash(sgrAttrsBytes) !== context.sgrAttrsBundleSha256 || hash(sgrPaletteBytes) !== context.sgrPaletteBundleSha256
        || hash(sgrTruecolorBytes) !== context.sgrTruecolorBundleSha256 || hash(sgrUnderlineBytes) !== context.sgrUnderlineBundleSha256
        || hash(sgrUnderlineColorBytes) !== context.sgrUnderlineColorBundleSha256 || hash(sgrResetBytes) !== context.sgrResetBundleSha256
        || hash(sgrClosureBytes) !== context.sgrClosureBundleSha256 || hash(eraseLineBytes) !== context.eraseLineBundleSha256) {
        throw new Error('character-editing adapter is not derived from the retained erase-in-line, SGR closure and earlier extensions.');
    }
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256
        || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || report.contractsVersion !== binding.contractsVersion) {
        throw new Error('character-editing report adapter or contract identity differs from the receipt.');
    }
    const journalText = readFileSync(join(output, 'ched-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('character-editing journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildCharEditGradingReport(frames, run, runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('character-editing observations do not match retained evidence.');
    if (JSON.stringify(frames) !== JSON.stringify(report.snapshots)) throw new Error('character-editing journal does not match the report.');
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    if (JSON.stringify(counts) !== JSON.stringify(binding.counts)) throw new Error('character-editing receipt counts do not match the report.');
}
