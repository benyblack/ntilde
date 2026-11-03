#!/usr/bin/env node
// Launches a private macOS desktop instance; never attaches to an existing pane.
import { spawn, execFileSync } from 'node:child_process';
import { createHash, randomBytes, randomUUID, timingSafeEqual } from 'node:crypto';
import { appendFileSync, chmodSync, closeSync, existsSync, mkdirSync, mkdtempSync, openSync, readFileSync, realpathSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { adapterId, adaptCollector, upstreamBundleSha256, upstreamReceipt, validateCollectorReceipt } from './terminfo/collector.mjs';
import { assertProcessOwnership, processRecord, terminateRecordedWorker, workerExists } from './terminfo/ownership.mjs';
import { createPrivateScratch, createProcessBudget, installLockedCollector, readCollectorLockfile, resolveOwnedExecutable, validateCollectorInputs } from './terminfo/runtime.mjs';
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
import { readOwnedOutputMode, ownedOutputPolicy } from './terminfo/terminal-output.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const writeJson = (path, value) => writeFileSync(path, JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
const quote = value => "'" + value.replaceAll("'", String.raw`'\''`) + "'";

/** Preserve only the known optional App refusal in the private owner response. */
function ownerFailure(error) {
    return { ok: false, error: error.message,
        ...(error.code === 'screenDetailsUnavailable' ? { code: error.code } : {}) };
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
    const ownerModule = join(repo, 'scripts', 'terminfo', 'ownership.mjs');
    const adapted = adaptCollector(source, ownerModule);
    const adapterBundleSha256 = hash(adapted);
    // Keep external package resolution beside the original bundle. A derived
    // file is content-addressed; the downloaded original is never overwritten.
    const bundle = join(tool, 'node_modules', 'terminfo.dev', 'dist', `ntilde-${adapterBundleSha256}.mjs`);
    if (!existsSync(bundle)) writeFileSync(bundle, adapted, { flag: 'wx', mode: 0o600 });
    if (hash(readFileSync(bundle)) !== adapterBundleSha256) throw new Error('Derived adapter bundle digest mismatch.');

    return { ownerModule, bundle, adapterBundleSha256, receiptBytes, lockBytes };
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
    const sources = {
        runnerSourceSha256: fileURLToPath(import.meta.url),
        adapterSourceSha256: join(repo, 'scripts', 'terminfo', 'collector.mjs'),
        ownershipSourceSha256: ownerModule, workerSourceSha256: workerScript,
        runtimeSourceSha256: join(repo, 'scripts', 'terminfo', 'runtime.mjs'),
        observerSourceSha256: join(repo, 'scripts', 'terminfo', 'agent-observer.mjs'),
        cellSuiteSourceSha256: join(repo, 'scripts', 'terminfo', 'cells.mjs'),
        cellFenceSourceSha256: join(repo, 'scripts', 'terminfo', 'terminal-fence.mjs'),
        attributeSourceSha256: join(repo, 'scripts', 'terminfo', 'attributes.mjs'),
        sgrSuiteSourceSha256: join(repo, 'scripts', 'terminfo', 'sgr.mjs'),
        cellDetailsSourceSha256: join(repo, 'scripts', 'terminfo', 'cell-details.mjs'),
        rgbSuiteSourceSha256: join(repo, 'scripts', 'terminfo', 'rgb.mjs'),
        renditionSuiteSourceSha256: join(repo, 'scripts', 'terminfo', 'rendition.mjs'),
        cursorSuiteSourceSha256: join(repo, 'scripts', 'terminfo', 'cursor.mjs'),
        pixelCaptureSourceSha256: join(repo, 'scripts', 'terminfo', 'pixels-capture.mjs'),
        pixelSuiteSourceSha256: join(repo, 'scripts', 'terminfo', 'pixels.mjs'),
        pixelCasesSourceSha256: join(repo, 'scripts', 'terminfo', 'pixels-cases.mjs'),
        pixelPngSourceSha256: join(repo, 'scripts', 'terminfo', 'pixels-png.mjs'),
        colorCaptureSourceSha256: join(repo, 'scripts', 'terminfo', 'color-capture.mjs'),
        colorSuiteSourceSha256: join(repo, 'scripts', 'terminfo', 'colors.mjs'),
        colorCasesSourceSha256: join(repo, 'scripts', 'terminfo', 'color-cases.mjs'),
        colorQuerySourceSha256: join(repo, 'scripts', 'terminfo', 'color-query.mjs'),
        editingStateSourceSha256: join(repo, 'scripts', 'terminfo', 'editing-state.mjs'),
        editingCasesSourceSha256: join(repo, 'scripts', 'terminfo', 'editing-cases.mjs'),
        editingExpectationsSourceSha256: join(repo, 'scripts', 'terminfo', 'editing-expectations.mjs'),
        editingCaptureSourceSha256: join(repo, 'scripts', 'terminfo', 'editing-capture.mjs'),
        editingSuiteSourceSha256: join(repo, 'scripts', 'terminfo', 'editing.mjs'),
        historyCasesSourceSha256: join(repo, 'scripts', 'terminfo', 'history-cases.mjs'),
        historyCaptureSourceSha256: join(repo, 'scripts', 'terminfo', 'history-capture.mjs'),
        historySuiteSourceSha256: join(repo, 'scripts', 'terminfo', 'history.mjs'),
        terminalOutputSourceSha256: join(repo, 'scripts', 'terminfo', 'terminal-output.mjs'),
        receiptSourceSha256: join(repo, 'scripts', 'terminfo', 'receipt.mjs'),
        collectorManifestSha256: join(repo, 'scripts', 'terminfo', 'collector-package.json'),
        collectorLockSha256: join(repo, 'scripts', 'terminfo', 'collector-package-lock.json'),
    };
    const digests = {};
    for (const [field, path] of Object.entries(sources)) {
        const bytes = readFileSync(path);
        writeFileSync(join(sourceDirectory, basename(path)), bytes, { flag: 'wx', mode: 0o600 });
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
    const args = process.argv.slice(2);
    if (args.length === 1 && ['--help', '-h'].includes(args[0])) {
        console.log(`Usage: node scripts/terminfo-owned.mjs --executable /absolute/path/Ntilde --app-version VERSION

Requires macOS, Node 24+ and an uninstalled sidecar outside .app bundles.
Packaged apps are refused because a staged update can restart outside the fixture.
Requires an npm installation (or NTILDE_TERMINFO_NPM_CLI) to recreate locked dependencies.
Prepare terminfo.dev@4.0.0 under
artifacts/terminfo/tool (see docs/qa/QA_TERMINFO.md). Launches a disposable
Ntilde process with private settings and runs a locally extended collector.
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
    // Persistent handlers also protect against a second signal during teardown.
    process.on('SIGINT', onInt);
    process.on('SIGTERM', onTerm);
    try {
        output = mkdtempSync(join(repo, 'artifacts', 'terminfo', 'owned-'));
        scratch = createPrivateScratch();
        if (interruption) throw interruption;
        const privateTool = join(scratch, 'collector');
        installLockedCollector(privateTool, input.manifestBytes, input.lockBytes);
        const { ownerModule, bundle, adapterBundleSha256, receiptBytes, lockBytes } = prepareCollector(privateTool);
        const appData = join(scratch, 'appdata');
        mkdirSync(appData, { mode: 0o700 });
        const runId = randomBytes(16).toString('hex');
        const token = randomBytes(32).toString('hex');
        const configPath = join(scratch, 'worker.json');
        const workerScript = join(repo, 'scripts', 'terminfo', 'worker.mjs');
        const profileName = `terminfo disposable worker ${runId}`;
        const config = { runId, token, appData, output, executable, bundle, profileName, cellNonce: `NTILDE_QA_${runId}`,
            socket: join(scratch, 'owner.sock'), adapterBundleSha256, upstreamBundleSha256 };
        writeJson(configPath, config);
        const command = join(scratch, 'worker.sh');
        writeFileSync(command, `#!/bin/sh\nexec ${quote(process.execPath)} ${quote(workerScript)} ${quote(configPath)} --ntilde-terminfo-worker=${runId}\n`, { flag: 'wx', mode: 0o700 });
        const profileId = randomUUID();
        const settings = {
            Profiles: [{ Id: profileId, Name: profileName, Command: command, Arguments: '', StartingDirectory: scratch, Type: 0 }],
            DefaultProfileId: profileId, SessionPersistence: 'Off', ShellExitPolicy: 'Always', PaneClosePolicy: 'Always',
            QuakeModeEnabled: false, AutomaticUpdateChecks: false, AllowOsc52ClipboardWrite: false,
            MinimumContrast: 1, EnableLigatures: false,
            BellAudioEnabled: false, BellVisualEnabled: false, SmoothScrolling: false,
            CommandAssistEnabled: false, CommandAssistShellIntegrationEnabled: false,
            CommandAssistPowerShellIntegrationEnabled: false, AgentAccessObserveEnabled: true, AgentAccessActEnabled: false,
        };
        writeJson(join(appData, 'settings.json'), settings);
        writeJson(join(output, 'initial-settings.json'), settings);
        writeFileSync(join(output, 'upstream-suite-receipt.json'), receiptBytes, { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'package-lock.json'), lockBytes, { flag: 'wx', mode: 0o600 });

        let app;
        let workerIdentity;
        let cellObserver;
        let sgrObserver;
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
        let completed = false, outputModeVerified = false;
        const proofs = [];
        const image = statSync(executable);
        const context = {
            adapter: adapterId, adapterBundleSha256, upstreamBundleSha256, appVersion: args[3],
            appVersionSource: 'operator supplied; not independently verified',
            executable, executableSha256: hash(readFileSync(executable)),
            assemblySha256: existsSync(join(dirname(executable), 'Ntilde.dll')) ? hash(readFileSync(join(dirname(executable), 'Ntilde.dll'))) : null,
            nodeVersion: process.version, dependencyInstall: 'isolated npm ci --ignore-scripts', startedAt: new Date().toISOString(), runId,
            ...retainCollectorEvidence(output, bundle, adapterBundleSha256, ownerModule, workerScript),
            retainedAdapterBundle: 'collector-adapter.mjs', retainedSources: 'sources',
            clipboard: 'disabled; no clipboard fixture', terminalOutput: ownedOutputPolicy, historySuiteId, eraseSuiteId, editingSuiteId, colorSuiteId, pixelSuiteId, pixels: 'nonce-bound owned offscreen renders; fixed synthetic phases, retained PNGs and render context; no live desktop timing',
            cells: 'private observe-only AgentHost; nonce-bound pane; separate ASCII cell-state suite',
            attributes: 'separate ASCII SGR flags and palette-index suite',
            cellDetails: 'separate stored ARGB/underline and rendition/charset suites; no theme resolution or pixels',
            cursorModes: 'stored cursor style/blink/default/visibility; pristine owned DECSTR cleanup, no host appearance or blink phase',
        };
        writeFileSync(join(output, 'cell-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        writeFileSync(join(output, 'sgr-snapshots.jsonl'), '', { flag: 'wx', mode: 0o600 });
        for (const prefix of [...Object.keys(detailSpecs), 'pixel', 'color', ...Object.keys(editingSpecs)])
            writeFileSync(join(output, `${prefix}-snapshots.jsonl`), '', { flag: 'wx', mode: 0o600 });
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
        /** Route owned observations serially, preserving suite restoration and pane identity dependencies. */
        const observeCells = async (request, budget) => {
            const prefix = request.kind.split('-')[0];
            if (prefix === 'pixel') return observePixels(request, budget);
            if (prefix === 'color') return observeColors(request, budget);
            if (Object.hasOwn(editingSpecs, prefix)) return observeEditing(prefix, request, budget);
            if (Object.hasOwn(detailSpecs, prefix)) return observeDetails(prefix, request, budget);
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
        const retainFirstProof = (owner, worker, tty) => {
            if (proofs.length !== 0) return;
            proofs.push({ app: owner, worker, tty, at: new Date().toISOString() });
        };
        /** Bind one kernel-observed output-mode proof to the already verified worker. */
        const attestOutputMode = (request, budget) => {
            if (outputModeVerified || !proofs.length) throw new Error('Unexpected private PTY output proof.');
            const mode = readOwnedOutputMode(request.tty, budget());
            proofs[0].terminalOutput = { ...mode, verifiedAt: new Date().toISOString() };
            outputModeVerified = true;
        };
        const server = createServer(socket => {
            let buffer = '';
            socket.setTimeout(5000, () => socket.destroy());
            socket.on('error', () => {});
            socket.on('data', async chunk => {
                buffer += chunk;
                if (buffer.length > 8192) return socket.destroy();
                if (!buffer.includes('\n')) return;
                socket.removeAllListeners('data');
                try {
                    const request = JSON.parse(buffer.slice(0, buffer.indexOf('\n')));
                    if (typeof request.token !== 'string' || request.token.length !== token.length
                        || !timingSafeEqual(Buffer.from(request.token), Buffer.from(token)) || request.runId !== runId) {
                        throw new Error('Invalid run capability.');
                    }
                    if (!app?.pid || app.exitCode !== null || app.signalCode !== null) throw new Error('Owned app is no longer live.');
                    const currentImage = statSync(executable);
                    if (image.ino !== currentImage.ino || image.size !== currentImage.size || image.mtimeMs !== currentImage.mtimeMs) throw new Error('Owned executable changed during collection.');
                    // Three serial queries must fit inside the existing 5s reply timeout.
                    const queryTimeout = createProcessBudget(4000);
                    const owner = processRecord(app.pid, queryTimeout());
                    const worker = processRecord(request.workerPid, queryTimeout());
                    assertProcessOwnership({ app: { ...owner, executable: realpathSync(owner.executable) }, worker,
                        ownerPid: app.pid, executable, uid: process.getuid(), runId, tty: request.tty,
                        appCommand: execFileSync('/bin/ps', ['-p', String(app.pid), '-o', 'command='], { encoding: 'utf8', timeout: queryTimeout(), maxBuffer: 16384 }) });
                    if (workerIdentity && workerIdentity.pid !== worker.pid) throw new Error('Another worker tried to use this run.');
                    workerIdentity ??= worker;
                    if (/^(?:cell|sgr|rgb|rendition|cursor|pixel|color|erase|editing|history)-(?:start|bind|read|finish)$/.test(request.kind)) {
                        const result = await observeUntranslatedCells(request, queryTimeout);
                        socket.end(JSON.stringify({ ok: true, ownerPid: app.pid, ...result }) + '\n');
                        return;
                    }
                    if (request.kind === 'prove') {
                        // Retain the measured proof, never the capability token.
                        retainFirstProof(owner, worker, request.tty);
                    } else if (request.kind === 'raw-output') {
                        attestOutputMode(request, queryTimeout);
                    } else if (request.kind === 'done') {
                        completed = true;
                        resolveDone();
                    } else if (request.kind === 'failed') {
                        rejectDone(new Error(request.error ?? 'Worker failed.'));
                    } else throw new Error('Unknown owner request.');
                    socket.end(JSON.stringify({ ok: true, ownerPid: app.pid }) + '\n');
                } catch (error) {
                    socket.end(JSON.stringify(ownerFailure(error)) + '\n');
                }
            });
        });
        let deadline;
        let logFd;
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
            app = spawn(executable, [`--ntilde-terminfo-owner=${runId}`], { env, cwd: dirname(executable), stdio: ['ignore', logFd, logFd] });
            app.once('error', rejectDone);
            app.once('exit', (code, signal) => { if (!completed) rejectDone(new Error(`Owned app exited before collection (${code ?? signal}).`)); });
            context.ownerPid = app.pid;
            writeJson(join(output, 'context.json'), context);
            deadline = setTimeout(() => rejectDone(new Error('Owned collection exceeded 5 minutes.')), 300000);
            console.log(`Collecting in disposable Ntilde pid ${app.pid}; results: ${output}`);
            await done;
            const summary = collectionSummary(output, args[3]);
            cellEvidence = retainCellReport(output, cellObserver, runId);
            sgrEvidence = retainCellReport(output, sgrObserver, runId, buildSgrReport, 'sgr-observations.json');
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
            writeJson(join(output, 'summary.json'), summary);
            console.log(JSON.stringify(summary, null, 2));
        } catch (error) {
            writeJson(join(output, 'failure.json'), { error: error.message, at: new Date().toISOString() });
            throw new Error(`${error.message} Evidence retained in ${output}`);
        } finally {
            clearTimeout(deadline);
            preserveScratch = true;
            await cleanupOwnedRun({ workerIdentity, runId, app, logFd, server, output, proofs, completed, scratch });
            preserveScratch = false;
        }
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
    try { completeOwnedReceipt(output, scratch, { ...cellEvidence, sgrEvidence, rgbEvidence, renditionEvidence, cursorEvidence, pixelEvidence, colorEvidence, eraseEvidence, editingEvidence, historyEvidence }); }
    catch (error) { retainPreparationFailure(output, error); throw error; }
}

try {
    await main();
} catch (error) {
    console.error(`[terminfo-owned] ${error.message}`);
    process.exitCode = 1;
}
