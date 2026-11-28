import { appendFileSync, existsSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { isDeepStrictEqual as same } from 'node:util';
import { OwnedCellObserver, screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { copyPaneMetadata } from './metadata-state.mjs';
import { pngHash } from './pixels-capture.mjs';
import { resizeOwnedPane } from './resize-action.mjs';
import { pristineImageBaseline, validateImageOwnership } from './static-images.mjs';
import { journal, json, read } from './static-images-receipt.mjs';
import { validateOutputGeometry } from './terminal-output.mjs';
import { gridOf, nonce, planFor, prefix, requireSixel, sameScale, scope, smallerGrid, verifyControl, verifySample } from './sixel-reports-contracts.mjs';

export { prefix, scope };
export const workerFile = 'sixel-reports-worker.mjs';
export const allowGeneratedInput = true;
export const imageOwnership = true;
const names = { frames: 'sixel-frames.jsonl', actions: 'sixel-resizes.jsonl', report: 'sixel-report.json',
    receipt: 'sixel-receipt.json', context: 'context.json', proof: 'ownership-proof.json', source: 'source-snapshot.json',
    settings: 'initial-settings.json' };
const save = (output, name, value) => writeFileSync(join(output, name), JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
const append = (output, name, value) => appendFileSync(join(output, name), JSON.stringify(value) + '\n', { mode: 0o600 });
const validTime = value => typeof value === 'string' && Number.isFinite(Date.parse(value)) && new Date(value).toISOString() === value;

export function requireResize(action, stage, target, paneId, runId) {
    requireSixel(action?.stage === stage && same(action.target, target) && !action.error, 'unexpected resize action');
    const { observed } = action;
    requireSixel(observed?.paneId === paneId && observed.runId === runId && validTime(observed.observedAt)
        && observed.app?.exact === true && observed.app.layoutSettled === true && same(gridOf(observed.app), target)
        && observed.terminalOutput?.tool === '/bin/stty' && observed.terminalOutput.outputProcessing === false, 'resize did not settle exactly');
    validateOutputGeometry(observed.terminalOutput.description, target.rows, target.cols);
}

export function verifySeal(record, finish) {
    const { attempts } = record, { sample } = finish;
    requireSixel(attempts?.completed === true && attempts.fenceRequested === true && attempts.error === null
        && attempts.hex === sample.hex + Buffer.from(sample.barrier).toString('hex') && same(attempts.nonces, sample.nonces),
    'raw capture did not seal with the final control');
}

function verifyPane(frame, step, initial, target, runId) {
    const screen = screenSnapshot(frame.screen, false, true, true, true, true);
    requireSixel(frame.paneId === initial.paneId && frame.sha256 === snapshotHash(screen)
        && frame.afterSha256 === frame.sha256 && same(gridOf(screen), target), 'changed pane or unstable App grid');
    const { paneId, cellGeometry, buffer, presentation } = copyPaneMetadata(frame.metadata);
    requireSixel(paneId === frame.paneId && same(gridOf(buffer), target) && presentation.windowVisible && presentation.paneActive
        && buffer.alternateScreen === screen.bufferState.alternateScreen && cellGeometry && sameScale(cellGeometry, initial.metadata.cellGeometry),
    'App cell geometry or scale was unavailable or changed');
    const main = step.stage === 'start' || step.stage === 'finish';
    if (main) {
        requireSixel(pristineImageBaseline(screen) && frame.sha256 === initial.sha256, 'original screen was not pristine or restored');
    } else {
        requireSixel(screen.bufferState.alternateScreen && screen.lines[0] === nonce(runId)
            && screen.cursorRow === 0 && screen.cursorCol === nonce(runId).length && screen.lines.slice(1).every(line => line === ''),
        'nonce or blank alternate-screen control changed');
    }
    return cellGeometry;
}

function verifyStage(frame, step, initial, previous, target, runId, used) {
    requireSixel(frame?.stage === step.stage, 'missing or out-of-order stage');
    if (step.stage === 'seal') {
        validateImageOwnership(frame.ownership, initial.ownership, previous.screen);
        verifySeal(frame, previous);
        return null;
    }
    const cell = verifyPane(frame, step, initial, target, runId);
    const sizeBefore = previous?.screen ?? frame.screen;
    validateImageOwnership(frame.ownership, initial.ownership, sizeBefore);
    if (step.resize) requireResize(frame.action, step.stage, target, frame.paneId, runId);
    if (step.write) {
        requireSixel(frame.requestBytes === step.write, 'fixture write changed');
        verifyControl(frame.sample, used);
    }
    return step.sample ? verifySample(frame.samples, used, cell) : null;
}

export class Collection {
    constructor(run, { plan = planFor(run.runId), actionsFile = names.actions, stagePrefix = prefix } = {}) {
        this.run = run;
        this.plan = plan;
        this.actionsFile = actionsFile;
        this.stagePrefix = stagePrefix;
        this.frames = [];
        this.used = new Set();
        // Use unbound readers. The plan owns all geometry assertions across resize.
        const options = { appData: run.appData, ownerPid: run.app.pid, profileName: run.profileName,
            includeCellDetails: true, includeCursorMode: true, includeOverline: true, includeBufferState: true };
        this.before = new OwnedCellObserver(options);
        this.after = new OwnedCellObserver(options);
    }

    async observe(request, budget, ownership) {
        const stage = request.kind.slice(this.stagePrefix.length), step = this.plan[this.frames.length];
        requireSixel(!this.busy && !this.restored && !this.aborted && (step?.stage === stage || stage === 'abort'), 'unexpected or concurrent stage');
        this.busy = true;
        try {
            if (stage === 'abort') return await this.abort(request, budget);
            return await this.observeStage(request, budget, ownership, step);
        } finally { this.busy = false; }
    }

    async abort(request, budget) {
        this.aborted = true;
        if (this.resizeAttempted) {
            const original = this.frames[0], target = gridOf(original.screen);
            const action = await this.resize('abort', target, request, budget);
            requireResize(action, 'abort', target, original.paneId, this.run.runId);
        }
        return {};
    }

    async resize(stage, target, request, budget) {
        this.resizeAttempted = true;
        const { app, appData, executable, runId, output } = this.run;
        const config = { app, appData, executable, paneId: this.before.paneId, uid: process.getuid(), runId,
            workerPid: request.workerPid, tty: request.tty };
        const action = { stage, target, observed: null };
        try {
            action.observed = await resizeOwnedPane(config, target.rows, target.cols, budget);
            return action;
        } catch (error) {
            action.error = error.message;
            throw error;
        } finally { append(output, this.actionsFile, action); }
    }

    async capture(budget, { settling = false } = {}) {
        const screen = await this.before.capture(budget), paneId = this.before.paneId;
        this.after.paneId ??= paneId;
        const wire = await this.before.request('readPaneMetadata', { paneId }, budget);
        const metadata = settling ? wire : copyPaneMetadata(wire);
        const after = await this.after.capture(budget);
        requireSixel(settling || snapshotHash(screen) === snapshotHash(after), 'App pane changed during paired read');
        return { paneId, screen, metadata, sha256: snapshotHash(screen), afterSha256: snapshotHash(after) };
    }

    async observeStage(request, budget, ownership, step) {
        const initial = this.frames[0], previous = this.frames.at(-1);
        const frame = { stage: step.stage, ownership };
        let target = previous ? gridOf(previous.screen) : null;
        if (step.resize) {
            target = step.resize === 'original' ? gridOf(initial.screen) : smallerGrid(initial.metadata.cellGeometry);
            frame.action = await this.resize(step.stage, target, request, budget);
        }
        if (step.stage === 'seal') frame.attempts = request.attempts;
        else Object.assign(frame, await this.capture(budget));
        if (step.write) Object.assign(frame, { requestBytes: request.requestBytes, sample: request.sample });
        if (step.sample) frame.samples = request.samples;
        verifyStage(frame, step, initial ?? frame, previous, target ?? gridOf(frame.screen), this.run.runId, this.used);
        append(this.run.output, names.frames, frame);
        this.frames.push(frame);
        return {};
    }

    get restored() { return !this.aborted && this.frames.length === this.plan.length; }
}

/** Recheck the direct-local custody and kernel restoration common to the owned device queries. */
export function verifyDirectReportCleanup(output, context, frames, { scope, require, allowAct }) {
    const proof = json(output, names.proof), settings = json(output, names.settings);
    const worker = proof.proofs?.[0], raw = worker?.privateClipboard, initial = frames[0]?.ownership;
    const error = 'direct local ownership and exact PTY cleanup did not verify';
    require(!['failure.json', 'cleanup-failure.json'].some(file => existsSync(join(output, file)))
        && same(context.collectionScope, scope) && same(proof.collectionScope, scope) && proof.proofs?.length === 1
        && proof.completed === true && !proof.cleanupError && !proof.persistentDaemon
        && proof.sourceSnapshot?.policy === context.sourceSnapshotPolicy && Date.parse(proof.cleanedUpAt) <= Date.parse(proof.sourceSnapshot.removedAt), error);
    require(Date.parse(frames.at(-1)?.ownership?.at) <= Date.parse(raw?.restoredAt) && Date.parse(raw?.restoredAt) <= Date.parse(proof.cleanedUpAt)
        && worker?.app.pid === context.ownerPid && worker.app.executable === context.executable
        && same(worker.app, initial?.app) && same(worker.worker, initial?.worker) && worker.tty === initial.tty
        && typeof raw?.originalTerminalState === 'string' && raw.originalTerminalState === raw.restoredTerminalState, error);
    require(settings.SessionPersistence === 'Off' && settings.AllowOsc52ClipboardWrite === false && settings.AgentAccessObserveEnabled === true
        && (allowAct === undefined || settings.AgentAccessActEnabled === allowAct), error);
    return worker;
}

function deriveStages(frames, actions, runId) {
    const plan = planFor(runId), initial = frames[0];
    requireSixel(frames.length === plan.length && actions.length === 2 && /^[a-f\d]{32}$/.test(runId)
        && /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(initial.paneId), 'missing or incomplete pane journal');
    const used = new Set(), samples = [];
    let target = gridOf(initial.screen), lastTime = -Infinity, resized = 0;
    for (const [index, step] of plan.entries()) {
        const frame = frames[index];
        const at = Date.parse(frame.ownership.at);
        requireSixel(lastTime <= at, 'observation or action time moved backward');
        lastTime = at;
        if (step.resize) {
            target = step.resize === 'original' ? gridOf(initial.screen) : smallerGrid(initial.metadata.cellGeometry);
            requireSixel(same(actions[resized++], frame.action), 'action journal differs from paired pane stage');
            lastTime = Date.parse(frame.action.observed.observedAt);
            requireSixel(at <= lastTime, 'resize preceded its admission');
        }
        const sample = verifyStage(frame, step, initial, frames[index - 1], target, runId, used);
        if (sample) samples.push({ stage: step.stage, ...sample });
    }
    const [before, after, restored] = samples;
    requireSixel(same(before.geometry, restored.geometry) && !same(before.geometry.expected, after.geometry.expected),
        'resize did not provide distinct geometry and exact restoration controls');
    return { samples, actions, restored: true };
}

export function deriveRun(output) {
    const context = json(output, names.context), proof = json(output, names.proof), settings = json(output, names.settings);
    const frames = journal(output, names.frames), actions = journal(output, names.actions);
    const worker = proof.proofs?.[0], raw = worker?.privateClipboard, initial = frames[0]?.ownership;
    requireSixel(!['failure.json', 'cleanup-failure.json'].some(name => existsSync(join(output, name)))
        && same(context.collectionScope, scope) && same(proof.collectionScope, scope) && proof.proofs?.length === 1
        && proof.completed === true && !proof.cleanupError && !proof.persistentDaemon
        && proof.sourceSnapshot?.policy === context.sourceSnapshotPolicy && Date.parse(proof.cleanedUpAt) <= Date.parse(proof.sourceSnapshot.removedAt)
        && Date.parse(frames.at(-1)?.ownership?.at) <= Date.parse(proof.cleanedUpAt)
        && worker?.app.pid === context.ownerPid && same(worker.app, initial?.app) && same(worker.worker, initial?.worker)
        && worker.tty === initial.tty && typeof raw?.originalTerminalState === 'string' && raw.originalTerminalState === raw.restoredTerminalState
        && settings.SessionPersistence === 'Off' && settings.AllowOsc52ClipboardWrite === false && settings.AgentAccessActEnabled === true,
    'direct local ownership and exact PTY cleanup did not verify');
    const report = { runId: context.runId, suite: scope.id, ...deriveStages(frames, actions, context.runId) };
    const inputs = [names.frames, names.actions, names.context, names.proof, names.source, names.settings];
    const files = Object.fromEntries(inputs.map(name => [name, pngHash(read(output, name))]));
    requireSixel(files[names.source] === context.sourceSnapshotManifestSha256 && files[names.settings] === context.initialSettingsSha256,
        'source or initial settings digest differs');
    return { context, frames, report, receipt: { schemaVersion: 1, suite: scope.id, runId: context.runId, fixtureRemoved: true, files } };
}

export function completeReceipt(output, scratch) {
    requireSixel(!existsSync(scratch), 'fixture remains');
    const { report, receipt } = deriveRun(output);
    save(output, names.report, report);
    save(output, names.receipt, receipt);
    return { verdict: report.samples.map(row => ({ stage: row.stage, ...row.geometry })) };
}
