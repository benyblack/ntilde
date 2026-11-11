// Receipt binding for the OSC 133 qualification (extensions.osc133-a, -b, -c and -d). The graded evidence stays provisional until the shared cleanup receipt
// exists, and the check re-derives the whole report from the retained paired journal, the worker's pinned-run log and the layered adapter bytes without the
// collector. A run carrying this evidence must also carry every earlier grading layer (through left/right-margin): these conclusions sit beside theirs, never
// replace them.
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
import { adaptRectChecksumCollector } from './rect-checksum-grading-collector.mjs';
import { adaptLrMarginsCollector } from './lr-margins-grading-collector.mjs';
import { osc133CaseIds, osc133ContractsVersion, osc133SuiteId } from './osc133-contracts.mjs';
import { adaptOsc133Collector, osc133AdapterId } from './osc133-grading-collector.mjs';
import { buildOsc133GradingReport, osc133Counts } from './osc133-grading.mjs';
export { osc133Counts };
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['shi-observations.json', 'shi-run.json', 'shi-snapshots.jsonl', 'shi-unavailable.json', 'collector-shi.mjs'];
/** The first source policy whose worker always runs this suite. */
export const osc133FirstPolicy = 39;
/** The only typed refusals a first observation may end in: the owned metadata API or the fixture's grid is unavailable. */
const unavailableReasons = new Set(['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']);

/** Whether a retained artifact path exists; any failure other than a missing entry is fatal. */
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);

/** Any retained OSC 133 artifact or declaration requires this suffix, and a run from the first OSC 133 policy on always carries them, because the current
 * worker runs this suite unconditionally after the Core-gap suite. */
