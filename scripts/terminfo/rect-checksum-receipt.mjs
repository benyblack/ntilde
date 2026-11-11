// Receipt binding for the rectangle-checksum qualification (editing.decrqcra). The graded evidence stays provisional until the
// shared cleanup receipt exists, and the check re-derives the whole report from retained bytes without the collector. The run must
// have turned on AllowRectangleChecksumReport in its retained initial settings (the report is off by default). A run carrying this
// evidence must also carry its column-editing, rectangle-editing, selective-erasure, erase-character/saved-lines, cursor, attribute-rectangle, erase attributes/margins, saved-state/reset, scrolling/history, charsets, line-editing, erase-in-display, character-editing, erase-in-line, SGR closure, SGR selective-reset, SGR-underline-color, SGR-underline,
// SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors predecessors: these conclusions sit beside theirs,
// never replace them.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { adaptAttrRectCollector } from './attr-rect-grading-collector.mjs';
import { adaptCharEditCollector } from './char-edit-grading-collector.mjs';
import { adaptCharsetsCollector } from './charsets-grading-collector.mjs';
import { adaptCursorQualCollector } from './cursor-qual-grading-collector.mjs';
import { adaptEraseAttrsRegionCollector } from './erase-attrs-region-grading-collector.mjs';
import { adaptEraseCharsHistoryCollector } from './erase-chars-history-grading-collector.mjs';
import { adaptEraseDisplayCollector } from './erase-display-grading-collector.mjs';
import { adaptEraseLineCollector } from './erase-line-grading-collector.mjs';
import { adaptGradingCollector } from './grading-collector.mjs';
import { adaptLineEditCollector } from './line-edit-grading-collector.mjs';
import { adaptSelectiveEraseCollector } from './selective-erase-grading-collector.mjs';
import { adaptSavedResetCollector } from './saved-reset-grading-collector.mjs';
import { adaptScrollbackCollector } from './scrollback-grading-collector.mjs';
import { adaptSgrAttrsCollector } from './sgr-attrs-grading-collector.mjs';
import { adaptSgrClosureCollector } from './sgr-closure-grading-collector.mjs';
import { adaptSgrColorsCollector } from './sgr-colors-grading-collector.mjs';
import { adaptSgrPaletteCollector } from './sgr-palette-grading-collector.mjs';
import { adaptSgrResetCollector } from './sgr-reset-grading-collector.mjs';
import { adaptSgrRgbCollector } from './sgr-rgb-grading-collector.mjs';
import { adaptSgrTruecolorCollector } from './sgr-truecolor-grading-collector.mjs';
import { adaptSgrUnderlineColorCollector } from './sgr-underline-color-grading-collector.mjs';
import { adaptSgrUnderlineCollector } from './sgr-underline-grading-collector.mjs';
import { adaptRectEditCollector } from './rect-edit-grading-collector.mjs';
import { adaptColEditCollector } from './col-edit-grading-collector.mjs';
import { rectChecksumSuiteId, rectChecksumContractsVersion } from './rect-checksum-contracts.mjs';
import { adaptRectChecksumCollector, rectChecksumAdapterId } from './rect-checksum-grading-collector.mjs';
import { buildRectChecksumGradingReport } from './rect-checksum-grading.mjs';
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['csum-observations.json', 'csum-run.json', 'csum-snapshots.jsonl', 'collector-csum.mjs', 'initial-settings.json'];

/** Whether a retained artifact path exists; any failure other than a missing entry is fatal. */
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;

/** Any retained rectangle-checksum artifact or declaration requires the v39 suffix, and a policy-v33 or later run always carries them,
 * because the current worker collects this suite unconditionally after the column-editing suite. (initial-settings.json is
 * retained by every run, so it alone never requires the suffix.) */
export function includesRectChecksumEvidence(output, evidence, context) {
    return ['readonly-owned-source-graph-v33', 'readonly-owned-source-graph-v34', 'readonly-owned-source-graph-v35'].includes(context.sourceSnapshotPolicy)
        || context.rectChecksumAdapterId !== undefined || context.rectChecksumSuiteId !== undefined
        || context.rectChecksumBundleSha256 !== undefined || context.rectChecksumContractsVersion !== undefined
        || evidence.rectChecksumEvidence !== undefined
        || files.slice(0, 4).some(file => present(join(output, file)));
}

/** Every retained artifact must be a regular file inside the output directory: a symlink could point at valid evidence
 * that later disappears, leaving a run that can no longer be rederived. */
function requireRegularFiles(output) {
    for (const file of files) {
        if (!lstatSync(join(output, file)).isFile()) throw new Error(`rectangle-checksum artifact is not a regular file: ${file}`);
    }
}

/** The outcome counts of the case conclusions. */
export function rectChecksumCounts(report) {
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return counts;
}

/** The predecessor layers, oldest first. Each entry names the layer (its `<name>AdapterId`, `<name>Evidence` and `<name>BundleSha256` in the receipt)
 * and the adapter that derives its bundle from the layer before it; the first derives from the retained first grading extension. */
