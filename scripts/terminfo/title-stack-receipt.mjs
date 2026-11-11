// Receipt binding for the XTWINOPS title/icon stack qualification (device.xtwinops-22 and device.xtwinops-23). The graded evidence stays provisional until the shared cleanup receipt
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
import { titleStackCaseIds, titleStackContractsVersion, titleStackSuiteId } from './title-stack-contracts.mjs';
import { adaptTitleStackCollector, titleStackAdapterId } from './title-stack-grading-collector.mjs';
import { buildTitleStackGradingReport, titleStackCounts } from './title-stack-grading.mjs';
export { titleStackCounts };
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['tst-observations.json', 'tst-run.json', 'tst-snapshots.jsonl', 'tst-unavailable.json', 'collector-tst.mjs'];
/** The first source policy whose worker always runs this suite. */
export const titleStackFirstPolicy = 46;
/** The only typed refusals a first observation may end in: the owned metadata API or the fixture's grid is unavailable. */
const unavailableReasons = new Set(['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable']);

/** Whether a retained artifact path exists; any failure other than a missing entry is fatal. */
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);

/** Any retained Title stack artifact or declaration requires this suffix, and a run from the first title-stack policy on always carries them, because the current
 * worker runs this suite unconditionally after the Core-gap suite. */
export function includesTitleStackEvidence(output, evidence, context) {
    return policyNumber(context.sourceSnapshotPolicy) >= titleStackFirstPolicy
        || context.titleStackAdapterId !== undefined || context.titleStackSuiteId !== undefined
        || context.titleStackBundleSha256 !== undefined || context.titleStackContractsVersion !== undefined
        || evidence.titleStackEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Every retained artifact must be a regular file inside the output directory: a symlink could point at valid evidence that later disappears. */
function requireRegularFiles(output, names) {
    for (const file of names) {
        if (!lstatSync(join(output, file)).isFile()) throw new Error(`Title stack artifact is not a regular file: ${file}`);
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
    const binding = evidence.titleStackEvidence;
    if (binding === undefined) throw new Error('Missing Title stack suite receipt binding.');
    const identity = binding && binding.runId === runId && binding.suiteId === titleStackSuiteId && binding.file === 'tst-observations.json'
        && binding.runFile === 'tst-run.json' && binding.provisionalUntil === 'cell-receipt.json';
    const digests = binding && [binding.sha256, binding.runSha256, binding.adapterBundleSha256].every(isDigest);
    if (!identity || !digests || binding.contractsVersion !== titleStackContractsVersion || context.titleStackContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid Title stack suite receipt binding.');
    }
    return binding;
}

/** The retained bytes of the suite, each checked against its receipt digest. */
function readRetained(output, binding, context) {
    requireRegularFiles(output, ['tst-observations.json', 'tst-run.json', 'tst-snapshots.jsonl', 'collector-tst.mjs']);
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('Title stack observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('Title stack run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-tst.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.titleStackBundleSha256 !== binding.adapterBundleSha256) throw new Error('Title stack adapter bytes changed.');
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
    if (Buffer.compare(Buffer.from(adaptTitleStackCollector(layer.toString())), adapterBytes) !== 0) throw new Error('Title stack adapter is not the retained layered derivation.');
    if (derived.some(([name, bytes]) => hash(bytes) !== context[`${name}BundleSha256`])) throw new Error(`Title stack adapter is not derived from the retained ${predecessorList}.`);
}

/** The owned metadata API (or the fixture's grid) was typed unavailable at the first observation: a marker, no run log and no report. */
function checkUnavailable(output, evidence, runId, context) {
    const binding = evidence.titleStackEvidence;
    const identity = binding && binding.runId === runId && binding.suiteId === titleStackSuiteId && binding.file === 'tst-unavailable.json'
        && binding.status === 'unavailable' && unavailableReasons.has(binding.reason) && binding.provisionalUntil === 'cell-receipt.json'
        && binding.contractsVersion === titleStackContractsVersion && context.titleStackContractsVersion === binding.contractsVersion
        && sameJson(binding.counts, {}) && isDigest(binding.sha256) && isDigest(binding.adapterBundleSha256);
    if (!identity) throw new Error('Invalid Title stack unavailable receipt binding.');
    // Every existing graded artifact must be a regular file before it is read (a symlink could name an external empty file, a FIFO could block the check).
    requireRegularFiles(output, ['tst-observations.json', 'tst-run.json', 'tst-snapshots.jsonl'].filter(file => present(join(output, file))));
    if (['tst-observations.json', 'tst-run.json', 'tst-snapshots.jsonl'].some(file => present(join(output, file)) && readFileSync(join(output, file)).length > 0)) {
        throw new Error('Title stack unavailable run retains graded artifacts.');
    }
    requireRegularFiles(output, ['tst-unavailable.json', 'collector-tst.mjs']);
    const markerBytes = readFileSync(join(output, 'tst-unavailable.json')), marker = JSON.parse(markerBytes);
    if (hash(markerBytes) !== binding.sha256 || marker.schemaVersion !== 1 || marker.runId !== runId || marker.suiteId !== titleStackSuiteId
        || marker.status !== 'unavailable' || marker.reason !== binding.reason || marker.plannedCases !== titleStackCaseIds.length) throw new Error('Title stack unavailable marker changed.');
    const adapterBytes = readFileSync(join(output, 'collector-tst.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.titleStackBundleSha256 !== binding.adapterBundleSha256) throw new Error('Title stack adapter bytes changed.');
    requireLayeredDerivation(output, adapterBytes, context);
}

/** Full rederivation: paired journal, run log, layered adapter bytes and conclusions. */
export function checkTitleStackEvidence(output, evidence, runId, context) {
    if (context.titleStackAdapterId !== titleStackAdapterId || context.titleStackSuiteId !== titleStackSuiteId) throw new Error('Missing or changed Title stack adapter declaration.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) {
        throw new Error(`Title stack evidence requires ${predecessorList}.`);
    }
    if (evidence.titleStackEvidence?.status === 'unavailable') return checkUnavailable(output, evidence, runId, context);
    const binding = requireBinding(evidence, runId, context);
    const { bytes, runBytes, adapterBytes } = readRetained(output, binding, context);
    requireLayeredDerivation(output, adapterBytes, context);
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    const identityMatches = [report.adapter.bundleSha256 === binding.adapterBundleSha256,
        report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256, report.contractsVersion === binding.contractsVersion];
    if (!identityMatches.every(Boolean)) throw new Error('Title stack report adapter or contract identity differs from the receipt.');
    const journalText = readFileSync(join(output, 'tst-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('Title stack journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildTitleStackGradingReport(frames, run, runId, report.adapter, { contractsVersion: report.contractsVersion });
    if (!sameJson(report, derived)) throw new Error('Title stack observations do not match retained evidence.');
    if (!sameJson(frames, report.snapshots)) throw new Error('Title stack journal does not match the report.');
    if (!sameJson(titleStackCounts(report), binding.counts)) throw new Error('Title stack receipt counts do not match the report.');
}
