import { execFile, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { appendFileSync, existsSync, lstatSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { once } from 'node:events';
import { join } from 'node:path';
import { createInterface } from 'node:readline';
import { setTimeout as delay } from 'node:timers/promises';
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
const helperName = 'observe-tab-tooltip';
const near = (actual, expected) => Math.abs(actual?.x - expected?.x) <= 1 && Math.abs(actual?.y - expected?.y) <= 1;

function bytes(output, file) {
    const path = join(output, file), stat = lstatSync(path);
    requireCwd(stat.isFile() && stat.size > 0 && stat.size < 16 * 1024 * 1024, 'invalid retained file ' + file);
    return readFileSync(path);
}

const json = (output, file) => JSON.parse(bytes(output, file));

function validateNative(read, pid, previous) {
    const { initial, before, hover, captured, restored, window, lines } = read;
    const states = [initial, before, hover, captured, restored];
    requireCwd(states.every(state => state?.frontPid === pid
        && Array.isArray(state.windows) && state.windows.every(item => item.pid === pid)),
    'the native reading is outside the owned foreground App');
    const times = states.map(state => state.uptime);
    requireCwd(times.every((time, index) => Number.isFinite(time) && time > (times[index - 1] ?? previous?.uptime ?? 0)),
        'stale or out-of-order native reading');
    const appWindows = state => state.windows.filter(item => item.layer === 0 && item.width >= 400 && item.height >= 200);
    const initialWindows = appWindows(initial), departureWindows = appWindows(before);
    const [startingWindow] = initialWindows, [main] = departureWindows;
    requireCwd(initialWindows.length === 1 && departureWindows.length === 1 && startingWindow.id === main.id
        && (!previous || same(main, previous.main)), 'the owned native window changed');
    const target = pointAt(main, 18);
    requireCwd(near(before.pointer, pointAt(startingWindow, 95)) && near(hover.pointer, target)
        && near(captured.pointer, hover.pointer) && near(restored.pointer, initial.pointer), 'unverified hover move or pointer restoration');
    const popups = hover.windows.filter(item => !before.windows.some(prior => prior.id === item.id)
        && Math.abs(item.x - target.x) < 80 && Math.abs(item.y - target.y) < 80
        && item.width > 20 && item.height > 10 && item.height < 120);
    requireCwd(popups.length === 1 && same(window, popups[0])
        && [hover, captured, restored].every(state => state.windows.some(item => same(item, main)))
        && captured.windows.some(item => same(item, window)), 'the tooltip window was not bound to the owned tab');
    requireCwd(Array.isArray(lines) && lines.length > 0 && lines.length <= 30
        && lines.every(line => typeof line.text === 'string' && line.text.length <= 256 && line.confidence >= 0 && line.confidence <= 1
            && line.bounds?.length === 4 && line.bounds.every(value => Number.isFinite(value) && value >= 0 && value <= 1)),
    'empty or malformed public tooltip reading');
    return { main, uptime: restored.uptime };
}

function stageReading(output, frame, step, context, pane, previous) {
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

    const { activationRequested } = frame.native;
    requireCwd(typeof activationRequested === 'boolean' && (!activationRequested || step.phase === 'launch'),
        'unexpected native activation during the attempt');
    const native = validateNative(frame.native, context.ownerPid, previous);
    const publicPath = readPublicPath(frame.native.lines);
    return { native, reading: { publicPath, metadataPath: workingDirectory, image: frame.image, lines: frame.native.lines } };
}

async function within(work, timeout, reason) {
    const controller = new AbortController();
    try {
        return await Promise.race([
            work, delay(timeout, null, { signal: controller.signal }).then(() => { throw new Error(reason); }),
        ]);
    } finally {
        controller.abort();
    }
}

export async function withFixture(run, collect) {
    requireCwd(process.platform === 'darwin', 'expected a macOS tooltip observer');
    const helper = join(run.scratch, helperName);
    await runFile('/usr/bin/swiftc', ['-o', helper, swift], { timeout: 20000, maxBuffer: 8192 });
    run.context.nativeHelperSha256 = hash(readFileSync(helper));

    const child = spawn(helper, ['--ocr-server'], { stdio: ['pipe', 'pipe', 'pipe'] });
    let diagnostic = '';
    child.stderr.on('data', chunk => { diagnostic = (diagnostic + chunk).slice(-4096); });
    const closed = once(child, 'close');
    closed.catch(() => {});
    child.stdin.on('error', () => {}); // A reader crash is reported by the closure of its output.
    const lines = createInterface({ input: child.stdout });
    const replies = lines[Symbol.asyncIterator]();
    const receive = async timeout => {
        const { value, done } = await within(replies.next(), timeout, 'native text reader timed out');
        requireCwd(!done && value.length <= 32768, 'native text reader stopped: ' + diagnostic);
        return JSON.parse(value);
    };
    let result;
    let failure;
    try {
        requireCwd(await Promise.race([run.done, receive(45000)]) === 'ready', 'native text reader did not warm up');
        run.readTooltipText = (path, budget) => {
            child.stdin.write(JSON.stringify(path) + '\n');
            return receive(budget());
        };
        result = await collect();
    } catch (error) {
        failure = error;
    }
    lines.close();
    child.stdin.end();
    try {
        const [code, signal] = await within(closed, 3000, 'native text reader did not exit');
        requireCwd(code === 0 && signal === null, 'native text reader exited unsuccessfully: ' + diagnostic);
        run.nativeReaderExit = { pid: child.pid, code, signal };
    } catch (error) {
        child.kill('SIGKILL');
        try { await closed; } catch { /* The failed spawn or exit is already recorded. */ }
        if (failure) throw new AggregateError([failure, error], 'Modern OSC 7 collection and native reader cleanup failed.');
        throw error;
    }
    if (failure) throw failure;
    writeFileSync(join(run.output, 'native-reader-exit.json'), JSON.stringify(run.nativeReaderExit) + '\n', { flag: 'wx', mode: 0o600 });
    return result;
}

export class Collection {
    constructor(run) {
        this.run = run;
        this.observer = new OwnedCellObserver({ appData: run.appData, ownerPid: run.app.pid, profileName: run.profileName });
        this.plan = cwdPlan(run.runId);
        this.stages = {};
        this.helper = join(run.scratch, helperName);
    }

    async observe(request, budget) {
        requireCwd(!this.busy && !this.restored, 'unexpected concurrent or completed tooltip reading');
        this.busy = true;
        try {
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
            requireCwd(step?.phase === phase, 'out-of-order tooltip reading');
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
            const nativeArgs = [String(this.run.app.pid), path, '/usr/sbin/screencapture', phase === 'launch' ? 'launch' : 'stage'];
            const result = await runFile(this.helper, nativeArgs, { timeout: budget(), maxBuffer: 32768 });
            frame.native = JSON.parse(result.stdout);
            frame.image = { file, sha256: hash(bytes(this.run.output, file)) };
            frame.native.lines = await this.run.readTooltipText(path, budget);
            frame.metadata = await this.observer.request('readPaneMetadata', { paneId }, budget);

            const { native, reading } = stageReading(this.run.output, frame, step, { ownerPid: this.run.app.pid }, this.pane, this.lastNative);
            this.lastNative = native;
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
        && isDigest(context.executableSha256) && isDigest(context.assemblySha256) && isDigest(context.nativeHelperSha256), 'wrong run, App or source identity');
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
    let lastNative;
    requireCwd(frames.length === plan.length, 'missing or extra stages');
    const stages = Object.fromEntries(plan.map((step, index) => {
        const { native, reading } = stageReading(output, frames[index], step, context, pane, lastNative);
        lastNative = native;
        return [step.phase, { ...reading, request: frames[index].action.request, barrier: frames[index].action.barrier }];
    }));
    validateOutputGeometry(terminalOutput.description, pane.rows, pane.cols);
    const result = gradeCwd(plan, stages);
    requireCwd(result.incomingVerified && result.restored, 'incomplete verified fixture setup or restoration');
    return { runId: context.runId, pane, window: lastNative.main, result };
}

function derive(output) {
    requireCwd(!existsSync(join(output, 'failure.json')) && !existsSync(join(output, 'cleanup-failure.json')), 'capture or cleanup failed');
    const contextBytes = bytes(output, 'context.json'), ownershipBytes = bytes(output, 'ownership-proof.json');
    const settingsBytes = bytes(output, 'initial-settings.json'), journal = bytes(output, journalFile);
    const context = JSON.parse(contextBytes), ownership = JSON.parse(ownershipBytes);
    const readerExit = bytes(output, 'native-reader-exit.json'), reader = JSON.parse(readerExit);
    requireCwd(Number.isSafeInteger(reader.pid) && reader.pid > 0 && reader.code === 0 && reader.signal === null,
        'missing verified native reader exit');
    requireCwd(hash(settingsBytes) === context.initialSettingsSha256, 'changed settings bytes');
    const terminalOutput = verifyOwnership(context, ownership, JSON.parse(settingsBytes));
    verifySources(output, context);
    const report = replay(output, journal, context, terminalOutput);
    const receipt = { schemaVersion: 1, runId: context.runId, contextSha256: hash(contextBytes), ownershipProofSha256: hash(ownershipBytes),
        journalSha256: hash(journal), nativeReaderExitSha256: hash(readerExit), fixtureRemoved: true, verdict: report.result.verdict };
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
