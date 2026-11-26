import { appendFileSync, existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { isDeepStrictEqual as same } from 'node:util';
import { OwnedCellObserver, captureRequest, observerEndpoint, screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { cursorReportQuery, validateCursorReport } from './cursor-report.mjs';
import { pngHash, retainedCapture, stablePixelContext, totalPixelLimit, validatePixelCapture } from './pixels-capture.mjs';
import { pristineImageBaseline, validateImageOwnership } from './static-images.mjs';
import { journal, json, read } from './static-images-receipt.mjs';
import { da, gradeInk, nonce, planFor, prefix, requireOsc66, scope } from './osc66-contracts.mjs';

export { prefix, scope };
export const workerFile = 'osc66-worker.mjs';
export const imageOwnership = true;
const pixelDir = 'osc66-pixels';
const names = { frames: 'osc66-frames.jsonl', report: 'osc66-report.json', receipt: 'osc66-receipt.json',
    context: 'context.json', proof: 'ownership-proof.json', source: 'source-snapshot.json', settings: 'initial-settings.json' };

const isBarrier = value => typeof value === 'string' && value.length <= 128 && /^\x1b\[\?[\d;]+c$/.test(value);
const pixelFile = (index, side) => pixelDir + '/frame-' + String(index * 2 + side).padStart(3, '0') + '.png';
const write = (output, name, value) => writeFileSync(join(output, name), JSON.stringify(value, null, 2) + '\n', { mode: 0o600, flag: 'wx' });

function verifyFrame(frame, step, first, runId) {
    requireOsc66(frame?.stage === step.stage && frame.paneId === first.paneId, 'stage or pane differs');
    const screen = screenSnapshot(frame.screen, false, true, true, true, true);
    requireOsc66(frame.sha256 === snapshotHash(screen) && screen.rows === first.screen.rows && screen.cols === first.screen.cols
        && screen.rows >= 8 && screen.cols >= 60, 'screen hash or geometry differs');
    validateImageOwnership(frame.ownership, first.ownership, screen);
    const query = step.query ? cursorReportQuery : '';
    const expected = step.bytes === null ? null : step.bytes + query + da;
    requireOsc66(frame.requestBytes === expected && (expected === null ? frame.barrier === null : isBarrier(frame.barrier)), 'write or fence differs');
    const main = ['start', 'finish'].includes(step.stage);
    requireOsc66(screen.bufferState.alternateScreen === !main && !screen.bufferState.originMode && !screen.bufferState.insertMode,
        'wrong screen or origin/insert mode');
    if (step.stage === 'start') requireOsc66(pristineImageBaseline(screen), 'main screen did not start pristine');
    if (step.stage === 'finish') requireOsc66(frame.sha256 === first.sha256, 'main screen did not restore');
    if (step.stage === 'bind') requireOsc66(screen.lines[0] === nonce(runId) && screen.cursorRow === 0
        && screen.cursorCol === nonce(runId).length, 'App pane nonce did not bind');
    if (step.pixel && !main) requireOsc66(!screen.cursorVisible && screen.lines[4][2] === '|'
        && screen.lines[4].endsWith('KEEP') && screen.lines[6] === '    KEEP', 'visible fixture guards or cursor changed');
    if (!step.query) return null;
    const { reply } = validateCursorReport(frame);
    requireOsc66(reply === null || (reply.row === screen.cursorRow + 1 && reply.col === screen.cursorCol + 1), 'CPR disagrees with physical cursor');
    return { stage: step.stage, write: step.bytes, query, raw: reply?.raw ?? null, barrier: frame.barrier,
        physical: { row: screen.cursorRow + 1, col: screen.cursorCol + 1 } };
}

function verifyPair(frame, step, index, getCapture) {
    if (!step.pixel) {
        requireOsc66(frame.pixels === null && frame.afterScreen === null && frame.afterSha256 === null, 'unexpected pixel data');
        return null;
    }
    const after = screenSnapshot(frame.afterScreen, false, true, true, true, true);
    requireOsc66(frame.afterSha256 === snapshotHash(after) && frame.afterSha256 === frame.sha256
        && frame.pixels?.length === 2, 'render did not have a stable screen on both sides');
    const pair = frame.pixels.map((pixel, side) => {
        requireOsc66(pixel.file === pixelFile(index, side) && pixel.presentationTimeMs === 0, 'pixel capture order or phase differs');
        return getCapture(pixel, side);
    });
    requireOsc66(frame.pixels[0].sha256 === frame.pixels[1].sha256
        && same(pair[0].details, pair[1].details), 'render pair differs');
    return pair[0];
}

export class Collection {
    constructor(run) {
        const options = { appData: run.appData, ownerPid: run.app.pid, profileName: run.profileName,
            nonce: nonce(run.runId), prefix: 'osc66', includeCellDetails: true, includeCursorMode: true,
            includeOverline: true, includeBufferState: true };
        this.before = new OwnedCellObserver(options);
        this.after = new OwnedCellObserver(options);
        this.run = run;
        this.frames = [];
        mkdirSync(join(run.output, pixelDir), { mode: 0o700 });
    }

    async render(paneId, screen, index, side, budget) {
        const endpoint = observerEndpoint(this.run.appData, this.run.app.pid);
        const result = await captureRequest(endpoint, paneId, 0, budget());
        const view = validatePixelCapture(result, screen, 0);
        this.pixelTotal = (this.pixelTotal ?? 0) + view.image.width * view.image.height;
        requireOsc66(this.pixelTotal <= totalPixelLimit, 'pixel budget exhausted');
        const { pngBase64, ...description } = result;
        const file = pixelFile(index, side);
        writeFileSync(join(this.run.output, file), view.bytes, { mode: 0o600, flag: 'wx' });
        return { pixel: { ...description, file, sha256: pngHash(view.bytes) }, view };
    }

    async capturePair(frame, index, budget) {
        this.after.paneId ??= frame.paneId;
        const first = await this.render(frame.paneId, frame.screen, index, 0, budget);
        const second = await this.render(frame.paneId, frame.screen, index, 1, budget);
        const afterScreen = await this.after.capture(budget);
        return { pixels: [first.pixel, second.pixel], afterScreen, afterSha256: snapshotHash(afterScreen), views: [first.view, second.view] };
    }

    async observe(request, budget, ownership) {
        const index = this.frames.length, stage = request.kind.slice(prefix.length);
        const step = this.plan?.[index] ?? { stage: 'start', bytes: null, pixel: true };
        requireOsc66(!this.busy && !this.restored && stage === step.stage, 'unexpected or concurrent stage');
        this.busy = true;
        try {
            const operation = ['start', 'bind', 'finish'].includes(stage) ? stage : 'read';
            const { paneId, screen } = await this.before.handle(prefix + operation, budget, request.barrier);
            const frame = { stage, paneId, screen, sha256: snapshotHash(screen), requestBytes: request.requestBytes ?? null,
                barrier: request.barrier ?? null, ownership, pixels: null, afterScreen: null, afterSha256: null };
            if (step.query) frame.reply = request.reply;
            verifyFrame(frame, step, this.frames[0] ?? frame, this.run.runId);
            if (step.pixel) {
                const { views, ...pair } = await this.capturePair(frame, index, budget);
                Object.assign(frame, pair);
                verifyPair(frame, step, index, (_, side) => views[side]);
            }
            if (index === 0) this.plan = planFor(this.run.runId, screen.cursorVisible);
            appendFileSync(join(this.run.output, names.frames), JSON.stringify(frame) + '\n', { mode: 0o600 });
            this.frames.push(frame);
            return index === 0 ? { cursorVisible: screen.cursorVisible } : {};
        } finally {
            this.busy = false;
        }
    }

    get restored() { return this.before.restored && this.frames.length === this.plan?.length; }
}

function deriveFrames(output, frames, runId) {
    const first = frames[0], plan = planFor(runId, first?.screen?.cursorVisible);
    requireOsc66(frames.length === plan.length && /^[a-f\d]{32}$/.test(runId)
        && /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(first.paneId), 'incomplete frame or pane journal');
    const captures = {}, exchanges = [];
    let stable, pixelCount = 0, lastTime = -Infinity;
    for (const [index, stage] of plan.entries()) {
        const frame = frames[index], exchange = verifyFrame(frame, stage, first, runId);
        const at = Date.parse(frame.ownership.at);
        requireOsc66(lastTime <= at, 'stage ownership time moved backward');
        lastTime = at;
        if (exchange) exchanges.push(exchange);
        const view = verifyPair(frame, stage, index, pixel => {
            const retained = retainedCapture(output, { pixel }, pixelDir);
            pixelCount += pixel.width * pixel.height;
            requireOsc66(pixelCount <= totalPixelLimit, 'retained pixel budget exhausted');
            return validatePixelCapture(retained, frame.screen, 0);
        });
        if (!view) continue;
        stable ??= stablePixelContext(view.details);
        requireOsc66(stablePixelContext(view.details) === stable, 'render context changed');
        captures[stage.stage] = view;
    }
    requireOsc66(captures.start.bytes.equals(captures.finish.bytes) && captures.blank.bytes.equals(captures.cleared.bytes),
        'main screen or visible fixture pixels did not restore');
    const { grid, theme, font, platform, architecture } = captures.blank.details;
    const views = Object.fromEntries(Object.entries(captures).map(([stage, capture]) => [stage, capture.image]));
    return { exchanges, ownedPixels: { ...gradeInk(views, grid, theme.backgroundArgb), grid, font, platform, architecture,
        restoredMain: true, restoredFixture: true, frames: frames.filter(frame => frame.pixels).map(frame => ({
            stage: frame.stage, pngs: frame.pixels.map(pixel => pixel.file),
        })) } };
}

export function deriveRun(output) {
    const context = json(output, names.context), proof = json(output, names.proof), settings = json(output, names.settings);
    const worker = proof.proofs?.[0], raw = worker?.privateClipboard;
    const frames = journal(output, names.frames), initial = frames[0]?.ownership;
    requireOsc66(!['failure.json', 'cleanup-failure.json'].some(name => existsSync(join(output, name)))
        && same(context.collectionScope, scope) && same(proof.collectionScope, scope) && proof.proofs?.length === 1
        && proof.completed === true && !proof.cleanupError && !proof.persistentDaemon
        && proof.sourceSnapshot?.policy === context.sourceSnapshotPolicy
        && Date.parse(frames.at(-1)?.ownership?.at) <= Date.parse(proof.cleanedUpAt)
        && Date.parse(proof.cleanedUpAt) <= Date.parse(proof.sourceSnapshot.removedAt)
        && worker?.app.pid === context.ownerPid && same(worker.app, initial?.app) && same(worker.worker, initial?.worker)
        && worker.tty === initial.tty && typeof raw?.originalTerminalState === 'string' && raw.originalTerminalState === raw.restoredTerminalState
        && settings.SessionPersistence === 'Off' && settings.AllowOsc52ClipboardWrite === false && settings.AgentAccessActEnabled === false,
    'direct owned fixture or its cleanup did not verify');
    const report = { runId: context.runId, suite: scope.id, ...deriveFrames(output, frames, context.runId) };
    const bound = [names.context, names.proof, names.source, names.settings, names.frames,
        ...frames.flatMap(frame => (frame.pixels ?? []).map(pixel => pixel.file))];
    const files = Object.fromEntries(bound.map(name => [name, pngHash(read(output, name))]));
    requireOsc66(files[names.source] === context.sourceSnapshotManifestSha256 && files[names.settings] === context.initialSettingsSha256,
        'source or settings digest differs');
    return { context, frames, report, receipt: { schemaVersion: 1, runId: context.runId, suite: scope.id, fixtureRemoved: true, files } };
}

export function completeReceipt(output, scratch) {
    requireOsc66(!existsSync(scratch), 'fixture directory remains');
    const { report, receipt } = deriveRun(output);
    write(output, names.report, report);
    write(output, names.receipt, receipt);
    return { verdict: { replies: report.exchanges.map(item => item.raw), width: report.ownedPixels.width.outcome,
        scale: report.ownedPixels.scale.outcome } };
}
