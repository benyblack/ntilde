import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { appendFileSync, existsSync, lstatSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { isDeepStrictEqual as same, promisify } from 'node:util';
import { OwnedCellObserver } from './agent-observer.mjs';
import { copyPaneMetadata } from './metadata-state.mjs';
import { checkAction, cwdPlan, gradeCwd, prefix, readPublicPath, requireCwd, scope } from './modern-osc7-cwd.mjs';
import { assertRetainedSourceClosure, capturedOscCwdSourceFiles, capturedOscCwdSourcePolicy } from './owned-sources.mjs';
import { readOwnedOutputMode, validateOutputGeometry } from './terminal-output.mjs';

export { prefix, scope };
export const workerFile = 'modern-osc7-cwd-worker.mjs';
export const reportFile = 'osc7-cwd-observation.json';
const receiptFile = 'osc7-cwd-receipt.json';
const journalFile = 'osc7-cwd-journal.jsonl';
const swift = fileURLToPath(new URL('./modern-osc7-cwd-native.swift', import.meta.url));
const runFile = promisify(execFile);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const isDigest = value => /^[a-f\d]{64}$/.test(value ?? '');
const fileAt = phase => 'tooltip-' + phase + '.png';
const pointAt = (window, offsetY) => ({ x: window.x + 112, y: window.y + offsetY });

function bytes(output, file) {
    const path = join(output, file), stat = lstatSync(path);
    requireCwd(stat.isFile() && stat.size > 0 && stat.size < 16 * 1024 * 1024, 'invalid retained file ' + file);
    return readFileSync(path);
}

const json = (output, file) => JSON.parse(bytes(output, file));

function validateNative(read, pid, expectedWindow) {
    const { original, before, hover, captured, restored, window, lines } = read;
    requireCwd([before, hover, captured, restored].every(state => state?.frontPid === pid
        && Array.isArray(state.windows) && state.windows.every(item => item.pid === pid)),
    'the native reading is outside the owned foreground App');
    const mains = before.windows.filter(item => item.layer === 0 && item.width >= 400 && item.height >= 200);
    const [main] = mains;
    requireCwd(mains.length === 1 && (!expectedWindow || same(main, expectedWindow)), 'the owned native window changed');
    const target = pointAt(main, 18);
    requireCwd(same(before.pointer, pointAt(main, 95)) && same(hover.pointer, target)
        && same(captured.pointer, target) && same(restored.pointer, original), 'unverified hover move or pointer restoration');
    const popups = hover.windows.filter(item => !before.windows.some(prior => prior.id === item.id)
        && Math.abs(item.x - target.x) < 80 && Math.abs(item.y - target.y) < 80
        && item.width > 20 && item.height > 10 && item.height < 120);
    requireCwd(popups.length <= 1 && same(window, popups[0] ?? main)
        && [hover, captured, restored].every(state => state.windows.some(item => same(item, main)))
        && captured.windows.some(item => same(item, window)), 'the tooltip window was not bound to the owned tab');
    requireCwd(Array.isArray(lines) && lines.length > 0 && lines.length <= 30
        && lines.every(line => typeof line.text === 'string' && line.text.length <= 256 && line.confidence >= 0 && line.confidence <= 1
            && line.bounds?.length === 4 && line.bounds.every(value => Number.isFinite(value) && value >= 0 && value <= 1)),
    'empty or malformed public tooltip reading');
    return main;
}

function stageReading(output, frame, step, context, pane, previousWindow) {
    requireCwd(frame && !frame.error && frame.phase === step.phase && same(frame.pane, pane)
        && /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(pane?.id ?? '')
        && frame.terminalOutput?.outputProcessing === false, 'changed step, pane or PTY mode');
    validateOutputGeometry(frame.terminalOutput.description, pane.rows, pane.cols);
    checkAction(step, frame.action);

    const { paneId, workingDirectory, presentation, buffer } = copyPaneMetadata(frame.metadata);
    requireCwd(paneId === pane.id && presentation.windowVisible && presentation.paneActive && !buffer.alternateScreen
        && buffer.rows === pane.rows && buffer.cols === pane.cols, 'private corroboration came from a different pane or state');
    requireCwd(frame.image?.file === fileAt(step.phase) && isDigest(frame.image.sha256), 'wrong stage screenshot reference');
    const raw = bytes(output, frame.image.file);
    requireCwd(hash(raw) === frame.image.sha256 && raw.subarray(0, 8).equals(Buffer.from('89504e470d0a1a0a', 'hex')),
        'changed screenshot bytes');

    const main = validateNative(frame.native, context.ownerPid, previousWindow);
    const publicPath = readPublicPath(frame.native.lines);
    return { main, reading: { publicPath, metadataPath: workingDirectory, image: frame.image, lines: frame.native.lines } };
}

export class Collection {
    constructor(run) {
        this.run = run;
        this.observer = new OwnedCellObserver({ appData: run.appData, ownerPid: run.app.pid, profileName: run.profileName });
        this.plan = cwdPlan(run.runId);
        this.stages = {};
        this.helper = join(run.scratch, 'observe-tab-tooltip');
    }

    async observe(request, budget) {
        requireCwd(!this.busy && !this.restored, 'unexpected concurrent or completed tooltip reading');
        this.busy = true;
        const kind = request.kind;
        try {
            if (kind === prefix + 'prepare') {
                requireCwd(!this.prepared && process.platform === 'darwin', 'expected a private macOS tooltip observer');
                await runFile('/usr/bin/swiftc', ['-o', this.helper, swift], { timeout: budget(), maxBuffer: 8192 });
                this.prepared = true;
                return {};
            }
            return await this.capture(request, budget);
        } finally {
            this.busy = false;
        }
    }

    async capture(request, budget) {
        const { phase, action } = request;
        const step = this.plan[Object.keys(this.stages).length];
        const frame = { phase, action };
        try {
            requireCwd(this.prepared && step?.phase === phase, 'out-of-order tooltip reading');
            if (request.kind === prefix + 'fault') throw new Error(request.error);
            requireCwd(request.kind === prefix + 'stage', 'unexpected request kind');
            checkAction(step, action);

            const { paneId, rows, cols, isActive } = await this.observer.session(budget);
            requireCwd(isActive, 'the direct-local pane is inactive');
            this.observer.paneId ??= paneId;
            frame.pane = { id: paneId, rows, cols };
            this.pane ??= frame.pane;
            frame.terminalOutput = readOwnedOutputMode(request.tty, budget());
            const file = fileAt(phase), path = join(this.run.output, file);
            const result = await runFile(this.helper, [String(this.run.app.pid), path], { timeout: budget(), maxBuffer: 32768 });
            frame.native = JSON.parse(result.stdout);
            frame.image = { file, sha256: hash(bytes(this.run.output, file)) };
            frame.metadata = await this.observer.request('readPaneMetadata', { paneId }, budget);

            const { main, reading } = stageReading(this.run.output, frame, step, { ownerPid: this.run.app.pid }, this.pane, this.window);
            this.window = main;
            this.stages[phase] = reading;
            if (phase === 'launch') requireCwd(reading.metadataPath === null && reading.publicPath === null, 'startup has a directory whose incoming URI is unknown');
            if (phase === 'incoming') requireCwd(gradeCwd(this.plan, this.stages).incomingVerified, 'the fixture URI could not be verified before the attempt');
            if (phase === 'restored') {
                requireCwd(gradeCwd(this.plan, this.stages).restored, 'incoming directory was not restored and independently reread');
                this.restored = true;
            }
            return {};
        } catch (error) {
            frame.error = error.message;
            throw error;
        } finally {
            appendFileSync(join(this.run.output, journalFile), JSON.stringify(frame) + '\n', { mode: 0o600 });
        }
    }
}

function verifySources(output, context) {
    const raw = bytes(output, 'source-snapshot.json'), manifest = JSON.parse(raw);
    const sources = join(output, 'sources'), files = Object.entries(capturedOscCwdSourceFiles);
    requireCwd(manifest.schemaVersion === 1 && manifest.policy === capturedOscCwdSourcePolicy
        && hash(raw) === context.sourceSnapshotManifestSha256 && Object.keys(manifest.hashes).length === files.length
        && readdirSync(sources).length === files.length && same(json(sources, 'owned-source-inventory.json'), capturedOscCwdSourceFiles),
    'incomplete captured source graph');
    for (const [field, name] of files) {
        requireCwd(context[field] === manifest.hashes[field] && context[field] === hash(bytes(sources, name)), 'changed source ' + name);
    }
    assertRetainedSourceClosure(sources, capturedOscCwdSourceFiles);
    const dispatch = bytes(sources, capturedOscCwdSourceFiles.privateClipboardInput18SourceSha256).toString();
    requireCwd(dispatch.includes('switch (code)') && !dispatch.includes('9999'), 'OSC 9999 is not established as unhandled by the captured dispatch');
}

function verifyOwnership(context, ownership, settings) {
    const [proof] = ownership.proofs;
    const terminal = proof?.privateClipboard;
    requireCwd(context.sourceSnapshotPolicy === capturedOscCwdSourcePolicy && same(context.collectionScope, scope)
        && /^[a-f\d]{32}$/.test(context.runId) && Number.isFinite(Date.parse(context.startedAt))
        && isDigest(context.executableSha256) && isDigest(context.assemblySha256), 'wrong run, App or source identity');
    requireCwd(settings.SessionPersistence === 'Off' && settings.TabStripOrientation === 'Horizontal' && settings.Profiles?.length === 1
        && settings.Profiles[0].Name === 'terminfo disposable worker ' + context.runId && settings.AgentAccessObserveEnabled === true
        && settings.AgentAccessActEnabled === false && settings.AllowOsc52ClipboardWrite === false, 'changed disposable settings');
    requireCwd(ownership.proofs.length === 1 && same(ownership.collectionScope, scope) && ownership.completed === true
        && !ownership.cleanupError && !ownership.persistentDaemon && proof?.app.pid === context.ownerPid
        && proof.app.executable === context.executable && proof.worker.ppid === context.ownerPid
        && typeof terminal?.originalTerminalState === 'string' && terminal.originalTerminalState === terminal.restoredTerminalState
        && Date.parse(terminal.restoredAt) <= Date.parse(ownership.cleanedUpAt)
        && Date.parse(ownership.cleanedUpAt) <= Date.parse(ownership.sourceSnapshot?.removedAt)
        && ownership.sourceSnapshot.policy === capturedOscCwdSourcePolicy && proof.terminalOutput?.outputProcessing === false,
    'missing direct-local ownership, terminal restoration or verified cleanup');
    return proof.terminalOutput;
}

function replay(output, journal, context, terminalOutput) {
    const frames = journal.toString().trimEnd().split('\n').map(line => JSON.parse(line));
    const pane = frames[0]?.pane, plan = cwdPlan(context.runId);
    let window;
    requireCwd(frames.length === plan.length, 'missing or extra stages');
    const stages = Object.fromEntries(plan.map((step, index) => {
        const { main, reading } = stageReading(output, frames[index], step, context, pane, window);
        window = main;
        return [step.phase, { ...reading, request: frames[index].action.request, barrier: frames[index].action.barrier }];
    }));
    validateOutputGeometry(terminalOutput.description, pane.rows, pane.cols);
    const result = gradeCwd(plan, stages);
    requireCwd(result.incomingVerified && result.restored, 'incomplete verified fixture setup or restoration');
    return { runId: context.runId, pane, window, result };
}

function derive(output) {
    requireCwd(!existsSync(join(output, 'failure.json')) && !existsSync(join(output, 'cleanup-failure.json')), 'capture or cleanup failed');
    const contextBytes = bytes(output, 'context.json'), ownershipBytes = bytes(output, 'ownership-proof.json');
    const settingsBytes = bytes(output, 'initial-settings.json'), journal = bytes(output, journalFile);
    const context = JSON.parse(contextBytes), ownership = JSON.parse(ownershipBytes);
    requireCwd(hash(settingsBytes) === context.initialSettingsSha256, 'changed settings bytes');
    const terminalOutput = verifyOwnership(context, ownership, JSON.parse(settingsBytes));
    verifySources(output, context);
    const report = replay(output, journal, context, terminalOutput);
    const receipt = { schemaVersion: 1, runId: context.runId, contextSha256: hash(contextBytes), ownershipProofSha256: hash(ownershipBytes),
        journalSha256: hash(journal), fixtureRemoved: true, verdict: report.result.verdict };
    return { report, receipt };
}

export function completeReceipt(output, scratch) {
    requireCwd(!existsSync(scratch), 'disposable fixture remains');
    const { report, receipt } = derive(output);
    for (const [name, value] of [[reportFile, report], [receiptFile, receipt]]) {
        writeFileSync(join(output, name), JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
    }
    return receipt;
}

export function verifyOscCwdRun(output) {
    const derived = derive(output);
    requireCwd(same(json(output, receiptFile), derived.receipt) && same(json(output, reportFile), derived.report), 'run does not rederive');
    return derived;
}
