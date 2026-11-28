import { appendFileSync, existsSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { isDeepStrictEqual as same } from 'node:util';
import { colorCaptureRequest, observerEndpoint, screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { pristineColorTheme, validateColorCapture } from './color-capture.mjs';
import { copyPaneMetadata } from './metadata-state.mjs';
import { Collection as ReportCollection, verifyDirectReportCleanup, verifySeal } from './sixel-reports-owned.mjs';
import { gridOf, verifyControl } from './sixel-reports-contracts.mjs';
import { pristineImageBaseline, validateImageOwnership } from './static-images.mjs';
import { journal, json, read } from './static-images-receipt.mjs';
import { caseIds, colorState, effectId, expectedState, nonce, planFor, prefix, requireStack, resolveWrite, scope, verifySamples, witness } from './kitty-color-stack-contracts.mjs';

export { prefix, scope };
export const workerFile = 'kitty-color-stack-worker.mjs';
export const imageOwnership = true;
export const framesFile = 'kitty-color-stack-frames.jsonl';
const save = (output, file, value) => writeFileSync(join(output, file), JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 });

function verifyPane(frame, initial) {
    const screen = screenSnapshot(frame.screen, false, true, true, true, true, true);
    const metadata = copyPaneMetadata(frame.metadata);
    requireStack(!frame.ownership.daemon && frame.paneId === initial.paneId && metadata.paneId === frame.paneId
        && frame.sha256 === snapshotHash(screen) && frame.afterSha256 === frame.sha256 && same(frame.screen, screen)
        && metadata.presentation.paneActive && metadata.presentation.windowVisible
        && same(gridOf(screen), gridOf(initial.screen)) && same(gridOf(screen), gridOf(metadata.buffer))
        && metadata.buffer.alternateScreen === screen.bufferState.alternateScreen
        && metadata.buffer.cursorRow === screen.cursorRow && metadata.buffer.cursorCol === screen.cursorCol,
    'direct-local pane or paired screen and metadata did not agree');
}

function storedScreen(screen) {
    const { colorBinding, resolvedColors, ...stored } = screen;
    return stored;
}

function verifyScreen(step, frame, initial, previous, runId) {
    const { screen } = frame;
    if (step.stage === 'start' || step.stage === 'finish') {
        requireStack(pristineImageBaseline(screen) && frame.sha256 === initial.sha256 && same(frame.metadata, initial.metadata)
            && same(frame.theme, initial.theme) && pristineColorTheme(frame.theme), 'original pane or color overrides were not restored');
    } else if (step.stage === 'bind') {
        requireStack(screen.bufferState.alternateScreen && screen.lines[0] === nonce(runId) && screen.lines[1] === witness
            && screen.lines.slice(2).every(line => !line) && screen.cursorRow === 1 && screen.cursorCol === witness.length
            && screen.cellDetails.some(cell => cell.fg === 1), 'attributed screen witness was not established');
    } else {
        requireStack(same(storedScreen(previous.screen), storedScreen(screen)) && same(frame.metadata, previous.metadata),
            'operation changed stored cells, cursor, geometry, or unrelated pane state');
        if (step.sample) requireStack(frame.sha256 === previous.sha256 && same(frame.theme, previous.theme), 'query changed App colors');
    }
}

function verifyStage(frame, step, { initial, previous, runId, used, reads }) {
    requireStack(frame?.stage === step.stage && !Object.hasOwn(frame, 'error'), 'missing, failed or out-of-order stage');
    validateImageOwnership(frame.ownership, initial.ownership, (previous ?? initial).screen);
    requireStack(!previous || Date.parse(previous.ownership.at) <= Date.parse(frame.ownership.at), 'stage time moved backward');
    if (step.stage === 'seal') {
        verifySeal(frame, previous);
        return null;
    }
    verifyPane(frame, initial);
    verifyScreen(step, frame, initial, previous, runId);
    const original = colorState(initial.theme, initial.screen.colorBinding);
    const actual = colorState(frame.theme, frame.screen.colorBinding);
    if (step.state) {
        requireStack(same(actual, expectedState(original, step.state)), step.stage + ' did not preserve or restore the expected colors, overrides and full palette');
    }
    if (step.write) {
        requireStack(frame.requestBytes === resolveWrite(step, reads), 'fixture write bytes changed');
        verifyControl(frame.sample, used);
        if (step.pinned) return { type: 'write', request: frame.requestBytes };
    }
    if (!step.sample) return null;
    const result = verifySamples(step, frame.samples, used, actual);
    reads.set(step.stage, frame.samples);
    return result;
}

export class Collection extends ReportCollection {
    constructor(run) {
        super(run, { plan: planFor(run.runId), stagePrefix: prefix });
        this.before.includeResolvedColors = true;
        this.after.includeResolvedColors = true;
        this.reads = new Map();
    }

    async capture(budget) {
        const screen = await this.before.capture(budget), paneId = this.before.paneId;
        this.after.paneId ??= paneId;
        const metadata = copyPaneMetadata(await this.before.request('readPaneMetadata', { paneId }, budget));
        const endpoint = observerEndpoint(this.run.appData, this.run.app.pid);
        const rendered = await colorCaptureRequest(endpoint, paneId, null, budget());
        // Retain the App's structured theme projection. The experiment makes no pixel assertion.
        const { details: { theme } } = validateColorCapture(rendered, screen, null);
        const after = await this.after.capture(budget);
        return { paneId, screen, metadata, theme, sha256: snapshotHash(screen), afterSha256: snapshotHash(after) };
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
            verifyStage(frame, step, { initial: this.frames[0] ?? frame, previous: this.frames.at(-1), runId: this.run.runId, used: this.used, reads: this.reads });
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

function deriveStages(frames, runId) {
    const plan = planFor(runId), used = new Set(), reads = new Map();
    requireStack(/^[a-f\d]{32}$/.test(runId) && frames.length === plan.length, 'incomplete pane journal');
    const cases = caseIds.map(id => ({ id, exchanges: [] }));
    const observations = [];
    for (const [index, step] of plan.entries()) {
        const result = verifyStage(frames[index], step, { initial: frames[0], previous: frames[index - 1], runId, used, reads });
        if (step.pinned) cases.find(row => row.id === step.pinned).exchanges.push(result);
        else if (step.continuation) observations.at(-1).queriedPaletteEntries += result.queriedPaletteEntries;
        else if (result) observations.push(result);
    }
    return { cases, nested: { id: effectId, outcome: 'passed', observations } };
}

export function deriveRun(output) {
    const context = json(output, 'context.json'), frames = journal(output, framesFile);
    const worker = verifyDirectReportCleanup(output, context, frames, { scope, require: requireStack, allowAct: false });
    const report = { runId: context.runId, suite: scope.id, ...deriveStages(frames, context.runId),
        paneId: frames[0].paneId, tty: worker.tty, appPid: worker.app.pid, workerPid: worker.worker.pid, restored: true };
    const inputs = [framesFile, 'context.json', 'ownership-proof.json', 'source-snapshot.json', 'initial-settings.json'];
    const files = Object.fromEntries(inputs.map(file => [file, createHash('sha256').update(read(output, file)).digest('hex')]));
    requireStack(files['source-snapshot.json'] === context.sourceSnapshotManifestSha256
        && files['initial-settings.json'] === context.initialSettingsSha256, 'source or initial settings digest differs');
    return { context, frames, report, receipt: { schemaVersion: 1, suite: scope.id, runId: context.runId, fixtureRemoved: true, files } };
}

export function completeReceipt(output, scratch) {
    requireStack(!existsSync(scratch), 'fixture remains');
    const { report, receipt } = deriveRun(output);
    save(output, 'kitty-color-stack-report.json', report);
    save(output, 'kitty-color-stack-receipt.json', receipt);
    return { verdict: report.nested.outcome };
}
