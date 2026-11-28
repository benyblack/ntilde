import { appendFileSync, existsSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { isDeepStrictEqual as same } from 'node:util';
import { screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { copyPaneMetadata } from './metadata-state.mjs';
import { Collection as ReportCollection, verifySeal } from './sixel-reports-owned.mjs';
import { gridOf, verifyControl } from './sixel-reports-contracts.mjs';
import { pristineImageBaseline, validateImageOwnership } from './static-images.mjs';
import { journal, json, read } from './static-images-receipt.mjs';
import { backgroundOf, planFor, prefix, requireState, scope, verifySamples, verifyWrite } from './device-state-contracts.mjs';

export { prefix, scope };
export const workerFile = 'device-state-worker.mjs';
export const imageOwnership = true;
const framesFile = 'device-state-frames.jsonl';
const save = (output, file, value) => writeFileSync(join(output, file), JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 });

function verifyPane(frame, initial) {
    const screen = screenSnapshot(frame.screen, false, true, true, true, true, true);
    const metadata = copyPaneMetadata(frame.metadata);
    requireState(!frame.ownership.daemon && frame.paneId === initial.paneId && metadata.paneId === frame.paneId
        && frame.sha256 === snapshotHash(screen) && frame.afterSha256 === frame.sha256 && same(frame.screen, screen)
        && metadata.presentation.paneActive && metadata.presentation.windowVisible
        && same(gridOf(screen), gridOf(initial.screen)) && same(gridOf(screen), gridOf(metadata.buffer))
        && metadata.buffer.alternateScreen === screen.bufferState.alternateScreen
        && metadata.buffer.cursorRow === screen.cursorRow && metadata.buffer.cursorCol === screen.cursorCol,
    'direct-local pane or paired screen and metadata did not agree');
}

function verifyStage(frame, step, initial, previous, runId, used) {
    requireState(frame?.stage === step.stage && !Object.hasOwn(frame, 'error'), 'missing, failed or out-of-order stage');
    validateImageOwnership(frame.ownership, initial.ownership, (previous ?? initial).screen);
    requireState(!previous || Date.parse(previous.ownership.at) <= Date.parse(frame.ownership.at), 'stage time moved backward');
    if (step.stage === 'seal') {
        verifySeal(frame, previous);
        return null;
    }
    verifyPane(frame, initial);
    if (step.write) {
        requireState(frame.requestBytes === step.write, 'fixture bytes changed');
        verifyControl(frame.sample, used);
    }
    if (step.stage === 'start' || step.stage === 'finish') {
        requireState(pristineImageBaseline(frame.screen) && frame.sha256 === initial.sha256 && same(frame.metadata, initial.metadata)
            && !frame.screen.colorBinding.backgroundOverride && !frame.screen.colorBinding.reverseVideo,
        'initial pane was not pristine or its original state and background were not restored');
    } else if (step.write) {
        verifyWrite(step, frame.screen, initial.screen, previous.screen, runId);
    } else {
        requireState(frame.sha256 === previous.sha256 && same(frame.metadata, previous.metadata),
            'query changed cells, cursor, effective colors, modes or pane state');
    }
    return step.sample ? verifySamples(step, frame.samples, used, frame.screen) : null;
}

export class Collection extends ReportCollection {
    constructor(run) {
        super(run, { plan: planFor(run.runId), stagePrefix: prefix });
        this.before.includeResolvedColors = true;
        this.after.includeResolvedColors = true;
    }

    async observeStage(request, budget, ownership, step) {
        const frame = { stage: step.stage, ownership };
        try {
            if (step.stage === 'seal') frame.attempts = request.attempts;
            else {
                if (step.write) Object.assign(frame, { requestBytes: request.requestBytes, sample: request.sample });
                if (step.sample) frame.samples = request.samples;
                Object.assign(frame, await this.capture(budget));
            }
            verifyStage(frame, step, this.frames[0] ?? frame, this.frames.at(-1), this.run.runId, this.used);
        } catch (error) {
            frame.error = error.message;
            throw error;
        } finally {
            appendFileSync(join(this.run.output, framesFile), JSON.stringify(frame) + '\n', { mode: 0o600 });
        }
        this.frames.push(frame);
        return {};
    }
}

function verifyCleanup(output, context, frames) {
    const proof = json(output, 'ownership-proof.json'), settings = json(output, 'initial-settings.json');
    const worker = proof.proofs?.[0], raw = worker?.privateClipboard, initial = frames[0]?.ownership;
    requireState(!['failure.json', 'cleanup-failure.json'].some(file => existsSync(join(output, file)))
        && same(context.collectionScope, scope) && same(proof.collectionScope, scope) && proof.proofs?.length === 1
        && proof.completed === true && !proof.cleanupError && !proof.persistentDaemon
        && proof.sourceSnapshot?.policy === context.sourceSnapshotPolicy && Date.parse(proof.cleanedUpAt) <= Date.parse(proof.sourceSnapshot.removedAt)
        && Date.parse(frames.at(-1)?.ownership?.at) <= Date.parse(raw?.restoredAt) && Date.parse(raw?.restoredAt) <= Date.parse(proof.cleanedUpAt)
        && worker?.app.pid === context.ownerPid && worker.app.executable === context.executable
        && same(worker.app, initial?.app) && same(worker.worker, initial?.worker) && worker.tty === initial.tty
        && typeof raw?.originalTerminalState === 'string' && raw.originalTerminalState === raw.restoredTerminalState
        && settings.SessionPersistence === 'Off' && settings.AllowOsc52ClipboardWrite === false
        && settings.AgentAccessObserveEnabled === true && settings.AgentAccessActEnabled === false,
    'direct local ownership and exact PTY cleanup did not verify');
    return worker;
}

export function deriveRun(output) {
    const context = json(output, 'context.json'), frames = journal(output, framesFile);
    const worker = verifyCleanup(output, context, frames);
    const plan = planFor(context.runId), used = new Set();
    requireState(/^[a-f\d]{32}$/.test(context.runId) && frames.length === plan.length, 'incomplete pane journal');
    const samples = plan.map((step, index) =>
        verifyStage(frames[index], step, frames[0], frames[index - 1], context.runId, used)).filter(Boolean);
    const report = { runId: context.runId, suite: scope.id, exchanges: samples.filter(row => row.outcome),
        colorControls: samples.filter(row => !row.outcome), initialBackground: backgroundOf(frames[0].screen),
        paneId: frames[0].paneId, tty: worker.tty, appPid: worker.app.pid, workerPid: worker.worker.pid, restored: true };
    const inputs = [framesFile, 'context.json', 'ownership-proof.json', 'source-snapshot.json', 'initial-settings.json'];
    const files = Object.fromEntries(inputs.map(file => [file, createHash('sha256').update(read(output, file)).digest('hex')]));
    requireState(files['source-snapshot.json'] === context.sourceSnapshotManifestSha256
        && files['initial-settings.json'] === context.initialSettingsSha256, 'source or initial settings digest differs');
    return { context, frames, report, receipt: { schemaVersion: 1, suite: scope.id, runId: context.runId, fixtureRemoved: true, files } };
}

export function completeReceipt(output, scratch) {
    requireState(!existsSync(scratch), 'fixture remains');
    const { report, receipt } = deriveRun(output);
    save(output, 'device-state-report.json', report);
    save(output, 'device-state-receipt.json', receipt);
    return { verdict: Object.fromEntries(report.exchanges.map(row => [row.stage, row.outcome])) };
}
