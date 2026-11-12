// Receipt binding for the XTGETTCAP qualification (device.xtgettcap). The graded evidence stays provisional until the shared
// cleanup receipt exists, and the check re-derives the whole report from retained bytes without the collector. A run carrying
// this evidence must also carry every earlier grading layer (through title/icon-stack): these conclusions sit beside theirs,
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
import { adaptRectChecksumCollector } from './rect-checksum-grading-collector.mjs';
import { adaptLrMarginsCollector } from './lr-margins-grading-collector.mjs';
import { adaptTitleStackCollector } from './title-stack-grading-collector.mjs';
import { xtgettcapContractsVersion, xtgettcapSuiteId } from './xtgettcap-contracts.mjs';
import { adaptXtgettcapCollector, xtgettcapAdapterId } from './xtgettcap-grading-collector.mjs';
import { buildXtgettcapGradingReport } from './xtgettcap-grading.mjs';
/** SHA-256 of exact bytes. */
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
/** Every retained artifact this suffix owns. */
const files = ['xgtc-observations.json', 'xgtc-run.json', 'xgtc-snapshots.jsonl', 'collector-xgtc.mjs'];
/** The first source policy whose worker always runs this suite. */
export const xtgettcapFirstPolicy = 48;

/** Whether a retained artifact path exists; any failure other than a missing entry is fatal. */
const present = path => lstatSync(path, { throwIfNoEntry: false }) !== undefined;
const policyNumber = policy => Number(/^readonly-owned-source-graph-v(\d+)$/.exec(policy ?? '')?.[1] ?? 0);

/** Any retained XTGETTCAP artifact or declaration requires this suffix, and a run from the first XTGETTCAP policy on always
 * carries them, because the current worker runs this suite unconditionally after the title/icon-stack suite. */
export function includesXtgettcapEvidence(output, evidence, context) {
    return policyNumber(context.sourceSnapshotPolicy) >= xtgettcapFirstPolicy
        || context.xtgettcapAdapterId !== undefined || context.xtgettcapSuiteId !== undefined
        || context.xtgettcapBundleSha256 !== undefined || context.xtgettcapContractsVersion !== undefined
        || evidence.xtgettcapEvidence !== undefined
        || files.some(file => present(join(output, file)));
}

/** Every retained artifact must be a regular file inside the output directory: a symlink could point at valid evidence that later
 * disappears, leaving a run that can no longer be rederived. */
function requireRegularFiles(output) {
    for (const file of files) {
        if (!lstatSync(join(output, file)).isFile()) throw new Error(`XTGETTCAP artifact is not a regular file: ${file}`);
    }
}

/** The outcome counts of the case conclusions (the pinned verdict, preserved by design). */
export function xtgettcapCounts(report) {
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return counts;
}

/** The predecessor layers, oldest first (the title/icon-stack layer last). Each entry names the layer (its `<name>AdapterId`,
 * `<name>Evidence` and `<name>BundleSha256` in the receipt) and the adapter that derives its bundle from the layer before it. */
const layerChain = [
    ['sgrColors', adaptSgrColorsCollector], ['sgrRgb', adaptSgrRgbCollector], ['sgrAttrs', adaptSgrAttrsCollector], ['sgrPalette', adaptSgrPaletteCollector],
    ['sgrTruecolor', adaptSgrTruecolorCollector], ['sgrUnderline', adaptSgrUnderlineCollector], ['sgrUnderlineColor', adaptSgrUnderlineColorCollector],
    ['sgrReset', adaptSgrResetCollector], ['sgrClosure', adaptSgrClosureCollector], ['eraseLine', adaptEraseLineCollector], ['charEdit', adaptCharEditCollector],
    ['eraseDisplay', adaptEraseDisplayCollector], ['lineEdit', adaptLineEditCollector], ['charsets', adaptCharsetsCollector],
    ['scrollback', adaptScrollbackCollector], ['savedReset', adaptSavedResetCollector], ['eraseAttrsRegion', adaptEraseAttrsRegionCollector],
    ['attrRect', adaptAttrRectCollector], ['cursorQual', adaptCursorQualCollector], ['eraseCharsHistory', adaptEraseCharsHistoryCollector], ['selectiveErase', adaptSelectiveEraseCollector],
    ['rectEdit', adaptRectEditCollector], ['colEdit', adaptColEditCollector], ['rectChecksum', adaptRectChecksumCollector], ['lrMargins', adaptLrMarginsCollector],
    ['titleStack', adaptTitleStackCollector],
];
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const predecessorList = 'every earlier grading layer (SGR through title/icon-stack)';

