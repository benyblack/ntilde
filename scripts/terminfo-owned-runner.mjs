#!/usr/bin/env node
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
import { inputSuiteId, ownedInputPolicy } from './terminfo/input-cases.mjs';
import { generateOwnedInput } from './terminfo/input-action.mjs';
import { OwnedQueryObserver, rejectQueryOwnerRequest } from './terminfo/query-capture.mjs';
import { querySuiteId, ownedQueryPolicy } from './terminfo/queries.mjs';
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

function retainFailedMetadataAttempt(output, request, error, observer) {
    if (observer.lastAttempt && !initialMetadataRefusal(request, error, observer))
        writeJson(join(output, 'metadata-failed-observation.json'), { ...observer.lastAttempt, error: observer.failure(error) });
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
        const appData = join(scratch, 'appdata');
        mkdirSync(appData, { mode: 0o700 });
        const runId = randomBytes(16).toString('hex');
        const token = randomBytes(32).toString('hex');
        const configPath = join(scratch, 'worker.json');
        const workerScript = join(sourceRepo, 'scripts', 'terminfo', 'worker.mjs');
        const profileName = `terminfo disposable worker ${runId}`;
        const config = { privateClipboard: true, runId, token, appData, output, executable, bundle, gradingBundle, gradingBundleSha256,
            sgrColorsBundle, sgrColorsBundleSha256, sgrRgbBundle, sgrRgbBundleSha256, sgrAttrsBundle, sgrAttrsBundleSha256,
            sgrPaletteBundle, sgrPaletteBundleSha256, sgrTruecolorBundle, sgrTruecolorBundleSha256, sgrUnderlineBundle, sgrUnderlineBundleSha256, sgrUnderlineColorBundle, sgrUnderlineColorBundleSha256, sgrResetBundle, sgrResetBundleSha256, sgrClosureBundle, sgrClosureBundleSha256, eraseLineBundle, eraseLineBundleSha256, charEditBundle, charEditBundleSha256, eraseDisplayBundle, eraseDisplayBundleSha256, lineEditBundle, lineEditBundleSha256, charsetsBundle, charsetsBundleSha256, scrollbackBundle, scrollbackBundleSha256, savedResetBundle, savedResetBundleSha256, eraseAttrsRegionBundle, eraseAttrsRegionBundleSha256,
            profileName, cellNonce: `NTILDE_QA_${runId}`,
            socket: join(scratch, 'owner.sock'), adapterBundleSha256, upstreamBundleSha256 };
        writeJson(configPath, config);
        const command = join(scratch, 'worker.sh');
        writeFileSync(command, `#!/bin/sh\nexec ${quote(process.execPath)} ${quote(workerScript)} ${quote(configPath)} --ntilde-terminfo-worker=${runId}\n`, { flag: 'wx', mode: 0o700 });
        const profileId = randomUUID();
        const settings = {
            Profiles: [{ Id: profileId, Name: profileName, Command: command, Arguments: '', StartingDirectory: scratch, Type: 0 }],
            DefaultProfileId: profileId, SessionPersistence: 'Off', ShellExitPolicy: 'Always', PaneClosePolicy: 'Always',
            QuakeModeEnabled: false, AutomaticUpdateChecks: false, AllowOsc52ClipboardWrite: true,
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
        /** Route owned observations serially, preserving suite restoration and pane identity dependencies. */
        const recordInputUnavailable = reason => {
            const file = 'input-unavailable.json';
            writeJson(join(output, file), { schemaVersion: 1, runId, suiteId: inputSuiteId, status: 'unavailable',
                reason, fixtureStarted: false, plannedExperiments: 39 });
            inputState.unavailable = { runId, suiteId: inputSuiteId, file, sha256: hash(readFileSync(join(output, file))),
                status: 'unavailable', reason, counts: {} };
        };
        const observeInput = async (request, budget) => {
            if (!metadataState?.observer?.restored && !metadataState?.unavailable) throw new Error('Metadata must restore before input observations.');
            if (!inputState) {
                inputState = { workerPid: request.workerPid, tty: request.tty, recordedFrames: 0, recordedActions: 0 };
                inputState.observer = new OwnedInputObserver({ appData, ownerPid: app.pid, profileName,
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
                    if (priorChallenges.length !== 110) throw new Error('Incomplete earlier query challenges.');
                    queryState = { workerPid: worker.pid, tty: request.tty, recordedFrames: 0, recordedActions: 0 };
                    const binding = { runId, ownerPid: owner.pid, workerPid: worker.pid, uid: worker.uid,
                        appExecutable: owner.executable, workerExecutable: worker.executable, tty: request.tty, profileName, paneId: cellObserver.paneId };
                    queryState.observer = new OwnedQueryObserver({ binding, appData, priorChallenges, attest: remaining => attestQuery(queryState, remaining) });
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
            if (/^(?:cell|sgr|grading|sgcolors|sgrgb|sgattr|sgpal|sgtc|sgtf|sgus|sguc|sgur|sgsr|sgcl|erln|ched|erdi|ledt|chrs|scrl|svrs|erar|rgb|rendition|cursor|pixel|color|erase|editing|history|saved|metadata)-(?:start|bind|read|finish)$/.test(request.kind)
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
            staticImageEvidence = staticImageState?.unavailable ?? retainCellReport(output, staticImageState?.observer, runId,
                () => staticImageState.observer.report, 'image-observations.json');
            queryEvidence = retainQueryReport(output, queryState, runId);
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
        completeOwnedReceipt(output, scratch, { ...cellEvidence, sgrEvidence, gradingEvidence, sgrColorsEvidence, sgrRgbEvidence, sgrAttrsEvidence, sgrPaletteEvidence, sgrTruecolorEvidence, truecolorFindingEvidence, sgrUnderlineEvidence, sgrUnderlineColorEvidence, underlineResolvedEvidence, sgrResetEvidence, sgrClosureEvidence, eraseLineEvidence, charEditEvidence, eraseDisplayEvidence, lineEditEvidence, charsetsEvidence, scrollbackEvidence, savedResetEvidence, eraseAttrsRegionEvidence, rgbEvidence, renditionEvidence, cursorEvidence, pixelEvidence, colorEvidence, eraseEvidence, editingEvidence, historyEvidence, resizeEvidence, savedEvidence, metadataEvidence, inputEvidence, staticImageEvidence, queryEvidence, privateClipboardEvidence });
    }
    catch (error) { retainPreparationFailure(output, error); throw error; }
}

try {
    await main();
} catch (error) {
    console.error(`[terminfo-owned] ${error.message}`);
    process.exitCode = 1;
}
