// Receipt binding for the routed-input qualification (input.x10-mouse, input.button-event-mouse, modes.application-keypad). The graded
// evidence stays provisional until the shared cleanup receipt exists, and the check re-derives the whole report from the retained input
// journal, the worker's pinned-run log and the layered adapter bytes without the collector. A run carrying this evidence must also carry
// the owned routed-input suite evidence and every earlier grading layer: these conclusions sit beside theirs, never replace them.
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
import { inputPlanV2, routedInputFirstPolicy } from './input-plan.mjs';
import { routedInputCaseIds, routedInputContractsVersion, routedInputGradedSuiteId } from './routed-input-contracts.mjs';
import { adaptRoutedInputCollector, routedInputAdapterId } from './routed-input-grading-collector.mjs';
import { buildRoutedInputGradingReport, routedInputCounts } from './routed-input-grading.mjs';
export { routedInputCounts };
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns (the input journals belong to the owned input suite). */
const files = ['rin-observations.json', 'rin-run.json', 'rin-unavailable.json', 'collector-rin.mjs'];

/** Whether a retained artifact path exists; any failure other than a missing entry is fatal. */
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);

/** Any retained routed-input artifact or declaration requires this suffix, and a run from the first routed-input policy on always carries
 * them, because the current worker runs the pinned probes unconditionally after the owned input suite. */
export function includesRoutedInputEvidence(output, evidence, context) {
    return policyNumber(context.sourceSnapshotPolicy) >= routedInputFirstPolicy
        || context.routedInputAdapterId !== undefined || context.routedInputSuiteId !== undefined
        || context.routedInputBundleSha256 !== undefined || context.routedInputContractsVersion !== undefined
        || evidence.routedInputEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Every retained artifact must be a regular file inside the output directory: a symlink could point at valid evidence
 * that later disappears, leaving a run that can no longer be rederived. */
function requireRegularFiles(output) {
    for (const file of ['rin-observations.json', 'rin-run.json', 'collector-rin.mjs', 'input-snapshots.jsonl', 'input-actions.jsonl', 'input-observations.json']) {
        if (!lstatSync(join(output, file)).isFile()) throw new Error(`routed-input artifact is not a regular file: ${file}`);
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
    const binding = evidence.routedInputEvidence;
    if (binding === undefined) throw new Error('Missing routed-input suite receipt binding.');
    const identity = binding && binding.runId === runId && binding.suiteId === routedInputGradedSuiteId && binding.file === 'rin-observations.json'
        && binding.runFile === 'rin-run.json' && binding.provisionalUntil === 'cell-receipt.json';
    const digests = binding && [binding.sha256, binding.runSha256, binding.adapterBundleSha256].every(isDigest);
    if (!identity || !digests || binding.contractsVersion !== routedInputContractsVersion || context.routedInputContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid routed-input suite receipt binding.');
    }
    return binding;
}

/** The retained bytes of the suite, each checked against its receipt digest. */
function readRetained(output, binding, context) {
    requireRegularFiles(output);
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('routed-input observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('routed-input run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-rin.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.routedInputBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('routed-input adapter bytes changed.');
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
    if (Buffer.compare(Buffer.from(adaptRoutedInputCollector(layer.toString())), adapterBytes) !== 0) {
        throw new Error('routed-input adapter is not the retained layered derivation.');
    }
    if (derived.some(([name, bytes]) => hash(bytes) !== context[`${name}BundleSha256`])) {
        throw new Error(`routed-input adapter is not derived from the retained ${predecessorList}.`);
    }
}

const jsonLines = (output, file) => readFileSync(join(output, file), 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line));

/** The owned input suite was typed unavailable (a grid below the plan's minimum): the layer carries the same marker, no run log and no report. */
function checkUnavailable(output, evidence, runId, context) {
    const binding = evidence.routedInputEvidence, input = evidence.inputEvidence;
    const identity = binding && binding.runId === runId && binding.suiteId === routedInputGradedSuiteId && binding.file === 'rin-unavailable.json'
        && binding.status === 'unavailable' && binding.reason === input.reason && binding.provisionalUntil === 'cell-receipt.json'
        && binding.contractsVersion === routedInputContractsVersion && context.routedInputContractsVersion === binding.contractsVersion
        && sameJson(binding.counts, {}) && isDigest(binding.sha256) && isDigest(binding.adapterBundleSha256);
    if (!identity) throw new Error('Invalid routed-input unavailable receipt binding.');
    if (present(join(output, 'rin-observations.json')) || present(join(output, 'rin-run.json'))) throw new Error('routed-input unavailable run retains graded artifacts.');
    for (const file of ['rin-unavailable.json', 'collector-rin.mjs']) if (!lstatSync(join(output, file)).isFile()) throw new Error(`routed-input artifact is not a regular file: ${file}`);
    const markerBytes = readFileSync(join(output, 'rin-unavailable.json')), marker = JSON.parse(markerBytes);
    if (hash(markerBytes) !== binding.sha256 || marker.schemaVersion !== 1 || marker.runId !== runId || marker.suiteId !== routedInputGradedSuiteId
        || marker.status !== 'unavailable' || marker.reason !== input.reason || marker.plannedCases !== routedInputCaseIds.length) throw new Error('routed-input unavailable marker changed.');
    const adapterBytes = readFileSync(join(output, 'collector-rin.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.routedInputBundleSha256 !== binding.adapterBundleSha256) throw new Error('routed-input adapter bytes changed.');
    requireLayeredDerivation(output, adapterBytes, context);
}

/** Full rederivation: owned input journal, run log, layered adapter bytes and conclusions. */
export function checkRoutedInputEvidence(output, evidence, runId, context, proof) {
    if (context.routedInputAdapterId !== routedInputAdapterId || context.routedInputSuiteId !== routedInputGradedSuiteId) {
        throw new Error('Missing or changed routed-input adapter declaration.');
    }
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) {
        throw new Error(`routed-input evidence requires ${predecessorList}.`);
    }
    if (evidence.inputEvidence?.suiteId !== inputPlanV2.suiteId || context.inputSuiteId !== inputPlanV2.suiteId) {
        throw new Error('routed-input evidence requires the owned routed-input suite.');
    }
    if (evidence.inputEvidence.status === 'unavailable') return checkUnavailable(output, evidence, runId, context);
    if (evidence.inputEvidence.file !== 'input-observations.json') throw new Error('routed-input evidence requires the completed owned routed-input suite.');
    const binding = requireBinding(evidence, runId, context);
    const { bytes, runBytes, adapterBytes } = readRetained(output, binding, context);
    requireLayeredDerivation(output, adapterBytes, context);
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    const identityMatches = [report.adapter.bundleSha256 === binding.adapterBundleSha256,
        report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256, report.contractsVersion === binding.contractsVersion,
        report.inputReportSha256 === evidence.inputEvidence.sha256];
    if (!identityMatches.every(Boolean)) throw new Error('routed-input report adapter, contract or input identity differs from the receipt.');
    const input = { frames: jsonLines(output, 'input-snapshots.jsonl'), actions: jsonLines(output, 'input-actions.jsonl'), seal: proof?.proofs?.[0]?.input?.seal };
    const derived = buildRoutedInputGradingReport(input, run, runId, report.adapter, { contractsVersion: report.contractsVersion, inputReportSha256: evidence.inputEvidence.sha256 });
    if (!sameJson(report, derived)) throw new Error('routed-input observations do not match retained evidence.');
    if (!sameJson(routedInputCounts(report), binding.counts)) throw new Error('routed-input receipt counts do not match the report.');
}
