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
import { fileURLToPath } from 'node:url';
import { adapterId, adaptCollector, upstreamBundleSha256, upstreamReceipt, validateCollectorReceipt } from './terminfo/collector.mjs';
import { adaptGradingCollector, gradingAdapterId } from './terminfo/grading-collector.mjs';
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
import { OwnedPrivateClipboardObserver } from './terminfo/private-clipboard-capture.mjs';
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
        const appData = join(scratch, 'appdata');
        mkdirSync(appData, { mode: 0o700 });
        const runId = randomBytes(16).toString('hex');
        const token = randomBytes(32).toString('hex');
        const configPath = join(scratch, 'worker.json');
        const workerScript = join(sourceRepo, 'scripts', 'terminfo', 'worker.mjs');
        const profileName = `terminfo disposable worker ${runId}`;
        const config = { privateClipboard: true, runId, token, appData, output, executable, bundle, gradingBundle, gradingBundleSha256,
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
        let gradingEvidence;
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
            cells: 'private AgentHost readScreen; nonce-bound pane; separate ASCII cell-state suite',
            attributes: 'separate ASCII SGR flags and palette-index suite',
            cellDetails: 'separate stored ARGB/underline and rendition/charset suites; no theme resolution or pixels',
            cursorModes: 'stored cursor style/blink/default/visibility; pristine owned DECSTR cleanup, no host appearance or blink phase',
        };
        sourceContext = context;
        writeFileSync(join(output, 'cell-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'sgr-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'grading-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
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
                        includeAttributes: true, includeCellDetails: true, includeCursorMode: true, includeOverline: true, includeBufferState: true });
                    privateClipboardState.observer = new OwnedPrivateClipboardObserver({ binding, appData,
                        attest: remaining => attestPrivateClipboard(privateClipboardState, remaining), capture: remaining => reader.capture(remaining) });
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
            if (/^(?:cell|sgr|grading|rgb|rendition|cursor|pixel|color|erase|editing|history|saved|metadata)-(?:start|bind|read|finish)$/.test(request.kind)
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
        completeOwnedReceipt(output, scratch, { ...cellEvidence, sgrEvidence, gradingEvidence, rgbEvidence, renditionEvidence, cursorEvidence, pixelEvidence, colorEvidence, eraseEvidence, editingEvidence, historyEvidence, resizeEvidence, savedEvidence, metadataEvidence, inputEvidence, staticImageEvidence, queryEvidence, privateClipboardEvidence });
    }
    catch (error) { retainPreparationFailure(output, error); throw error; }
}

try {
    await main();
} catch (error) {
    console.error(`[terminfo-owned] ${error.message}`);
    process.exitCode = 1;
}
