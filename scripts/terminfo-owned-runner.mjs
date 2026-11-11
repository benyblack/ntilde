#!/usr/bin/env node
import { WrapObserver } from './terminfo/core-wrap-observer.mjs';
import { deriveWrapEvidence } from './terminfo/core-wrap-contract.mjs';
import { wrapSuiteId } from './terminfo/core-wrap-collector.mjs';
import { eraseSuiteId as coreEraseSuiteId, EraseObserver, buildEraseReport as buildCoreEraseReport } from './terminfo/core-erase.mjs';
import { textCursorSuiteId, TextCursorObserver, buildTextCursorReport } from './terminfo/core-text-cursor.mjs';
import { coreGapsSuiteId, coreGapsAdapterId, adaptCoreGapsCollector, CoreGapsObserver, buildCoreGapsReport } from './terminfo/core-gaps.mjs';
import { ownedSourceFiles, ownedSourcePath, assertOwnedSourcesUnchanged, readSourceSnapshot, removeSourceSnapshot, sourceSnapshotPolicy } from './terminfo/owned-sources.mjs';
import { OwnedMetadataObserver, initialMetadataRefusal, metadataFailure } from './terminfo/metadata-capture.mjs';
import { metadataSuiteId, ownedMetadataPolicy, makeMetadataCases } from './terminfo/metadata-cases.mjs';
import { buildMetadataReport } from './terminfo/metadata.mjs';
// Launches a private macOS desktop instance; never attaches to an existing pane.
import { spawn, execFileSync } from 'node:child_process';
import { createHash, randomBytes, randomUUID, timingSafeEqual } from 'node:crypto';
import { appendFileSync, chmodSync, closeSync, existsSync, mkdirSync, mkdtempSync, openSync, readFileSync, realpathSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { adapterId, adaptCollector, upstreamBundleSha256, upstreamReceipt, validateCollectorReceipt } from './terminfo/collector.mjs';
import { adaptGradingCollector, gradingAdapterId } from './terminfo/grading-collector.mjs';
import { adaptSgrColorsCollector, sgrColorsAdapterId } from './terminfo/sgr-colors-grading-collector.mjs';
import { sgrColorsContractsVersion, sgrColorsSuiteId } from './terminfo/sgr-colors-contracts.mjs';
import { buildSgrColorsGradingReport } from './terminfo/sgr-colors-grading.mjs';
import { adaptSgrRgbCollector, sgrRgbAdapterId } from './terminfo/sgr-rgb-grading-collector.mjs';
import { sgrRgbContractsVersion, sgrRgbSuiteId } from './terminfo/sgr-rgb-contracts.mjs';
import { buildSgrRgbGradingReport } from './terminfo/sgr-rgb-grading.mjs';
import { adaptSgrAttrsCollector, sgrAttrsAdapterId } from './terminfo/sgr-attrs-grading-collector.mjs';
import { sgrAttrsContractsVersion, sgrAttrsSuiteId } from './terminfo/sgr-attrs-contracts.mjs';
import { buildSgrAttrsGradingReport } from './terminfo/sgr-attrs-grading.mjs';
import { adaptSgrPaletteCollector, sgrPaletteAdapterId } from './terminfo/sgr-palette-grading-collector.mjs';
import { sgrPaletteContractsVersion, sgrPaletteSuiteId } from './terminfo/sgr-palette-contracts.mjs';
import { buildSgrPaletteGradingReport } from './terminfo/sgr-palette-grading.mjs';
import { adaptSgrTruecolorCollector, sgrTruecolorAdapterId } from './terminfo/sgr-truecolor-grading-collector.mjs';
import { sgrTruecolorContractsVersion, sgrTruecolorSuiteId } from './terminfo/sgr-truecolor-contracts.mjs';
import { buildSgrTruecolorGradingReport } from './terminfo/sgr-truecolor-grading.mjs';
import { truecolorFindingContractsVersion, truecolorFindingSuiteId } from './terminfo/truecolor-literal-finding-contracts.mjs';
import { adaptSgrUnderlineCollector, sgrUnderlineAdapterId } from './terminfo/sgr-underline-grading-collector.mjs';
import { sgrUnderlineContractsVersion, sgrUnderlineSuiteId } from './terminfo/sgr-underline-contracts.mjs';
import { buildSgrUnderlineGradingReport } from './terminfo/sgr-underline-grading.mjs';
import { adaptSgrUnderlineColorCollector, sgrUnderlineColorAdapterId } from './terminfo/sgr-underline-color-grading-collector.mjs';
import { sgrUnderlineColorContractsVersion, sgrUnderlineColorSuiteId } from './terminfo/sgr-underline-color-contracts.mjs';
import { buildSgrUnderlineColorGradingReport } from './terminfo/sgr-underline-color-grading.mjs';
import { underlineResolvedContractsVersion, underlineResolvedSuiteId } from './terminfo/sgr-underline-color-resolved-contracts.mjs';
import { buildUnderlineResolvedReport } from './terminfo/sgr-underline-color-resolved-grading.mjs';
import { adaptSgrResetCollector, sgrResetAdapterId } from './terminfo/sgr-reset-grading-collector.mjs';
import { sgrResetContractsVersion, sgrResetSuiteId } from './terminfo/sgr-reset-contracts.mjs';
import { buildSgrResetGradingReport } from './terminfo/sgr-reset-grading.mjs';
import { adaptSgrClosureCollector, sgrClosureAdapterId } from './terminfo/sgr-closure-grading-collector.mjs';
import { sgrClosureContractsVersion, sgrClosureSuiteId } from './terminfo/sgr-closure-contracts.mjs';
import { buildSgrClosureGradingReport } from './terminfo/sgr-closure-grading.mjs';
import { adaptEraseLineCollector, eraseLineAdapterId } from './terminfo/erase-line-grading-collector.mjs';
import { eraseLineContractsVersion, eraseLineSuiteId } from './terminfo/erase-line-contracts.mjs';
import { buildEraseLineGradingReport } from './terminfo/erase-line-grading.mjs';
import { adaptCharEditCollector, charEditAdapterId } from './terminfo/char-edit-grading-collector.mjs';
import { charEditContractsVersion, charEditSuiteId } from './terminfo/char-edit-contracts.mjs';
import { buildCharEditGradingReport } from './terminfo/char-edit-grading.mjs';
import { adaptEraseDisplayCollector, eraseDisplayAdapterId } from './terminfo/erase-display-grading-collector.mjs';
import { adaptLineEditCollector, lineEditAdapterId } from './terminfo/line-edit-grading-collector.mjs';
import { eraseDisplayContractsVersion, eraseDisplaySuiteId } from './terminfo/erase-display-contracts.mjs';
import { lineEditContractsVersion, lineEditSuiteId } from './terminfo/line-edit-contracts.mjs';
import { buildEraseDisplayGradingReport } from './terminfo/erase-display-grading.mjs';
import { buildLineEditGradingReport } from './terminfo/line-edit-grading.mjs';
import { adaptCharsetsCollector, charsetsAdapterId } from './terminfo/charsets-grading-collector.mjs';
import { charsetsContractsVersion, charsetsSuiteId } from './terminfo/charsets-contracts.mjs';
import { buildCharsetsGradingReport } from './terminfo/charsets-grading.mjs';
import { adaptScrollbackCollector, scrollbackAdapterId } from './terminfo/scrollback-grading-collector.mjs';
import { scrollbackContractsVersion, scrollbackSuiteId } from './terminfo/scrollback-contracts.mjs';
import { buildScrollbackGradingReport } from './terminfo/scrollback-grading.mjs';
import { adaptSavedResetCollector, savedResetAdapterId } from './terminfo/saved-reset-grading-collector.mjs';
import { savedResetContractsVersion, savedResetSuiteId } from './terminfo/saved-reset-contracts.mjs';
import { buildSavedResetGradingReport } from './terminfo/saved-reset-grading.mjs';
import { savedResetCounts } from './terminfo/saved-reset-receipt.mjs';
import { adaptEraseAttrsRegionCollector, eraseAttrsRegionAdapterId } from './terminfo/erase-attrs-region-grading-collector.mjs';
import { eraseAttrsRegionContractsVersion, eraseAttrsRegionSuiteId } from './terminfo/erase-attrs-region-contracts.mjs';
import { buildEraseAttrsRegionGradingReport } from './terminfo/erase-attrs-region-grading.mjs';
import { eraseAttrsRegionCounts } from './terminfo/erase-attrs-region-receipt.mjs';
import { adaptAttrRectCollector, attrRectAdapterId } from './terminfo/attr-rect-grading-collector.mjs';
import { attrRectContractsVersion, attrRectSuiteId } from './terminfo/attr-rect-contracts.mjs';
import { buildAttrRectGradingReport } from './terminfo/attr-rect-grading.mjs';
import { attrRectCounts } from './terminfo/attr-rect-receipt.mjs';
import { adaptCursorQualCollector, cursorQualAdapterId } from './terminfo/cursor-qual-grading-collector.mjs';
import { cursorQualContractsVersion, cursorQualSuiteId } from './terminfo/cursor-qual-contracts.mjs';
import { buildCursorQualGradingReport, verifyCursorQualLiveReplay } from './terminfo/cursor-qual-grading.mjs';
import { CursorQualObserver } from './terminfo/cursor-qual-capture.mjs';
import { adaptEraseCharsHistoryCollector, eraseCharsHistoryAdapterId } from './terminfo/erase-chars-history-grading-collector.mjs';
import { eraseCharsHistoryContractsVersion, eraseCharsHistorySuiteId } from './terminfo/erase-chars-history-contracts.mjs';
import { buildEraseCharsHistoryGradingReport } from './terminfo/erase-chars-history-grading.mjs';
import { eraseCharsHistoryCounts } from './terminfo/erase-chars-history-receipt.mjs';
import { adaptSelectiveEraseCollector, selectiveEraseAdapterId } from './terminfo/selective-erase-grading-collector.mjs';
import { selectiveEraseContractsVersion, selectiveEraseSuiteId } from './terminfo/selective-erase-contracts.mjs';
import { buildSelectiveEraseGradingReport } from './terminfo/selective-erase-grading.mjs';
import { selectiveEraseCounts } from './terminfo/selective-erase-receipt.mjs';
import { adaptRectEditCollector, rectEditAdapterId } from './terminfo/rect-edit-grading-collector.mjs';
import { rectEditContractsVersion, rectEditSuiteId } from './terminfo/rect-edit-contracts.mjs';
import { buildRectEditGradingReport } from './terminfo/rect-edit-grading.mjs';
import { rectEditCounts } from './terminfo/rect-edit-receipt.mjs';
import { adaptColEditCollector, colEditAdapterId } from './terminfo/col-edit-grading-collector.mjs';
import { colEditContractsVersion, colEditSuiteId } from './terminfo/col-edit-contracts.mjs';
import { buildColEditGradingReport } from './terminfo/col-edit-grading.mjs';
import { colEditCounts } from './terminfo/col-edit-receipt.mjs';
import { adaptRectChecksumCollector, rectChecksumAdapterId } from './terminfo/rect-checksum-grading-collector.mjs';
import { rectChecksumContractsVersion, rectChecksumSuiteId } from './terminfo/rect-checksum-contracts.mjs';
import { buildRectChecksumGradingReport } from './terminfo/rect-checksum-grading.mjs';
import { rectChecksumCounts } from './terminfo/rect-checksum-receipt.mjs';
import { RectChecksumObserver } from './terminfo/rect-checksum-capture.mjs';
import { adaptLrMarginsCollector, lrMarginsAdapterId } from './terminfo/lr-margins-grading-collector.mjs';
import { lrMarginsContractsVersion, lrMarginsSuiteId } from './terminfo/lr-margins-contracts.mjs';
import { buildLrMarginsGradingReport } from './terminfo/lr-margins-grading.mjs';
import { lrMarginsCounts } from './terminfo/lr-margins-receipt.mjs';
import { adaptRoutedInputCollector, routedInputAdapterId } from './terminfo/routed-input-grading-collector.mjs';
import { routedInputContractsVersion, routedInputGradedSuiteId } from './terminfo/routed-input-contracts.mjs';
import { buildRoutedInputGradingReport } from './terminfo/routed-input-grading.mjs';
import { routedInputCounts } from './terminfo/routed-input-receipt.mjs';
import { adaptColorResetCollector, colorResetAdapterId } from './terminfo/color-reset-grading-collector.mjs';
import { colorResetContractsVersion, colorResetSuiteId } from './terminfo/color-reset-contracts.mjs';
import { buildColorResetGradingReport } from './terminfo/color-reset-grading.mjs';
import { colorResetCounts } from './terminfo/color-reset-receipt.mjs';
import { adaptOsc133Collector, osc133AdapterId } from './terminfo/osc133-grading-collector.mjs';
import { osc133ContractsVersion, osc133Minimum, osc133Prefix, osc133SuiteId } from './terminfo/osc133-contracts.mjs';
import { buildOsc133GradingReport, matchesOsc133Baseline, osc133Counts, osc133ObserverPlan } from './terminfo/osc133-grading.mjs';
import { ColorResetObserver } from './terminfo/color-reset-capture.mjs';
import { buildTruecolorFindingReport } from './terminfo/truecolor-literal-finding-grading.mjs';
import { asciiSgrContractsVersion, asciiSgrSuiteId } from './terminfo/ascii-sgr-contracts.mjs';
import { buildAsciiSgrGradingReport } from './terminfo/ascii-sgr-grading.mjs';
import { processBirth, outputDevice, assertProcessOwnership, matchesRecordedWorker, processRecord, terminateRecordedWorker, workerExists } from './terminfo/ownership.mjs';
import { createPrivateScratch, createProcessBudget, installLockedCollector, readCollectorLockfile, resolveOwnedExecutable, validateCollectorInputs } from './terminfo/runtime.mjs';
import { OwnedSavedObserver } from './terminfo/saved-capture.mjs';
import { savedSuiteId, ownedSavedPolicy } from './terminfo/saved-cases.mjs';
import { buildSavedReport } from './terminfo/saved.mjs';
import { OwnedResizeObserver } from './terminfo/resize-capture.mjs';
import { resizeSuiteId, ownedResizePolicy } from './terminfo/resize-cases.mjs';
import { buildResizeReport } from './terminfo/resize.mjs';
import { resizeOwnedPane } from './terminfo/resize-action.mjs';
import { OwnedInputObserver } from './terminfo/input-capture.mjs';
import { currentInputPlan } from './terminfo/input-plan.mjs';
const inputSuiteId = currentInputPlan.suiteId, ownedInputPolicy = currentInputPlan.policy;
import { generateOwnedInput } from './terminfo/input-action.mjs';
import { OwnedQueryObserver, rejectQueryOwnerRequest } from './terminfo/query-capture.mjs';
import { querySuiteId, ownedQueryPolicy, queryPriorCounts } from './terminfo/queries.mjs';
import { OwnedStaticImageObserver } from './terminfo/static-images-capture.mjs';
import { staticImageSuiteId } from './terminfo/static-images-cases.mjs';
import { ownedStaticImagePolicy } from './terminfo/static-images.mjs';
import { OwnedCellObserver } from './terminfo/agent-observer.mjs';
import { buildCellReport } from './terminfo/cells.mjs';
import { buildSgrReport } from './terminfo/sgr.mjs';
import { buildRgbReport, rgbCases, rgbSuiteId } from './terminfo/rgb.mjs';
import { buildRenditionReport, renditionCases, renditionSuiteId } from './terminfo/rendition.mjs';
import { buildCursorReport, cursorCases, cursorSuiteId } from './terminfo/cursor.mjs';
import { OwnedPixelObserver } from './terminfo/pixels-capture.mjs';
import { pixelSuiteId, pixelScenes } from './terminfo/pixels-cases.mjs';
import { OwnedColorObserver } from './terminfo/color-capture.mjs';
import { colorSuiteId, colorCases } from './terminfo/color-cases.mjs';
import { buildColorReport } from './terminfo/colors.mjs';
import { buildPixelReport } from './terminfo/pixels.mjs';
import { OwnedEditingObserver } from './terminfo/editing-capture.mjs';
import { eraseSuiteId, editingSuiteId } from './terminfo/editing-cases.mjs';
import { buildEraseReport, buildCellEditingReport } from './terminfo/editing.mjs';
import { OwnedHistoryObserver } from './terminfo/history-capture.mjs';
import { historySuiteId } from './terminfo/history-cases.mjs';
import { buildHistoryReport } from './terminfo/history.mjs';
import { completeOwnedReceipt } from './terminfo/receipt.mjs';
import { readOwnedTerminalState, privateClipboardOutputPolicy, readOwnedOutputMode } from './terminfo/terminal-output.mjs';

import { abortPrivateClipboardWithFacts, privateClipboardFailureFacts, privateClipboardEvidenceJson } from './terminfo/private-clipboard.mjs';
import { OwnedPrivateClipboardObserver, privateClipboardScreen } from './terminfo/private-clipboard-capture.mjs';
import { ownedPrivateClipboardPolicy, ownedPrivateClipboardSuiteId } from './terminfo/private-clipboard-worker.mjs';

/** The observation request prefixes whose `-start`, `-bind`, `-read` and `-finish` kinds are cell observations served without translation. */
const untranslatedCellPrefixes = new Set([
    'cell', 'sgr', 'grading', 'sgcolors', 'sgrgb', 'sgattr', 'sgpal', 'sgtc', 'sgtf', 'sgus', 'sguc', 'sgur', 'sgsr', 'sgcl', 'erln', 'ched', 'erdi',
    'ledt', 'chrs', 'scrl', 'svrs', 'erar', 'atrc', 'curq', 'echh', 'sels', 'rect', 'cole', 'csum', 'lrmm', 'cg', 'tc', 'ce', 'crst', 'shi', 'rgb', 'rendition', 'cursor', 'pixel', 'color', 'erase', 'editing',
    'history', 'saved', 'metadata',
]);
const coreWrapKinds = new Set(['cw-geometry-start', 'cw-narrow', 'cw-start', 'cw-bind', 'cw-read', 'cw-finish', 'cw-restore', 'cw-abort', 'cw-seal']);
const untranslatedCellSteps = new Set(['start', 'bind', 'read', 'finish']);
/** Whether a request kind is exactly `<prefix>-<step>` for one of the cell observation prefixes and steps. */
function untranslatedCellKind(kind) {
    const parts = typeof kind === 'string' ? kind.split('-') : [];
    return parts.length === 2 && untranslatedCellPrefixes.has(parts[0]) && untranslatedCellSteps.has(parts[1]);
}

const sourceRepo = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repo = process.env.NTILDE_TERMINFO_ORIGINAL_REPO ?? sourceRepo;
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const writeJson = (path, value) => writeFileSync(path, JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
const quote = value => "'" + value.replaceAll("'", String.raw`'\''`) + "'";

/** Attempt parent-owned abort, then retain bounded primary/cleanup facts before independent process shutdown. */
async function retainPrivateClipboardAbort(state, error, phase, retain) {
    if (!state?.observer) return;
    await abortPrivateClipboardWithFacts(state, error, phase, createProcessBudget(4000));
    retain();
}
/** Keep at most eight privacy-safe journal-write failures; retention failure never grants completion or skips shutdown. */
function recordPrivateClipboardRetentionFailure(state, phase, error) {
    state.retentionFailures ??= [];
    if (state.retentionFailures.length < 8) state.retentionFailures.push({ phase, error: privateClipboardFailureFacts(error) });
    else state.retentionFailuresTruncated = true;
}

/** Retain privacy-safe private failure facts and return an Error preserving the original cause. */
function retainOwnedFailure(output, error, privateState) {
    let message = 'Owned private clipboard collection failed; see bounded failure facts.';
    if (!privateState) message = String(error?.message ?? error);
    const failure = { error: message, at: new Date().toISOString() };
    if (privateState) {
        failure.primary = privateClipboardFailureFacts(error);
        failure.privateClipboardFailures = privateState.failures ?? [];
        failure.failureFactsTruncated = privateState.failureFactsTruncated === true;
        failure.privateClipboardRetentionFailures = privateState.retentionFailures ?? [];
        failure.retentionFailuresTruncated = privateState.retentionFailuresTruncated === true;
    }
    writeJson(join(output, 'failure.json'), failure);
    return new Error(`${message} Evidence retained in ${output}`, { cause: error });
}

/** Await one shutdown attempt and retain rejection presence independently of its value. */
async function captureOwnedCleanup(action) {
    try { await action(); return { failed: false }; }
    catch (error) { return { failed: true, error }; }
}
/** Await one source-known evidence write; keep arbitrary rejection reasons inside an explicit outcome, never Promise-assimilate them. */
async function retainOwnedFailureStep(outcome, action) {
    try {
        const replacement = await action(outcome.error);
        if (replacement !== undefined) outcome.error = replacement;
    } catch (error_) {
        outcome.error = new AggregateError([outcome.error, error_], 'Owned failure evidence retention failed.');
    }
}
/** Chain every source-known failure journal serially so later writes see all earlier original/save outcomes before shutdown. */
async function retainOwnedFailureSequence(error, actions) {
    const outcome = { failed: true, error };
    await actions.reduce((pending, action) => pending.then(() => retainOwnedFailureStep(outcome, action)), Promise.resolve());
    return outcome;
}
/** Run after finally: aggregate the original run/cause and shutdown failure without truthiness tests. */
function throwOwnedRunFailures(run, cleanup) {
    if (run.failed && cleanup.failed) throw new AggregateError([run.error, cleanup.error], 'Owned run and process cleanup failed.');
    if (run.failed) throw run.error;
    if (cleanup.failed) throw cleanup.error;
}

/** Preserve only source-known initial App/geometry-budget refusal codes. */
function ownerFailure(error, privateFailure = false) {
    if (privateFailure) return { ok: false, error: 'Owned private clipboard owner refused; see bounded parent failure facts.' };
    return { ok: false, error: error.message,
        ...(['screenDetailsUnavailable', 'resizeGeometryUnavailable', 'savedGeometryUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable', 'inputGeometryUnavailable', 'imageGeometryUnavailable'].includes(error.code) ? { code: error.code } : {}) };
}

function requireInputCompletion(state) {
    if (!state?.observer?.restored && !state?.unavailable) throw new Error('Input suite is not sealed or initially unavailable.');
}
function requireStaticImageCompletion(state) {
    if (!state?.observer?.restored && !state?.unavailable) throw new Error('Static images must be restored and sealed.');
}

function requireQueryCompletion(state) {
    if (!state?.observer?.restored && !state?.unavailable) throw new Error('Query suite must be sealed and restored or initially unavailable.');
}
function assertExecutableUnchanged(executable, image) {
    const current = statSync(executable);
    if (image.ino !== current.ino || image.size !== current.size || image.mtimeMs !== current.mtimeMs)
        throw new Error('Owned executable changed during collection.');
}
function retainQueryReport(output, state, runId) {
    requireQueryCompletion(state);
    if (state.unavailable) return state.unavailable;
    const report = state.observer.report, file = 'query-observations.json'; writeJson(join(output, file), report);
    const counts = {};
    for (const item of report.observations) { const key = item.contractMatched ? 'matched' : 'mismatched'; counts[key] = (counts[key] ?? 0) + 1; }
    return { runId, suiteId: querySuiteId, file, sha256: hash(readFileSync(join(output, file))), counts, provisionalUntil: 'cell-receipt.json' };
}

function retainFailedMetadataAttempt(output, request, error, observer, file = 'metadata-failed-observation.json') {
    if (observer.lastAttempt && !initialMetadataRefusal(request, error, observer))
        writeJson(join(output, file), { ...observer.lastAttempt, error: observer.failure(error) });
}

function validateInvocation(args) {
    if (args.length !== 4 || args[0] !== '--executable' || args[2] !== '--app-version'
        || !/^\d[\dA-Za-z.+_-]*$/.test(args[3])) throw new Error('Use --help for the required executable and version arguments.');
    if (process.platform !== 'darwin') throw new Error('The owned adapter currently supports macOS only.');
    if (Number(process.versions.node.split('.')[0]) < 24) throw new Error('Node 24+ is required.');
    return resolveOwnedExecutable(args[1]);
}

function readPreparedCollector(tool) {
    const pkg = join(tool, 'node_modules', 'terminfo.dev');
    const original = join(pkg, 'dist', 'terminfo.bundle.mjs');
    if (!existsSync(original)) throw new Error('Prepare the pinned collector first; see docs/qa/QA_TERMINFO.md.');
    if (JSON.parse(readFileSync(join(pkg, 'package.json'))).version !== '4.0.0') throw new Error('Expected terminfo.dev 4.0.0.');
    const source = readFileSync(original, 'utf8');
    const receiptBytes = readFileSync(join(pkg, 'dist', 'terminfo.bundle.receipt.json'));
    validateCollectorReceipt(source, JSON.parse(receiptBytes));
    const lockBytes = readCollectorLockfile(tool);
    const manifestBytes = readFileSync(join(tool, 'package.json'));
    validateCollectorInputs(manifestBytes, lockBytes);
    return { source, receiptBytes, lockBytes, manifestBytes };
}

function prepareCollector(tool) {
    const { source, receiptBytes, lockBytes } = readPreparedCollector(tool);
    const ownerModule = join(sourceRepo, 'scripts', 'terminfo', 'ownership.mjs');
    const privateClipboardModulePath = join(sourceRepo, 'scripts', 'terminfo', 'private-clipboard-worker.mjs');
    const adapted = adaptCollector(source, ownerModule, { privateClipboardModulePath });
    const adapterBundleSha256 = hash(adapted);
    // Keep external package resolution beside the original bundle. A derived
    // file is content-addressed; the downloaded original is never overwritten.
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-${adapterBundleSha256}.mjs`);
    if (!existsSync(bundle)) writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== adapterBundleSha256) throw new Error('Derived adapter bundle digest mismatch.');

    return { ownerModule, bundle, adapterBundleSha256, receiptBytes, lockBytes, originalBundle: source };
}

/** Derive and lock the ASCII/SGR grading extension beside the owned adapter. */
function prepareGradingCollector(tool, ownedBundle, output) {
    const adapted = adaptGradingCollector(readFileSync(ownedBundle));
    const gradingBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-grading-${gradingBundleSha256}.mjs`);
    if (!existsSync(bundle)) writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== gradingBundleSha256) throw new Error('Derived grading bundle digest mismatch.');
    writeFileSync(join(output, 'collector-grading.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, gradingBundleSha256 };
}

/** Derive and lock the layered SGR-colors grading extension over the first. */
function prepareSgrColorsCollector(tool, gradingBundle, output) {
    const adapted = adaptSgrColorsCollector(readFileSync(gradingBundle));
    const sgrColorsBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-sgrcolors-${sgrColorsBundleSha256}.mjs`);
    if (!existsSync(bundle)) writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== sgrColorsBundleSha256) throw new Error('Derived SGR-colors bundle digest mismatch.');
    writeFileSync(join(output, 'collector-sgcolors.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, sgrColorsBundleSha256 };
}

/** Derive and lock the layered SGR-RGB grading extension over the SGR-colors one. */
function prepareSgrRgbCollector(tool, sgrColorsBundle, output) {
    const adapted = adaptSgrRgbCollector(readFileSync(sgrColorsBundle));
    const sgrRgbBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-sgrrgb-${sgrRgbBundleSha256}.mjs`);
    if (!existsSync(bundle)) writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== sgrRgbBundleSha256) throw new Error('Derived SGR-RGB bundle digest mismatch.');
    writeFileSync(join(output, 'collector-sgrgb.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, sgrRgbBundleSha256 };
}

/** Derive and lock the layered SGR-attributes grading extension over the SGR-RGB one. */
function prepareSgrAttrsCollector(tool, sgrRgbBundle, output) {
    const adapted = adaptSgrAttrsCollector(readFileSync(sgrRgbBundle));
    const sgrAttrsBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-sgrattr-${sgrAttrsBundleSha256}.mjs`);
    if (!existsSync(bundle)) writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== sgrAttrsBundleSha256) throw new Error('Derived SGR-attributes bundle digest mismatch.');
    writeFileSync(join(output, 'collector-sgattr.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, sgrAttrsBundleSha256 };
}

/** Derive and lock the layered SGR-palette grading extension over the SGR-attributes one. */
function prepareSgrPaletteCollector(tool, sgrAttrsBundle, output) {
    const adapted = adaptSgrPaletteCollector(readFileSync(sgrAttrsBundle));
    const sgrPaletteBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-sgrpal-${sgrPaletteBundleSha256}.mjs`);
    if (!existsSync(bundle)) writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== sgrPaletteBundleSha256) throw new Error('Derived SGR-palette bundle digest mismatch.');
    writeFileSync(join(output, 'collector-sgpal.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, sgrPaletteBundleSha256 };
}

/** Derive and lock the layered SGR-underline-color grading extension over the SGR-underline one. */
function prepareSgrUnderlineColorCollector(tool, sgrUnderlineBundle, output) {
    const adapted = adaptSgrUnderlineColorCollector(readFileSync(sgrUnderlineBundle));
    const sgrUnderlineColorBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-sgruc-${sgrUnderlineColorBundleSha256}.mjs`);
    if (!existsSync(bundle)) writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== sgrUnderlineColorBundleSha256) throw new Error('Derived SGR-underline-color bundle digest mismatch.');
    writeFileSync(join(output, 'collector-sguc.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, sgrUnderlineColorBundleSha256 };
}

/** Derive and lock the layered SGR-underline grading extension over the SGR-truecolor one. */
function prepareSgrUnderlineCollector(tool, sgrTruecolorBundle, output) {
    const adapted = adaptSgrUnderlineCollector(readFileSync(sgrTruecolorBundle));
    const sgrUnderlineBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-sgrus-${sgrUnderlineBundleSha256}.mjs`);
    if (!existsSync(bundle)) writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== sgrUnderlineBundleSha256) throw new Error('Derived SGR-underline bundle digest mismatch.');
    writeFileSync(join(output, 'collector-sgus.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, sgrUnderlineBundleSha256 };
}

/** Derive and lock the layered SGR-truecolor grading extension over the SGR-palette one. */
function prepareSgrTruecolorCollector(tool, sgrPaletteBundle, output) {
    const adapted = adaptSgrTruecolorCollector(readFileSync(sgrPaletteBundle));
    const sgrTruecolorBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-sgrtc-${sgrTruecolorBundleSha256}.mjs`);
    if (!existsSync(bundle)) writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== sgrTruecolorBundleSha256) throw new Error('Derived SGR-truecolor bundle digest mismatch.');
    writeFileSync(join(output, 'collector-sgtc.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, sgrTruecolorBundleSha256 };
}

function collectionSummary(output, appVersion) {
    const rawPath = join(output, 'raw.json');
    const raw = JSON.parse(readFileSync(rawPath));
    if (raw.schemaVersion !== 2 || raw.target?.id !== 'ntilde' || raw.target?.version !== appVersion
        || raw.collectorAdapter?.id !== adapterId || raw.probeHash !== upstreamReceipt.probeHash
        || raw.sourceRevision !== upstreamReceipt.collectorRevision) throw new Error('Unexpected collected identity or suite.');
    const counts = {};
    const reasons = {};
    for (const observation of raw.observations) {
        counts[observation.outcome] = (counts[observation.outcome] ?? 0) + 1;
        if (observation.reason) reasons[observation.reason] = (reasons[observation.reason] ?? 0) + 1;
    }
    return { runId: raw.runId, suiteId: raw.suiteId, suiteComplete: raw.suiteComplete,
        ungradedDiagnostics: raw.ungradedDiagnostics, measuredAt: raw.measuredAt, target: raw.target,
        adapter: raw.collectorAdapter, observations: raw.observations.length, counts, reasons, rawSha256: hash(readFileSync(rawPath)) };
}

/** Archive exact executed adapter and source/input bytes before publishing their digests. */
function retainCollectorEvidence(output, bundle, expectedDigest, ownerModule, workerScript) {
    const bundleBytes = readFileSync(bundle);
    if (hash(bundleBytes) !== expectedDigest) throw new Error('Derived adapter changed before evidence retention.');
    writeFileSync(join(output, 'collector-adapter.mjs'), bundleBytes, { flag: 'wx', mode: 0o600 });
    const sourceDirectory = join(output, 'sources');
    mkdirSync(sourceDirectory, { mode: 0o700 });
    const sources = Object.fromEntries(Object.entries(ownedSourceFiles).map(([key, file]) => [key,
        ownedSourcePath(sourceRepo, file)]));
    if (sources.ownershipSourceSha256 !== ownerModule || sources.workerSourceSha256 !== workerScript)
        throw new Error('Executed worker or ownership source differs from the retained inventory.');
    const digests = {};
    for (const [field, path] of Object.entries(sources)) {
        const bytes = readFileSync(path);
        writeFileSync(join(sourceDirectory, ownedSourceFiles[field]), bytes, { flag: 'wx', mode: 0o600 });
        digests[field] = hash(bytes);
    }
    return digests;
}

/** Retain a restored suite report as provisional evidence until the shared cleanup receipt exists. */
function retainCellReport(output, observer, runId, buildReport = buildCellReport, file = 'cell-observations.json') {
    if (!observer?.restored) throw new Error('Owned cell observation or restoration did not complete.');
    const report = buildReport(observer.frames, runId);
    writeJson(join(output, file), report);
    const counts = {};
    for (const item of report.observations) counts[item.result] = (counts[item.result] ?? 0) + 1;
    return { runId, suiteId: report.suiteId, file, sha256: hash(readFileSync(join(output, file))), counts,
        provisionalUntil: 'cell-receipt.json' };
}

/** Re-derive the SGR-colors report parent-side with the layered pinned replay. */
async function retainSgrColorsEvidence(output, runId, sgrColorsBundle, upstream) {
    const runBytes = readFileSync(join(output, 'sgcolors-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'sgcolors-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-sgcolors.mjs'));
    const adapter = { id: sgrColorsAdapterId, derivedFromAdapterId: 'ntilde-owned-ascii-sgr-grading-v1',
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(sgrColorsBundle).href);
    const report = buildSgrColorsGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'sgcolors-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: sgrColorsSuiteId, file: 'sgcolors-observations.json',
        sha256: hash(readFileSync(join(output, 'sgcolors-observations.json'))),
        runFile: 'sgcolors-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: sgrColorsContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

/** Re-derive the SGR-RGB report parent-side with the layered pinned replay. */
async function retainSgrRgbEvidence(output, runId, sgrRgbBundle, upstream) {
    const runBytes = readFileSync(join(output, 'sgrgb-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'sgrgb-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-sgrgb.mjs'));
    const adapter = { id: sgrRgbAdapterId, derivedFromAdapterId: sgrColorsAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(sgrRgbBundle).href);
    const report = buildSgrRgbGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'sgrgb-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: sgrRgbSuiteId, file: 'sgrgb-observations.json',
        sha256: hash(readFileSync(join(output, 'sgrgb-observations.json'))),
        runFile: 'sgrgb-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: sgrRgbContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

/** Re-derive the SGR-attributes report parent-side with the layered pinned replay. */
async function retainSgrAttrsEvidence(output, runId, sgrAttrsBundle, upstream) {
    const runBytes = readFileSync(join(output, 'sgattr-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'sgattr-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-sgattr.mjs'));
    const adapter = { id: sgrAttrsAdapterId, derivedFromAdapterId: sgrRgbAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(sgrAttrsBundle).href);
    const report = buildSgrAttrsGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'sgattr-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: sgrAttrsSuiteId, file: 'sgattr-observations.json',
        sha256: hash(readFileSync(join(output, 'sgattr-observations.json'))),
        runFile: 'sgattr-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: sgrAttrsContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

/** Re-derive the SGR-palette report parent-side with the layered pinned replay. */
async function retainSgrPaletteEvidence(output, runId, sgrPaletteBundle, upstream) {
    const runBytes = readFileSync(join(output, 'sgpal-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'sgpal-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-sgpal.mjs'));
    const adapter = { id: sgrPaletteAdapterId, derivedFromAdapterId: sgrAttrsAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(sgrPaletteBundle).href);
    const report = buildSgrPaletteGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'sgpal-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: sgrPaletteSuiteId, file: 'sgpal-observations.json',
        sha256: hash(readFileSync(join(output, 'sgpal-observations.json'))),
        runFile: 'sgpal-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: sgrPaletteContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

/** Re-derive the SGR-truecolor report parent-side with the layered pinned replay. */
async function retainSgrTruecolorEvidence(output, runId, sgrTruecolorBundle, upstream) {
    const runBytes = readFileSync(join(output, 'sgtc-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'sgtc-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-sgtc.mjs'));
    const adapter = { id: sgrTruecolorAdapterId, derivedFromAdapterId: sgrPaletteAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(sgrTruecolorBundle).href);
    const report = buildSgrTruecolorGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'sgtc-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: sgrTruecolorSuiteId, file: 'sgtc-observations.json',
        sha256: hash(readFileSync(join(output, 'sgtc-observations.json'))),
        runFile: 'sgtc-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: sgrTruecolorContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered SGR selective-reset grading extension over the SGR-underline-color one. */
function prepareSgrResetCollector(tool, sgrUnderlineColorBundle, output) {
    const adapted = adaptSgrResetCollector(readFileSync(sgrUnderlineColorBundle));
    const sgrResetBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-sgsr-${sgrResetBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== sgrResetBundleSha256) throw new Error('Derived SGR selective-reset bundle digest mismatch.');
    writeFileSync(join(output, 'collector-sgsr.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, sgrResetBundleSha256 };
}

/** Derive and lock the layered SGR closure grading extension over the SGR selective-reset one. */
function prepareSgrClosureCollector(tool, sgrResetBundle, output) {
    const adapted = adaptSgrClosureCollector(readFileSync(sgrResetBundle));
    const sgrClosureBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-sgcl-${sgrClosureBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== sgrClosureBundleSha256) throw new Error('Derived SGR closure bundle digest mismatch.');
    writeFileSync(join(output, 'collector-sgcl.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, sgrClosureBundleSha256 };
}

/** Re-derive the SGR closure report parent-side with the layered pinned replay. */
async function retainSgrClosureEvidence(output, runId, sgrClosureBundle, upstream) {
    const runBytes = readFileSync(join(output, 'sgcl-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'sgcl-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-sgcl.mjs'));
    const adapter = { id: sgrClosureAdapterId, derivedFromAdapterId: sgrResetAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(sgrClosureBundle).href);
    const report = buildSgrClosureGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'sgcl-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: sgrClosureSuiteId, file: 'sgcl-observations.json',
        sha256: hash(readFileSync(join(output, 'sgcl-observations.json'))),
        runFile: 'sgcl-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: sgrClosureContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

/** Re-derive the SGR selective-reset report parent-side with the layered pinned replay. */
async function retainSgrResetEvidence(output, runId, sgrResetBundle, upstream) {
    const runBytes = readFileSync(join(output, 'sgsr-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'sgsr-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-sgsr.mjs'));
    const adapter = { id: sgrResetAdapterId, derivedFromAdapterId: sgrUnderlineColorAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(sgrResetBundle).href);
    const report = buildSgrResetGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'sgsr-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: sgrResetSuiteId, file: 'sgsr-observations.json',
        sha256: hash(readFileSync(join(output, 'sgsr-observations.json'))),
        runFile: 'sgsr-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: sgrResetContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered erase-in-line grading extension over the SGR closure one. */
function prepareEraseLineCollector(tool, sgrClosureBundle, output) {
    const adapted = adaptEraseLineCollector(readFileSync(sgrClosureBundle));
    const eraseLineBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-erln-${eraseLineBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== eraseLineBundleSha256) throw new Error('Derived erase-in-line bundle digest mismatch.');
    writeFileSync(join(output, 'collector-erln.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, eraseLineBundleSha256 };
}

/** Re-derive the erase-in-line report parent-side with the layered pinned replay. */
async function retainEraseLineEvidence(output, runId, eraseLineBundle, upstream) {
    const runBytes = readFileSync(join(output, 'erln-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'erln-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-erln.mjs'));
    const adapter = { id: eraseLineAdapterId, derivedFromAdapterId: sgrClosureAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(eraseLineBundle).href);
    const report = buildEraseLineGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'erln-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: eraseLineSuiteId, file: 'erln-observations.json',
        sha256: hash(readFileSync(join(output, 'erln-observations.json'))),
        runFile: 'erln-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: eraseLineContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered character-editing grading extension over the erase-in-line one. */
function prepareCharEditCollector(tool, eraseLineBundle, output) {
    const adapted = adaptCharEditCollector(readFileSync(eraseLineBundle));
    const charEditBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-ched-${charEditBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== charEditBundleSha256) throw new Error('Derived character-editing bundle digest mismatch.');
    writeFileSync(join(output, 'collector-ched.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, charEditBundleSha256 };
}

/** Re-derive the character-editing report parent-side with the layered pinned replay. */
async function retainCharEditEvidence(output, runId, charEditBundle, upstream) {
    const runBytes = readFileSync(join(output, 'ched-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'ched-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-ched.mjs'));
    const adapter = { id: charEditAdapterId, derivedFromAdapterId: eraseLineAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(charEditBundle).href);
    const report = buildCharEditGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'ched-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: charEditSuiteId, file: 'ched-observations.json',
        sha256: hash(readFileSync(join(output, 'ched-observations.json'))),
        runFile: 'ched-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: charEditContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered erase-in-display grading extension over the character-editing one. */
function prepareEraseDisplayCollector(tool, charEditBundle, output) {
    const adapted = adaptEraseDisplayCollector(readFileSync(charEditBundle));
    const eraseDisplayBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-erdi-${eraseDisplayBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== eraseDisplayBundleSha256) throw new Error('Derived erase-in-display bundle digest mismatch.');
    writeFileSync(join(output, 'collector-erdi.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, eraseDisplayBundleSha256 };
}

/** Re-derive the erase-in-display report parent-side with the layered pinned replay. */
async function retainEraseDisplayEvidence(output, runId, eraseDisplayBundle, upstream) {
    const runBytes = readFileSync(join(output, 'erdi-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'erdi-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-erdi.mjs'));
    const adapter = { id: eraseDisplayAdapterId, derivedFromAdapterId: charEditAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(eraseDisplayBundle).href);
    const report = buildEraseDisplayGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'erdi-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: eraseDisplaySuiteId, file: 'erdi-observations.json',
        sha256: hash(readFileSync(join(output, 'erdi-observations.json'))),
        runFile: 'erdi-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: eraseDisplayContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered line-editing grading extension over the character-editing one. */
function prepareLineEditCollector(tool, eraseDisplayBundle, output) {
    const adapted = adaptLineEditCollector(readFileSync(eraseDisplayBundle));
    const lineEditBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-ledt-${lineEditBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== lineEditBundleSha256) throw new Error('Derived line-editing bundle digest mismatch.');
    writeFileSync(join(output, 'collector-ledt.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, lineEditBundleSha256 };
}

/** Re-derive the line-editing report parent-side with the layered pinned replay. */
async function retainLineEditEvidence(output, runId, lineEditBundle, upstream) {
    const runBytes = readFileSync(join(output, 'ledt-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'ledt-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-ledt.mjs'));
    const adapter = { id: lineEditAdapterId, derivedFromAdapterId: eraseDisplayAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(lineEditBundle).href);
    const report = buildLineEditGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'ledt-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: lineEditSuiteId, file: 'ledt-observations.json',
        sha256: hash(readFileSync(join(output, 'ledt-observations.json'))),
        runFile: 'ledt-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: lineEditContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered charsets grading extension over the line-editing one. */
function prepareCharsetsCollector(tool, lineEditBundle, output) {
    const adapted = adaptCharsetsCollector(readFileSync(lineEditBundle));
    const charsetsBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-chrs-${charsetsBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== charsetsBundleSha256) throw new Error('Derived charsets bundle digest mismatch.');
    writeFileSync(join(output, 'collector-chrs.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, charsetsBundleSha256 };
}

/** Re-derive the charsets report parent-side with the layered pinned replay. */
async function retainCharsetsEvidence(output, runId, charsetsBundle, upstream) {
    const runBytes = readFileSync(join(output, 'chrs-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'chrs-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-chrs.mjs'));
    const adapter = { id: charsetsAdapterId, derivedFromAdapterId: lineEditAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(charsetsBundle).href);
    const report = buildCharsetsGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'chrs-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: charsetsSuiteId, file: 'chrs-observations.json',
        sha256: hash(readFileSync(join(output, 'chrs-observations.json'))),
        runFile: 'chrs-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: charsetsContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered scrolling/history grading extension over the charsets one. */
function prepareScrollbackCollector(tool, charsetsBundle, output) {
    const adapted = adaptScrollbackCollector(readFileSync(charsetsBundle));
    const scrollbackBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-scrl-${scrollbackBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== scrollbackBundleSha256) throw new Error('Derived scrolling/history bundle digest mismatch.');
    writeFileSync(join(output, 'collector-scrl.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, scrollbackBundleSha256 };
}

/** Re-derive the scrolling/history report parent-side with the layered pinned replay. */
async function retainScrollbackEvidence(output, runId, scrollbackBundle, upstream) {
    const runBytes = readFileSync(join(output, 'scrl-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'scrl-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-scrl.mjs'));
    const adapter = { id: scrollbackAdapterId, derivedFromAdapterId: charsetsAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(scrollbackBundle).href);
    const report = buildScrollbackGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'scrl-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: scrollbackSuiteId, file: 'scrl-observations.json',
        sha256: hash(readFileSync(join(output, 'scrl-observations.json'))),
        runFile: 'scrl-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: scrollbackContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered saved-state/reset grading extension over the scrolling/history one. */
function prepareSavedResetCollector(tool, scrollbackBundle, output) {
    const adapted = adaptSavedResetCollector(readFileSync(scrollbackBundle));
    const savedResetBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-svrs-${savedResetBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== savedResetBundleSha256) throw new Error('Derived saved-state/reset bundle digest mismatch.');
    writeFileSync(join(output, 'collector-svrs.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, savedResetBundleSha256 };
}

/** Re-derive the saved-state/reset report parent-side with the layered pinned replay. */
async function retainSavedResetEvidence(output, runId, savedResetBundle, upstream) {
    const runBytes = readFileSync(join(output, 'svrs-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'svrs-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-svrs.mjs'));
    const adapter = { id: savedResetAdapterId, derivedFromAdapterId: scrollbackAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(savedResetBundle).href);
    const report = buildSavedResetGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'svrs-observations.json'), report);
    const { counts, effectCounts } = savedResetCounts(report);
    return { runId, suiteId: savedResetSuiteId, file: 'svrs-observations.json',
        sha256: hash(readFileSync(join(output, 'svrs-observations.json'))),
        runFile: 'svrs-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: savedResetContractsVersion, counts, effectCounts, provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered erase attributes / erase margins grading extension over the saved-state/reset one. */
function prepareEraseAttrsRegionCollector(tool, savedResetBundle, output) {
    const adapted = adaptEraseAttrsRegionCollector(readFileSync(savedResetBundle));
    const eraseAttrsRegionBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-erar-${eraseAttrsRegionBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== eraseAttrsRegionBundleSha256) throw new Error('Derived erase-attributes bundle digest mismatch.');
    writeFileSync(join(output, 'collector-erar.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, eraseAttrsRegionBundleSha256 };
}

/** Re-derive the erase attributes / erase margins report parent-side with the layered pinned replay. */
async function retainEraseAttrsRegionEvidence(output, runId, eraseAttrsRegionBundle, upstream) {
    const runBytes = readFileSync(join(output, 'erar-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'erar-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-erar.mjs'));
    const adapter = { id: eraseAttrsRegionAdapterId, derivedFromAdapterId: savedResetAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(eraseAttrsRegionBundle).href);
    const report = buildEraseAttrsRegionGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'erar-observations.json'), report);
    const { counts } = eraseAttrsRegionCounts(report);
    return { runId, suiteId: eraseAttrsRegionSuiteId, file: 'erar-observations.json',
        sha256: hash(readFileSync(join(output, 'erar-observations.json'))),
        runFile: 'erar-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: eraseAttrsRegionContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered attribute-rectangle grading extension over the erase attributes / erase margins one. */
function prepareAttrRectCollector(tool, eraseAttrsRegionBundle, output) {
    const adapted = adaptAttrRectCollector(readFileSync(eraseAttrsRegionBundle));
    const attrRectBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-atrc-${attrRectBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== attrRectBundleSha256) throw new Error('Derived attribute-rectangle bundle digest mismatch.');
    writeFileSync(join(output, 'collector-atrc.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, attrRectBundleSha256 };
}

/** Re-derive the attribute-rectangle report parent-side with the layered pinned replay. */
async function retainAttrRectEvidence(output, runId, attrRectBundle, upstream) {
    const runBytes = readFileSync(join(output, 'atrc-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'atrc-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-atrc.mjs'));
    const adapter = { id: attrRectAdapterId, derivedFromAdapterId: eraseAttrsRegionAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(attrRectBundle).href);
    const report = buildAttrRectGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'atrc-observations.json'), report);
    const { counts, effectCounts } = attrRectCounts(report);
    return { runId, suiteId: attrRectSuiteId, file: 'atrc-observations.json',
        sha256: hash(readFileSync(join(output, 'atrc-observations.json'))),
        runFile: 'atrc-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: attrRectContractsVersion, counts, effectCounts, provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered cursor grading extension over the attribute-rectangle one. */
function prepareCursorQualCollector(tool, attrRectBundle, output) {
    const adapted = adaptCursorQualCollector(readFileSync(attrRectBundle));
    const cursorQualBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-curq-${cursorQualBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== cursorQualBundleSha256) throw new Error('Derived cursor bundle digest mismatch.');
    writeFileSync(join(output, 'collector-curq.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, cursorQualBundleSha256 };
}

/** Re-derive the cursor report parent-side (ports, retained CPR exchanges and every retained PNG), then replay the LIVE pinned callbacks. */
async function retainCursorQualEvidence(output, runId, cursorQualBundle, upstream) {
    const runBytes = readFileSync(join(output, 'curq-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'curq-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-curq.mjs'));
    const adapter = { id: cursorQualAdapterId, derivedFromAdapterId: attrRectAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const report = buildCursorQualGradingReport(frames, run, runId, adapter, { output });
    await verifyCursorQualLiveReplay(frames, run, runId, adapter, await import(pathToFileURL(cursorQualBundle).href));
    writeJson(join(output, 'curq-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: cursorQualSuiteId, file: 'curq-observations.json',
        sha256: hash(readFileSync(join(output, 'curq-observations.json'))),
        runFile: 'curq-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: cursorQualContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered erase-character/saved-lines grading extension over the cursor-qualification one. */
function prepareEraseCharsHistoryCollector(tool, cursorQualBundle, output) {
    const adapted = adaptEraseCharsHistoryCollector(readFileSync(cursorQualBundle));
    const eraseCharsHistoryBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-echh-${eraseCharsHistoryBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== eraseCharsHistoryBundleSha256) throw new Error('Derived erase-character/saved-lines bundle digest mismatch.');
    writeFileSync(join(output, 'collector-echh.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, eraseCharsHistoryBundleSha256 };
}

/** Re-derive the erase-character/saved-lines report parent-side with the layered pinned replay. */
async function retainEraseCharsHistoryEvidence(output, runId, eraseCharsHistoryBundle, upstream) {
    const runBytes = readFileSync(join(output, 'echh-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'echh-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-echh.mjs'));
    const adapter = { id: eraseCharsHistoryAdapterId, derivedFromAdapterId: cursorQualAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(eraseCharsHistoryBundle).href);
    const report = buildEraseCharsHistoryGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'echh-observations.json'), report);
    return { runId, suiteId: eraseCharsHistorySuiteId, file: 'echh-observations.json',
        sha256: hash(readFileSync(join(output, 'echh-observations.json'))),
        runFile: 'echh-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: eraseCharsHistoryContractsVersion, counts: eraseCharsHistoryCounts(report), provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered selective-erasure grading extension over the erase-character/saved-lines one. */
function prepareSelectiveEraseCollector(tool, eraseCharsHistoryBundle, output) {
    const adapted = adaptSelectiveEraseCollector(readFileSync(eraseCharsHistoryBundle));
    const selectiveEraseBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-sels-${selectiveEraseBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== selectiveEraseBundleSha256) throw new Error('Derived selective-erasure bundle digest mismatch.');
    writeFileSync(join(output, 'collector-sels.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, selectiveEraseBundleSha256 };
}

/** Re-derive the selective-erasure report parent-side with the layered pinned replay. */
async function retainSelectiveEraseEvidence(output, runId, selectiveEraseBundle, upstream) {
    const runBytes = readFileSync(join(output, 'sels-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'sels-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-sels.mjs'));
    const adapter = { id: selectiveEraseAdapterId, derivedFromAdapterId: eraseCharsHistoryAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(selectiveEraseBundle).href);
    const report = buildSelectiveEraseGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'sels-observations.json'), report);
    return { runId, suiteId: selectiveEraseSuiteId, file: 'sels-observations.json',
        sha256: hash(readFileSync(join(output, 'sels-observations.json'))),
        runFile: 'sels-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: selectiveEraseContractsVersion, counts: selectiveEraseCounts(report), provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered rectangle-editing grading extension over the selective-erasure one. */
function prepareRectEditCollector(tool, selectiveEraseBundle, output) {
    const adapted = adaptRectEditCollector(readFileSync(selectiveEraseBundle));
    const rectEditBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-rect-${rectEditBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== rectEditBundleSha256) throw new Error('Derived rectangle-editing bundle digest mismatch.');
    writeFileSync(join(output, 'collector-rect.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, rectEditBundleSha256 };
}

/** Re-derive the rectangle-editing report parent-side with the layered pinned replay. */
async function retainRectEditEvidence(output, runId, rectEditBundle, upstream) {
    const runBytes = readFileSync(join(output, 'rect-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'rect-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-rect.mjs'));
    const adapter = { id: rectEditAdapterId, derivedFromAdapterId: selectiveEraseAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(rectEditBundle).href);
    const report = buildRectEditGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'rect-observations.json'), report);
    return { runId, suiteId: rectEditSuiteId, file: 'rect-observations.json',
        sha256: hash(readFileSync(join(output, 'rect-observations.json'))),
        runFile: 'rect-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: rectEditContractsVersion, counts: rectEditCounts(report), provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered column-editing grading extension over the rectangle-editing one. */
function prepareColEditCollector(tool, rectEditBundle, output) {
    const adapted = adaptColEditCollector(readFileSync(rectEditBundle));
    const colEditBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-cole-${colEditBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== colEditBundleSha256) throw new Error('Derived column-editing bundle digest mismatch.');
    writeFileSync(join(output, 'collector-cole.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, colEditBundleSha256 };
}

/** Re-derive the column-editing report parent-side with the layered pinned replay. */
async function retainColEditEvidence(output, runId, colEditBundle, upstream) {
    const runBytes = readFileSync(join(output, 'cole-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'cole-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-cole.mjs'));
    const adapter = { id: colEditAdapterId, derivedFromAdapterId: rectEditAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(colEditBundle).href);
    const report = buildColEditGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'cole-observations.json'), report);
    return { runId, suiteId: colEditSuiteId, file: 'cole-observations.json',
        sha256: hash(readFileSync(join(output, 'cole-observations.json'))),
        runFile: 'cole-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: colEditContractsVersion, counts: colEditCounts(report), provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered rectangle-checksum grading extension over the column-editing one. */
function prepareRectChecksumCollector(tool, colEditBundle, output) {
    const adapted = adaptRectChecksumCollector(readFileSync(colEditBundle));
    const rectChecksumBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-csum-${rectChecksumBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== rectChecksumBundleSha256) throw new Error('Derived rectangle-checksum bundle digest mismatch.');
    writeFileSync(join(output, 'collector-csum.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, rectChecksumBundleSha256 };
}

/** Re-derive the rectangle-checksum report parent-side with the layered pinned replay. */
async function retainRectChecksumEvidence(output, runId, rectChecksumBundle, upstream) {
    const runBytes = readFileSync(join(output, 'csum-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'csum-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-csum.mjs'));
    const adapter = { id: rectChecksumAdapterId, derivedFromAdapterId: colEditAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(rectChecksumBundle).href);
    const report = buildRectChecksumGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'csum-observations.json'), report);
    return { runId, suiteId: rectChecksumSuiteId, file: 'csum-observations.json',
        sha256: hash(readFileSync(join(output, 'csum-observations.json'))),
        runFile: 'csum-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: rectChecksumContractsVersion, counts: rectChecksumCounts(report), provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered left/right-margin grading extension over the rectangle-checksum one. */
function prepareLrMarginsCollector(tool, rectChecksumBundle, output) {
    const adapted = adaptLrMarginsCollector(readFileSync(rectChecksumBundle));
    const lrMarginsBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-lrmm-${lrMarginsBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== lrMarginsBundleSha256) throw new Error('Derived left/right-margin bundle digest mismatch.');
    writeFileSync(join(output, 'collector-lrmm.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, lrMarginsBundleSha256 };
}

/** Re-derive the left/right-margin report parent-side with the layered pinned replay. */
async function retainLrMarginsEvidence(output, runId, lrMarginsBundle, upstream) {
    const runBytes = readFileSync(join(output, 'lrmm-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'lrmm-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-lrmm.mjs'));
    const adapter = { id: lrMarginsAdapterId, derivedFromAdapterId: rectChecksumAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(lrMarginsBundle).href);
    const report = buildLrMarginsGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'lrmm-observations.json'), report);
    return { runId, suiteId: lrMarginsSuiteId, file: 'lrmm-observations.json',
        sha256: hash(readFileSync(join(output, 'lrmm-observations.json'))),
        runFile: 'lrmm-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: lrMarginsContractsVersion, counts: lrMarginsCounts(report), provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered routed-input grading extension over the left/right-margin one. */
function prepareRoutedInputCollector(tool, lrMarginsBundle, output) {
    const adapted = adaptRoutedInputCollector(readFileSync(lrMarginsBundle));
    const routedInputBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-rin-${routedInputBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== routedInputBundleSha256) throw new Error('Derived routed-input bundle digest mismatch.');
    writeFileSync(join(output, 'collector-rin.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, routedInputBundleSha256 };
}

/** Derive and lock the layered OSC 133 grading extension over the left/right-margin one. */
function prepareOsc133Collector(tool, lrMarginsBundle, output) {
    const adapted = adaptOsc133Collector(readFileSync(lrMarginsBundle));
    const osc133BundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-shi-${osc133BundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== osc133BundleSha256) throw new Error('Derived OSC 133 bundle digest mismatch.');
    writeFileSync(join(output, 'collector-shi.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, osc133BundleSha256 };
}

/** Re-derive the OSC 133 report parent-side from the paired journal and the layered pinned replay. */
async function retainOsc133Evidence(output, runId, osc133Bundle, upstream, unavailable) {
    const adapterBytes = readFileSync(join(output, 'collector-shi.mjs'));
    if (unavailable) {
        const markerBytes = readFileSync(join(output, 'shi-unavailable.json')), marker = JSON.parse(markerBytes);
        return { runId, suiteId: osc133SuiteId, file: 'shi-unavailable.json', sha256: hash(markerBytes), status: 'unavailable', reason: marker.reason, counts: {},
            adapterBundleSha256: hash(adapterBytes), contractsVersion: osc133ContractsVersion, provisionalUntil: 'cell-receipt.json' };
    }
    const runBytes = readFileSync(join(output, 'shi-run.json'));
    const frames = readFileSync(join(output, 'shi-snapshots.jsonl'), 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line));
    const adapter = { id: osc133AdapterId, derivedFromAdapterId: lrMarginsAdapterId, bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(osc133Bundle).href);
    const report = buildOsc133GradingReport(frames, JSON.parse(runBytes), runId, adapter, { replayModule });
    writeJson(join(output, 'shi-observations.json'), report);
    return { runId, suiteId: osc133SuiteId, file: 'shi-observations.json', sha256: hash(readFileSync(join(output, 'shi-observations.json'))),
        runFile: 'shi-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: osc133ContractsVersion, counts: osc133Counts(report), provisionalUntil: 'cell-receipt.json' };
}

/** Re-derive the routed-input report parent-side from the owned input journal and the layered pinned replay. */
async function retainRoutedInputEvidence(output, runId, routedInputBundle, upstream, inputBinding, seal) {
    if (inputBinding?.status === 'unavailable') {
        // The owned input suite could not run on this grid; the layer records the same typed unavailability and nothing else.
        const markerBytes = readFileSync(join(output, 'rin-unavailable.json')), marker = JSON.parse(markerBytes);
        if (marker.reason !== inputBinding.reason) throw new Error('Routed-input unavailable marker differs from the input suite.');
        return { runId, suiteId: routedInputGradedSuiteId, file: 'rin-unavailable.json', sha256: hash(markerBytes), status: 'unavailable', reason: marker.reason, counts: {},
            adapterBundleSha256: hash(readFileSync(join(output, 'collector-rin.mjs'))), contractsVersion: routedInputContractsVersion, provisionalUntil: 'cell-receipt.json' };
    }
    if (inputBinding?.file !== 'input-observations.json') throw new Error('Routed-input evidence requires the owned input suite.');
    const runBytes = readFileSync(join(output, 'rin-run.json'));
    const run = JSON.parse(runBytes);
    const lines = file => readFileSync(join(output, file), 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-rin.mjs'));
    const adapter = { id: routedInputAdapterId, derivedFromAdapterId: lrMarginsAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(routedInputBundle).href);
    const report = buildRoutedInputGradingReport({ frames: lines('input-snapshots.jsonl'), actions: lines('input-actions.jsonl'), seal }, run, runId, adapter,
        { replayModule, inputReportSha256: inputBinding.sha256 });
    writeJson(join(output, 'rin-observations.json'), report);
    return { runId, suiteId: routedInputGradedSuiteId, file: 'rin-observations.json',
        sha256: hash(readFileSync(join(output, 'rin-observations.json'))),
        runFile: 'rin-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: routedInputContractsVersion, counts: routedInputCounts(report), provisionalUntil: 'cell-receipt.json' };
}

/** Re-derive the SGR-underline-color report parent-side with the layered pinned replay. */
async function retainSgrUnderlineColorEvidence(output, runId, sgrUnderlineColorBundle, upstream) {
    const runBytes = readFileSync(join(output, 'sguc-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'sguc-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-sguc.mjs'));
    const adapter = { id: sgrUnderlineColorAdapterId, derivedFromAdapterId: sgrUnderlineAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(sgrUnderlineColorBundle).href);
    const report = buildSgrUnderlineColorGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'sguc-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: sgrUnderlineColorSuiteId, file: 'sguc-observations.json',
        sha256: hash(readFileSync(join(output, 'sguc-observations.json'))),
        runFile: 'sguc-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: sgrUnderlineColorContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

/** Derive and lock the layered runtime-color reset grading extension over the left/right-margin one. */
function prepareColorResetCollector(tool, lrMarginsBundle, output) {
    const adapted = adaptColorResetCollector(readFileSync(lrMarginsBundle));
    const colorResetBundleSha256 = hash(adapted);
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-crst-${colorResetBundleSha256}.mjs`);
    writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== colorResetBundleSha256) throw new Error('Derived runtime-color reset bundle digest mismatch.');
    writeFileSync(join(output, 'collector-crst.mjs'), readFileSync(bundle), { flag: 'wx', mode: 0o600 });
    return { bundle, colorResetBundleSha256 };
}

