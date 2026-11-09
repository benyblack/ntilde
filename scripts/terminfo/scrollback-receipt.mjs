// Receipt binding for the scrolling/history qualification. The graded
// evidence stays provisional until the shared cleanup receipt exists, and the
// check re-derives the whole report from retained bytes without the collector.
// A run carrying scrolling/history evidence must also carry its character-editing, erase-in-line, SGR closure, SGR selective-reset, SGR-underline-color,
// SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors
// predecessors: these conclusions sit beside theirs, never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { scrollbackSuiteId, scrollbackContractsVersion } from './scrollback-contracts.mjs';
import { adaptScrollbackCollector, scrollbackAdapterId } from './scrollback-grading-collector.mjs';
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
import { buildScrollbackGradingReport } from './scrollback-grading.mjs';
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['scrl-observations.json', 'scrl-run.json', 'scrl-snapshots.jsonl', 'collector-scrl.mjs'];

/** Whether a retained artifact path exists. */
function present(path) {
    try { lstatSync(path); return true; }
    catch (error) {
        if (error.code === 'ENOENT') return false;
        throw error;
    }
}

/** Any retained scrolling/history artifact or declaration requires the v27 suffix, and a
 * policy-v21 run always carries them, because the current worker collects this suite
 * unconditionally after the character-editing suite. */
export function includesScrollbackEvidence(output, evidence, context) {
    return context.sourceSnapshotPolicy === 'readonly-owned-source-graph-v21'
        || context.scrollbackAdapterId !== undefined || context.scrollbackSuiteId !== undefined
        || context.scrollbackBundleSha256 !== undefined || context.scrollbackContractsVersion !== undefined
        || evidence.scrollbackEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Every retained artifact must be a regular file inside the output directory: a symlink could point at valid evidence
 * that later disappears, leaving a run that can no longer be rederived. */
function requireRegularFiles(output) {
    for (const file of files) {
        if (!lstatSync(join(output, file)).isFile()) throw new Error(`scrolling/history artifact is not a regular file: ${file}`);
    }
}

/** Full rederivation: journal, run log, layered adapter bytes and conclusions. */
export function checkScrollbackEvidence(output, evidence, runId, context) {
    if (context.scrollbackAdapterId !== scrollbackAdapterId || context.scrollbackSuiteId !== scrollbackSuiteId) {
        throw new Error('Missing or changed scrolling/history adapter declaration.');
    }
    if (context.charEditAdapterId === undefined || evidence.charEditEvidence === undefined
        || context.eraseLineAdapterId === undefined || evidence.eraseLineEvidence === undefined
        || context.sgrClosureAdapterId === undefined || evidence.sgrClosureEvidence === undefined
        || context.sgrResetAdapterId === undefined || evidence.sgrResetEvidence === undefined
        || context.sgrUnderlineColorAdapterId === undefined || evidence.sgrUnderlineColorEvidence === undefined
        || context.sgrUnderlineAdapterId === undefined || evidence.sgrUnderlineEvidence === undefined
        || context.sgrTruecolorAdapterId === undefined || evidence.sgrTruecolorEvidence === undefined
        || context.sgrColorsAdapterId === undefined || evidence.sgrColorsEvidence === undefined
        || context.sgrRgbAdapterId === undefined || evidence.sgrRgbEvidence === undefined
        || context.sgrAttrsAdapterId === undefined || evidence.sgrAttrsEvidence === undefined
        || context.sgrPaletteAdapterId === undefined || evidence.sgrPaletteEvidence === undefined) {
        throw new Error('scrolling/history evidence requires its character-editing, erase-in-line, SGR closure, SGR selective-reset, SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors predecessors.');
    }
    const binding = evidence.scrollbackEvidence;
    if (binding === undefined) throw new Error('Missing scrolling/history suite receipt binding.');
    if (!binding || binding.runId !== runId || binding.suiteId !== scrollbackSuiteId
        || binding.file !== 'scrl-observations.json' || binding.runFile !== 'scrl-run.json'
        || binding.provisionalUntil !== 'cell-receipt.json'
        || !/^[a-f\d]{64}$/.test(binding.sha256 ?? '') || !/^[a-f\d]{64}$/.test(binding.runSha256 ?? '')
        || !/^[a-f\d]{64}$/.test(binding.adapterBundleSha256 ?? '')
        || binding.contractsVersion !== scrollbackContractsVersion
        || context.scrollbackContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid scrolling/history suite receipt binding.');
    }
    requireRegularFiles(output);
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('scrolling/history observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('scrolling/history run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-scrl.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.scrollbackBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('scrolling/history adapter bytes changed.');
    }
    // The retained extension must be exactly the layered derivation of the retained
    // character-editing extension over the erase-in-line extension over the SGR closure extension over the SGR selective-reset, SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette,
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
    const charEditBytes = Buffer.from(adaptCharEditCollector(eraseLineBytes.toString()));
    const derivedBytes = Buffer.from(adaptScrollbackCollector(charEditBytes.toString()));
    if (Buffer.compare(derivedBytes, adapterBytes) !== 0) {
        throw new Error('scrolling/history adapter is not the retained layered derivation.');
    }
    if (hash(sgrColorsBytes) !== context.sgrColorsBundleSha256 || hash(sgrRgbBytes) !== context.sgrRgbBundleSha256
        || hash(sgrAttrsBytes) !== context.sgrAttrsBundleSha256 || hash(sgrPaletteBytes) !== context.sgrPaletteBundleSha256
        || hash(sgrTruecolorBytes) !== context.sgrTruecolorBundleSha256 || hash(sgrUnderlineBytes) !== context.sgrUnderlineBundleSha256
        || hash(sgrUnderlineColorBytes) !== context.sgrUnderlineColorBundleSha256 || hash(sgrResetBytes) !== context.sgrResetBundleSha256
        || hash(sgrClosureBytes) !== context.sgrClosureBundleSha256 || hash(eraseLineBytes) !== context.eraseLineBundleSha256
        || hash(charEditBytes) !== context.charEditBundleSha256) {
        throw new Error('scrolling/history adapter is not derived from the retained character-editing, erase-in-line, SGR closure and earlier extensions.');
    }
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    if (report.adapter.bundleSha256 !== binding.adapterBundleSha256
        || report.adapter.upstreamBundleSha256 !== context.upstreamBundleSha256
        || report.contractsVersion !== binding.contractsVersion) {
        throw new Error('scrolling/history report adapter or contract identity differs from the receipt.');
    }
    const journalText = readFileSync(join(output, 'scrl-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('scrolling/history journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildScrollbackGradingReport(frames, run, runId, report.adapter,
        { contractsVersion: report.contractsVersion });
    if (JSON.stringify(report) !== JSON.stringify(derived)) throw new Error('scrolling/history observations do not match retained evidence.');
    if (JSON.stringify(frames) !== JSON.stringify(report.snapshots)) throw new Error('scrolling/history journal does not match the report.');
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    if (JSON.stringify(counts) !== JSON.stringify(binding.counts)) throw new Error('scrolling/history receipt counts do not match the report.');
}
