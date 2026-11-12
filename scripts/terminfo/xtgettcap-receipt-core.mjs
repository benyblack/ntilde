// The single shared copy of the graded-receipt machinery this suite needs (the older suites each carry their own frozen copy; new
// code must not duplicate them again). It re-derives a suite's report from retained bytes, re-derives the layered adapter chain and
// checks every digest the receipt bound. Parameterized by the suite's file names, identities and report builder.
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { adaptGradingCollector } from './grading-collector.mjs';
import { adaptSgrColorsCollector } from './sgr-colors-grading-collector.mjs';
import { adaptSgrRgbCollector } from './sgr-rgb-grading-collector.mjs';
import { adaptSgrAttrsCollector } from './sgr-attrs-grading-collector.mjs';
import { adaptSgrPaletteCollector } from './sgr-palette-grading-collector.mjs';
import { adaptSgrTruecolorCollector } from './sgr-truecolor-grading-collector.mjs';
import { adaptSgrUnderlineCollector } from './sgr-underline-grading-collector.mjs';
import { adaptSgrUnderlineColorCollector } from './sgr-underline-color-grading-collector.mjs';
import { adaptSgrResetCollector } from './sgr-reset-grading-collector.mjs';
import { adaptSgrClosureCollector } from './sgr-closure-grading-collector.mjs';
import { adaptEraseLineCollector } from './erase-line-grading-collector.mjs';
import { adaptCharEditCollector } from './char-edit-grading-collector.mjs';
import { adaptEraseDisplayCollector } from './erase-display-grading-collector.mjs';
import { adaptLineEditCollector } from './line-edit-grading-collector.mjs';
import { adaptCharsetsCollector } from './charsets-grading-collector.mjs';
import { adaptScrollbackCollector } from './scrollback-grading-collector.mjs';
import { adaptSavedResetCollector } from './saved-reset-grading-collector.mjs';
import { adaptEraseAttrsRegionCollector } from './erase-attrs-region-grading-collector.mjs';
import { adaptAttrRectCollector } from './attr-rect-grading-collector.mjs';
import { adaptCursorQualCollector } from './cursor-qual-grading-collector.mjs';
import { adaptEraseCharsHistoryCollector } from './erase-chars-history-grading-collector.mjs';
import { adaptSelectiveEraseCollector } from './selective-erase-grading-collector.mjs';
import { adaptRectEditCollector } from './rect-edit-grading-collector.mjs';
import { adaptColEditCollector } from './col-edit-grading-collector.mjs';
import { adaptRectChecksumCollector } from './rect-checksum-grading-collector.mjs';
import { adaptLrMarginsCollector } from './lr-margins-grading-collector.mjs';
import { adaptTitleStackCollector } from './title-stack-grading-collector.mjs';

/** The canonical adapters of every earlier grading layer, keyed by the receipt's `<name>AdapterId` prefix. */
const adapters = {
    sgrColors: adaptSgrColorsCollector, sgrRgb: adaptSgrRgbCollector, sgrAttrs: adaptSgrAttrsCollector, sgrPalette: adaptSgrPaletteCollector,
    sgrTruecolor: adaptSgrTruecolorCollector, sgrUnderline: adaptSgrUnderlineCollector, sgrUnderlineColor: adaptSgrUnderlineColorCollector,
    sgrReset: adaptSgrResetCollector, sgrClosure: adaptSgrClosureCollector, eraseLine: adaptEraseLineCollector, charEdit: adaptCharEditCollector,
    eraseDisplay: adaptEraseDisplayCollector, lineEdit: adaptLineEditCollector, charsets: adaptCharsetsCollector,
    scrollback: adaptScrollbackCollector, savedReset: adaptSavedResetCollector, eraseAttrsRegion: adaptEraseAttrsRegionCollector,
    attrRect: adaptAttrRectCollector, cursorQual: adaptCursorQualCollector, eraseCharsHistory: adaptEraseCharsHistoryCollector, selectiveErase: adaptSelectiveEraseCollector,
    rectEdit: adaptRectEditCollector, colEdit: adaptColEditCollector, rectChecksum: adaptRectChecksumCollector, lrMargins: adaptLrMarginsCollector,
    titleStack: adaptTitleStackCollector,
};

const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const sameJson = (left, right) => JSON.stringify(left) === JSON.stringify(right);

/**
 * The complete check for one graded suite's receipt suffix. `suite` names its identities and files; `layerNames` are the predecessor
 * layers oldest-first; `build(observations, run, runId, adapter)` re-derives the report; `counts(report)` derives the bound counts.
 */