/** The receipt binding of this suite, or a refusal when it is missing or does not match the run. */
function requireBinding(evidence, runId, context) {
    const binding = evidence.xtgettcapEvidence;
    if (binding === undefined) throw new Error('Missing XTGETTCAP suite receipt binding.');
    const identity = binding && binding.runId === runId && binding.suiteId === xtgettcapSuiteId && binding.file === 'xgtc-observations.json'
        && binding.runFile === 'xgtc-run.json' && binding.provisionalUntil === 'cell-receipt.json';
    const digests = binding && [binding.sha256, binding.runSha256, binding.adapterBundleSha256].every(isDigest);
    if (!identity || !digests || binding.contractsVersion !== xtgettcapContractsVersion || context.xtgettcapContractsVersion !== binding.contractsVersion) {
        throw new Error('Invalid XTGETTCAP suite receipt binding.');
    }
    return binding;
}

/** The retained bytes of the suite, each checked against its receipt digest. */
function readRetained(output, binding, context) {
    requireRegularFiles(output);
    const bytes = readFileSync(join(output, binding.file));
    if (hash(bytes) !== binding.sha256) throw new Error('XTGETTCAP observation bytes changed.');
    const runBytes = readFileSync(join(output, binding.runFile));
    if (hash(runBytes) !== binding.runSha256) throw new Error('XTGETTCAP run log bytes changed.');
    const adapterBytes = readFileSync(join(output, 'collector-xgtc.mjs'));
    if (hash(adapterBytes) !== binding.adapterBundleSha256 || context.xtgettcapBundleSha256 !== binding.adapterBundleSha256) throw new Error('XTGETTCAP adapter bytes changed.');
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
    if (Buffer.compare(Buffer.from(adaptXtgettcapCollector(layer.toString())), adapterBytes) !== 0) throw new Error('XTGETTCAP adapter is not the retained layered derivation.');
    if (derived.some(([name, bytes]) => hash(bytes) !== context[`${name}BundleSha256`])) {
        throw new Error(`XTGETTCAP adapter is not derived from the retained ${predecessorList}.`);
    }
}

/** Full rederivation: journal, run log, layered adapter bytes and conclusions. */
export function checkXtgettcapEvidence(output, evidence, runId, context) {
    if (context.xtgettcapAdapterId !== xtgettcapAdapterId || context.xtgettcapSuiteId !== xtgettcapSuiteId) throw new Error('Missing or changed XTGETTCAP adapter declaration.');
    if (layerChain.some(([name]) => context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined)) {
        throw new Error(`XTGETTCAP evidence requires ${predecessorList}.`);
    }
    const binding = requireBinding(evidence, runId, context);
    const { bytes, runBytes, adapterBytes } = readRetained(output, binding, context);
    requireLayeredDerivation(output, adapterBytes, context);
    const report = JSON.parse(bytes), run = JSON.parse(runBytes);
    const identityMatches = [report.adapter.bundleSha256 === binding.adapterBundleSha256,
        report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256, report.contractsVersion === binding.contractsVersion];
    if (!identityMatches.every(Boolean)) throw new Error('XTGETTCAP report adapter or contract identity differs from the receipt.');
    const journalText = readFileSync(join(output, 'xgtc-snapshots.jsonl'), 'utf8').trim();
    if (!journalText) throw new Error('XTGETTCAP journal is empty.');
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = buildXtgettcapGradingReport(frames, run, runId, report.adapter, { contractsVersion: report.contractsVersion });
    if (!sameJson(report, derived)) throw new Error('XTGETTCAP observations do not match retained evidence.');
    if (!sameJson(frames, report.snapshots)) throw new Error('XTGETTCAP journal does not match the report.');
    if (!sameJson(xtgettcapCounts(report), binding.counts)) throw new Error('XTGETTCAP receipt counts do not match the report.');
}

/** The case ids this suite grades, exposed for the receipt's planned-case declarations. */
export { xtgettcapCaseIds } from './xtgettcap-contracts.mjs';
