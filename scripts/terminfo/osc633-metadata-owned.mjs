import { createHash } from 'node:crypto';
import { appendFileSync, existsSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { isDeepStrictEqual as same } from 'node:util';
import { OwnedCellObserver, screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { validateInputBytes } from './input-state.mjs';
import { copyMetadataStatus, copyPaneMetadata } from './metadata-state.mjs';
import { pristineImageBaseline, validateImageOwnership } from './static-images.mjs';
import { journal, json, read } from './static-images-receipt.mjs';
import { deriveEffects, expectedCwd, nonce, planFor, prefix, requireMetadata, scope } from './osc633-metadata-contracts.mjs';

export { prefix, scope };
export const workerFile = 'osc633-metadata-worker.mjs';
export const imageOwnership = true; // Reuse the direct worker and kernel PTY identity proof.
const names = { frames: 'osc633-metadata-frames.jsonl', report: 'osc633-metadata-effects.json',
    receipt: 'osc633-metadata-receipt.json', context: 'context.json', proof: 'ownership-proof.json', source: 'source-snapshot.json',
    settings: 'initial-settings.json' };
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const save = (output, name, value) => writeFileSync(join(output, name), JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 });

function projection(raw, paneId) {
    const screen = screenSnapshot(raw.screen, false, true, true, true, true);
    const metadata = copyPaneMetadata(raw.metadata), status = copyMetadataStatus(raw.rawStatus, paneId);
    const { buffer, presentation } = metadata;
    requireMetadata(metadata.paneId === paneId && buffer.rows === screen.rows && buffer.cols === screen.cols
        && buffer.alternateScreen === screen.bufferState.alternateScreen && !buffer.alternateScreen
        && buffer.scrollbackLines === 0 && buffer.links.length === 0
        && presentation.windowVisible && presentation.paneActive && status.exitCode === null && status.needsUser === null,
    'the metadata does not describe the active, direct fixture pane');
    return { screen, metadata, status };
}

function checkSeal(attempt, sample) {
    requireMetadata(attempt?.completed === true && attempt.fenceRequested === true && attempt.error === null
        && same(attempt.nonces, sample.nonces) && attempt.hex === sample.hex + Buffer.from(sample.barrier).toString('hex'),
    'the last fenced exchange did not seal');
}

function checkStage(frame, step, initial, prior, runId, used) {
    requireMetadata(frame?.stage === step.stage && frame.paneId === initial.paneId, 'unexpected stage or pane');
    validateImageOwnership(frame.ownership, initial.ownership, initial.before.screen);
    requireMetadata(!prior || Date.parse(frame.ownership.at) >= Date.parse(prior.ownership.at), 'ownership time moved backward');
    if (step.stage === 'seal') {
        checkSeal(frame.attempts, prior.sample);
        return null;
    }

    const before = projection(frame.before, frame.paneId), after = projection(frame.after, frame.paneId);
    const { screen, metadata, status } = before;
    requireMetadata(same(before, after) && snapshotHash(screen) === frame.screenSha256
        && screen.rows === initial.before.screen.rows && screen.cols === initial.before.screen.cols
        && metadata.oscTitle === (initial.before.metadata.oscTitle ?? null), 'paired App state changed during a read');
    const baseline = expectedCwd(runId, 'p-baseline');
    if (step.stage === 'start' || step.stage === 'finish') {
        requireMetadata(pristineImageBaseline(screen) && frame.screenSha256 === initial.screenSha256
            && !metadata.buffer.acceptingCommandInput && metadata.buffer.commandStart === null && metadata.buffer.outputStart === null
            && status.currentCommand === null, 'clean viewport and lifecycle baseline were not verified');
    } else {
        requireMetadata(screen.lines[4] === nonce(runId) && screen.lines.slice(1).every((line, index) => index === 3 || line === ''),
            'fixture screen did not retain its nonce and blank control rows');
    }
    if (step.stage === 'bind' || step.stage === 'finish') {
        requireMetadata(metadata.workingDirectory === baseline && status.confidence === 'precise'
            && ['awaitingInput', 'idle'].includes(status.status), 'prepared directory or prompt baseline did not verify');
    }
    if (step.writes) {
        requireMetadata(same(frame.writes, step.writes) && frame.query === step.query, 'PTY write schedule differs');
        validateInputBytes(frame.sample, null, used, false);
        const reply = Buffer.from(frame.sample.hex, 'hex').toString('latin1');
        if (step.query) {
            // Pin the exact CPR, and cross-check the coordinates against the independent App read.
            requireMetadata(reply === '\x1b[' + (screen.cursorRow + 1) + ';' + (screen.cursorCol + 1) + 'R', 'CPR and App cursor disagree');
        } else requireMetadata(reply === '', 'unexpected reply to a metadata write');
    } else requireMetadata(!frame.writes && !frame.query && !frame.sample, 'initial frame unexpectedly carried PTY traffic');
    return before;
}

export class Collection {
    constructor(run) {
        this.run = run;
        this.plan = planFor(run.runId);
        this.frames = [];
        this.used = new Set();
        const options = { appData: run.appData, ownerPid: run.app.pid, profileName: run.profileName,
            includeCellDetails: true, includeCursorMode: true, includeOverline: true, includeBufferState: true };
        this.before = new OwnedCellObserver(options);
        this.after = new OwnedCellObserver(options);
    }

    async capture(observer, budget) {
        const screen = await observer.capture(budget);
        const paneId = observer.paneId;
        const metadata = await observer.request('readPaneMetadata', { paneId }, budget);
        const rawStatus = await observer.request('getSessionStatus', { paneId }, budget);
        return { screen, metadata, rawStatus };
    }

    async observe(request, budget, ownership) {
        const step = this.plan[this.frames.length];
        requireMetadata(!this.busy && step && request.kind === prefix + step.stage, 'concurrent, missing or out-of-order stage');
        this.busy = true;
        try {
            const frame = { stage: step.stage, ownership };
            if (step.stage === 'seal') {
                Object.assign(frame, { paneId: this.before.paneId, attempts: request.attempts });
            } else {
                frame.before = await this.capture(this.before, budget);
                frame.paneId = this.after.paneId ??= this.before.paneId;
                frame.after = await this.capture(this.after, budget);
                frame.screenSha256 = snapshotHash(frame.before.screen);
                Object.assign(frame, { writes: request.writes, query: request.query, sample: request.sample });
            }
            checkStage(frame, step, this.frames[0] ?? frame, this.frames.at(-1), this.run.runId, this.used);
            appendFileSync(join(this.run.output, names.frames), JSON.stringify(frame) + '\n', { mode: 0o600 });
            this.frames.push(frame);
            return {};
        } finally { this.busy = false; }
    }

    get restored() { return this.frames.length === this.plan.length; }
}

function checkCleanup(output, context, proof, settings, frames) {
    const owner = frames[0]?.ownership, worker = proof.proofs?.[0], terminal = worker?.privateClipboard;
    requireMetadata(!['failure.json', 'cleanup-failure.json'].some(name => existsSync(join(output, name)))
        && same(context.collectionScope, scope) && same(proof.collectionScope, scope) && proof.proofs?.length === 1
        && proof.completed === true && !proof.cleanupError && !proof.persistentDaemon
        && proof.sourceSnapshot?.policy === context.sourceSnapshotPolicy && proof.sourceSnapshot.root === context.sourceSnapshotRoot
        && Date.parse(frames.at(-1)?.ownership?.at) <= Date.parse(terminal?.restoredAt)
        && Date.parse(terminal?.restoredAt) <= Date.parse(proof.cleanedUpAt)
        && Date.parse(proof.cleanedUpAt) <= Date.parse(proof.sourceSnapshot.removedAt)
        && worker?.app.pid === context.ownerPid && same(worker.app, owner?.app) && same(worker.worker, owner?.worker)
        && worker.tty === owner.tty && typeof terminal?.originalTerminalState === 'string'
        && terminal.originalTerminalState === terminal.restoredTerminalState
        && settings.SessionPersistence === 'Off' && settings.AllowOsc52ClipboardWrite === false
        && settings.AgentAccessActEnabled === false && settings.CommandAssistEnabled === false,
    'owned worker cleanup or original PTY mode restoration did not verify');
}

export function deriveRun(output) {
    const context = json(output, names.context), proof = json(output, names.proof), settings = json(output, names.settings);
    const frames = journal(output, names.frames), plan = planFor(context.runId);
    requireMetadata(frames.length === plan.length, 'incomplete pane journal');
    const used = new Set();
    const projected = plan.map((step, index) => ({ stage: step.stage,
        before: checkStage(frames[index], step, frames[0], frames[index - 1], context.runId, used) }));
    checkCleanup(output, context, proof, settings, frames);
    const report = { runId: context.runId, suiteId: scope.id, cases: deriveEffects(context.runId, projected) };
    const files = Object.fromEntries([names.frames, names.context, names.proof, names.source, names.settings]
        .map(name => [name, hash(read(output, name))]));
    requireMetadata(files[names.source] === context.sourceSnapshotManifestSha256
        && files[names.settings] === context.initialSettingsSha256, 'source or settings digest differs');
    const receipt = { schemaVersion: 1, suiteId: scope.id, runId: context.runId, fixtureRemoved: true, files };
    return { context, frames, report, receipt };
}

export function completeReceipt(output, scratch) {
    requireMetadata(!existsSync(scratch), 'private fixture remains');
    const { report, receipt } = deriveRun(output);
    save(output, names.report, report);
    save(output, names.receipt, receipt);
    return { verdict: report.cases.map(({ caseId, outcome }) => ({ caseId, outcome })) };
}