const layerChain = [
    ['sgrColors', adaptSgrColorsCollector], ['sgrRgb', adaptSgrRgbCollector], ['sgrAttrs', adaptSgrAttrsCollector], ['sgrPalette', adaptSgrPaletteCollector],
    ['sgrTruecolor', adaptSgrTruecolorCollector], ['sgrUnderline', adaptSgrUnderlineCollector], ['sgrUnderlineColor', adaptSgrUnderlineColorCollector],
    ['sgrReset', adaptSgrResetCollector], ['sgrClosure', adaptSgrClosureCollector], ['eraseLine', adaptEraseLineCollector], ['charEdit', adaptCharEditCollector],
    ['eraseDisplay', adaptEraseDisplayCollector], ['lineEdit', adaptLineEditCollector], ['charsets', adaptCharsetsCollector],
    ['scrollback', adaptScrollbackCollector], ['savedReset', adaptSavedResetCollector], ['eraseAttrsRegion', adaptEraseAttrsRegionCollector],
    ['attrRect', adaptAttrRectCollector], ['cursorQual', adaptCursorQualCollector], ['eraseCharsHistory', adaptEraseCharsHistoryCollector], ['selectiveErase', adaptSelectiveEraseCollector],
    ['rectEdit', adaptRectEditCollector], ['colEdit', adaptColEditCollector],
];
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const predecessorList = 'column-editing, rectangle-editing, selective-erasure, erase-character/saved-lines, cursor, attribute-rectangle, erase attributes/margins, saved-state/reset, scrolling/history, charsets, line-editing, erase-in-display, character-editing, erase-in-line, SGR closure, SGR selective-reset, SGR-underline-color, SGR-underline, SGR-truecolor, SGR-palette, SGR-attributes, SGR-RGB and SGR-colors';

/** The receipt binding of this suite, or a refusal when it is missing or does not match the run. */
function requireBinding(evidence, runId, context) {
    const binding = evidence.rectChecksumEvidence;
    if (binding === undefined) throw new Error('Missing rectangle-checksum suite receipt binding.');
    const identity = binding && binding.runId === runId && binding.suiteId === rectChecksumSuiteId && binding.file === 'csum-observations.json'
        && binding.runFile === 'csum-run.json' && binding.provisionalUntil === 'cell-receipt.json';
    const digests = binding && [binding.sha256, binding.runSha256, binding.adapterBundleSha256].every(isDigest);
    if (!identity || !digests || binding.contractsVersion !== rectChecksumContractsVersion || context.rectChecksumContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid rectangle-checksum suite receipt binding.');
    }
    return binding;
}

/** The retained bytes of the suite, each checked against its receipt digest. */
function readRetained(output, binding, context) {
    requireRegularFiles(output);
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('rectangle-checksum observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('rectangle-checksum run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-csum.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.rectChecksumBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('rectangle-checksum adapter bytes changed.');
    }
    return { bytes, runBytes, adapterBytes };
}

/** The retained extension must be exactly the layered derivation of every predecessor extension over the retained first grading
 * extension over the retained owned adapter, and every predecessor bundle must be the one the run declared. */
function requireLayeredDerivation(output, adapterBytes, context) {
    let layer = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    const derived = [];
    for (const [name, adapt] of layerChain) {
        layer = Buffer.from(adapt(layer.toString()));
        derived.push([name, layer]);
    }
    if (Buffer.compare(Buffer.from(adaptRectChecksumCollector(layer.toString())), adapterBytes) !== 0) {
        throw new Error('rectangle-checksum adapter is not the retained layered derivation.');
    }
    if (derived.some(([name, bytes]) => hash(bytes) !== context[`${name}BundleSha256`])) {
        throw new Error(`rectangle-checksum adapter is not derived from the retained ${predecessorList} and earlier extensions.`);
    }
}

/** The retained initial settings must turn the off-by-default report on, and the run must declare the same digest. */
function requireGateOn(output, context) {
    const bytes = readFileSync(join(output, 'initial-settings.json'));
    if (hash(bytes) !== context.initialSettingsSha256) throw new Error('rectangle-checksum initial settings changed.');
    if (JSON.parse(bytes).AllowRectangleChecksumReport !== true) throw new Error('rectangle-checksum evidence requires AllowRectangleChecksumReport on in the retained initial settings.');
}

/** Full rederivation: journal, run log, layered adapter bytes and conclusions. */
export function checkRectChecksumEvidence(output, evidence, runId, context) {
    if (context.rectChecksumAdapterId !== rectChecksumAdapterId || context.rectChecksumSuiteId !== rectChecksumSuiteId) {
        throw new Error('Missing or changed rectangle-checksum adapter declaration.');
    }
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) {
        throw new Error(`rectangle-checksum evidence requires its ${predecessorList} predecessors.`);
    }
    requireGateOn(output, context);
    const binding = requireBinding(evidence, runId, context);
    const { bytes, runBytes, adapterBytes } = readRetained(output, binding, context);
    requireLayeredDerivation(output, adapterBytes, context);
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    const identityMatches = [report.adapter.bundleSha256 === binding.adapterBundleSha256,
        report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256, report.contractsVersion === binding.contractsVersion];
    if (!identityMatches.every(Boolean)) throw new Error('rectangle-checksum report adapter or contract identity differs from the receipt.');
    const journalText = readFileSync(join(output, 'csum-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('rectangle-checksum journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildRectChecksumGradingReport(frames, run, runId, report.adapter, { contractsVersion: report.contractsVersion });
    if (!sameJson(report, derived)) throw new Error('rectangle-checksum observations do not match retained evidence.');
    if (!sameJson(frames, report.snapshots)) throw new Error('rectangle-checksum journal does not match the report.');
    if (!sameJson(rectChecksumCounts(report), binding.counts)) throw new Error('rectangle-checksum receipt counts do not match the report.');
}
