// Receipt binding for the modifyOtherKeys qualification (input.modify-other-keys, levels 1 and 2). The graded evidence stays
// provisional until the shared cleanup receipt exists, and the check re-derives the whole report from the retained input journal,
// the worker's pinned-run log and the layered adapter bytes without the collector. A run carrying this evidence must also carry the
// owned routed-input suite evidence (the v3 plan keeps its events verbatim) and every earlier grading layer: these conclusions sit
// beside theirs, never replace them.
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
import { inputPlanForSuiteId, modifyOtherKeysFirstPolicy } from './input-plan.mjs';
import { modifyOtherKeysCaseIds, modifyOtherKeysContractsVersion, modifyOtherKeysGradedSuiteId } from './modify-other-keys-contracts.mjs';
import { adaptModifyOtherKeysCollector, modifyOtherKeysAdapterId } from './modify-other-keys-grading-collector.mjs';
import { buildModifyOtherKeysGradingReport, modifyOtherKeysCounts } from './modify-other-keys-grading.mjs';
export { modifyOtherKeysCounts };
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns (the input journals belong to the owned input suite). */
const files = ['mok-observations.json', 'mok-run.json', 'mok-unavailable.json', 'collector-mok.mjs'];

/** Whether a retained artifact path exists; any failure other than a missing entry is fatal. */
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const predecessorList = 'every earlier grading layer (SGR through left/right-margin)';

/** Any retained modifyOtherKeys artifact or declaration requires this suffix, and a run from the first modifyOtherKeys policy on
 * always carries them, because the current worker runs the pinned probe unconditionally after the owned input suite. */