export function checkGradedReceiptSuite(output, evidence, runId, context, suite) {
    const { adapterId, suiteId, contractsVersion, evidenceKey, observationsFile, runFile, adapterFile, journalFile, layerNames, build, counts } = suite;
    if (context[suite.contextAdapterKey] !== adapterId || context[suite.contextSuiteKey] !== suiteId) {
        throw new Error(`Missing or changed ${suite.label} adapter declaration.`);
    }
    for (const name of layerNames) {
        if (context[`${name}AdapterId`] === undefined || evidence[`${name}Evidence`] === undefined) {
            throw new Error(`${suite.label} evidence requires its ${layerNames.length} predecessor layers.`);
        }
    }
    const binding = requireSuiteBinding(evidence[evidenceKey], runId, suite);
    const bytes = readRetainedSuite(output, binding, context, suite);
    requireLayeredAdapter(output, bytes.adapterBytes, context, layerNames, suite);
    const report = JSON.parse(bytes.observations), run = JSON.parse(bytes.run);
    if (![report.adapter.bundleSha256 === binding.adapterBundleSha256,
        report.adapter.upstreamBundleSha256 === context.upstreamBundleSha256, report.contractsVersion === binding.contractsVersion].every(Boolean)) {
        throw new Error(`${suite.label} report adapter or contract identity differs from the receipt.`);
    }
    const journalText = readFileSync(join(output, journalFile), 'utf8').trim();
    if (!journalText) throw new Error(`${suite.label} journal is empty.`);
    const frames = journalText.split('\n').map(line => JSON.parse(line));
    const derived = build(frames, run, runId, report.adapter, { contractsVersion: report.contractsVersion });
    if (!sameJson(report, derived)) throw new Error(`${suite.label} observations do not match retained evidence.`);
    if (!sameJson(frames, report.snapshots)) throw new Error(`${suite.label} journal does not match the report.`);
    if (!sameJson(counts(report), binding.counts)) throw new Error(`${suite.label} receipt counts do not match the report.`);
}

/** Every retained artifact of the suite must be a regular file: a symlink could name evidence that later disappears. */
function requireRegularSuiteFiles(output, names, label) {
    for (const file of names) {
        if (!lstatSync(join(output, file)).isFile()) throw new Error(`${label} artifact is not a regular file: ${file}`);
    }
}

/** The suite's receipt binding, or a refusal when it is missing or does not match the run. */
function requireSuiteBinding(binding, runId, suite) {
    if (binding === undefined) throw new Error(`Missing ${suite.label} suite receipt binding.`);
    const identity = binding && binding.runId === runId && binding.suiteId === suite.suiteId && binding.file === suite.observationsFile
        && binding.runFile === suite.runFile && binding.provisionalUntil === 'cell-receipt.json';
    if (!identity || ![binding.sha256, binding.runSha256, binding.adapterBundleSha256].every(isDigest)
        || binding.contractsVersion !== suite.contractsVersion || contextVersionDrift(binding, suite)) {
        throw new Error(`Invalid ${suite.label} suite receipt binding.`);
    }
    return binding;
}

/** The declared contracts version must agree with the binding's (the caller supplies the context through the suite). */
function contextVersionDrift(binding, suite) {
    return suite.contextContractsValue !== undefined && suite.contextContractsValue !== binding.contractsVersion;
}

/** The retained bytes of the suite, each checked against its receipt digest. */
function readRetainedSuite(output, binding, context, suite) {
    const files = [suite.observationsFile, suite.runFile, suite.adapterFile];
    requireRegularSuiteFiles(output, files, suite.label);
    const observations = readFileSync(join(output, suite.observationsFile));
    if (digest(observations) !== binding.sha256) throw new Error(`${suite.label} observation bytes changed.`);
    const run = readFileSync(join(output, suite.runFile));
    if (digest(run) !== binding.runSha256) throw new Error(`${suite.label} run log bytes changed.`);
    const adapterBytes = readFileSync(join(output, suite.adapterFile));
    if (digest(adapterBytes) !== binding.adapterBundleSha256 || context[suite.contextBundleKey] !== binding.adapterBundleSha256) {
        throw new Error(`${suite.label} adapter bytes changed.`);
    }
    return { observations, run, adapterBytes };
}

/** The retained extension must be exactly the layered derivation of every predecessor over the retained owned adapter. */
function requireLayeredAdapter(output, adapterBytes, context, layerNames, suite) {
    let layer = Buffer.from(adaptGradingCollector(readFileSync(join(output, 'collector-adapter.mjs'))));
    const derived = [];
    for (const name of layerNames) {
        layer = Buffer.from(adapters[name](layer.toString()));
        derived.push([name, layer]);
    }
    if (Buffer.compare(Buffer.from(suite.finalAdapt(layer.toString())), adapterBytes) !== 0) {
        throw new Error(`${suite.label} adapter is not the retained layered derivation.`);
    }
    if (derived.some(([name, bytes]) => digest(bytes) !== context[`${name}BundleSha256`])) {
        throw new Error(`${suite.label} adapter is not derived from the retained predecessor extensions.`);
    }
}