/** Re-derive the runtime-color reset report parent-side with the layered pinned replay over the retained captures. */
async function retainColorResetEvidence(output, runId, colorResetBundle, upstream) {
    const runBytes = readFileSync(join(output, 'crst-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'crst-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-crst.mjs'));
    const adapter = { id: colorResetAdapterId, derivedFromAdapterId: lrMarginsAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(colorResetBundle).href);
    const report = buildColorResetGradingReport(frames, run, runId, adapter, { replayModule, output });
    writeJson(join(output, 'crst-observations.json'), report);
    return { runId, suiteId: colorResetSuiteId, file: 'crst-observations.json',
        sha256: hash(readFileSync(join(output, 'crst-observations.json'))),
        runFile: 'crst-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: colorResetContractsVersion, counts: colorResetCounts(report), provisionalUntil: 'cell-receipt.json' };
}

/** Re-derive the SGR-underline report parent-side with the layered pinned replay. */
async function retainSgrUnderlineEvidence(output, runId, sgrUnderlineBundle, upstream) {
    const runBytes = readFileSync(join(output, 'sgus-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'sgus-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-sgus.mjs'));
    const adapter = { id: sgrUnderlineAdapterId, derivedFromAdapterId: sgrTruecolorAdapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(sgrUnderlineBundle).href);
    const report = buildSgrUnderlineGradingReport(frames, run, runId, adapter, { replayModule });
    writeJson(join(output, 'sgus-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: sgrUnderlineSuiteId, file: 'sgus-observations.json',
        sha256: hash(readFileSync(join(output, 'sgus-observations.json'))),
        runFile: 'sgus-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: sgrUnderlineContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

/** Re-derive the App-observed underline-color resolution report parent-side (no pinned probe to replay). */
function retainUnderlineResolvedEvidence(output, runId) {
    const runBytes = readFileSync(join(output, 'sgur-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'sgur-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const report = buildUnderlineResolvedReport(frames, run, runId);
    writeJson(join(output, 'sgur-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: underlineResolvedSuiteId, file: 'sgur-observations.json',
        sha256: hash(readFileSync(join(output, 'sgur-observations.json'))),
        runFile: 'sgur-run.json', runSha256: hash(runBytes), contractsVersion: underlineResolvedContractsVersion, counts,
        provisionalUntil: 'cell-receipt.json' };
}

/** Re-derive the truecolor-literal finding report parent-side (no pinned probe to replay). */
function retainTruecolorFindingEvidence(output, runId) {
    const runBytes = readFileSync(join(output, 'sgtf-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'sgtf-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const report = buildTruecolorFindingReport(frames, run, runId);
    writeJson(join(output, 'sgtf-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: truecolorFindingSuiteId, file: 'sgtf-observations.json',
        sha256: hash(readFileSync(join(output, 'sgtf-observations.json'))),
        runFile: 'sgtf-run.json', runSha256: hash(runBytes), contractsVersion: truecolorFindingContractsVersion, counts,
        provisionalUntil: 'cell-receipt.json' };
}

/** Re-derive the graded report parent-side, replaying the pinned probes over
 * the retained journal before publishing the binding. */
async function retainGradingEvidence(output, runId, gradingBundle, upstream) {
    const runBytes = readFileSync(join(output, 'grading-run.json'));
    const run = JSON.parse(runBytes);
    const frames = readFileSync(join(output, 'grading-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
    const adapterBytes = readFileSync(join(output, 'collector-grading.mjs'));
    const adapter = { id: gradingAdapterId, derivedFromAdapterId: adapterId,
        bundleSha256: hash(adapterBytes), upstreamBundleSha256: upstream };
    const replayModule = await import(pathToFileURL(gradingBundle).href);
    const report = buildAsciiSgrGradingReport(frames, run, runId, adapter, replayModule);
    writeJson(join(output, 'grading-observations.json'), report);
    const counts = {};
    for (const item of report.cases) counts[item.conclusion.outcome] = (counts[item.conclusion.outcome] ?? 0) + 1;
    return { runId, suiteId: asciiSgrSuiteId, file: 'grading-observations.json',
        sha256: hash(readFileSync(join(output, 'grading-observations.json'))),
        runFile: 'grading-run.json', runSha256: hash(runBytes), adapterBundleSha256: adapter.bundleSha256,
        contractsVersion: asciiSgrContractsVersion, counts, provisionalUntil: 'cell-receipt.json' };
}

async function terminateLaunchedApp(app) {
    if (!app?.pid || app.exitCode !== null || app.signalCode !== null) return;
    app.kill('SIGTERM');
    await new Promise(resolve => {
        const timer = setTimeout(() => app.kill('SIGKILL'), 2000);
        app.once('exit', () => { clearTimeout(timer); resolve(); });
    });
}

async function cleanupOwnedRun({ workerIdentity, runId, app, logFd, server, output, proofs, completed, scratch }) {
    let workerError;
    try { await terminateRecordedWorker(workerIdentity, runId); }
    catch (error) { workerError = error; }
    // App shutdown still runs if worker verification or termination failed.
    let appError;
    try { await terminateLaunchedApp(app); }
    catch (error) { appError = error; }
    if (logFd !== undefined) closeSync(logFd);
    await new Promise(resolve => server.close(() => resolve()));
    // Parent shutdown can reap an already-exited worker that was a zombie.
    if (workerError && workerIdentity && !workerExists(workerIdentity.pid)) workerError = undefined;
    const cleanupError = workerError ?? appError;
    const cleanup = cleanupError
        ? { cleanupError: cleanupError.message, retainedFixture: scratch }
        : { cleanedUpAt: new Date().toISOString() };
    writeJson(join(output, 'ownership-proof.json'), { proofs, completed, ...cleanup });
    if (cleanupError) throw cleanupError;
}

function retainPreparationFailure(output, error) {
    if (output && !existsSync(join(output, 'failure.json'))) {
        writeJson(join(output, 'failure.json'), { error: error.message, at: new Date().toISOString() });
    }
}

/** Collect only through a proved disposable app/PTY, then finalize after complete cleanup. */
async function main() {
    const snapshot = readSourceSnapshot(sourceRepo);
    const args = process.argv.slice(2);
    if (args.length === 1 && ['--help', '-h'].includes(args[0])) {
        console.log(`Usage: node scripts/terminfo-owned.mjs --executable /absolute/path/Ntilde --app-version VERSION

Requires macOS, Node 24+ and an uninstalled sidecar outside .app bundles.
Packaged apps are refused because a staged update can restart outside the fixture.
Enables Act access only in fresh private settings for parent-selected resize and declared routed-input actions.
Requires an npm installation (or NTILDE_TERMINFO_NPM_CLI) to recreate locked dependencies.
Prepare terminfo.dev@4.0.0 under
artifacts/terminfo/tool (see docs/qa/QA_TERMINFO.md). Launches a disposable
Ntilde process with private settings and runs a locally extended collector.
Includes synthetic App-private clipboard transactions with native providers disabled; no OS clipboard qualification.
Results go to artifacts/terminfo/owned-*. No existing sessions are used.
The collector extension and its digest are recorded; this is not stock CLI data.`);
        return;
    }
    const executable = validateInvocation(args);
    const input = readPreparedCollector(join(repo, 'artifacts', 'terminfo', 'tool'));

    let resolveDone;
    let rejectDone;
    const done = new Promise((resolve, reject) => { resolveDone = resolve; rejectDone = reject; });
    done.catch(() => {});
    let interruption;
    const interrupted = signal => {
        interruption ??= new Error(`Interrupted by ${signal}.`);
        process.exitCode = 1;
        rejectDone(interruption);
    };
    const onInt = () => interrupted('SIGINT');
    const onTerm = () => interrupted('SIGTERM');
    let scratch;
    let output;
    let preserveScratch = false;
    let cellEvidence;
    let sgrEvidence;
    let gradingEvidence;
    let sgrColorsEvidence;
    let sgrRgbEvidence;
    let sgrAttrsEvidence;
    let sgrPaletteEvidence;
    let sgrTruecolorEvidence;
    let truecolorFindingEvidence;
    let sgrUnderlineEvidence;
    let sgrUnderlineColorEvidence;
    let underlineResolvedEvidence;
    let sgrResetEvidence;
    let sgrClosureEvidence;
    let eraseLineEvidence;
    let charEditEvidence;
    let eraseDisplayEvidence;
    let lineEditEvidence;
    let charsetsEvidence;
    let scrollbackEvidence;
    let savedResetEvidence;
    let eraseAttrsRegionEvidence;
    let attrRectEvidence;
    let cursorQualEvidence;
    let eraseCharsHistoryEvidence;
    let selectiveEraseEvidence;
    let rectEditEvidence;
    let colEditEvidence;
    let rectChecksumEvidence;
    let lrMarginsEvidence;
    let coreGapsEvidence;
    let textCursorEvidence;
    let coreEraseEvidence;
    let coreWrapEvidence;
    let routedInputEvidence;
    let osc133Evidence;
    let colorResetEvidence;
    let rgbEvidence;
    let renditionEvidence;
    let cursorEvidence;
    let pixelEvidence;
    let colorEvidence;
    let eraseEvidence;
    let editingEvidence;
    let historyEvidence;
    let resizeEvidence;
    let savedEvidence;
    let metadataEvidence;
    let inputEvidence;
    let staticImageEvidence;
    let queryEvidence;
    let privateClipboardEvidence;
    let sourceContext;
    // Persistent handlers also protect against a second signal during teardown.
    process.on('SIGINT', onInt);
    process.on('SIGTERM', onTerm);
    if (['SIGINT', 'SIGTERM'].includes(process.env.NTILDE_TERMINFO_PENDING_SIGNAL)) interrupted(process.env.NTILDE_TERMINFO_PENDING_SIGNAL);
    try {
        output = mkdtempSync(join(repo, 'artifacts', 'terminfo', 'owned-'));
        writeFileSync(join(output, 'source-snapshot.json'), snapshot.bytes, { flag: 'wx', mode: 0o600 });
        scratch = createPrivateScratch();
        if (interruption) throw interruption;
        const privateTool = join(scratch, 'collector');
        installLockedCollector(privateTool, input.manifestBytes, input.lockBytes);
        const { ownerModule, bundle, adapterBundleSha256, receiptBytes, lockBytes, originalBundle } = prepareCollector(privateTool);
        const { bundle: gradingBundle, gradingBundleSha256 } = prepareGradingCollector(privateTool, bundle, output);
        const { bundle: sgrColorsBundle, sgrColorsBundleSha256 } = prepareSgrColorsCollector(privateTool, gradingBundle, output);
        const { bundle: sgrRgbBundle, sgrRgbBundleSha256 } = prepareSgrRgbCollector(privateTool, sgrColorsBundle, output);
        const { bundle: sgrAttrsBundle, sgrAttrsBundleSha256 } = prepareSgrAttrsCollector(privateTool, sgrRgbBundle, output);
        const { bundle: sgrPaletteBundle, sgrPaletteBundleSha256 } = prepareSgrPaletteCollector(privateTool, sgrAttrsBundle, output);
        const { bundle: sgrTruecolorBundle, sgrTruecolorBundleSha256 } = prepareSgrTruecolorCollector(privateTool, sgrPaletteBundle, output);
        const { bundle: sgrUnderlineBundle, sgrUnderlineBundleSha256 } = prepareSgrUnderlineCollector(privateTool, sgrTruecolorBundle, output);
        const { bundle: sgrUnderlineColorBundle, sgrUnderlineColorBundleSha256 } = prepareSgrUnderlineColorCollector(privateTool, sgrUnderlineBundle, output);
        const { bundle: sgrResetBundle, sgrResetBundleSha256 } = prepareSgrResetCollector(privateTool, sgrUnderlineColorBundle, output);
        const { bundle: sgrClosureBundle, sgrClosureBundleSha256 } = prepareSgrClosureCollector(privateTool, sgrResetBundle, output);
        const { bundle: eraseLineBundle, eraseLineBundleSha256 } = prepareEraseLineCollector(privateTool, sgrClosureBundle, output);
        const { bundle: charEditBundle, charEditBundleSha256 } = prepareCharEditCollector(privateTool, eraseLineBundle, output);
        const { bundle: eraseDisplayBundle, eraseDisplayBundleSha256 } = prepareEraseDisplayCollector(privateTool, charEditBundle, output);
        const { bundle: lineEditBundle, lineEditBundleSha256 } = prepareLineEditCollector(privateTool, eraseDisplayBundle, output);
        const { bundle: charsetsBundle, charsetsBundleSha256 } = prepareCharsetsCollector(privateTool, lineEditBundle, output);
        const { bundle: scrollbackBundle, scrollbackBundleSha256 } = prepareScrollbackCollector(privateTool, charsetsBundle, output);
        const { bundle: savedResetBundle, savedResetBundleSha256 } = prepareSavedResetCollector(privateTool, scrollbackBundle, output);
        const { bundle: eraseAttrsRegionBundle, eraseAttrsRegionBundleSha256 } = prepareEraseAttrsRegionCollector(privateTool, savedResetBundle, output);
        const { bundle: attrRectBundle, attrRectBundleSha256 } = prepareAttrRectCollector(privateTool, eraseAttrsRegionBundle, output);
        const { bundle: cursorQualBundle, cursorQualBundleSha256 } = prepareCursorQualCollector(privateTool, attrRectBundle, output);
        const { bundle: eraseCharsHistoryBundle, eraseCharsHistoryBundleSha256 } = prepareEraseCharsHistoryCollector(privateTool, cursorQualBundle, output);
        const { bundle: selectiveEraseBundle, selectiveEraseBundleSha256 } = prepareSelectiveEraseCollector(privateTool, eraseCharsHistoryBundle, output);
        const { bundle: rectEditBundle, rectEditBundleSha256 } = prepareRectEditCollector(privateTool, selectiveEraseBundle, output);
        const { bundle: colEditBundle, colEditBundleSha256 } = prepareColEditCollector(privateTool, rectEditBundle, output);
        const { bundle: rectChecksumBundle, rectChecksumBundleSha256 } = prepareRectChecksumCollector(privateTool, colEditBundle, output);
        const { bundle: lrMarginsBundle, lrMarginsBundleSha256 } = prepareLrMarginsCollector(privateTool, rectChecksumBundle, output);
        const coreGapsSource = adaptCoreGapsCollector(readFileSync(lrMarginsBundle));
        const coreGapsBundleSha256 = hash(coreGapsSource);
        const coreGapsBundle = join(privateTool, 'node_modules', 'terminfo.dev', 'dist', 'ntilde-cg-' + coreGapsBundleSha256 + '.mjs');
        writeFileSync(coreGapsBundle, coreGapsSource, { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'collector-cg.mjs'), coreGapsSource, { flag: 'wx', mode: 0o600 });
        const { bundle: routedInputBundle, routedInputBundleSha256 } = prepareRoutedInputCollector(privateTool, lrMarginsBundle, output);
        const { bundle: osc133Bundle, osc133BundleSha256 } = prepareOsc133Collector(privateTool, lrMarginsBundle, output);
        const { bundle: colorResetBundle, colorResetBundleSha256 } = prepareColorResetCollector(privateTool, lrMarginsBundle, output);
        const appData = join(scratch, 'appdata');
        mkdirSync(appData, { mode: 0o700 });
        const runId = randomBytes(16).toString('hex');
        const token = randomBytes(32).toString('hex');
        const configPath = join(scratch, 'worker.json');
        const workerScript = join(sourceRepo, 'scripts', 'terminfo', 'worker.mjs');
        const profileName = `terminfo disposable worker ${runId}`;
        const config = { privateClipboard: true, runId, token, appData, output, executable, bundle, gradingBundle, gradingBundleSha256,
            sgrColorsBundle, sgrColorsBundleSha256, sgrRgbBundle, sgrRgbBundleSha256, sgrAttrsBundle, sgrAttrsBundleSha256,
            sgrPaletteBundle, sgrPaletteBundleSha256, sgrTruecolorBundle, sgrTruecolorBundleSha256, sgrUnderlineBundle, sgrUnderlineBundleSha256, sgrUnderlineColorBundle, sgrUnderlineColorBundleSha256, sgrResetBundle, sgrResetBundleSha256, sgrClosureBundle, sgrClosureBundleSha256, eraseLineBundle, eraseLineBundleSha256, charEditBundle, charEditBundleSha256, eraseDisplayBundle, eraseDisplayBundleSha256, lineEditBundle, lineEditBundleSha256, charsetsBundle, charsetsBundleSha256, scrollbackBundle, scrollbackBundleSha256, savedResetBundle, savedResetBundleSha256, eraseAttrsRegionBundle, eraseAttrsRegionBundleSha256, attrRectBundle, attrRectBundleSha256, cursorQualBundle, cursorQualBundleSha256, eraseCharsHistoryBundle, eraseCharsHistoryBundleSha256, selectiveEraseBundle, selectiveEraseBundleSha256, rectEditBundle, rectEditBundleSha256, colEditBundle, colEditBundleSha256, rectChecksumBundle, rectChecksumBundleSha256, lrMarginsBundle, lrMarginsBundleSha256, colorResetBundle, colorResetBundleSha256,
            coreGapsBundle, coreGapsBundleSha256, routedInputBundle, routedInputBundleSha256, osc133Bundle, osc133BundleSha256,
            profileName, cellNonce: `NTILDE_QA_${runId}`,
            socket: join(scratch, 'owner.sock'), adapterBundleSha256, upstreamBundleSha256 };
        writeJson(configPath, config);
        const command = join(scratch, 'worker.sh');
        writeFileSync(command, `#!/bin/sh\nexec ${quote(process.execPath)} ${quote(workerScript)} ${quote(configPath)} --ntilde-terminfo-worker=${runId}\n`, { flag: 'wx', mode: 0o700 });
        const profileId = randomUUID();
        const settings = {
            Profiles: [{ Id: profileId, Name: profileName, Command: command, Arguments: '', StartingDirectory: scratch, Type: 0 }],
            DefaultProfileId: profileId, SessionPersistence: 'Off', ShellExitPolicy: 'Always', PaneClosePolicy: 'Always',
            QuakeModeEnabled: false, AutomaticUpdateChecks: false, AllowOsc52ClipboardWrite: true, AllowRectangleChecksumReport: true,
            MinimumContrast: 1, EnableLigatures: false,
            BellAudioEnabled: false, BellVisualEnabled: false, SmoothScrolling: false,
            Osc9NotificationsEnabled: true, Osc9ProgressReportingEnabled: true, TabStripOrientation: 'Horizontal',
            CommandAssistEnabled: false, CommandAssistShellIntegrationEnabled: false,
            CommandAssistPowerShellIntegrationEnabled: false, AgentAccessObserveEnabled: true, AgentAccessActEnabled: true,
        };
        writeJson(join(appData, 'settings.json'), settings);
        writeJson(join(output, 'initial-settings.json'), settings);
        writeFileSync(join(output, 'upstream-suite-receipt.json'), receiptBytes, { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'collector-original.mjs'), originalBundle, { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'package-lock.json'), lockBytes, { flag: 'wx', mode: 0o600 });

        let app;
        let workerIdentity;
        let cellObserver;
        let sgrObserver;
        let gradingObserver;
        let sgrColorsObserver;
        let sgrRgbObserver;
        let sgrAttrsObserver;
        let sgrPaletteObserver;
        let sgrTruecolorObserver;
        let truecolorFindingObserver;
        let sgrUnderlineObserver;
        let sgrUnderlineColorObserver;
        let underlineResolvedObserver;
        let sgrResetObserver;
        let sgrClosureObserver;
        let eraseLineObserver;
        let charEditObserver;
        let eraseDisplayObserver;
        let lineEditObserver;
        let charsetsObserver;
        let scrollbackObserver;
        let savedResetObserver;
        let eraseAttrsRegionObserver;
        let attrRectObserver;
        let cursorQualObserver;
        let eraseCharsHistoryObserver;
        let selectiveEraseObserver;
        let rectEditObserver;
        let colEditObserver;
        let rectChecksumObserver;
        let lrMarginsObserver;
        let coreGapsObserver;
        let textCursorObserver;
        let coreEraseObserver;
        let coreWrapState;
        let colorResetObserver;
        let pixelObserver;
        let pixelUnavailable;
        let colorObserver;
        let colorUnavailable;
        const editingStates = new Map();
        const editingSpecs = {
            erase: { nonce: 'NTILDE_ER_', suiteId: eraseSuiteId, count: 13, report: buildEraseReport },
            editing: { previous: 'erase', nonce: 'NTILDE_EC_', suiteId: editingSuiteId, count: 22, report: buildCellEditingReport },
            history: { previous: 'editing', nonce: 'NTILDE_HS_', suiteId: historySuiteId, count: 30, report: buildHistoryReport, observer: OwnedHistoryObserver },
        };
        const detailStates = new Map();
        const detailSpecs = {
            rgb: { previous: 'sgr', nonce: 'NTILDE_RG_', suiteId: rgbSuiteId, cases: rgbCases, report: buildRgbReport },
            rendition: { previous: 'rgb', nonce: 'NTILDE_RD_', suiteId: renditionSuiteId, cases: renditionCases, report: buildRenditionReport, includeOverline: true },
            cursor: { previous: 'rendition', nonce: 'NTILDE_CS_', suiteId: cursorSuiteId, cases: cursorCases, report: buildCursorReport, includeCursorMode: true },
        };
        let resizeState;
        let savedState;
        let metadataState;
        let inputState;
        let staticImageState;
        let queryState;
        let privateClipboardState;
        let completed = false, outputModeVerified = false;
        const proofs = [];
        const image = statSync(executable);
        const context = {
            adapter: adapterId, adapterBundleSha256, upstreamBundleSha256, appVersion: args[3],
            coreGapsSuiteId, coreGapsAdapterId, coreGapsBundleSha256, textCursorSuiteId, coreEraseSuiteId, coreWrapSuiteId: wrapSuiteId,
            sourceSnapshotPolicy, sourceSnapshotManifestSha256: snapshot.sha256, sourceSnapshotRoot: sourceRepo,
            appVersionSource: 'operator supplied; not independently verified',
            initialSettingsSha256: hash(readFileSync(join(output, 'initial-settings.json'))),
            executable, executableSha256: hash(readFileSync(executable)),
            assemblySha256: existsSync(join(dirname(executable), 'Ntilde.dll')) ? hash(readFileSync(join(dirname(executable), 'Ntilde.dll'))) : null,
            nodeVersion: process.version, dependencyInstall: 'isolated npm ci --ignore-scripts', startedAt: new Date().toISOString(), runId,
            ...retainCollectorEvidence(output, bundle, adapterBundleSha256, ownerModule, workerScript),
            retainedAdapterBundle: 'collector-adapter.mjs', retainedSources: 'sources',
            clipboard: 'armed synthetic App-private storage only; no native interchange or OSC52 read permission',
            privateClipboardSuiteId: ownedPrivateClipboardSuiteId, ownedPrivateClipboardPolicy,
            privateClipboardReferenceScope: 'selected App/VT observational reference inputs; not complete App closure or binary-source attestation',
            retainedOriginalCollector: 'collector-original.mjs', terminalOutput: privateClipboardOutputPolicy, querySuiteId, ownedQueryPolicy, staticImageSuiteId, ownedStaticImagePolicy, inputSuiteId, ownedInputPolicy, metadataSuiteId, ownedMetadataPolicy, savedSuiteId, ownedSavedPolicy, resizeSuiteId, ownedResizePolicy, historySuiteId, eraseSuiteId, editingSuiteId, colorSuiteId, pixelSuiteId, pixels: 'nonce-bound owned offscreen renders; fixed synthetic phases, retained PNGs and render context; no live desktop timing',
            gradingAdapterId, asciiSgrSuiteId, gradingBundleSha256, gradingContractsVersion: asciiSgrContractsVersion,
            grading: 'versioned collector extension; pinned termless criteria replayed on parent-observed cells; not stock output',
            sgrColorsAdapterId, sgrColorsSuiteId, sgrColorsBundleSha256, sgrColorsContractsVersion,
            sgrColors: 'layered versioned collector extension; pinned reset/standard-color termless criteria replayed on parent-observed stored attributes and palette indices; not stock output',
            sgrPaletteAdapterId, sgrPaletteSuiteId, sgrPaletteBundleSha256, sgrPaletteContractsVersion,
            sgrTruecolorAdapterId, sgrTruecolorSuiteId, sgrTruecolorBundleSha256, sgrTruecolorContractsVersion,
            truecolorFindingSuiteId, truecolorFindingContractsVersion,
            sgrUnderlineAdapterId, sgrUnderlineSuiteId, sgrUnderlineBundleSha256, sgrUnderlineContractsVersion,
            sgrUnderlineColorAdapterId, sgrUnderlineColorSuiteId, sgrUnderlineColorBundleSha256, sgrUnderlineColorContractsVersion,
            underlineResolvedSuiteId, underlineResolvedContractsVersion,
            sgrResetAdapterId, sgrResetSuiteId, sgrResetBundleSha256, sgrResetContractsVersion,
            sgrClosureAdapterId, sgrClosureSuiteId, sgrClosureBundleSha256, sgrClosureContractsVersion,
            sgrClosure: 'layered versioned collector extension; pinned termless criteria sgr.blink, sgr.overline and sgr.underline.color replayed byte-exactly on parent-observed stored blink flag, explicit overline boolean and stored underline color with reset, decoy, independence and sibling-shared color controls; sgr.underline.color is graded from its own reads, never from the sibling sgr.underline-color-rgb verdict; no blink timing, rendering or App-resolved color; not stock output',
            eraseLineAdapterId, eraseLineSuiteId, eraseLineBundleSha256, eraseLineContractsVersion,
            charEditAdapterId, charEditSuiteId, charEditBundleSha256, charEditContractsVersion,
            charEdit: 'layered versioned collector extension; pinned character-editing termless criteria (editing.insert-chars with the explicit-1 ICH spelling, editing.delete-chars with the explicit-1 DCH spelling, editing.repeat-char with REP count 3) replayed on parent-observed stored ASCII cells, cursor and stored attribute details, with unmodified-seed, other-count-spelling, wide-row, right-edge, REP-after-barrier and attribute controls; REP graphic and repeat sent in one fenced write; created-blank and repeated-cell attributes observed not required; the pinned pixel/query paths never measure cells and are not qualified; no Unicode, wide-cell or combining editing; not stock output',
            eraseDisplayAdapterId, eraseDisplaySuiteId, eraseDisplayBundleSha256, eraseDisplayContractsVersion,
            lineEditAdapterId, lineEditSuiteId, lineEditBundleSha256, lineEditContractsVersion,
            charsetsAdapterId, charsetsSuiteId, charsetsBundleSha256, charsetsContractsVersion,
            charsets: 'layered versioned collector extension; pinned charset termless criteria (charsets.utf8, charsets.dec-special, charsets.dec-line-drawing, charsets.g0-g1-switching) replayed byte-exactly (UTF-8 text, escape designations and shift controls untouched) on parent-observed decoded physical cells from complete stored cell details (never attribute strings or UTF-16 offsets), the cursor and neutral stored attributes, with ASCII, designation, invocation, uppercase and lowercase restoration-witness controls; the pinned terminal pixel capture and cursor-query paths are never verdicts and are not qualified; no glyph, pixel, font, keyboard, locale or platform claim',
            scrollbackAdapterId, scrollbackSuiteId, scrollbackBundleSha256, scrollbackContractsVersion,
            savedResetAdapterId, savedResetSuiteId, savedResetBundleSha256, savedResetContractsVersion,
            eraseAttrsRegionAdapterId, eraseAttrsRegionSuiteId, eraseAttrsRegionBundleSha256, eraseAttrsRegionContractsVersion,
            eraseAttrsRegion: 'layered versioned collector extension; pinned termless criteria erase.el-with-attrs (EL with the omitted parameter over a measured SGR 42 background: the erased cell (0,0) reads blank with the same App-resolved background RGB as the X it replaced) and erase.ed-scroll-region (ED with the omitted parameter, margins 3;10 and the cursor at row 2: the preceding row KEEP! survives and the target cells read blank) replayed byte-exactly on the disposable App main screen and graded from parent-observed stored cells, App-resolved colors, the cursor, margins, mode flags and history, with a colored-X seed proof, pen and margin restoration witnesses, a full-screen control against accidental whole-screen clearing, resolved-color versus stored-representation comparison and current-pen versus preserved-background controls (observed, not required) and a marker fill recording where ED stopped relative to the margins (observed, not graded; no clipping claim); the pinned terminal cursor-query paths never carry a verdict and are not qualified; no rendered-appearance claim; not stock output',
            savedReset: 'layered versioned collector extension; pinned termless criteria modes.alt-screen.exit (the seeded ?1049 roundtrip), modes.altscreen-47, modes.altscreen-1047 (inconclusive by design in the pinned probe), reset.sgr (a case separate from sgr.reset) and reset.decaln (cell 0,0) replayed byte-exactly on the disposable App main screen and graded from parent-observed stored cells, cursor, active-buffer flag, margins, mode flags and history; the visible-buffer effect contracts for ?47 and ?1047 are separately identified Ntilde observations beside the preserved pinned verdicts; active-screen reads only, no direct inactive-bank inspection; reset.method, RIS and the pinned DECALN pixel path are not claimed',
            cursorQualAdapterId, cursorQualSuiteId, cursorQualBundleSha256, cursorQualContractsVersion,
            eraseCharsHistoryAdapterId, eraseCharsHistorySuiteId, eraseCharsHistoryBundleSha256, eraseCharsHistoryContractsVersion,
            selectiveEraseAdapterId, selectiveEraseSuiteId, selectiveEraseBundleSha256, selectiveEraseContractsVersion,
            selectiveErase: 'layered versioned collector extension; pinned termless criteria erase.selective (DECSED, ESC[?2J) and editing.decsera (DECSERA, ESC[1;1;1;5${) replayed byte-exactly over the seeded DECSCA-protected P and unprotected ABCDZ and graded from parent-observed stored cells, per-cell DECSCA protection (observed, never inferred from a surviving marker), cursor, stored attributes, mode flags, buffer state and saved lines, with ordinary-erase, protection-off, overwrite, restoration, full-screen, edge, bold-attribute, history and incorrect-bounds or other-mode controls; the pinned terminal pixel and cursor-query paths never carry a verdict and are not qualified; ASCII only; not stock output',
            rectEditAdapterId, rectEditSuiteId, rectEditBundleSha256, rectEditContractsVersion,
            colEditAdapterId, colEditSuiteId, colEditBundleSha256, colEditContractsVersion,
            rectChecksumAdapterId, rectChecksumSuiteId, rectChecksumBundleSha256, rectChecksumContractsVersion,
            lrMarginsAdapterId, lrMarginsSuiteId, lrMarginsBundleSha256, lrMarginsContractsVersion,
            colorResetAdapterId, colorResetSuiteId, colorResetBundleSha256, colorResetContractsVersion,
            colorReset: 'layered versioned collector extension; pinned termless criteria extensions.osc104-reset-palette, osc110-reset-fg, osc111-reset-bg and osc112-reset-cursor (the BEL-spelled query, set of rgb:aa/bb/cc, query, reset and query) replayed byte-exactly on a disposable App and graded from exact fenced OSC replies, the App-resolved colors and override metadata, stored default/palette/literal cells, the cursor and controlled offscreen renders by an independent runtime-color model over the measured baseline; the pinned terminal paths (unmeasuredStateEffect) stay inconclusive by design; not stock CLI data',
            lrMargins: 'layered versioned collector extension; pinned termless modes.left-right-margin code (ESC[?69h, the mode metadata, ESC[?69l; inconclusive by design) replayed byte-exactly and preserved, beside a separately identified effect contract graded from parent-observed stored cells, the cursor and the App-stored region, left/right margins, DECLRMM, origin and insert mode by an independent margins model over sixteen controls; stored ASCII cells only, no stock grades',
            routedInputAdapterId, routedInputSuiteId: routedInputGradedSuiteId, routedInputBundleSha256, routedInputContractsVersion,
            routedInput: 'layered versioned collector extension; pinned termless input.x10-mouse, input.button-event-mouse and modes.application-keypad code (always inconclusive by design) replayed over mode values the child itself received as DECRQM replies and preserved, beside separately identified effect contracts graded from the owned routed-input suite: declared Avalonia events routed through the production App input path and the exact bytes the child received, literal protocol expectations cross-checked by an independent oracle; generated events, not physical input; direct-local only; no stock grades',
            osc133AdapterId, osc133SuiteId, osc133BundleSha256, osc133ContractsVersion,
            osc133: 'layered versioned collector extension; pinned extensions.osc133-a, -b, -c and -d termless and terminal code (marker + X consumption, always inconclusive by design) replayed over parent-journaled observations and preserved, beside separately identified effect contracts graded from paired complete stored cells, pane metadata (title, cwd, input window, raw command/output anchors) and session status by an independent lifecycle oracle: each pinned fixture executed from a measured prior state with an unmarked control and gating markers, plus canonical cycle, payload, boundary, terminator-variant and exit-payload observations; protocol handling in a disposable direct-local App only, no real-shell, remote, OSC 133 P, OSC 633 or stock-grade claim',
            rectChecksum: 'layered versioned collector extension; pinned termless criterion editing.decrqcra (ESC[1;1;1;1;1;5*y after ABCDE ESC[1;1H, a framed DCS 1 ! ~ hhhh ST) replayed byte-exactly on a disposable App whose initial settings turn on AllowRectangleChecksumReport (off by default), graded from raw replies bound to their DA barriers and parent-observed stored cells by an independent checksum model (character sum, bold/blink/reverse/underline weights, defaults, origin offset, page clipping, request id, non-admitted spellings, unchanged state); stored ASCII cells only, no stock grades',
            colEdit: 'layered versioned collector extension; pinned termless criteria editing.sl (ESC[2 @), editing.sr (ESC[2 A), editing.decic (ESC[2\'}) and editing.decdc (ESC[2\'~) replayed byte-exactly on a disposable App and graded from parent-observed stored cells of the whole screen, the cursor, App-stored margins, origin and insert mode, stored attribute tuples and per-cell DECSCA protection, with count, default, zero, clamp, region-inside/outside, origin, insert-mode, non-admitted-identity, first/last-column, moved-attribute, attribute-free-blank and moved-protection controls over a seed at both page edges; the pending-wrap state is observed, never graded; stored ASCII state with full-width horizontal margins only (DECLRMM is not implemented or claimed), the pinned pixel path never carries a verdict and is not qualified; no Unicode, wide-cell, scaled-text, image-plane, DECRQCRA, rectangle or rendered-appearance claim; not stock output',
            rectEdit: 'layered versioned collector extension; pinned termless criteria editing.decfra (ESC[88;1;1;3;5$x), editing.decera (ESC[1;1;3;5$z) and editing.deccra (ESC[1;1;2;5;1;5;10$v) replayed byte-exactly on a disposable App and graded from parent-observed stored cells of the whole screen, the cursor, App-stored margins, origin and insert mode and stored attribute tuples, with single-cell, inclusive-border, omitted, zero, clipped, no-op, origin, margin, insert-mode, live-pen, literal-pen and attributed-seed controls, unconditional erasure over DECSCA-marked text, and for DECCRA non-overlapping, overlapping (every direction), self, clipped, attribute-preserving and unsupported-page controls (pages are an observed Ntilde policy, never graded); stored ASCII state only, the pinned pixel path never carries a verdict and is not qualified; no Unicode, wide-cell, scaled-text, image-plane, protected-cell, DECSERA, DECCARA/DECRARA, DECSACE, checksum or rendered-appearance claim; not stock output',
            eraseCharsHistory: 'layered versioned collector extension; pinned termless criteria erase.character (ECH ESC[3X, explicit count 3, over ABCDE at column 0) and erase.screen.scrollback (ED 3 ESC[3J after rows+2 line feeds) replayed byte-exactly on the disposable App main screen and graded from parent-observed viewport text, stored cells and attributes, cursor, margins, modes, active buffer and the actual ordered saved lines and count, with erased-range, retained-cell, wide-row, oversized-count, right-edge, attribute, ordered-history, CSI 2J, ordinary-scrolling, empty-history and regrowth controls; ECH spellings other than the pinned one are observed, not graded; physical rows only; the pinned pixel/query paths never carry a verdict and are not qualified; ASCII only; not stock output',
            cursorQual: 'layered versioned collector extension; the four pinned cursor criteria (cursor.cup-scroll-region, cursor.hide, cursor.shape, cursor.reverse-wrap): the termless AND app callbacks replayed byte-exactly on a measured reset alternate screen over parent-observed stored state, physical cursor, margins, origin and cursor mode, raw CPR exchanges and controlled synthetic-phase render captures, with controls for DECOM off/on, region boundaries, restoration, visible and hidden references, steady shapes, blink phase and the mode-off reverse-wrap fixture; the geometry-repaired derivation for cursor.reverse-wrap and cursor.shape keeps the original callbacks and retains the original UndeclaredTerminalGeometry failures',
            attrRectAdapterId, attrRectSuiteId, attrRectBundleSha256, attrRectContractsVersion,
            attrRect: 'layered versioned collector extension; pinned termless criteria editing.deccara (ESC[1;1;3;5;7$r, set inverse over rows 1-3 columns 1-5 of aaaaaZ/bbbbbY/cccccX), editing.decrara (ESC[1;1;3;5;7$t, toggle inverse off the same seeded area) and editing.decsace (inconclusive by design in the pinned probe: it only feeds ESC[2*x and reports consumption) replayed byte-exactly on parent-observed stored cells, cursor and stored attribute details; the pinned verdicts are preserved and the separately identified effect contracts grade the attribute change itself with default-stream versus rectangle extent controls (DECSACE 0, 1 and 2), set/clear/toggle operand controls, preserved text, colors and current pen, outside-area controls, second-application restoration and extent-restoration witnesses; the extent setting is not readable from the App, so it is observed only through behavior; stored state only, no rendered appearance, no stock grades',
            scrollback: 'layered versioned collector extension; the nine pinned scrolling/history termless criteria (scrollback.accumulate, total-lines, scroll-up, reverse-index, scroll-down, set-region, alt-screen, decstbm, decstbm-reset) replayed byte-exactly on a measured reset main screen and graded from parent-observed viewport text, actual scrollback lines and count, margins, active screen, modes, cursor and stored attributes, with empty-history, pre-overflow, region-confinement, wrong-direction, no-op, whole-screen, alternate-screen isolation and unreset-region controls; three pinned probes are null by construction and the contract measurement beyond them is recorded as a divergence; ASCII only; not stock output',
            lineEdit: 'layered versioned collector extension; pinned line-editing termless criteria (editing.insert-lines with ESC[1L, editing.delete-lines with ESC[1M, each at row 2 column 1 over four seeded rows) replayed byte-exactly on parent-observed stored cells of the whole screen, cursor, App-stored margins, origin and insert mode and stored attribute details, with no-op, count-spelling (observed), full-height boundary, scroll-region, origin-mode and attribute controls; stored ASCII state only, the pinned pixel path never carries a verdict and is not qualified, scrollback observed on the alternate screen only',
            eraseDisplay: 'layered versioned collector extension; pinned erase-in-display termless criteria (erase.screen.below with ED 0, erase.screen.above with ED 1, erase.screen.all with ED 2, all explicit-parameter) replayed byte-exactly on parent-observed stored cells, cursor, stored attribute details, buffer state and saved lines, with unmodified-seed, other-spelling (omitted, explicit 0/1/2), full-screen fill, boundary, bold-seeded attribute and main-screen history controls; background-color erase observed not required; the pinned pixel capture path is capture-only upstream and not qualified; CSI 3J not graded; not stock output',
            eraseLine: 'layered versioned collector extension; pinned erase-in-line termless criteria (erase.line.right with the omitted-parameter spelling, erase.line.left with EL 1, erase.line.all with EL 2) replayed byte-exactly on parent-observed stored cells, cursor and stored attribute details, with unmodified-seed, other-spelling (omitted, explicit 0/1/2), wide-row and bold-seeded attribute controls; background-color erase observed not required; the pinned pixel capture path is capture-only upstream and not qualified; not stock output',
            sgrReset: 'layered versioned collector extension; pinned selective-reset termless criteria (sgr.selective-reset.underline via SGR 24 and sgr.selective-reset.inverse via SGR 27) replayed byte-exactly on parent-observed stored flags, exact underline style, stored colors and overline, with no-op, full-reset, wrong-reset and DECSCNM controls; underline-color behavior observed not required; rendered appearance not claimed; not stock output',
            underlineResolved: 'Ntilde extension controls (not terminfo.dev cases, no pinned probe, no collector extension): the opt-in App-reported resolved underline color (readScreen includeResolvedUnderlineColors) compared with an independent expectation derived from stored underline state, the recorded palette binding and contracted OSC 4 constants, for indexed, literal and default-inheritance underlines with an OSC 4 mutation and its OSC 104 restoration; theme-resolution stage only; rendered appearance is not claimed',
            sgrUnderlineColor: 'layered versioned collector extension; pinned underline-color termless criteria (sgr.underline-color-indexed, -rgb, -reset; the aggregate sgr.underline.color is not qualified) replayed on parent-observed stored underline style and color index/literal/default with parent-derived resolved RGB and an OSC 4 mutation control; colon forms are ungraded controls; rendered underline appearance not claimed; not stock output',
            sgrUnderline: 'layered versioned collector extension; pinned underline-style termless criteria (sgr.underline.double via SGR 21; sgr.underline.curly, .dotted, .dashed via SGR 4:3, 4:4, 4:5) replayed on parent-observed stored underline flag and exact stored style; SGR 4, 4:1 and 4:2 are labeled controls without a grade; stroke appearance not claimed; not stock output',
            truecolorFinding: 'Ntilde finding (not a terminfo.dev case, no pinned probe, no collector extension): five exact direct-RGB values on fg/bg graded from parent-observed stored literal bytes and App-resolved RGB bound to the per-frame recorded palette, with an indexed positive control, an OSC 4 mutation of the aliased entry and its restoration; no pixels',
            sgrTruecolor: 'layered versioned collector extension; pinned truecolor termless criteria (sgr.fg.truecolor, sgr.bg.truecolor; semicolon 38/48;2;r;g;b form only) replayed on parent-observed stored literal bytes (palette index and default marker cleared) with App-resolved RGB bound to one recorded palette/theme; no pixels; not stock output',
            sgrPalette: 'layered versioned collector extension; pinned bright, 256-color and default-color termless criteria (sgr.fg/bg.bright, .256, .default) replayed on parent-observed stored palette indices, default markers and literal bytes with App-resolved RGB bound to one recorded palette/theme; no pixels; not stock output',
            sgrAttrsAdapterId, sgrAttrsSuiteId, sgrAttrsBundleSha256, sgrAttrsContractsVersion,
            sgrAttrs: 'layered versioned collector extension; pinned SGR attribute termless criteria (underline.single, inverse, faint, hidden, strikethrough) replayed on parent-observed stored flags and underline style with DECSCNM, neutral-baseline and reset controls; stored state only, no pixels; not stock output',
            sgrRgbAdapterId, sgrRgbSuiteId, sgrRgbBundleSha256, sgrRgbContractsVersion,
            sgrRgb: 'layered versioned collector extension; pinned named-color RGB termless criteria replayed on parent-observed App-resolved colors bound to one recorded theme state; not stock output',
            resolvedColors: 'opt-in readScreen projection resolved in the App rendering layer; storage-to-theme resolution stage, one coherent render snapshot; no pixels',
            cells: 'private AgentHost readScreen; nonce-bound pane; separate ASCII cell-state suite',
            attributes: 'separate ASCII SGR flags and palette-index suite',
            cellDetails: 'separate stored ARGB/underline and rendition/charset suites; no theme resolution or pixels',
            cursorModes: 'stored cursor style/blink/default/visibility; pristine owned DECSTR cleanup, no host appearance or blink phase',
        };
        sourceContext = context;
        writeFileSync(join(output, 'cell-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'sgr-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'grading-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'sgcolors-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'sgrgb-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'sgattr-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'sgpal-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'sgtc-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'sgtf-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'sgus-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'sguc-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'sgur-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'sgsr-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'sgcl-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'erln-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'ched-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'erdi-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'ledt-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'chrs-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'scrl-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'svrs-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'erar-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'atrc-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'curq-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'echh-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'sels-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'rect-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'cole-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'csum-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'lrmm-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'shi-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'crst-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        for (const prefix of [...Object.keys(detailSpecs), 'pixel', 'color', ...Object.keys(editingSpecs), 'resize', 'saved', 'metadata', 'input', 'image', 'query'])
            writeFileSync(join(output, `${prefix}-snapshots.jsonl`), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'resize-actions.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'input-actions.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'image-actions.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'query-actions.jsonl'), '', { flag: 'wx', mode: 0o600 });
        /** Complete each prior suite before capturing this one; only initial typed refusal is optional. */
        const observeDetails = async (prefix, request, budget) => {
            const spec = detailSpecs[prefix];
            const previous = detailStates.get(spec.previous);
            const ready = spec.previous === 'sgr' ? sgrObserver?.restored : previous?.observer.restored || previous?.unavailable;
            if (!ready) throw new Error('The preceding owned suite must complete before detail observations.');
            let state = detailStates.get(prefix);
            if (!state) {
                state = { observer: new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: spec.nonce + runId, includeCellDetails: true, includeCursorMode: spec.includeCursorMode ?? false, includeOverline: spec.includeOverline ?? false,
                    prefix, expectedPaneId: cellObserver.paneId }) };
                detailStates.set(prefix, state);
            }
            if (state.unavailable) throw new Error('Detail observation was already recorded as unavailable.');
            try {
                const result = await state.observer.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, `${prefix}-snapshots.jsonl`), JSON.stringify(state.observer.frames.at(-1)) + '\n');
                return result;
            } catch (error) {
                if (request.kind === prefix + '-start' && error.code === 'screenDetailsUnavailable'
                    && !state.observer.baseline && !state.observer.frames.length && state.observer.snapshots === 1) {
                    const file = `${prefix}-unavailable.json`;
                    writeJson(join(output, file), { schemaVersion: 1, runId, suiteId: spec.suiteId,
                        status: 'unavailable', reason: error.code, fixtureStarted: false, plannedExperiments: spec.cases.length });
                    state.unavailable = { runId, suiteId: spec.suiteId, file, sha256: hash(readFileSync(join(output, file))),
                        status: 'unavailable', reason: error.code, counts: {}, provisionalUntil: 'cell-receipt.json' };
                }
                throw error;
            }
        };
        const retainDetails = prefix => {
            const state = detailStates.get(prefix), spec = detailSpecs[prefix];
            return state?.unavailable ?? retainCellReport(output, state?.observer, runId, spec.report, `${prefix}-observations.json`);
        };
        const recordPixelUnavailable = () => {
            const file = 'pixel-unavailable.json';
            writeJson(join(output, file), { schemaVersion: 1, runId, suiteId: pixelSuiteId,
                status: 'unavailable', reason: 'screenDetailsUnavailable', fixtureStarted: false, plannedExperiments: pixelScenes.length });
            pixelUnavailable = { runId, suiteId: pixelSuiteId, file, sha256: hash(readFileSync(join(output, file))),
                status: 'unavailable', reason: 'screenDetailsUnavailable', counts: {}, provisionalUntil: 'cell-receipt.json' };
        };
        const observePixels = async (request, budget) => {
            const previous = detailStates.get('cursor');
            if (previous?.unavailable && request.kind === 'pixel-start' && !pixelUnavailable) {
                recordPixelUnavailable();
                const error = new Error('Pixel fixture requires complete screen details.');
                error.code = 'screenDetailsUnavailable';
                throw error;
            }
            if (!previous?.observer.restored) throw new Error('Cursor suite must restore before pixel observations.');
            pixelObserver ??= new OwnedPixelObserver({ output, appData, ownerPid: app.pid, profileName,
                nonce: `NTILDE_PX_${runId}`, expectedPaneId: cellObserver.paneId });
            try {
                const result = await pixelObserver.handle(request, budget);
                appendFileSync(join(output, 'pixel-snapshots.jsonl'), JSON.stringify(pixelObserver.frames.at(-1)) + '\n');
                return result;
            } catch (error) {
                if (request.kind === 'pixel-start' && error.code === 'screenDetailsUnavailable'
                    && !pixelObserver.before.baseline && pixelObserver.before.snapshots === 1 && !pixelObserver.frames.length) recordPixelUnavailable();
                throw error;
            }
        };
        const recordColorUnavailable = () => {
            const file = 'color-unavailable.json';
            writeJson(join(output, file), { schemaVersion: 1, runId, suiteId: colorSuiteId,
                status: 'unavailable', reason: 'screenDetailsUnavailable', fixtureStarted: false, plannedExperiments: colorCases.length });
            colorUnavailable = { runId, suiteId: colorSuiteId, file, sha256: hash(readFileSync(join(output, file))),
                status: 'unavailable', reason: 'screenDetailsUnavailable', counts: {}, provisionalUntil: 'cell-receipt.json' };
        };
        const observeColors = async (request, budget) => {
            const previous = { unavailable: pixelUnavailable, observer: pixelObserver };
            if (previous?.unavailable && request.kind === 'color-start' && !colorUnavailable) {
                recordColorUnavailable();
                const error = new Error('Color fixture requires complete screen details.');
                error.code = 'screenDetailsUnavailable';
                throw error;
            }
            if (!previous?.observer.restored) throw new Error('Pixel suite must restore before color observations.');
            colorObserver ??= new OwnedColorObserver({ output, appData, ownerPid: app.pid, profileName,
                nonce: `NTILDE_CL_${runId}`, expectedPaneId: cellObserver.paneId });
            try {
                const result = await colorObserver.handle(request, budget);
                appendFileSync(join(output, 'color-snapshots.jsonl'), JSON.stringify(colorObserver.frames.at(-1)) + '\n');
                return result;
            } catch (error) {
                if (request.kind === 'color-start' && error.code === 'screenDetailsUnavailable'
                    && !colorObserver.before.baseline && colorObserver.before.snapshots === 1 && !colorObserver.frames.length) recordColorUnavailable();
                throw error;
            }
        };
        const recordEditingUnavailable = (prefix, state) => {
            const spec = editingSpecs[prefix], file = `${prefix}-unavailable.json`;
            writeJson(join(output, file), { schemaVersion: 1, runId, suiteId: spec.suiteId,
                status: 'unavailable', reason: 'screenDetailsUnavailable', fixtureStarted: false, plannedExperiments: spec.count });
            state.unavailable = { runId, suiteId: spec.suiteId, file, sha256: hash(readFileSync(join(output, file))),
                status: 'unavailable', reason: 'screenDetailsUnavailable', counts: {}, provisionalUntil: 'cell-receipt.json' };
        };
        /** Run each known cell plan only after its predecessor restored or declined before writes. */
        const observeEditing = async (prefix, request, budget) => {
            const spec = editingSpecs[prefix];
            const previous = prefix === 'erase' ? { observer: colorObserver, unavailable: colorUnavailable } : editingStates.get(spec.previous);
            if (!previous?.observer?.restored && !previous?.unavailable) throw new Error('The previous suite must restore before editing observations.');
            let state = editingStates.get(prefix);
            if (!state) {
                state = { observer: new (spec.observer ?? OwnedEditingObserver)({ prefix, appData, ownerPid: app.pid, profileName,
                    nonce: editingSpecs[prefix].nonce + runId, expectedPaneId: cellObserver.paneId }) };
                editingStates.set(prefix, state);
            }
            if (state.unavailable) throw new Error('Editing unavailability was already recorded.');
            if (previous.unavailable && request.kind === prefix + '-start') {
                recordEditingUnavailable(prefix, state);
                const error = new Error('Editing fixture requires complete screen details.');
                error.code = 'screenDetailsUnavailable';
                throw error;
            }
            try {
                const result = await state.observer.handle(request, budget);
                appendFileSync(join(output, `${prefix}-snapshots.jsonl`), JSON.stringify(state.observer.frames.at(-1)) + '\n');
                return result;
            } catch (error) {
                if (request.kind === prefix + '-start' && error.code === 'screenDetailsUnavailable'
                    && !state.observer.before.baseline && state.observer.before.snapshots === 1 && !state.observer.frames.length) recordEditingUnavailable(prefix, state);
                throw error;
            }
        };
        const retainEditing = prefix => {
            const state = editingStates.get(prefix);
            return state?.unavailable ?? retainCellReport(output, state?.observer, runId, editingSpecs[prefix].report, `${prefix}-observations.json`);
        };
        /** Record only a source-known initial refusal, before binding or requesting any resize. */
        const recordResizeUnavailable = reason => {
            const file = 'resize-unavailable.json';
            writeJson(join(output, file), { schemaVersion: 1, runId, suiteId: resizeSuiteId,
                status: 'unavailable', reason, fixtureStarted: false, plannedExperiments: 3 });
            resizeState.unavailable = { runId, suiteId: resizeSuiteId, file, sha256: hash(readFileSync(join(output, file))),
                status: 'unavailable', reason, counts: {}, provisionalUntil: 'cell-receipt.json' };
        };
        /** Source-selected geometry changes stay separate from the ordinary fixed-baseline readers. */
        const observeResize = async (request, budget) => {
            const previous = editingStates.get('history');
            if (!previous?.observer?.restored && !previous?.unavailable) throw new Error('History must restore before resize observations.');
            if (!resizeState) {
                resizeState = { workerPid: request.workerPid, tty: request.tty, recordedFrames: 0, recordedActions: 0 };
                resizeState.observer = new OwnedResizeObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: 'NTILDE_RZ_' + runId, expectedPaneId: cellObserver.paneId,
                    performResize: (rows, cols, actionBudget) => resizeOwnedPane({ app, appData, executable, runId,
                        paneId: resizeState.observer.paneId, uid: process.getuid(),
                        workerPid: resizeState.workerPid, tty: resizeState.tty }, rows, cols, actionBudget) });
                proofs[0].resize.actions = resizeState.observer.actions;
            }
            const state = resizeState;
            if (state.unavailable || state.workerPid !== request.workerPid || state.tty !== request.tty) throw new Error('Resize fixture ownership or availability changed.');
            if (previous.unavailable && request.kind === 'resize-start') {
                recordResizeUnavailable('screenDetailsUnavailable');
                throw Object.assign(new Error('Resize fixture requires complete screen details.'), { code: 'screenDetailsUnavailable' });
            }
            try {
                const result = await state.observer.handle(request, budget);
                if (request.kind === 'resize-start') {
                    proofs[0].resize.paneId = state.observer.paneId;
                    proofs[0].resize.originalGeometry = state.observer.original;
                }
                for (const frame of state.observer.frames.slice(state.recordedFrames)) {
                    appendFileSync(join(output, 'resize-snapshots.jsonl'), JSON.stringify(frame) + '\n');
                    state.recordedFrames++;
                }
                return result;
            } catch (error) {
                if (request.kind === 'resize-start' && ['screenDetailsUnavailable', 'resizeGeometryUnavailable'].includes(error.code)
                    && !state.observer.baseline && !state.observer.frames.length && !state.observer.actions.length) recordResizeUnavailable(error.code);
                throw error;
            } finally {
                for (const action of state.observer.actions.slice(state.recordedActions)) {
                    appendFileSync(join(output, 'resize-actions.jsonl'), JSON.stringify(action) + '\n');
                    state.recordedActions++;
                }
            }
        };
        /** Only an initial typed refusal may omit source-known saved-state stages. */
        const recordSavedUnavailable = reason => {
            const file = 'saved-unavailable.json';
            writeJson(join(output, file), { schemaVersion: 1, runId, suiteId: savedSuiteId,
                status: 'unavailable', reason, fixtureStarted: false, plannedExperiments: 9 });
            savedState.unavailable = { runId, suiteId: savedSuiteId, file, sha256: hash(readFileSync(join(output, file))),
                status: 'unavailable', reason, counts: {}, provisionalUntil: 'cell-receipt.json' };
        };
        const observeSaved = async (request, budget) => {
            if (!resizeState?.observer?.restored && !resizeState?.unavailable) throw new Error('Resize must restore before saved-state observations.');
            savedState ??= { workerPid: request.workerPid, tty: request.tty,
                observer: new OwnedSavedObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: 'NTILDE_SV_' + runId, expectedPaneId: cellObserver.paneId }) };
            const state = savedState;
            if (state.unavailable || state.workerPid !== request.workerPid || state.tty !== request.tty) throw new Error('Saved-state fixture ownership or availability changed.');
            if (resizeState.unavailable?.reason === 'screenDetailsUnavailable' && request.kind === 'saved-start') {
                recordSavedUnavailable('screenDetailsUnavailable');
                throw Object.assign(new Error('Saved-state fixture requires complete screen details.'), { code: 'screenDetailsUnavailable' });
            }
            try {
                const result = await state.observer.handle(request, budget);
                if (request.kind === 'saved-start') {
                    if (result.screen.rows !== cellObserver.baseline.rows || result.screen.cols !== cellObserver.baseline.cols) throw new Error('Original geometry changed before saved-state observations.');
                    proofs[0].saved.paneId = state.observer.paneId;
                    proofs[0].saved.geometry = { rows: result.screen.rows, cols: result.screen.cols };
                }
                appendFileSync(join(output, 'saved-snapshots.jsonl'), JSON.stringify(state.observer.frames.at(-1)) + '\n');
                return result;
            } catch (error) {
                if (request.kind === 'saved-start' && ['screenDetailsUnavailable', 'savedGeometryUnavailable'].includes(error.code)
                    && !state.observer.baseline && !state.observer.frames.length) recordSavedUnavailable(error.code);
                throw error;
            }
        };
        const recordMetadataUnavailable = reason => {
            const file = 'metadata-unavailable.json';
            writeJson(join(output, file), { schemaVersion: 1, runId, suiteId: metadataSuiteId,
                status: 'unavailable', reason, fixtureStarted: false, plannedExperiments: 13 });
            metadataState.unavailable = { runId, suiteId: metadataSuiteId, file, sha256: hash(readFileSync(join(output, file))),
                status: 'unavailable', reason, counts: {}, provisionalUntil: 'cell-receipt.json' };
        };
        const observeMetadata = async (request, budget) => {
            if (!savedState?.observer?.restored && !savedState?.unavailable) throw new Error('Saved-state must restore before metadata observations.');
            metadataState ??= { workerPid: request.workerPid, tty: request.tty,
                observer: new OwnedMetadataObserver({ appData, ownerPid: app.pid, profileName,
                    runId, expectedPaneId: cellObserver.paneId }) };
            const state = metadataState;
            if (state.unavailable || state.workerPid !== request.workerPid || state.tty !== request.tty) throw new Error('Metadata-state fixture ownership or availability changed.');
            if (savedState.unavailable?.reason === 'screenDetailsUnavailable' && request.kind === 'metadata-start') {
                recordMetadataUnavailable('screenDetailsUnavailable');
                throw Object.assign(new Error('Metadata-state fixture requires complete screen details.'), { code: 'screenDetailsUnavailable' });
            }
            try {
                if (request.kind === 'metadata-start') makeMetadataCases(cellObserver.baseline.rows, cellObserver.baseline.cols, runId);
                const result = await state.observer.handle(request, budget);
                if (request.kind === 'metadata-start') {
                    if (result.screen.rows !== cellObserver.baseline.rows || result.screen.cols !== cellObserver.baseline.cols) throw new Error('Original geometry changed before metadata-state observations.');
                    proofs[0].metadata.paneId = state.observer.paneId;
                    proofs[0].metadata.geometry = { rows: result.screen.rows, cols: result.screen.cols };
                }
                appendFileSync(join(output, 'metadata-snapshots.jsonl'), JSON.stringify(state.observer.frames.at(-1)) + '\n');
                return result;
            } catch (error) {
                retainFailedMetadataAttempt(output, request, error, state.observer);
                if (initialMetadataRefusal(request, error, state.observer)) recordMetadataUnavailable(error.code);
                throw metadataFailure(request, error, state.observer);
            }
        };
        let osc133State;
        const recordOsc133Unavailable = reason => {
            writeJson(join(output, 'shi-unavailable.json'), { schemaVersion: 1, runId, suiteId: osc133SuiteId, status: 'unavailable', reason, plannedCases: 4 });
            osc133State.unavailable = true;
        };
        /** The OSC 133 suite reuses the paired metadata/status observer under its own prefix and plan; it runs after the left/right-margin suite. */
        const observeOsc133 = async (request, budget) => {
            if (!lrMarginsObserver?.restored) throw new Error('Left/right-margin suite must restore before OSC 133 observations.');
            osc133State ??= { workerPid: request.workerPid, tty: request.tty,
                observer: new OwnedMetadataObserver({ appData, ownerPid: app.pid, profileName, runId, expectedPaneId: cellObserver.paneId,
                    prefix: osc133Prefix, includeResolvedColors: true, planOf: osc133ObserverPlan, matchesBaseline: matchesOsc133Baseline }) };
            const state = osc133State;
            if (state.unavailable || state.workerPid !== request.workerPid || state.tty !== request.tty) throw new Error('OSC 133 fixture ownership or availability changed.');
            if (request.kind === `${osc133Prefix}-start`) {
                const { rows, cols } = cellObserver.baseline;
                const reason = metadataState?.unavailable?.reason ?? savedState?.unavailable?.reason
                    ?? (rows < osc133Minimum.rows || cols < osc133Minimum.cols ? 'metadataGeometryUnavailable' : null);
                if (reason !== null && !['screenDetailsUnavailable', 'paneMetadataUnavailable', 'metadataGeometryUnavailable'].includes(reason)) throw new Error('Unexpected OSC 133 unavailability reason.');
                if (reason !== null) {
                    recordOsc133Unavailable(reason);
                    throw Object.assign(new Error('OSC 133 fixture is unavailable.'), { code: reason });
                }
            }
            try {
                const result = await state.observer.handle(request, budget);
                if (request.kind === `${osc133Prefix}-start` && (result.screen.rows !== cellObserver.baseline.rows || result.screen.cols !== cellObserver.baseline.cols)) {
                    throw new Error('Original geometry changed before OSC 133 observations.');
                }
                appendFileSync(join(output, 'shi-snapshots.jsonl'), JSON.stringify(state.observer.frames.at(-1)) + '\n');
                return result;
            } catch (error) {
                retainFailedMetadataAttempt(output, request, error, state.observer, 'shi-failed-observation.json');
                if (initialMetadataRefusal(request, error, state.observer)) recordOsc133Unavailable(error.code);
                throw metadataFailure(request, error, state.observer);
            }
        };
        /** Route owned observations serially, preserving suite restoration and pane identity dependencies. */
        const recordInputUnavailable = reason => {
            const file = 'input-unavailable.json';
            writeJson(join(output, file), { schemaVersion: 1, runId, suiteId: inputSuiteId, status: 'unavailable',
                reason, fixtureStarted: false, plannedExperiments: currentInputPlan.make(24, 100).length });
            inputState.unavailable = { runId, suiteId: inputSuiteId, file, sha256: hash(readFileSync(join(output, file))),
                status: 'unavailable', reason, counts: {} };
        };
        const observeInput = async (request, budget) => {
            if (!metadataState?.observer?.restored && !metadataState?.unavailable) throw new Error('Metadata must restore before input observations.');
            if (!inputState) {
                inputState = { workerPid: request.workerPid, tty: request.tty, recordedFrames: 0, recordedActions: 0 };
                inputState.observer = new OwnedInputObserver({ appData, ownerPid: app.pid, profileName, plan: currentInputPlan,
                    nonce: 'NTILDE_IN_' + runId, expectedPaneId: cellObserver.paneId,
                    performInput: (event, actionBudget) => generateOwnedInput({ app, appData, executable, image, profileName, runId,
                        paneId: inputState.observer.paneId, uid: process.getuid(),
                        workerPid: inputState.workerPid, workerIdentity, tty: inputState.tty }, event, actionBudget) });
                proofs[0].input.actions = inputState.observer.actions;
            }
            const state = inputState;
            if (state.unavailable || state.workerPid !== request.workerPid || state.tty !== request.tty) throw new Error('Input fixture ownership or availability changed.');
            try {
                const result = await state.observer.handle(request, budget);
                if (request.kind === 'input-start') {
                    if (result.screen.rows !== cellObserver.baseline.rows || result.screen.cols !== cellObserver.baseline.cols) throw new Error('Original geometry changed before input observations.');
                    proofs[0].input.paneId = state.observer.paneId;
                    proofs[0].input.geometry = state.observer.geometry;
                }
                if (request.kind === 'input-seal') proofs[0].input.seal = state.observer.seal;
                return result;
            } catch (error) {
                const initial = request.kind === 'input-start' && ['screenDetailsUnavailable', 'inputGeometryUnavailable'].includes(error.code)
                    && !state.observer.baseline && !state.observer.frames.length && !state.observer.actions.length;
                if (initial) recordInputUnavailable(error.code);
                else if (state.observer.lastAttempt) writeJson(join(output, 'input-failed-observation.json'), state.observer.lastAttempt);
                throw error;
            } finally {
                for (const frame of state.observer.frames.slice(state.recordedFrames)) appendFileSync(join(output, 'input-snapshots.jsonl'), JSON.stringify(frame) + '\n');
                state.recordedFrames = state.observer.frames.length;
                for (const action of state.observer.actions.slice(state.recordedActions)) appendFileSync(join(output, 'input-actions.jsonl'), JSON.stringify(action) + '\n');
                state.recordedActions = state.observer.actions.length;
            }
        };
        const observeImages = async (request, budget, ownership) => {
            requireInputCompletion(inputState);
            if (!staticImageState) {
                const priorChallenges = inputState.observer?.frames.filter(frame => frame.capture).map(frame => frame.capture.nonces.join(',')) ?? [];
                staticImageState = { workerPid: request.workerPid, tty: request.tty, recordedFrames: 0, recordedActions: 0,
                    observer: new OwnedStaticImageObserver({ appData, ownerPid: app.pid, profileName, output,
                        nonce: 'NTILDE_IMG_' + runId, expectedPaneId: cellObserver.paneId, priorChallenges }) };
                proofs[0].staticImages.actions = staticImageState.observer.actions;
            }
            const state = staticImageState;
            if (state.unavailable || state.workerPid !== request.workerPid || state.tty !== request.tty) throw new Error('Image fixture ownership changed.');
            try {
                const result = await state.observer.handle(request, budget, ownership);
                if (request.kind === 'images-start') {
                    if (result.screen.rows !== cellObserver.baseline.rows || result.screen.cols !== cellObserver.baseline.cols) throw new Error('Image fixture geometry changed.');
                    proofs[0].staticImages.paneId = state.observer.paneId;
                    proofs[0].staticImages.geometry = { rows: result.screen.rows, cols: result.screen.cols };
                }
                if (request.kind === 'images-seal') proofs[0].staticImages.seal = state.observer.seal;
                return result;
            } catch (error) {
                const initial = request.kind === 'images-start' && ['screenDetailsUnavailable', 'imageGeometryUnavailable'].includes(error.code)
                    && !state.observer.baseline && !state.observer.frames.length && !state.observer.actions.length
                    && !existsSync(join(output, 'image-pixels')) && !existsSync(join(output, 'image-inputs'));
                if (initial) {
                    const file = 'image-unavailable.json';
                    writeJson(join(output, file), { schemaVersion: 1, runId, suiteId: staticImageSuiteId, status: 'unavailable',
                        reason: error.code, fixtureStarted: false, plannedExperiments: 5, plannedStages: 23 });
                    state.unavailable = { runId, suiteId: staticImageSuiteId, file, sha256: hash(readFileSync(join(output, file))),
                        status: 'unavailable', reason: error.code, counts: {} };
                } else if (state.observer.lastAttempt) writeJson(join(output, 'image-failed-observation.json'), state.observer.lastAttempt);
                throw error;
            } finally {
                for (const frame of state.observer.frames.slice(state.recordedFrames)) appendFileSync(join(output, 'image-snapshots.jsonl'), JSON.stringify(frame) + '\n');
                state.recordedFrames = state.observer.frames.length;
                for (const action of state.observer.actions.slice(state.recordedActions)) appendFileSync(join(output, 'image-actions.jsonl'), JSON.stringify(action) + '\n');
                state.recordedActions = state.observer.actions.length;
            }
        };
        const attestQuery = (state, budget) => {
            if (!outputModeVerified || !app?.pid || app.exitCode !== null || app.signalCode !== null)
                throw new Error('Owned query App/output proof is no longer live.');
            const owner = processRecord(app.pid, budget()), worker = processRecord(state.workerPid, budget());
            const appCommand = execFileSync('/bin/ps', ['-p', String(app.pid), '-o', 'command='], { encoding: 'utf8', timeout: budget(), maxBuffer: 16384 });
            const workerCommand = execFileSync('/bin/ps', ['-p', String(worker.pid), '-o', 'command='], { encoding: 'utf8', timeout: budget(), maxBuffer: 16384 });
            assertProcessOwnership({ app: { ...owner, executable: realpathSync(owner.executable) }, worker,
                ownerPid: app.pid, executable, uid: process.getuid(), runId, tty: state.tty, appCommand });
            if (!matchesRecordedWorker(worker, workerIdentity, workerCommand, runId)) throw new Error('Recorded query worker identity or marker changed.');
            return { app: owner, worker, tty: state.tty, appCommand, workerCommand,
                terminalOutput: readOwnedOutputMode(state.tty, budget()), at: new Date().toISOString() };
        };
        const retainQueryJournal = () => {
            if (!queryState?.observer) return;
            const journal = queryState.observer.journal;
            if (journal.initial?.after && !queryState.initialRecorded) {
                writeJson(join(output, 'query-initial.json'), journal.initial); queryState.initialRecorded = true;
            }
            for (const frame of journal.frames.slice(queryState.recordedFrames)) appendFileSync(join(output, 'query-snapshots.jsonl'), JSON.stringify(frame) + '\n');
            queryState.recordedFrames = journal.frames.length;
            for (const action of journal.actions.slice(queryState.recordedActions)) appendFileSync(join(output, 'query-actions.jsonl'), JSON.stringify(action) + '\n');
            queryState.recordedActions = journal.actions.length;
            if (journal.failed && !existsSync(join(output, 'query-failed-observation.json'))) writeJson(join(output, 'query-failed-observation.json'), journal);
        };
        const observeQueries = async (request, budget, owner, worker) => {
            try {
                const payload = { ...request };
                for (const key of ['token', 'runId', 'workerPid', 'tty', 'barrier']) delete payload[key];
                if (request.barrier !== null) throw new Error('Query barriers are retained only in their raw capture.');
                requireInputCompletion(inputState); requireStaticImageCompletion(staticImageState);
                if (inputState.unavailable || staticImageState.unavailable) {
                    if (queryState || request.kind !== 'queries-start' || Object.keys(payload).length !== 1) throw new Error('Query prerequisite refusal must precede all fixture actions.');
                    const file = 'query-unavailable.json';
                    writeJson(join(output, file), { schemaVersion: 1, runId, suiteId: querySuiteId, status: 'unavailable',
                        reason: 'queryPrerequisiteUnavailable', fixtureStarted: false, plannedActions: 44, plannedContracts: 40,
                        observedActions: 0, observedContracts: 0, observedFrames: 0,
                        prerequisites: [['inputEvidence', inputState.unavailable], ['staticImageEvidence', staticImageState.unavailable]]
                            .filter(([, value]) => value).map(([bindingKey, value]) => ({ bindingKey, ...value })) });
                    const unavailable = { runId, suiteId: querySuiteId, file, sha256: hash(readFileSync(join(output, file))),
                        status: 'unavailable', reason: 'queryPrerequisiteUnavailable', counts: {} };
                    queryState = { unavailable };
                    return { status: unavailable.status, reason: unavailable.reason };
                }
                if (!queryState) {
                    if (request.kind !== 'queries-start') throw new Error('Query fixture must start before admission.');
                    const priorChallenges = [...inputState.observer.frames, ...staticImageState.observer.frames]
                        .filter(frame => frame.capture).map(frame => frame.capture.nonces.join(','));
                    if (!queryPriorCounts.includes(priorChallenges.length)) throw new Error('Incomplete earlier query challenges.');
                    queryState = { workerPid: worker.pid, tty: request.tty, recordedFrames: 0, recordedActions: 0 };
                    const binding = { runId, ownerPid: owner.pid, workerPid: worker.pid, uid: worker.uid,
                        appExecutable: owner.executable, workerExecutable: worker.executable, tty: request.tty, profileName, paneId: cellObserver.paneId };
                    queryState.observer = new OwnedQueryObserver({ binding, appData, priorChallenges, checksumReport: settings.AllowRectangleChecksumReport === true, marginsReport: true, attest: remaining => attestQuery(queryState, remaining) });
                    proofs[0].queries.binding = binding;
                }
                if (queryState.workerPid !== request.workerPid || queryState.tty !== request.tty) throw new Error('Query fixture ownership changed.');
                const result = await queryState.observer.handle(payload, budget);
                const journal = queryState.observer.journal;
                proofs[0].queries.actions = journal.actions;
                if (journal.seal) proofs[0].queries.seal = journal.seal;
                return result;
            } catch (error) { queryState?.observer?.abort(error); throw error; }
            finally { retainQueryJournal(); }
        };
        /** Persist expected-only controller/state facts and bounded failures, without the owner capability. */
        const retainPrivateClipboardJournal = () => {
            if (!privateClipboardState?.observer) return;
            // The expected-only journal contains synthetic seed/nonce values, never arbitrary clipboard data.
            writeFileSync(join(output, 'private-clipboard-journal.json'), privateClipboardEvidenceJson({ ...privateClipboardState.observer.journal, ...(privateClipboardState.failures?.length ? { failures: privateClipboardState.failures, failureFactsTruncated: privateClipboardState.failureFactsTruncated === true } : {}) }), { mode: 0o600 });
        };
        /** Independently reread live process, source, binary, PTY and birth facts inside one shared event budget. */
        const attestPrivateClipboard = (state, budget) => {
            const fact = attestQuery(state, budget);
            assertExecutableUnchanged(executable, image);
            assertOwnedSourcesUnchanged(sourceRepo, context); budget();
            const executableSha256 = hash(readFileSync(executable)); budget();
            const assemblySha256 = context.assemblySha256 === null ? null : hash(readFileSync(join(dirname(executable), 'Ntilde.dll'))); budget();
            return { ...fact, app: { ...fact.app, birth: processBirth(fact.app.pid, budget()) },
                worker: { ...fact.worker, birth: processBirth(fact.worker.pid, budget()) },
                executableSha256, assemblySha256, sourceSnapshotManifestSha256: snapshot.sha256, outputDevice: outputDevice(state.tty) };
        };
        /** Lazily admit Q11 only after the query seal; route the fixed parent API and retain only a closed full report. */
        const observePrivateClipboard = async (request, budget, owner, worker) => {
            try {
                if (Object.keys(request).length !== 6 || !Object.hasOwn(request, 'frame')) throw new Error('Invalid private clipboard owner envelope.');
                requireQueryCompletion(queryState);
                if (queryState.unavailable) throw new Error('Private clipboard requires all sixteen completed prefix suites.');
                if (!privateClipboardState) {
                    if (request.frame?.kind !== 'start') throw new Error('Private clipboard must start at its original Q11 callback.');
                    const first = proofs[0].privateClipboard;
                    privateClipboardState = { workerPid: worker.pid, tty: request.tty };
                    const binding = { runId, ownerPid: owner.pid, workerPid: worker.pid, uid: worker.uid,
                        appExecutable: owner.executable, workerExecutable: worker.executable, tty: request.tty, profileName, paneId: cellObserver.paneId,
                        sourceSnapshotManifestSha256: snapshot.sha256, executableSha256: context.executableSha256, assemblySha256: context.assemblySha256,
                        appBirth: first.appBirth, workerBirth: first.workerBirth, outputDevice: first.outputDevice };
                    const reader = new OwnedCellObserver({ appData, ownerPid: owner.pid, profileName, expectedPaneId: binding.paneId,
                        includeCellDetails: true, includeCursorMode: true, includeOverline: true, includeBufferState: true });
                    privateClipboardState.observer = new OwnedPrivateClipboardObserver({ binding, appData,
                        attest: remaining => attestPrivateClipboard(privateClipboardState, remaining),
                        capture: async remaining => privateClipboardScreen(await reader.capture(remaining)) });
                    first.binding = binding;
                }
                if (privateClipboardState.workerPid !== worker.pid || privateClipboardState.tty !== request.tty) throw new Error('Private clipboard worker changed.');
                const result = await privateClipboardState.observer.handle(request.frame, budget);
                if (request.frame.kind !== 'finish') return result;
                const report = privateClipboardState.observer.report, file = 'private-clipboard-observations.json';
                writeFileSync(join(output, file), privateClipboardEvidenceJson(report), { mode: 0o600 });
                privateClipboardEvidence = { runId, suiteId: ownedPrivateClipboardSuiteId, file,
                    sha256: hash(readFileSync(join(output, file))), counts: { completedProbes: 3, acceptedWrites: 2 }, complete: true };
                return privateClipboardEvidence;
            } catch (error) { rejectDone(error); throw error; }
            finally { retainPrivateClipboardJournal(); }
        };
        const observeCells = async (request, budget) => {
            const prefix = request.kind.split('-')[0];
            if (prefix === 'input') return observeInput(request, budget);
            if (prefix === 'resize') return observeResize(request, budget);
            if (prefix === 'saved') return observeSaved(request, budget);
            if (prefix === 'metadata') return observeMetadata(request, budget);
            if (prefix === osc133Prefix) return observeOsc133(request, budget);
            if (prefix === 'pixel') return observePixels(request, budget);
            if (prefix === 'color') return observeColors(request, budget);
            if (Object.hasOwn(editingSpecs, prefix)) return observeEditing(prefix, request, budget);
            if (Object.hasOwn(detailSpecs, prefix)) return observeDetails(prefix, request, budget);
            if (prefix === 'grading') {
                // The graded suite runs after the attribute-reading SGR suite.
                if (!sgrObserver?.restored) throw new Error('SGR suite must restore before grading observations.');
                gradingObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_AG_${runId}`, includeAttributes: true, prefix: 'grading', expectedPaneId: cellObserver.paneId });
                const result = await gradingObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'grading-snapshots.jsonl'), JSON.stringify(gradingObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'sgcolors') {
                // The layered suite runs after the first graded suite.
                if (!gradingObserver?.restored) throw new Error('Grading suite must restore before SGR-colors observations.');
                sgrColorsObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_SC_${runId}`, includeAttributes: true, prefix: 'sgcolors', expectedPaneId: cellObserver.paneId });
                const result = await sgrColorsObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'sgcolors-snapshots.jsonl'), JSON.stringify(sgrColorsObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'sgrgb') {
                // The RGB suite runs after the SGR-colors suite: its conclusions sit
                // beside the stored-index ones, never replace them.
                if (!sgrColorsObserver?.restored) throw new Error('SGR-colors suite must restore before SGR-RGB observations.');
                sgrRgbObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_SR_${runId}`, includeAttributes: true, includeCellDetails: true,
                    includeResolvedColors: true, prefix: 'sgrgb', expectedPaneId: cellObserver.paneId });
                const result = await sgrRgbObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'sgrgb-snapshots.jsonl'), JSON.stringify(sgrRgbObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'sgattr') {
                // The attributes suite runs after the SGR-RGB suite: it adds stored
                // attribute conclusions beside the color ones, never replaces them.
                if (!sgrRgbObserver?.restored) throw new Error('SGR-RGB suite must restore before SGR-attribute observations.');
                sgrAttrsObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_SA_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeResolvedColors: true, prefix: 'sgattr', expectedPaneId: cellObserver.paneId });
                const result = await sgrAttrsObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'sgattr-snapshots.jsonl'), JSON.stringify(sgrAttrsObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'sgpal') {
                // The palette suite runs after the SGR-attributes suite: it adds stored and
                // resolved palette conclusions beside the earlier ones, never replaces them.
                if (!sgrAttrsObserver?.restored) throw new Error('SGR-attribute suite must restore before SGR-palette observations.');
                sgrPaletteObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_SP_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeResolvedColors: true, prefix: 'sgpal', expectedPaneId: cellObserver.paneId });
                const result = await sgrPaletteObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'sgpal-snapshots.jsonl'), JSON.stringify(sgrPaletteObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'sgtc') {
                // The truecolor suite runs after the SGR-palette suite and adds its conclusions beside the earlier ones.
                if (!sgrPaletteObserver?.restored) throw new Error('SGR-palette suite must restore before SGR-truecolor observations.');
                sgrTruecolorObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_ST_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeResolvedColors: true, prefix: 'sgtc', expectedPaneId: cellObserver.paneId });
                const result = await sgrTruecolorObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'sgtc-snapshots.jsonl'), JSON.stringify(sgrTruecolorObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'sgtf') {
                // The finding suite runs after the SGR-truecolor suite and adds its conclusions beside the earlier ones.
                if (!sgrTruecolorObserver?.restored) throw new Error('SGR-truecolor suite must restore before truecolor-finding observations.');
                truecolorFindingObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_TF_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeResolvedColors: true, prefix: 'sgtf', expectedPaneId: cellObserver.paneId });
                const result = await truecolorFindingObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'sgtf-snapshots.jsonl'), JSON.stringify(truecolorFindingObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'sgus') {
                // The underline-style suite runs after the truecolor-finding suite and adds its conclusions beside the earlier ones.
                if (!truecolorFindingObserver?.restored) throw new Error('Truecolor-finding suite must restore before SGR-underline observations.');
                sgrUnderlineObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_SU_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeResolvedColors: true, prefix: 'sgus', expectedPaneId: cellObserver.paneId });
                const result = await sgrUnderlineObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'sgus-snapshots.jsonl'), JSON.stringify(sgrUnderlineObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'sguc') {
                // The underline-color suite runs after the underline-style suite and adds its conclusions beside the earlier ones.
                if (!sgrUnderlineObserver?.restored) throw new Error('SGR-underline suite must restore before SGR-underline-color observations.');
                sgrUnderlineColorObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_UC_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeResolvedColors: true, prefix: 'sguc', expectedPaneId: cellObserver.paneId });
                const result = await sgrUnderlineColorObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'sguc-snapshots.jsonl'), JSON.stringify(sgrUnderlineColorObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'sgur') {
                // The App-observed underline-resolution suite runs after the underline-color suite and adds its conclusions beside the earlier ones.
                if (!sgrUnderlineColorObserver?.restored) throw new Error('SGR-underline-color suite must restore before underline-resolution observations.');
                underlineResolvedObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_UR_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeResolvedColors: true, includeResolvedUnderlineColors: true, prefix: 'sgur', expectedPaneId: cellObserver.paneId });
                const result = await underlineResolvedObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'sgur-snapshots.jsonl'), JSON.stringify(underlineResolvedObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'sgsr') {
                // The selective-reset suite runs after the underline-resolution suite and adds its conclusions beside the earlier ones.
                if (!underlineResolvedObserver?.restored) throw new Error('Underline-resolution suite must restore before SGR selective-reset observations.');
                sgrResetObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_SR_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeResolvedColors: true, prefix: 'sgsr', expectedPaneId: cellObserver.paneId });
                const result = await sgrResetObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'sgsr-snapshots.jsonl'), JSON.stringify(sgrResetObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'sgcl') {
                // The closure suite runs after the selective-reset suite and adds its conclusions beside the earlier ones.
                if (!sgrResetObserver?.restored) throw new Error('SGR selective-reset suite must restore before SGR closure observations.');
                sgrClosureObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_CL_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeResolvedColors: true, prefix: 'sgcl', expectedPaneId: cellObserver.paneId });
                const result = await sgrClosureObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'sgcl-snapshots.jsonl'), JSON.stringify(sgrClosureObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'erln') {
                // The erase-in-line suite runs after the SGR closure suite and adds its conclusions beside the earlier ones.
                if (!sgrClosureObserver?.restored) throw new Error('SGR SGR closure suite must restore before erase-in-line observations.');
                eraseLineObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_EL_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeResolvedColors: true, prefix: 'erln', expectedPaneId: cellObserver.paneId });
                const result = await eraseLineObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'erln-snapshots.jsonl'), JSON.stringify(eraseLineObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'ched') {
                // The character-editing suite runs after the erase-in-line suite and adds its conclusions beside the earlier ones.
                if (!eraseLineObserver?.restored) throw new Error('Erase-in-line suite must restore before character-editing observations.');
                charEditObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_CE_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeResolvedColors: true, prefix: 'ched', expectedPaneId: cellObserver.paneId });
                const result = await charEditObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'ched-snapshots.jsonl'), JSON.stringify(charEditObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'erdi') {
                // The erase-in-display suite runs after the character-editing suite and adds its conclusions beside the earlier ones.
                if (!charEditObserver?.restored) throw new Error('Character-editing suite must restore before erase-in-display observations.');
                eraseDisplayObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_ED_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeBufferState: true, includeResolvedColors: true, prefix: 'erdi', expectedPaneId: cellObserver.paneId });
                const result = await eraseDisplayObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'erdi-snapshots.jsonl'), JSON.stringify(eraseDisplayObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'ledt') {
                // The line-editing suite runs after the erase-in-display suite and adds its conclusions beside the earlier ones.
                if (!eraseDisplayObserver?.restored) throw new Error('The erase-in-display suite must restore before line-editing observations.');
                lineEditObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_LE_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeResolvedColors: true, includeBufferState: true, prefix: 'ledt', expectedPaneId: cellObserver.paneId });
                const result = await lineEditObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'ledt-snapshots.jsonl'), JSON.stringify(lineEditObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'chrs') {
                // The charsets suite runs after the line-editing suite. It requests stored cell details only: attribute
                // strings omit wide-continuation cells and would refuse any row holding a wide character.
                if (!lineEditObserver?.restored) throw new Error('Line-editing suite must restore before charset observations.');
                charsetsObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_CS_${runId}`, includeCellDetails: true, prefix: 'chrs', expectedPaneId: cellObserver.paneId });
                const result = await charsetsObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'chrs-snapshots.jsonl'), JSON.stringify(charsetsObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'scrl') {
                // The scrolling/history suite runs after the charsets suite on the main screen and reads buffer state and actual history.
                if (!charsetsObserver?.restored) throw new Error('Charsets suite must restore before scrolling/history observations.');
                scrollbackObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_SB_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeResolvedColors: true, includeBufferState: true, prefix: 'scrl', expectedPaneId: cellObserver.paneId });
                const result = await scrollbackObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'scrl-snapshots.jsonl'), JSON.stringify(scrollbackObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'svrs') {
                // The saved-state/reset suite runs after the scrolling/history suite and adds its conclusions beside the earlier ones.
                if (!scrollbackObserver?.restored) throw new Error('Scrolling/history suite must restore before saved-state/reset observations.');
                savedResetObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_SR_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeBufferState: true, includeResolvedColors: true, prefix: 'svrs', expectedPaneId: cellObserver.paneId });
                const result = await savedResetObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'svrs-snapshots.jsonl'), JSON.stringify(savedResetObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'erar') {
                // The erase attributes / erase margins suite runs after the saved-state/reset suite and adds its conclusions beside the earlier ones.
                if (!savedResetObserver?.restored) throw new Error('Saved-state/reset suite must restore before erase-attributes observations.');
                eraseAttrsRegionObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_EA_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeBufferState: true, includeResolvedColors: true, prefix: 'erar', expectedPaneId: cellObserver.paneId });
                const result = await eraseAttrsRegionObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'erar-snapshots.jsonl'), JSON.stringify(eraseAttrsRegionObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'atrc') {
                // The attribute-rectangle suite runs after the erase attributes / erase margins suite and adds its conclusions beside the earlier ones.
                if (!eraseAttrsRegionObserver?.restored) throw new Error('Erase attributes / erase margins suite must restore before attribute-rectangle observations.');
                attrRectObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_AR_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeBufferState: true, includeResolvedColors: true, prefix: 'atrc', expectedPaneId: cellObserver.paneId });
                const result = await attrRectObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'atrc-snapshots.jsonl'), JSON.stringify(attrRectObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'curq') {
                // The cursor suite runs after the attribute-rectangle suite on the alternate screen and adds its conclusions beside the earlier ones.
                if (!attrRectObserver?.restored) throw new Error('Attribute-rectangle suite must restore before cursor observations.');
                cursorQualObserver ??= new CursorQualObserver({ output, appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_CQ_${runId}`, expectedPaneId: cellObserver.paneId });
                const result = await cursorQualObserver.handle(request, budget);
                appendFileSync(join(output, 'curq-snapshots.jsonl'), JSON.stringify(cursorQualObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'echh') {
                // The erase-character/saved-lines suite runs after the cursor-qualification suite and adds its conclusions beside the earlier ones.
                if (!cursorQualObserver?.restored) throw new Error('Cursor-qualification suite must restore before erase-character/saved-lines observations.');
                eraseCharsHistoryObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_EH_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeBufferState: true, includeResolvedColors: true, prefix: 'echh', expectedPaneId: cellObserver.paneId });
                const result = await eraseCharsHistoryObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'echh-snapshots.jsonl'), JSON.stringify(eraseCharsHistoryObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'sels') {
                // The selective-erasure suite runs after the erase-character/saved-lines suite and adds its conclusions beside the earlier ones.
                if (!eraseCharsHistoryObserver?.restored) throw new Error('Erase-character/saved-lines suite must restore before selective-erasure observations.');
                selectiveEraseObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_SE_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeBufferState: true, includeResolvedColors: true, includeProtection: true, prefix: 'sels', expectedPaneId: cellObserver.paneId });
                const result = await selectiveEraseObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'sels-snapshots.jsonl'), JSON.stringify(selectiveEraseObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'rect') {
                // The rectangle-editing suite runs after the selective-erasure suite and adds its conclusions beside the earlier ones.
                if (!selectiveEraseObserver?.restored) throw new Error('Selective-erasure suite must restore before rectangle-editing observations.');
                rectEditObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_RC_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeBufferState: true, includeResolvedColors: true, prefix: 'rect', expectedPaneId: cellObserver.paneId });
                const result = await rectEditObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'rect-snapshots.jsonl'), JSON.stringify(rectEditObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'cole') {
                // The column-editing suite runs after the rectangle-editing suite and adds its conclusions beside the earlier ones.
                if (!rectEditObserver?.restored) throw new Error('Rectangle-editing suite must restore before column-editing observations.');
                colEditObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_CE_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeBufferState: true, includeResolvedColors: true, includeProtection: true, prefix: 'cole', expectedPaneId: cellObserver.paneId });
                const result = await colEditObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'cole-snapshots.jsonl'), JSON.stringify(colEditObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'csum') {
                // The rectangle-checksum suite runs after the column-editing suite; a report read also journals the raw reply bound to its barrier.
                if (!colEditObserver?.restored) throw new Error('Column-editing suite must restore before rectangle-checksum observations.');
                rectChecksumObserver ??= new RectChecksumObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_CK_${runId}`, expectedPaneId: cellObserver.paneId });
                const result = await rectChecksumObserver.handle(request, budget);
                appendFileSync(join(output, 'csum-snapshots.jsonl'), JSON.stringify(rectChecksumObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'cw') {
                if (!coreEraseObserver?.restored) throw new Error('Core erase must restore before Core wrap.');
                if (!coreWrapState) {
                    if (request.kind !== 'cw-geometry-start') throw new Error('Core wrap must start at original geometry.');
                    coreWrapState = { workerPid: request.workerPid, tty: request.tty, recordedFrames: 0 };
                    coreWrapState.observer = new WrapObserver({ appData, ownerPid: app.pid, profileName, runId,
                        expectedPaneId: cellObserver.paneId, expectedOriginal: { rows: cellObserver.baseline.rows, cols: cellObserver.baseline.cols },
                        performResize: (rows, cols, actionBudget) => resizeOwnedPane({ app, appData, executable, runId,
                            paneId: coreWrapState.observer.paneId, uid: process.getuid(),
                            workerPid: coreWrapState.workerPid, tty: coreWrapState.tty }, rows, cols, actionBudget) });
                }
                const state = coreWrapState;
                if (state.workerPid !== request.workerPid || state.tty !== request.tty) throw new Error('Core wrap worker changed.');
                try {
                    const result = await state.observer.handle(request, budget);
                    if (request.kind === 'cw-seal') {
                        const o = state.observer;
                        const geometry = { paneId: o.paneId, original: o.original, before: o.before, after: o.after, actions: o.actions };
                        proofs[0].coreWrap = geometry; writeJson(join(output, 'cw-geometry.json'), geometry);
                    }
                    return result;
                } finally {
                    for (const frame of state.observer.frames.slice(state.recordedFrames)) {
                        appendFileSync(join(output, 'cw-snapshots.jsonl'), JSON.stringify(frame) + '\n'); state.recordedFrames++;
                    }
                    writeFileSync(join(output, 'cw-progress.json'), JSON.stringify({ phase: state.observer.phase, actions: state.observer.actions,
                        before: state.observer.before, after: state.observer.after, lastAttempt: state.observer.reader.lastAttempt }) + '\n', { mode: 0o600 });
                }
            }
            if (prefix === 'ce') {
                if (!textCursorObserver?.restored) throw new Error('Text/cursor suite must restore before Core erase.');
                coreEraseObserver ??= new EraseObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_CE_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeBufferState: true, includeResolvedColors: true, includeHorizontalMargins: true,
                    prefix: 'ce', expectedPaneId: cellObserver.paneId });
                try {
                    const result = await coreEraseObserver.handleRequest(request, budget);
                    appendFileSync(join(output, 'ce-snapshots.jsonl'), JSON.stringify(coreEraseObserver.frames.at(-1)) + '\n');
                    return result;
                } catch (error) {
                    writeFileSync(join(output, 'ce-failed-observation.json'), JSON.stringify({ attempt: coreEraseObserver.lastAttempt, error: String(error) }) + '\n');
                    throw error;
                }
            }
            if (prefix === 'tc') {
                if (!coreGapsObserver?.restored || !colorResetObserver?.restored) throw new Error('Core gaps and color reset must restore before text/cursor observations.');
                textCursorObserver ??= new TextCursorObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_TC_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeBufferState: true, includeResolvedColors: true, includeHorizontalMargins: true,
                    prefix: 'tc', expectedPaneId: cellObserver.paneId });
                try {
                    const result = await textCursorObserver.handleRequest(request, budget);
                    appendFileSync(join(output, 'tc-snapshots.jsonl'), JSON.stringify(textCursorObserver.frames.at(-1)) + '\n');
                    return result;
                } catch (error) {
                    writeFileSync(join(output, 'tc-failed-observation.json'), JSON.stringify({ attempt: textCursorObserver.lastAttempt, error: String(error) }) + '\n');
                    throw error;
                }
            }
            if (prefix === 'cg') {
                if (!lrMarginsObserver?.restored) throw new Error('Margins suite must restore before Core gaps.');
                coreGapsObserver ??= new CoreGapsObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_CG_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeBufferState: true, includeResolvedColors: true, prefix: 'cg', expectedPaneId: cellObserver.paneId });
                try {
                    const result = await coreGapsObserver.handle(request.kind, budget, request.barrier);
                    appendFileSync(join(output, 'cg-snapshots.jsonl'), JSON.stringify(coreGapsObserver.frames.at(-1)) + '\n');
                    return result;
                } catch (error) {
                    writeFileSync(join(output, 'cg-failed-observation.json'), JSON.stringify({ attempt: coreGapsObserver.lastAttempt, error: String(error) }) + '\n');
                    throw error;
                }
            }
            if (prefix === 'lrmm') {
                // The left/right-margin suite runs after the rectangle-checksum suite; its reads also keep the App's stored horizontal margins.
                if (!rectChecksumObserver?.restored) throw new Error('Rectangle-checksum suite must restore before left/right-margin observations.');
                lrMarginsObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_LM_${runId}`, includeAttributes: true, includeCellDetails: true, includeOverline: true,
                    includeBufferState: true, includeResolvedColors: true, includeProtection: true, includeHorizontalMargins: true, prefix: 'lrmm', expectedPaneId: cellObserver.paneId });
                const result = await lrMarginsObserver.handle(request.kind, budget, request.barrier);
                appendFileSync(join(output, 'lrmm-snapshots.jsonl'), JSON.stringify(lrMarginsObserver.frames.at(-1)) + '\n');
                return result;
            }
            if (prefix === 'crst') {
                // The runtime-color reset suite runs after the left/right-margin suite; its reads also bracket a controlled render and carry the exact query replies.
                if (!lrMarginsObserver?.restored) throw new Error('Left/right-margin suite must restore before runtime-color reset observations.');
                colorResetObserver ??= new ColorResetObserver({ output, appData, ownerPid: app.pid, profileName,
                    nonce: `NTILDE_CR_${runId}`, expectedPaneId: cellObserver.paneId });
                const result = await colorResetObserver.handle(request, budget);
                appendFileSync(join(output, 'crst-snapshots.jsonl'), JSON.stringify(colorResetObserver.frames.at(-1)) + '\n');
                return result;
            }
            const attributes = request.kind.startsWith('sgr-');
            if (attributes && !cellObserver?.restored) throw new Error('ASCII suite must restore before SGR observations.');
            if (attributes) sgrObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName,
                nonce: `NTILDE_SG_${runId}`, includeAttributes: true, prefix: 'sgr', expectedPaneId: cellObserver.paneId });
            else cellObserver ??= new OwnedCellObserver({ appData, ownerPid: app.pid, profileName, nonce: config.cellNonce });
            const observer = attributes ? sgrObserver : cellObserver;
            const result = await observer.handle(request.kind, budget, request.barrier);
            appendFileSync(join(output, attributes ? 'sgr-snapshots.jsonl' : 'cell-snapshots.jsonl'), JSON.stringify(observer.frames.at(-1)) + '\n');
            return result;
        };
        /** Reject observations until the parent has independently proved untranslated output. */
        const observeUntranslatedCells = (request, budget) => {
            if (!outputModeVerified) throw new Error('Private PTY output mode has not been verified.');
            return observeCells(request, budget);
        };
        /** Retain the first measured lineage and PTY without storing the run capability. */
        const retainFirstProof = (owner, worker, tty, budget) => {
            if (proofs.length !== 0) return;
            proofs.push({ privateClipboard: { policy: ownedPrivateClipboardPolicy, appBirth: processBirth(owner.pid, budget()),
                workerBirth: processBirth(worker.pid, budget()), outputDevice: outputDevice(tty), originalTerminalState: readOwnedTerminalState(tty, budget()) }, app: owner, worker, tty, at: new Date().toISOString(), resize: { policy: ownedResizePolicy, actions: [] }, saved: { policy: ownedSavedPolicy }, metadata: { policy: ownedMetadataPolicy }, input: { policy: ownedInputPolicy, actions: [] }, staticImages: { policy: ownedStaticImagePolicy, actions: [] }, queries: { policy: ownedQueryPolicy, actions: [] } });
        };
        /** Bind one kernel-observed output-mode proof to the already verified worker. */
        const attestOutputMode = (request, budget) => {
            if (outputModeVerified || !proofs.length) throw new Error('Unexpected private PTY output proof.');
            const mode = readOwnedOutputMode(request.tty, budget());
            proofs[0].terminalOutput = { ...mode, verifiedAt: new Date().toISOString() };
            outputModeVerified = true;
        };
        const observeImageRequest = (request, budget, owner, worker) => {
            if (!outputModeVerified) throw new Error('Private PTY output mode has not been verified.');
            const command = execFileSync('/bin/ps', ['-p', String(worker.pid), '-o', 'command='],
                { encoding: 'utf8', timeout: budget(), maxBuffer: 16384 });
            if (!matchesRecordedWorker(worker, workerIdentity, command, runId)) throw new Error('Recorded image worker identity or marker changed.');
            const ownership = { app: owner, worker, tty: request.tty, at: new Date().toISOString(),
                terminalOutput: readOwnedOutputMode(request.tty, budget()) };
            return observeImages(request, budget, ownership);
        };
        /** Route already authenticated requests serially; preserve each suite's existing response shape and budget. */
        const routeOwnedObservation = async (request, queryTimeout, owner, worker) => {
            if (request.kind === 'private-clipboard') {
                const result = await observePrivateClipboard(request, queryTimeout, owner, worker);
                return { ok: true, ownerPid: app.pid, result };
            }
            if (request.kind?.startsWith('queries-')) {
                const result = await observeQueries(request, queryTimeout, owner, worker);
                return { ok: true, ownerPid: app.pid, ...result };
            }
            if (/^images-(?:start|admit|bind|read|restore|finish|seal)$/.test(request.kind)) {
                const result = await observeImageRequest(request, queryTimeout, owner, worker);
                return { ok: true, ownerPid: app.pid, ...result };
            }
            if (untranslatedCellKind(request.kind)
                || coreWrapKinds.has(request.kind)
                || /^resize-(?:start|bind|read|apply|finish|abort)$/.test(request.kind)
                || /^input-(?:start|bind|control|apply|read|finish|seal)$/.test(request.kind)) {
                const result = await observeUntranslatedCells(request, queryTimeout);
                return { ok: true, ownerPid: app.pid, ...result };
            }
            return null;
        };
        /** Handle proof/done/failure only after lineage verification; done additionally requires close and exact termios. */
        const controlOwnedWorker = (request, queryTimeout, owner, worker) => {
            if (request.kind === 'prove') {
                // Retain the measured proof, never the capability token.
                retainFirstProof(owner, worker, request.tty, queryTimeout);
            } else if (request.kind === 'raw-output') {
                attestOutputMode(request, queryTimeout);
            } else if (request.kind === 'done') {
                requireInputCompletion(inputState);
                requireStaticImageCompletion(staticImageState);
                requireQueryCompletion(queryState);
                if (!privateClipboardState?.observer.complete || !privateClipboardEvidence) throw new Error('Private clipboard did not close.');
                const original = proofs[0].privateClipboard.originalTerminalState;
                if (readOwnedTerminalState(request.tty, queryTimeout()) !== original) throw new Error('Original private kernel mode was not restored.');
                proofs[0].privateClipboard.restoredTerminalState = original;
                proofs[0].privateClipboard.restoredAt = new Date().toISOString();
                completed = true;
                resolveDone();
            } else if (request.kind === 'failed') {
                rejectDone(new Error(request.error ?? 'Worker failed.'));
            } else throw new Error('Unknown owner request.');
            return { ok: true, ownerPid: app.pid, ...(request.kind === 'prove' ? { privateClipboard: true, originalTerminalState: proofs[0].privateClipboard.originalTerminalState } : {}) };
        };
        /** Bound owner input to 8 KiB and authenticate before routing; asynchronous refusal must never bypass owned cleanup. */
        const server = createServer(socket => {
            let buffer = '';
            socket.setTimeout(5000, () => socket.destroy());
            socket.on('error', () => {});
            /** Consume one authenticated request; contain failed journal writes and still emit the closed refusal response. */
            socket.on('data', async chunk => {
                buffer += chunk;
                if (buffer.length > 8192) return socket.destroy();
                if (!buffer.includes('\n')) return;
                socket.removeAllListeners('data');
                let request, authenticated = false;
                try {
                    request = JSON.parse(buffer.slice(0, buffer.indexOf('\n')));
                    if (typeof request.token !== 'string' || request.token.length !== token.length
                        || !timingSafeEqual(Buffer.from(request.token), Buffer.from(token)) || request.runId !== runId) {
                        throw new Error('Invalid run capability.');
                    }
                    authenticated = true;
                    if (!app?.pid || app.exitCode !== null || app.signalCode !== null) throw new Error('Owned app is no longer live.');
                    assertExecutableUnchanged(executable, image);
                    // All lineage/mode queries and observations share the existing 4s budget.
                    const queryTimeout = createProcessBudget(4000);
                    const owner = processRecord(app.pid, queryTimeout());
                    const worker = processRecord(request.workerPid, queryTimeout());
                    assertProcessOwnership({ app: { ...owner, executable: realpathSync(owner.executable) }, worker,
                        ownerPid: app.pid, executable, uid: process.getuid(), runId, tty: request.tty,
                        appCommand: execFileSync('/bin/ps', ['-p', String(app.pid), '-o', 'command='], { encoding: 'utf8', timeout: queryTimeout(), maxBuffer: 16384 }) });
                    if (workerIdentity && workerIdentity.pid !== worker.pid) throw new Error('Another worker tried to use this run.');
                    workerIdentity ??= worker;
                    const observation = await routeOwnedObservation(request, queryTimeout, owner, worker);
                    if (observation) { socket.end(JSON.stringify(observation) + '\n'); return; }
                    socket.end(JSON.stringify(controlOwnedWorker(request, queryTimeout, owner, worker)) + '\n');
                } catch (error) {
                    if (authenticated && privateClipboardState?.observer) {
                        rejectDone(error);
                        try { await retainPrivateClipboardAbort(privateClipboardState, error, 'authenticatedOwnerDispatch', retainPrivateClipboardJournal); }
                        catch (error_) { recordPrivateClipboardRetentionFailure(privateClipboardState, 'authenticatedOwnerDispatch', error_); }
                    }
                    rejectQueryOwnerRequest({ authenticated, observer: queryState?.observer, error, request, reject: rejectDone });
                    socket.end(JSON.stringify(ownerFailure(error, Boolean(privateClipboardState))) + '\n');
                }
            });
        });
        let deadline;
        let logFd;
        let runOutcome = { failed: false }, cleanupOutcome;
        try {
            await Promise.race([done, new Promise((resolve, reject) => {
                server.once('error', reject);
                server.listen(config.socket, resolve);
            })]);
            if (interruption) throw interruption;
            chmodSync(config.socket, 0o600);
            const env = { ...process.env, NTILDE_APPDATA_ROOT: appData, TERMINFO_RUN_ID: runId,
                TERM_PROGRAM: 'Ntilde', TERM_PROGRAM_VERSION: args[3], PATH: dirname(process.execPath) + ':' + process.env.PATH };
            for (const name of ['__CFBundleIdentifier', 'TMUX', 'STY', 'SSH_CONNECTION', 'SSH_TTY',
                'TERMINFO_CAPTURE_DIRECTORY', 'TERMINFO_CLIPBOARD_FIXTURE_RECEIPT', 'TERMINFO_RUNTIME_PROVENANCE']) delete env[name];
            logFd = openSync(join(output, 'app-output.log'), 'wx', 0o600);
            app = spawn(executable, [`--ntilde-terminfo-owner=${runId}`, `--ntilde-terminfo-private-clipboard=${runId}`], { env, cwd: dirname(executable), stdio: ['ignore', logFd, logFd] });
            app.once('error', rejectDone);
            app.once('exit', (code, signal) => { if (!completed) rejectDone(new Error(`Owned app exited before collection (${code ?? signal}).`)); });
            context.ownerPid = app.pid;
            writeJson(join(output, 'context.json'), context);
            deadline = setTimeout(() => rejectDone(new Error('Owned collection exceeded 5 minutes.')), 300000);
            console.log(`Collecting in disposable Ntilde pid ${app.pid}; results: ${output}`);
            await done;
            assertOwnedSourcesUnchanged(sourceRepo, context);
            const summary = collectionSummary(output, args[3]);
            cellEvidence = retainCellReport(output, cellObserver, runId);
            sgrEvidence = retainCellReport(output, sgrObserver, runId, buildSgrReport, 'sgr-observations.json');
            gradingEvidence = await retainGradingEvidence(output, runId, gradingBundle, upstreamBundleSha256);
            sgrColorsEvidence = await retainSgrColorsEvidence(output, runId, sgrColorsBundle, upstreamBundleSha256);
            sgrRgbEvidence = await retainSgrRgbEvidence(output, runId, sgrRgbBundle, upstreamBundleSha256);
            sgrAttrsEvidence = await retainSgrAttrsEvidence(output, runId, sgrAttrsBundle, upstreamBundleSha256);
            sgrPaletteEvidence = await retainSgrPaletteEvidence(output, runId, sgrPaletteBundle, upstreamBundleSha256);
            sgrTruecolorEvidence = await retainSgrTruecolorEvidence(output, runId, sgrTruecolorBundle, upstreamBundleSha256);
            truecolorFindingEvidence = retainTruecolorFindingEvidence(output, runId);
            sgrUnderlineEvidence = await retainSgrUnderlineEvidence(output, runId, sgrUnderlineBundle, upstreamBundleSha256);
            sgrUnderlineColorEvidence = await retainSgrUnderlineColorEvidence(output, runId, sgrUnderlineColorBundle, upstreamBundleSha256);
            underlineResolvedEvidence = retainUnderlineResolvedEvidence(output, runId);
            sgrResetEvidence = await retainSgrResetEvidence(output, runId, sgrResetBundle, upstreamBundleSha256);
            sgrClosureEvidence = await retainSgrClosureEvidence(output, runId, sgrClosureBundle, upstreamBundleSha256);
            eraseLineEvidence = await retainEraseLineEvidence(output, runId, eraseLineBundle, upstreamBundleSha256);
            charEditEvidence = await retainCharEditEvidence(output, runId, charEditBundle, upstreamBundleSha256);
            eraseDisplayEvidence = await retainEraseDisplayEvidence(output, runId, eraseDisplayBundle, upstreamBundleSha256);
            lineEditEvidence = await retainLineEditEvidence(output, runId, lineEditBundle, upstreamBundleSha256);
            charsetsEvidence = await retainCharsetsEvidence(output, runId, charsetsBundle, upstreamBundleSha256);
            scrollbackEvidence = await retainScrollbackEvidence(output, runId, scrollbackBundle, upstreamBundleSha256);
            savedResetEvidence = await retainSavedResetEvidence(output, runId, savedResetBundle, upstreamBundleSha256);
            eraseAttrsRegionEvidence = await retainEraseAttrsRegionEvidence(output, runId, eraseAttrsRegionBundle, upstreamBundleSha256);
            attrRectEvidence = await retainAttrRectEvidence(output, runId, attrRectBundle, upstreamBundleSha256);
            cursorQualEvidence = await retainCursorQualEvidence(output, runId, cursorQualBundle, upstreamBundleSha256);
            eraseCharsHistoryEvidence = await retainEraseCharsHistoryEvidence(output, runId, eraseCharsHistoryBundle, upstreamBundleSha256);
            selectiveEraseEvidence = await retainSelectiveEraseEvidence(output, runId, selectiveEraseBundle, upstreamBundleSha256);
            rectEditEvidence = await retainRectEditEvidence(output, runId, rectEditBundle, upstreamBundleSha256);
            colEditEvidence = await retainColEditEvidence(output, runId, colEditBundle, upstreamBundleSha256);
            rectChecksumEvidence = await retainRectChecksumEvidence(output, runId, rectChecksumBundle, upstreamBundleSha256);
            lrMarginsEvidence = await retainLrMarginsEvidence(output, runId, lrMarginsBundle, upstreamBundleSha256);
            const coreRunBytes = readFileSync(join(output, 'cg-run.json'));
            const coreFrames = readFileSync(join(output, 'cg-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
            const coreAdapter = { id: coreGapsAdapterId, derivedFromAdapterId: lrMarginsAdapterId,
                bundleSha256: coreGapsBundleSha256, upstreamBundleSha256 };
            const coreReport = buildCoreGapsReport(coreFrames, JSON.parse(coreRunBytes), runId, coreAdapter, await import(pathToFileURL(coreGapsBundle).href));
            writeJson(join(output, 'cg-observations.json'), coreReport);
            coreGapsEvidence = { runId, suiteId: coreGapsSuiteId, file: 'cg-observations.json',
                sha256: hash(readFileSync(join(output, 'cg-observations.json'))), runFile: 'cg-run.json', runSha256: hash(coreRunBytes),
                adapterBundleSha256: coreGapsBundleSha256, provisionalUntil: 'cell-receipt.json' };
            colorResetEvidence = await retainColorResetEvidence(output, runId, colorResetBundle, upstreamBundleSha256);
            const tcRunBytes = readFileSync(join(output, 'tc-run.json'));
            const tcFrames = readFileSync(join(output, 'tc-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
            const tcReport = buildTextCursorReport(tcFrames, JSON.parse(tcRunBytes), runId);
            writeJson(join(output, 'tc-observations.json'), tcReport);
            textCursorEvidence = { runId, suiteId: textCursorSuiteId, file: 'tc-observations.json',
                sha256: hash(readFileSync(join(output, 'tc-observations.json'))), runFile: 'tc-run.json', runSha256: hash(tcRunBytes),
                provisionalUntil: 'cell-receipt.json' };
            const ceRunBytes = readFileSync(join(output, 'ce-run.json'));
            const ceFrames = readFileSync(join(output, 'ce-snapshots.jsonl'), 'utf8').trim().split('\n').map(line => JSON.parse(line));
            writeJson(join(output, 'ce-observations.json'), buildCoreEraseReport(ceFrames, JSON.parse(ceRunBytes), runId));
            coreEraseEvidence = { runId, suiteId: coreEraseSuiteId, file: 'ce-observations.json',
                sha256: hash(readFileSync(join(output, 'ce-observations.json'))), runFile: 'ce-run.json', runSha256: hash(ceRunBytes),
                provisionalUntil: 'cell-receipt.json' };
            if (!coreWrapState?.observer.restored) throw new Error('Core wrap did not restore.');
            const cwRunBytes = readFileSync(join(output, 'cw-run.json'));
            const cwFrames = readFileSync(join(output, 'cw-snapshots.jsonl'), 'utf8').trim().split('\n').map(JSON.parse);
            const cwGeometryBytes = readFileSync(join(output, 'cw-geometry.json'));
            writeJson(join(output, 'cw-observations.json'), deriveWrapEvidence(cwFrames, JSON.parse(cwRunBytes), runId, JSON.parse(cwGeometryBytes)));
            coreWrapEvidence = { runId, suiteId: wrapSuiteId, file: 'cw-observations.json',
                sha256: hash(readFileSync(join(output, 'cw-observations.json'))), runFile: 'cw-run.json', runSha256: hash(cwRunBytes),
                geometryFile: 'cw-geometry.json', geometrySha256: hash(cwGeometryBytes), provisionalUntil: 'cell-receipt.json' };
            rgbEvidence = retainDetails('rgb');
            renditionEvidence = retainDetails('rendition');
            cursorEvidence = retainDetails('cursor');
            pixelEvidence = pixelUnavailable ?? retainCellReport(output, pixelObserver, runId,
                (frames, id) => buildPixelReport(frames, id, output), 'pixel-observations.json');
            colorEvidence = colorUnavailable ?? retainCellReport(output, colorObserver, runId,
                (frames, id) => buildColorReport(frames, id, output), 'color-observations.json');
            eraseEvidence = retainEditing('erase');
            editingEvidence = retainEditing('editing');
            historyEvidence = retainEditing('history');
            resizeEvidence = resizeState?.unavailable ?? retainCellReport(output, resizeState?.observer, runId,
                (frames, id) => buildResizeReport(frames, resizeState.observer.actions, id), 'resize-observations.json');
            savedEvidence = savedState?.unavailable ?? retainCellReport(output, savedState?.observer, runId, buildSavedReport, 'saved-observations.json');
            metadataEvidence = metadataState?.unavailable ?? retainCellReport(output, metadataState?.observer, runId, buildMetadataReport, 'metadata-observations.json');
            inputEvidence = inputState?.unavailable ?? retainCellReport(output, inputState?.observer, runId,
                () => inputState.observer.report, 'input-observations.json');
            routedInputEvidence = await retainRoutedInputEvidence(output, runId, routedInputBundle, upstreamBundleSha256, inputEvidence, inputState.observer?.seal);
            osc133Evidence = await retainOsc133Evidence(output, runId, osc133Bundle, upstreamBundleSha256, osc133State?.unavailable === true);
            staticImageEvidence = staticImageState?.unavailable ?? retainCellReport(output, staticImageState?.observer, runId,
                () => staticImageState.observer.report, 'image-observations.json');
            queryEvidence = retainQueryReport(output, queryState, runId);
            summary.coreGapsEvidence = coreGapsEvidence;
            summary.textCursorEvidence = textCursorEvidence;
            summary.coreEraseEvidence = coreEraseEvidence;
            summary.coreWrapEvidence = coreWrapEvidence;
            summary.privateClipboardEvidence = privateClipboardEvidence;
            summary.queryEvidence = queryEvidence;
            summary.staticImageEvidence = staticImageEvidence;
            summary.inputEvidence = inputEvidence;
            summary.metadataEvidence = metadataEvidence;
            summary.savedEvidence = savedEvidence;
            summary.resizeEvidence = resizeEvidence;
            summary.historyEvidence = historyEvidence;
            summary.eraseEvidence = eraseEvidence;
            summary.editingEvidence = editingEvidence;
            summary.colorEvidence = colorEvidence;
            summary.pixelEvidence = pixelEvidence;
            summary.renditionEvidence = renditionEvidence;
            summary.cursorEvidence = cursorEvidence;
            summary.rgbEvidence = rgbEvidence;
            summary.cellEvidence = cellEvidence;
            summary.sgrEvidence = sgrEvidence;
            summary.gradingEvidence = gradingEvidence;
            summary.sgrColorsEvidence = sgrColorsEvidence;
            summary.sgrRgbEvidence = sgrRgbEvidence;
            summary.sgrAttrsEvidence = sgrAttrsEvidence;
            summary.sgrPaletteEvidence = sgrPaletteEvidence;
            summary.sgrTruecolorEvidence = sgrTruecolorEvidence;
            summary.truecolorFindingEvidence = truecolorFindingEvidence;
            summary.sgrUnderlineEvidence = sgrUnderlineEvidence;
            summary.sgrUnderlineColorEvidence = sgrUnderlineColorEvidence;
            summary.underlineResolvedEvidence = underlineResolvedEvidence;
            summary.sgrResetEvidence = sgrResetEvidence;
            summary.sgrClosureEvidence = sgrClosureEvidence;
            summary.eraseLineEvidence = eraseLineEvidence;
            summary.charEditEvidence = charEditEvidence;
            summary.eraseDisplayEvidence = eraseDisplayEvidence;
            summary.lineEditEvidence = lineEditEvidence;
            summary.charsetsEvidence = charsetsEvidence;
            summary.scrollbackEvidence = scrollbackEvidence;
            summary.savedResetEvidence = savedResetEvidence;
            summary.eraseAttrsRegionEvidence = eraseAttrsRegionEvidence;
            summary.attrRectEvidence = attrRectEvidence;
            summary.cursorQualEvidence = cursorQualEvidence;
            summary.eraseCharsHistoryEvidence = eraseCharsHistoryEvidence;
            summary.selectiveEraseEvidence = selectiveEraseEvidence;
            summary.rectEditEvidence = rectEditEvidence;
            summary.colEditEvidence = colEditEvidence;
            summary.rectChecksumEvidence = rectChecksumEvidence;
            summary.lrMarginsEvidence = lrMarginsEvidence;
            summary.routedInputEvidence = routedInputEvidence;
            summary.osc133Evidence = osc133Evidence;
            summary.colorResetEvidence = colorResetEvidence;
            writeJson(join(output, 'summary.json'), summary);
            console.log(JSON.stringify(summary, null, 2));
        } catch (error) {
            runOutcome = { failed: true, error };
            runOutcome = await retainOwnedFailureSequence(error, [
                async () => { await retainPrivateClipboardAbort(privateClipboardState, error, 'runFailure', retainPrivateClipboardJournal); },
                () => { queryState?.observer?.abort(error); retainQueryJournal(); },
                reason => retainOwnedFailure(output, reason, privateClipboardState),
            ]);
        } finally {
            clearTimeout(deadline);
            preserveScratch = true;
            cleanupOutcome = await captureOwnedCleanup(() => cleanupOwnedRun({ workerIdentity, runId, app, logFd, server, output, proofs, completed, scratch }));
            preserveScratch = cleanupOutcome.failed;
        }
        throwOwnedRunFailures(runOutcome, cleanupOutcome);
    } catch (error) {
        retainPreparationFailure(output, error);
        throw error;
    } finally {
        try {
            // Also handles preparation failures before the app or server is started.
            if (scratch && !preserveScratch) rmSync(scratch, { recursive: true, force: true });
        } finally {
            process.removeListener('SIGINT', onInt);
            process.removeListener('SIGTERM', onTerm);
        }
    }
    if (interruption) {
        writeJson(join(output, 'failure.json'), { error: interruption.message, at: new Date().toISOString() });
        throw interruption;
    }
    try {
        assertOwnedSourcesUnchanged(sourceRepo, sourceContext);
        removeSourceSnapshot(sourceRepo);
        const proofPath = join(output, 'ownership-proof.json');
        const proof = JSON.parse(readFileSync(proofPath));
        proof.sourceSnapshot = { policy: sourceSnapshotPolicy, root: sourceRepo, removedAt: new Date().toISOString() };
        writeFileSync(proofPath, JSON.stringify(proof, null, 2) + '\n', { mode: 0o600 });
        completeOwnedReceipt(output, scratch, { ...cellEvidence, sgrEvidence, gradingEvidence, sgrColorsEvidence, sgrRgbEvidence, sgrAttrsEvidence, sgrPaletteEvidence, sgrTruecolorEvidence, truecolorFindingEvidence, sgrUnderlineEvidence, sgrUnderlineColorEvidence, underlineResolvedEvidence, sgrResetEvidence, sgrClosureEvidence, eraseLineEvidence, charEditEvidence, eraseDisplayEvidence, lineEditEvidence, charsetsEvidence, scrollbackEvidence, savedResetEvidence, eraseAttrsRegionEvidence, attrRectEvidence, cursorQualEvidence, eraseCharsHistoryEvidence, selectiveEraseEvidence, rectEditEvidence, colEditEvidence, rectChecksumEvidence, lrMarginsEvidence, colorResetEvidence, coreGapsEvidence, routedInputEvidence, textCursorEvidence, osc133Evidence, coreEraseEvidence, coreWrapEvidence, rgbEvidence, renditionEvidence, cursorEvidence, pixelEvidence, colorEvidence, eraseEvidence, editingEvidence, historyEvidence, resizeEvidence, savedEvidence, metadataEvidence, inputEvidence, staticImageEvidence, queryEvidence, privateClipboardEvidence });
    }
    catch (error) { retainPreparationFailure(output, error); throw error; }
}

try {
    await main();
} catch (error) {
    console.error(`[terminfo-owned] ${error.message}`);
    process.exitCode = 1;
}