export function includesModifyOtherKeysEvidence(output, evidence, context) {
    return policyNumber(context.sourceSnapshotPolicy) >= modifyOtherKeysFirstPolicy
        || context.modifyOtherKeysAdapterId !== undefined || context.modifyOtherKeysSuiteId !== undefined
        || context.modifyOtherKeysBundleSha256 !== undefined || context.modifyOtherKeysContractsVersion !== undefined
        || evidence.modifyOtherKeysEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Every retained artifact must be a regular file inside the output directory: a symlink could point at valid evidence
 * that later disappears, leaving a run that can no longer be rederived. */
function requireRegularFiles(output) {
    for (const file of ['mok-observations.json', 'mok-run.json', 'collector-mok.mjs', 'input-snapshots.jsonl', 'input-actions.jsonl', 'input-observations.json']) {
        if (!lstatSync(join(output, file)).isFile()) throw new Error(`modifyOtherKeys artifact is not a regular file: ${file}`);
    }
}

/** The predecessor layers, oldest first (the left/right-margin layer last). */
const layerChain = [
    ['sgrColors', adaptSgrColorsCollector], ['sgrRgb', adaptSgrRgbCollector], ['sgrAttrs', adaptSgrAttrsCollector], ['sgrPalette', adaptSgrPaletteCollector],
    ['sgrTruecolor', adaptSgrTruecolorCollector], ['sgrUnderline', adaptSgrUnderlineCollector], ['sgrUnderlineColor', adaptSgrUnderlineColorCollector],
    ['sgrReset', adaptSgrResetCollector], ['sgrClosure', adaptSgrClosureCollector], ['eraseLine', adaptEraseLineCollector], ['charEdit', adaptCharEditCollector],
    ['eraseDisplay', adaptEraseDisplayCollector], ['lineEdit', adaptLineEditCollector], ['charsets', adaptCharsetsCollector],
    ['scrollback', adaptScrollbackCollector], ['savedReset', adaptSavedResetCollector], ['eraseAttrsRegion', adaptEraseAttrsRegionCollector],
    ['attrRect', adaptAttrRectCollector], ['cursorQual', adaptCursorQualCollector], ['eraseCharsHistory', adaptEraseCharsHistoryCollector], ['selectiveErase', adaptSelectiveEraseCollector],
    ['rectEdit', adaptRectEditCollector], ['colEdit', adaptColEditCollector], ['rectChecksum', adaptRectChecksumCollector], ['lrMargins', adaptLrMarginsCollector],
];

/** The receipt binding of this suite, or a refusal when it is missing or does not match the run. */
function requireBinding(evidence, runId, context) {
    const binding = evidence.modifyOtherKeysEvidence;
    if (binding === undefined) throw new Error('Missing modifyOtherKeys suite receipt binding.');
    const identity = binding && binding.runId === runId && binding.suiteId === modifyOtherKeysGradedSuiteId && binding.file === 'mok-observations.json'
        && binding.runFile === 'mok-run.json' && binding.provisionalUntil === 'cell-receipt.json';
    const digests = binding && [binding.sha256, binding.runSha256, binding.adapterBundleSha256].every(isDigest);
    if (!identity || !digests || binding.contractsVersion !== modifyOtherKeysContractsVersion || context.modifyOtherKeysContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid modifyOtherKeys suite receipt binding.');
    }
    return binding;
}

/** The retained bytes of the suite, each checked against its receipt digest. */
function readRetained(output, binding, context) {
    requireRegularFiles(output);
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('modifyOtherKeys observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('modifyOtherKeys run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-mok.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.modifyOtherKeysBundleSha256 !== binding.adapterBundleSha256) {
        throw new Error('modifyOtherKeys adapter bytes changed.');
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
    if (Buffer.compare(Buffer.from(adaptModifyOtherKeysCollector(layer.toString())), adapterBytes) !== 0) {
        throw new Error('modifyOtherKeys adapter is not the retained layered derivation.');
    }
    if (derived.some(([name, bytes]) => hash(bytes) !== context[`${name}BundleSha256`])) {
        throw new Error(`modifyOtherKeys adapter is not derived from the retained ${predecessorList}.`);
    }
}

const jsonLines = (output, file) => readFileSync(join(output, file), 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line));

/** The owned input suite was typed unavailable (a grid below the plan's minimum): the layer carries the same marker, no run log and no report. */
function checkUnavailable(output, evidence, runId, context) {
    const binding = evidence.modifyOtherKeysEvidence, input = evidence.inputEvidence;
    const identity = binding && binding.runId === runId && binding.suiteId === modifyOtherKeysGradedSuiteId && binding.file === 'mok-unavailable.json'
        && binding.status === 'unavailable' && binding.reason === input.reason && binding.provisionalUntil === 'cell-receipt.json'
        && binding.contractsVersion === modifyOtherKeysContractsVersion && context.modifyOtherKeysContractsVersion === binding.contractsVersion
        && sameJson(binding.counts, {}) && isDigest(binding.sha256) && isDigest(binding.adapterBundleSha256);
    if (!identity) throw new Error('Invalid modifyOtherKeys unavailable receipt binding.');
    if (present(join(output, 'mok-observations.json')) || present(join(output, 'mok-run.json'))) throw new Error('modifyOtherKeys unavailable run retains graded artifacts.');
    for (const file of ['mok-unavailable.json', 'collector-mok.mjs']) if (!lstatSync(join(output, file)).isFile()) throw new Error(`modifyOtherKeys artifact is not a regular file: ${file}`);
    const markerBytes = readFileSync(join(output, 'mok-unavailable.json')), marker = JSON.parse(markerBytes);
    if (hash(markerBytes) !== binding.sha256 || marker.schemaVersion !== 1 || marker.runId !== runId || marker.suiteId !== modifyOtherKeysGradedSuiteId
        || marker.status !== 'unavailable' || marker.reason !== input.reason || marker.plannedCases !== modifyOtherKeysCaseIds.length) throw new Error('modifyOtherKeys unavailable marker changed.');
    const adapterBytes = readFileSync(join(output, 'collector-mok.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.modifyOtherKeysBundleSha256 !== binding.adapterBundleSha256) throw new Error('modifyOtherKeys adapter bytes changed.');
    requireLayeredDerivation(output, adapterBytes, context);
}

/** Full rederivation: owned input journal, run log, layered adapter bytes and conclusions. */
export function checkModifyOtherKeysEvidence(output, evidence, runId, context, proof) {
    const plan = inputPlanForSuiteId(context.inputSuiteId);
    if (context.modifyOtherKeysAdapterId !== modifyOtherKeysAdapterId || context.modifyOtherKeysSuiteId !== modifyOtherKeysGradedSuiteId) {
        throw new Error('Missing or changed modifyOtherKeys adapter declaration.');
    }
    if (plan === undefined || plan.id !== 'v3') throw new Error('modifyOtherKeys evidence requires the owned routed-input v3 suite plan.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) {
        throw new Error(`modifyOtherKeys evidence requires ${predecessorList}.`);
    }
    if (evidence.routedInputEvidence === undefined || context.routedInputAdapterId === undefined) {
        throw new Error('modifyOtherKeys evidence requires the routed-input qualification of the same journal.');
    }
    if (evidence.inputEvidence?.suiteId !== plan.suiteId || context.inputSuiteId !== plan.suiteId) {
        throw new Error('modifyOtherKeys evidence requires the owned routed-input v3 suite.');
    }
    if (evidence.inputEvidence.status === 'unavailable') return checkUnavailable(output, evidence, runId, context);
    if (evidence.inputEvidence.file !== 'input-observations.json') throw new Error('modifyOtherKeys evidence requires the completed owned input suite.');
    const binding = requireBinding(evidence, runId, context);
    const { bytes, runBytes, adapterBytes } = readRetained(output, binding, context);
    requireLayeredDerivation(output, adapterBytes, context);
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    const identityMatches = [report.adapter.bundleSha256 === binding.adapterBundleSha256,
        report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256, report.contractsVersion === binding.contractsVersion,
        report.inputReportSha256 === evidence.inputEvidence.sha256];
    if (!identityMatches.every(Boolean)) throw new Error('modifyOtherKeys report adapter, contract or input identity differs from the receipt.');
    const input = { frames: jsonLines(output, 'input-snapshots.jsonl'), actions: jsonLines(output, 'input-actions.jsonl'), seal: proof?.proofs?.[0]?.input?.seal };
    const derived = buildModifyOtherKeysGradingReport(input, run, runId, report.adapter, { contractsVersion: report.contractsVersion, inputPlan: plan,
        inputReportSha256: evidence.inputEvidence.sha256 });
    if (!sameJson(report, derived)) throw new Error('modifyOtherKeys observations do not match retained evidence.');
    if (!sameJson(modifyOtherKeysCounts(report), binding.counts)) throw new Error('modifyOtherKeys receipt counts do not match the report.');
}