export function includesOsc133Evidence(output, evidence, context) {
    return policyNumber(context.sourceSnapshotPolicy) >= osc133FirstPolicy
        || context.osc133AdapterId !== undefined || context.osc133SuiteId !== undefined
        || context.osc133BundleSha256 !== undefined || context.osc133ContractsVersion !== undefined
        || evidence.osc133Evidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Every retained artifact must be a regular file inside the output directory: a symlink could point at valid evidence that later disappears. */
function requireRegularFiles(output, names) {
    for (const file of names) {
        if (!lstatSync(join(output, file)).isFile()) throw new Error(`OSC 133 artifact is not a regular file: ${file}`);
    }
}

/** The predecessor layers, oldest first (the left/right-margin layer last). Each entry names the layer (its `<name>AdapterId`, `<name>Evidence`
 * and `<name>BundleSha256` in the receipt) and the adapter that derives its bundle from the layer before it. */
const layerChain = [
    ['sgrColors', adaptSgrColorsCollector], ['sgrRgb', adaptSgrRgbCollector], ['sgrAttrs', adaptSgrAttrsCollector], ['sgrPalette', adaptSgrPaletteCollector],
    ['sgrTruecolor', adaptSgrTruecolorCollector], ['sgrUnderline', adaptSgrUnderlineCollector], ['sgrUnderlineColor', adaptSgrUnderlineColorCollector],
    ['sgrReset', adaptSgrResetCollector], ['sgrClosure', adaptSgrClosureCollector], ['eraseLine', adaptEraseLineCollector], ['charEdit', adaptCharEditCollector],
    ['eraseDisplay', adaptEraseDisplayCollector], ['lineEdit', adaptLineEditCollector], ['charsets', adaptCharsetsCollector],
    ['scrollback', adaptScrollbackCollector], ['savedReset', adaptSavedResetCollector], ['eraseAttrsRegion', adaptEraseAttrsRegionCollector],
    ['attrRect', adaptAttrRectCollector], ['cursorQual', adaptCursorQualCollector], ['eraseCharsHistory', adaptEraseCharsHistoryCollector], ['selectiveErase', adaptSelectiveEraseCollector],
    ['rectEdit', adaptRectEditCollector], ['colEdit', adaptColEditCollector], ['rectChecksum', adaptRectChecksumCollector], ['lrMargins', adaptLrMarginsCollector],
];
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const predecessorList = 'every earlier grading layer (SGR through left/right-margin)';

/** The receipt binding of this suite, or a refusal when it is missing or does not match the run. */
function requireBinding(evidence, runId, context) {
    const binding = evidence.osc133Evidence;
    if (binding === undefined) throw new Error('Missing OSC 133 suite receipt binding.');
    const identity = binding && binding.runId === runId && binding.suiteId === osc133SuiteId && binding.file === 'shi-observations.json'
        && binding.runFile === 'shi-run.json' && binding.provisionalUntil === 'cell-receipt.json';
    const digests = binding && [binding.sha256, binding.runSha256, binding.adapterBundleSha256].every(isDigest);
    if (!identity || !digests || binding.contractsVersion !== osc133ContractsVersion || context.osc133ContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid OSC 133 suite receipt binding.');
    }
    return binding;
}

/** The retained bytes of the suite, each checked against its receipt digest. */
function readRetained(output, binding, context) {
    requireRegularFiles(output, ['shi-observations.json', 'shi-run.json', 'shi-snapshots.jsonl', 'collector-shi.mjs']);
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('OSC 133 observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('OSC 133 run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-shi.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.osc133BundleSha256 !== binding.adapterBundleSha256) throw new Error('OSC 133 adapter bytes changed.');
    return { bytes, runBytes, adapterBytes };
}

/** The retained extension must be exactly the layered derivation of every predecessor extension over the retained first grading extension over the retained
 * owned adapter, and every predecessor bundle must be the one the run declared. */
function requireLayeredDerivation(output, adapterBytes, context) {
    let layer = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    const derived = [];
    for (const [name, adapt] of layerChain) {
        layer = Buffer.from(adapt(layer.toString()));
        derived.push([name, layer]);
    }
    if (Buffer.compare(Buffer.from(adaptOsc133Collector(layer.toString())), adapterBytes) !== 0) throw new Error('OSC 133 adapter is not the retained layered derivation.');
    if (derived.some(([name, bytes]) => hash(bytes) !== context[`${name}BundleSha256`])) throw new Error(`OSC 133 adapter is not derived from the retained ${predecessorList}.`);
}

/** The owned metadata API (or the fixture's grid) was typed unavailable at the first observation: a marker, no run log and no report. */
function checkUnavailable(output, evidence, runId, context) {
    const binding = evidence.osc133Evidence;
    const identity = binding && binding.runId === runId && binding.suiteId === osc133SuiteId && binding.file === 'shi-unavailable.json'
        && binding.status === 'unavailable' && unavailableReasons.has(binding.reason) && binding.provisionalUntil === 'cell-receipt.json'
        && binding.contractsVersion === osc133ContractsVersion && context.osc133ContractsVersion === binding.contractsVersion
        && sameJson(binding.counts, {}) && isDigest(binding.sha256) && isDigest(binding.adapterBundleSha256);
    if (!identity) throw new Error('Invalid OSC 133 unavailable receipt binding.');
    if (['shi-observations.json', 'shi-run.json', 'shi-snapshots.jsonl'].some(file => present(join(output, file)) && readFileSync(join(output, file)).length > 0)) {
        throw new Error('OSC 133 unavailable run retains graded artifacts.');
    }
    requireRegularFiles(output, ['shi-unavailable.json', 'collector-shi.mjs']);
    const markerBytes = readFileSync(join(output, 'shi-unavailable.json')), marker = JSON.parse(markerBytes);
    if (hash(markerBytes) !== binding.sha256 || marker.schemaVersion !== 1 || marker.runId !== runId || marker.suiteId !== osc133SuiteId
        || marker.status !== 'unavailable' || marker.reason !== binding.reason || marker.plannedCases !== osc133CaseIds.length) throw new Error('OSC 133 unavailable marker changed.');
    const adapterBytes = readFileSync(join(output, 'collector-shi.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.osc133BundleSha256 !== binding.adapterBundleSha256) throw new Error('OSC 133 adapter bytes changed.');
    requireLayeredDerivation(output, adapterBytes, context);
}

/** Full rederivation: paired journal, run log, layered adapter bytes and conclusions. */
export function checkOsc133Evidence(output, evidence, runId, context) {
    if (context.osc133AdapterId !== osc133AdapterId || context.osc133SuiteId !== osc133SuiteId) throw new Error('Missing or changed OSC 133 adapter declaration.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) {
        throw new Error(`OSC 133 evidence requires ${predecessorList}.`);
    }
    if (evidence.osc133Evidence?.status === 'unavailable') return checkUnavailable(output, evidence, runId, context);
    const binding = requireBinding(evidence, runId, context);
    const { bytes, runBytes, adapterBytes } = readRetained(output, binding, context);
    requireLayeredDerivation(output, adapterBytes, context);
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    const identityMatches = [report.adapter.bundleSha256 === binding.adapterBundleSha256,
        report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256, report.contractsVersion === binding.contractsVersion];
    if (!identityMatches.every(Boolean)) throw new Error('OSC 133 report adapter or contract identity differs from the receipt.');
    const journalText = readFileSync(join(output, 'shi-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('OSC 133 journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildOsc133GradingReport(frames, run, runId, report.adapter, { contractsVersion: report.contractsVersion });
    if (!sameJson(report, derived)) throw new Error('OSC 133 observations do not match retained evidence.');
    if (!sameJson(frames, report.snapshots)) throw new Error('OSC 133 journal does not match the report.');
    if (!sameJson(osc133Counts(report), binding.counts)) throw new Error('OSC 133 receipt counts do not match the report.');
}
