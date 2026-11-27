import { appendFileSync, existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { isDeepStrictEqual as same } from 'node:util';
import { OwnedCellObserver, colorCaptureRequest, observerEndpoint, screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { cursorReportQuery, validateCursorReport } from './cursor-report.mjs';
import { pngHash, retainedCapture, totalPixelLimit, validateRenderedCapture } from './pixels-capture.mjs';
import { pristineImageBaseline, validateImageOwnership } from './static-images.mjs';
import { journal, json, read } from './static-images-receipt.mjs';
import { da, family, fixtures, fontSha256, fontSize, nonce, planFor, prefix, requireLigatures as check, scope } from './font-ligatures-contracts.mjs';

export { prefix, scope };
export const workerFile = 'font-ligatures-worker.mjs';
export const imageOwnership = true;
const pixelDir = 'ligature-pixels';
const journalFile = 'ligature-frames.jsonl';
export const reportFile = 'ligature-report.json';
export const receiptFile = 'ligature-receipt.json';
const fileFor = (stage, index) => pixelDir + '/frame-' + String(stage * 10 + index).padStart(3, '0') + '.png';
const outputBytes = (step) => step.bytes === null ? null : step.bytes + (step.query ? cursorReportQuery : '') + da;

export function prepareSettings(settings) {
    const mode = process.env.NTILDE_TERMINFO_LIGATURES;
    check(['off', 'on'].includes(mode), 'set NTILDE_TERMINFO_LIGATURES to off or on');
    Object.assign(settings, { FontFamily: family, FontSize: fontSize, EnableLigatures: mode === 'on' });
}

function verifyFrame(frame, step, first, runId) {
    const screen = screenSnapshot(frame.screen, false, true, true, true, true);
    const main = ['start', 'finish'].includes(step.stage);
    check(frame.stage === step.stage && frame.paneId === first.paneId && frame.sha256 === snapshotHash(screen)
        && screen.rows === first.screen.rows && screen.cols === first.screen.cols && screen.rows >= 6 && screen.cols >= 55,
    'stage, pane, hash or geometry changed');
    check(screen.bufferState.alternateScreen === !main && !screen.bufferState.insertMode && !screen.bufferState.originMode,
        'unexpected terminal modes');
    validateImageOwnership(frame.ownership, first.ownership, screen);
    check(frame.requestBytes === outputBytes(step) && (step.bytes === null ? frame.barrier === null : /^\x1b\[\?[\d;]+c$/.test(frame.barrier ?? '')),
        'wrong write or fence');
    if (step.stage === 'start') check(pristineImageBaseline(screen), 'initial main screen was not pristine');
    if (step.stage === 'finish') check(frame.sha256 === first.sha256, 'main screen did not restore');
    if (step.stage === 'bind') check(screen.lines[0] === nonce(runId) && screen.cursorRow === 0 && screen.cursorCol === nonce(runId).length, 'nonce mismatch');
    if (!step.cursor) return;
    check(!screen.cursorVisible && same([screen.cursorRow, screen.cursorCol], step.cursor)
        && screen.lines.every((line, row) => line === (fixtures.some(f => f.row === row)
            ? '  ' + fixtures.find(f => f.row === row).text : '')), 'stored cells or logical cursor changed');
    if (!step.query) return;
    const { reply } = validateCursorReport(frame);
    check(reply?.row === screen.cursorRow + 1 && reply.col === screen.cursorCol + 1, 'child CPR disagrees with the App cursor');
}

function verifyCapture(result, frame, selected, enabled) {
    const view = validateRenderedCapture(result, frame.screen, 0, 1, enabled);
    const { font, syntheticSelection } = view.details;
    check(font.primaryFamily === family && font.configuredFamily === family && font.primarySha256 === fontSha256
        && font.sizeDip === fontSize && font.collectionIndex === 0 && font.weight === 400 && font.width === 5 && font.slant === 'Upright'
        && same(result.syntheticSelection ?? null, selected) && same(syntheticSelection ?? null, selected),
    'resolved face or selection changed');
    return view;
}

function verifyCaptures(frame, step, index, load) {
    const planned = step.captures ?? [];
    check(Array.isArray(frame.pixels) && frame.pixels.length === planned.length, 'unexpected capture count');
    if (!planned.length) {
        check(frame.afterScreen === null && frame.afterSha256 === null, 'unexpected bracketing read');
        return [];
    }
    const after = screenSnapshot(frame.afterScreen, false, true, true, true, true);
    check(frame.sha256 === frame.afterSha256 && frame.afterSha256 === snapshotHash(after), 'capture changed the screen');
    return frame.pixels.map((pixel, i) => {
        check(pixel.file === fileFor(index, i), 'wrong capture path');
        return load(pixel, planned[i]);
    });
}

export class Collection {
    constructor(run) {
        const options = { appData: run.appData, ownerPid: run.app.pid, profileName: run.profileName,
            nonce: nonce(run.runId), prefix: 'ligatures', includeCellDetails: true, includeCursorMode: true,
            includeOverline: true, includeBufferState: true };
        this.before = new OwnedCellObserver(options);
        this.after = new OwnedCellObserver(options);
        this.run = run;
        this.frames = [];
        this.enabled = json(run.output, 'initial-settings.json').EnableLigatures;
        this.pixelTotal = 0;
        mkdirSync(join(run.output, pixelDir), { mode: 0o700 });
    }

    async capture(frame, index, selected, i, budget) {
        const endpoint = observerEndpoint(this.run.appData, this.run.app.pid);
        const result = await colorCaptureRequest(endpoint, frame.paneId, selected, budget());
        const view = verifyCapture(result, frame, selected, this.enabled);
        this.pixelTotal += view.image.width * view.image.height;
        check(this.pixelTotal <= totalPixelLimit, 'pixel budget exhausted');
        const { pngBase64, ...description } = result;
        const file = fileFor(index, i);
        writeFileSync(join(this.run.output, file), view.bytes, { mode: 0o600, flag: 'wx' });
        return { ...description, file, sha256: pngHash(view.bytes) };
    }

    async observe(request, budget, ownership) {
        const index = this.frames.length, stage = request.kind.slice(prefix.length);
        const step = this.plan?.[index] ?? planFor(this.run.runId, true)[0];
        check(!this.busy && !this.restored && stage === step.stage, 'unexpected or concurrent stage');
        this.busy = true;
        try {
            const operation = ['start', 'bind', 'finish'].includes(stage) ? stage : 'read';
            const { paneId, screen } = await this.before.handle(prefix + operation, budget, request.barrier);
            const frame = { stage, paneId, screen, sha256: snapshotHash(screen), requestBytes: request.requestBytes ?? null,
                barrier: request.barrier ?? null, reply: request.reply ?? null, ownership, pixels: [], afterScreen: null, afterSha256: null };
            verifyFrame(frame, step, this.frames[0] ?? frame, this.run.runId);
            if (step.captures) {
                for (const [i, selected] of step.captures.entries()) {
                    frame.pixels.push(await this.capture(frame, index, selected, i, budget)); // NOSONAR: Keep capture ordering and bound one in-flight call.
                }
                this.after.paneId ??= paneId;
                frame.afterScreen = await this.after.capture(budget);
                frame.afterSha256 = snapshotHash(frame.afterScreen);
                check(frame.afterSha256 === frame.sha256, 'capture changed the screen');
            }
            if (!index) this.plan = planFor(this.run.runId, screen.cursorVisible);
            appendFileSync(join(this.run.output, journalFile), JSON.stringify(frame) + '\n', { mode: 0o600 });
            this.frames.push(frame);
            this.restored = this.before.restored;
            return { cursorVisible: screen.cursorVisible };
        } finally {
            this.busy = false;
        }
    }
}

export function deriveRun(output) {
    const fields = ['context.json', 'ownership-proof.json', 'source-snapshot.json', 'initial-settings.json', journalFile];
    const [context, proof, sources, settings] = fields.slice(0, 4).map(file => json(output, file));
    const frames = journal(output, journalFile), first = frames[0], worker = proof.proofs?.[0], raw = worker?.privateClipboard;
    const settingsValid = settings.FontFamily === family && settings.FontSize === fontSize
        && typeof settings.EnableLigatures === 'boolean' && settings.SessionPersistence === 'Off'
        && settings.AllowOsc52ClipboardWrite === false && settings.AgentAccessActEnabled === false;
    check(settingsValid && !['failure.json', 'cleanup-failure.json'].some(file => existsSync(join(output, file)))
        && same(context.collectionScope, scope) && same(proof.collectionScope, scope) && proof.proofs?.length === 1
        && proof.completed === true && !proof.cleanupError && !proof.persistentDaemon
        && proof.sourceSnapshot?.policy === context.sourceSnapshotPolicy && sources.policy === proof.sourceSnapshot.policy
        && Date.parse(frames.at(-1)?.ownership?.at) <= Date.parse(proof.cleanedUpAt)
        && Date.parse(proof.cleanedUpAt) <= Date.parse(proof.sourceSnapshot.removedAt)
        && worker?.app.pid === context.ownerPid && same(worker.app, first?.ownership.app) && same(worker.worker, first?.ownership.worker)
        && worker.tty === first.ownership.tty && typeof raw?.originalTerminalState === 'string' && raw.originalTerminalState === raw.restoredTerminalState,
    'direct owned fixture or its cleanup did not verify');
    const plan = planFor(context.runId, first.screen.cursorVisible);
    check(plan.length === frames.length, 'incomplete frame plan');
    const views = {};
    for (const [i, frame] of frames.entries()) {
        verifyFrame(frame, plan[i], first, context.runId);
        views[frame.stage] = verifyCaptures(frame, plan[i], i, (pixel, selected) =>
            verifyCapture(retainedCapture(output, { pixel }, pixelDir), frame, selected, settings.EnableLigatures));
        fields.push(...frame.pixels.map(pixel => pixel.file));
    }
    check(views.start[0].bytes.equals(views.finish[0].bytes), 'main pixels did not restore');
    check(views.fixture[0].bytes.equals(views.fixture.at(-1).bytes), 'unselected pixels did not restore');
    const files = Object.fromEntries(fields.map(file => [file, pngHash(read(output, file))]));
    check(files['source-snapshot.json'] === context.sourceSnapshotManifestSha256 && files['initial-settings.json'] === context.initialSettingsSha256,
        'source or settings digest changed');
    const report = { runId: context.runId, suite: scope.id, enabled: settings.EnableLigatures, restored: true,
        captures: frames.flatMap(frame => frame.pixels), cursors: frames.filter(frame => frame.reply !== null).map(frame => ({
            stage: frame.stage, raw: frame.reply.raw, row: frame.screen.cursorRow, col: frame.screen.cursorCol,
        })) };
    return { context, frames, views: views.fixture, report,
        receipt: { schemaVersion: 1, runId: context.runId, suite: scope.id, fixtureRemoved: true, files } };
}

export function completeReceipt(output, scratch) {
    check(!existsSync(scratch), 'fixture directory remains');
    const { report, receipt } = deriveRun(output);
    for (const [file, value] of [[reportFile, report], [receiptFile, receipt]]) {
        writeFileSync(join(output, file), JSON.stringify(value, null, 2) + '\n', { mode: 0o600, flag: 'wx' });
    }
    return { verdict: { restored: true, enabled: report.enabled } };
}
