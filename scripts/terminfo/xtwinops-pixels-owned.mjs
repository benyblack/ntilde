import { execFileSync } from 'node:child_process';
import { appendFileSync, existsSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import { isDeepStrictEqual as same } from 'node:util';
import { screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { copyPaneMetadata } from './metadata-state.mjs';
import { pngHash } from './pixels-capture.mjs';
import { gridOf, sameScale, smallerGrid, verifyControl } from './sixel-reports-contracts.mjs';
import { Collection as SixelCollection, requireResize, verifySeal } from './sixel-reports-owned.mjs';
import { pristineImageBaseline, validateImageOwnership } from './static-images.mjs';
import { journal, json, read } from './static-images-receipt.mjs';
import { readOwnedOutputMode, validateOutputGeometry } from './terminal-output.mjs';
import { initialFontSize, nonce, pixelDimensions, planFor, requireGeometry, scope, verifyReply } from './xtwinops-pixels-contracts.mjs';

export { prefix, scope } from './xtwinops-pixels-contracts.mjs';
export const workerFile = 'xtwinops-pixels-worker.mjs';
export const allowGeneratedInput = true;
export const imageOwnership = true;
export function prepareSettings(settings) { settings.FontSize = initialFontSize; }

const framesFile = 'xtwinops-frames.jsonl', actionsFile = 'xtwinops-resizes.jsonl';
const windowScript = fileURLToPath(new URL('./modern-blink-window.jxa', import.meta.url));
const fontScript = fileURLToPath(new URL('./xtwinops-pixels-font.jxa', import.meta.url));
const append = (output, frame) => appendFileSync(join(output, framesFile), JSON.stringify(frame) + '\n', { mode: 0o600 });
const save = (output, file, value) => writeFileSync(join(output, file), JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
const native = (script, args, budget) => execFileSync('/usr/bin/osascript', ['-l', 'JavaScript', script, ...args.map(String)],
    { encoding: 'utf8', timeout: budget(), maxBuffer: 4096 }).trim();

function verifyWindow(window, pid) {
    requireGeometry(Array.isArray(window) && window.length === 6 && window.every(Number.isSafeInteger)
        && window[0] > 0 && window[1] === pid && window[4] > 0 && window[5] > 0, 'owned native window could not be identified');
}

function verifyPane(frame, initial, main, runId) {
    const screen = screenSnapshot(frame.screen, false, true, true, true, true);
    const metadata = copyPaneMetadata(frame.metadata);
    const { paneId, cellGeometry: cell, fontGeometry: font, buffer, presentation } = metadata;
    requireGeometry(paneId === frame.paneId && paneId === initial.paneId && frame.sha256 === snapshotHash(screen)
        && frame.afterSha256 === frame.sha256 && same(gridOf(screen), gridOf(buffer)) && cell && font
        && presentation.paneActive && presentation.windowVisible
        && buffer.alternateScreen === screen.bufferState.alternateScreen, 'wrong pane or unstable App geometry');
    verifyWindow(frame.window, initial.ownership.app.pid);
    requireGeometry(frame.window[0] === initial.window[0], 'the owned native window changed identity');
    validateImageOwnership(frame.settledOwnership, initial.ownership, screen);
    requireGeometry(Date.parse(frame.ownership.at) <= Date.parse(frame.settledOwnership.at), 'settlement preceded stage admission');
    if (main) {
        requireGeometry(pristineImageBaseline(screen) && frame.sha256 === initial.sha256, 'original screen was not pristine or restored');
    } else {
        requireGeometry(screen.bufferState.alternateScreen && screen.lines[0] === nonce(runId)
            && screen.cursorRow === 0 && screen.cursorCol === nonce(runId).length && screen.lines.slice(1).every(line => line === ''),
        'alternate-screen fixture changed');
    }
}

function samePane(a, b) {
    return a.sha256 === b.sha256 && same(a.metadata, b.metadata) && same(a.window, b.window);
}

function verifyStage(frame, step, frames, runId, used) {
    requireGeometry(frame.stage === step.stage, 'missing or out-of-order stage');
    const initial = frames[0] ?? frame, previous = frames.at(-1) ?? frame;
    // Admission is before a resize, font action, or same-pane observation.
    validateImageOwnership(frame.ownership, initial.ownership, previous.screen);
    if (step.stage === 'seal') {
        verifySeal(frame, previous);
        return null;
    }
    verifyPane(frame, initial, step.stage === 'start' || step.stage === 'finish', runId);
    const font = frame.metadata.fontGeometry.fontSizeDip;
    const previousFont = previous.metadata.fontGeometry.fontSizeDip;
    const cell = frame.metadata.cellGeometry;
    const before = previous.metadata.cellGeometry;
    requireGeometry(cell.renderScaling === before.renderScaling && cell.backingScale === before.backingScale,
        'render or backing scale changed during collection');
    if (step.resize) {
        const target = step.resize === 'original' ? gridOf(initial.screen) : smallerGrid(initial.metadata.cellGeometry);
        requireResize(frame.action, step.stage, target, initial.paneId, runId);
        requireGeometry(same(gridOf(frame.screen), target), 'App did not retain the settled resize grid');
    }
    if (step.fontDelta) {
        requireGeometry(frame.fontAction?.operation === step.stage && frame.fontAction.pid === frame.ownership.app.pid
            && font === previousFont + step.fontDelta && same(frame.window, previous.window),
        'font shortcut did not settle exactly in the same native window');
        requireGeometry(cell.cellWidthDip !== before.cellWidthDip || cell.cellHeightDip !== before.cellHeightDip,
            'font shortcut did not change the logical cell metrics');
    } else {
        requireGeometry(font === previousFont && sameScale(cell, before), 'font or cell metrics changed outside the font action');
    }
    if (step.write) {
        requireGeometry(frame.requestBytes === step.write, 'fixture write changed');
        verifyControl(frame.sample, used);
    }
    if (step.sample) {
        requireGeometry(samePane(frame, previous), 'query changed the screen, pane metadata, or native window');
        return verifyReply(frame.samples, step, used, pixelDimensions(cell, frame.window));
    }
    requireGeometry(step.resize || step.fontDelta || same(gridOf(frame.screen), gridOf(previous.screen)), 'grid changed outside a planned action');
    if (step.stage === 'start') requireGeometry(font === initialFontSize, 'initial font setting did not apply');
    if (step.stage === 'restored' || step.stage === 'finish') {
        requireGeometry(same(cell, initial.metadata.cellGeometry) && font === initial.metadata.fontGeometry.fontSizeDip,
            'initial pane grid or font geometry was not restored');
    }
    return null;
}

export class Collection extends SixelCollection {
    constructor(run) { super(run, { plan: planFor(run.runId), actionsFile }); }

    async observation(budget, ownership, expectedFont, unsettled = []) {
        for (;;) {
            const frame = await this.capture(budget, { settling: true });
            const terminalOutput = readOwnedOutputMode(ownership.tty, budget());
            const at = new Date().toISOString();
            const rows = frame.screen.rows, cols = frame.screen.cols;
            const cell = frame.metadata.cellGeometry;
            if (frame.sha256 === frame.afterSha256 && cell?.viewRows === rows && cell?.viewCols === cols
                && frame.metadata.fontGeometry?.fontSizeDip === expectedFont
                && same(gridOf(frame.metadata.buffer), { rows, cols })
                && new RegExp(String.raw`(?:^|[;\s])${rows} rows;\s+${cols} columns;`).test(terminalOutput.description)) {
                frame.metadata = copyPaneMetadata(frame.metadata);
                validateOutputGeometry(terminalOutput.description, rows, cols);
                const windows = JSON.parse(native(windowScript, [ownership.app.pid], budget));
                requireGeometry(windows.length === 1, 'expected one on-screen owned native window');
                return { ...frame, window: windows[0], settledOwnership: { ...ownership, at, terminalOutput } };
            }
            unsettled.push({ at, screen: { rows, cols }, buffer: gridOf(frame.metadata.buffer), cell,
                font: frame.metadata.fontGeometry, terminalOutput });
            await delay(Math.min(40, budget())); // NOSONAR: wait for App and kernel after a UI action.
        }
    }

    applyFont(operation, budget, ownership) {
        requireGeometry(this.run.app.pid === ownership.app.pid && !this.run.stopping
            && this.run.app.exitCode === null && this.run.app.signalCode === null, 'private App identity changed');
        const pid = ownership.app.pid;
        requireGeometry(native(fontScript, [pid, operation], budget) === operation, 'font shortcut was not posted');
        return { operation, pid };
    }

    async abort(request, budget) {
        this.aborted = true;
        if (!this.resizeAttempted) return {};
        const original = this.frames[0], { fontSizeDip } = original.metadata.fontGeometry;
        const current = await this.capture(budget, { settling: true });
        const font = current.metadata.fontGeometry?.fontSizeDip;
        if (font === fontSizeDip + 1) {
            const ownership = original.ownership;
            this.applyFont('decrease', budget, ownership);
            await this.observation(budget, ownership, fontSizeDip);
        } else requireGeometry(font === fontSizeDip, 'font size was outside the rollback fixture');
        return super.abort(request, budget);
    }

    async observeStage(request, budget, ownership, step) {
        const frame = { stage: step.stage, ownership };
        try {
            if (step.stage === 'seal') frame.attempts = request.attempts;
            else {
                const initial = this.frames[0], previous = this.frames.at(-1);
                const expectedFont = (previous?.metadata.fontGeometry.fontSizeDip ?? initialFontSize) + (step.fontDelta ?? 0);
                if (step.resize) {
                    const target = step.resize === 'original' ? gridOf(initial.screen) : smallerGrid(initial.metadata.cellGeometry);
                    frame.action = await this.resize(step.stage, target, request, budget);
                }
                if (step.fontDelta) {
                    requireGeometry(previous.metadata.presentation.windowActive, 'font shortcut requires the owned active window');
                    frame.fontAction = this.applyFont(step.stage, budget, ownership);
                }
                if (step.write) Object.assign(frame, { requestBytes: request.requestBytes, sample: request.sample });
                if (step.sample) frame.samples = request.samples;
                frame.unsettled = [];
                Object.assign(frame, await this.observation(budget, ownership, expectedFont, frame.unsettled));
            }
            verifyStage(frame, step, this.frames, this.run.runId, this.used);
        } catch (error) {
            frame.error = error.message;
            throw error;
        } finally { append(this.run.output, frame); }
        this.frames.push(frame);
        return {};
    }
}

function deriveStages(frames, actions, runId) {
    const plan = planFor(runId), seen = [], used = new Set(), samples = [];
    requireGeometry(/^[a-f\d]{32}$/.test(runId) && frames.length === plan.length && actions.length === 2,
        'missing or incomplete pane journal');
    for (const [index, step] of plan.entries()) {
        const frame = frames[index], previous = seen.at(-1);
        requireGeometry(!Object.hasOwn(frame, 'error') && (!previous
            || Date.parse(frame.ownership.at) >= Date.parse(previous.settledOwnership.at)), 'error or out-of-order observations');
        const reply = verifyStage(frame, step, seen, runId, used);
        if (step.geometry && !step.sample) {
            const { cellGeometry: cell, fontGeometry: font } = frame.metadata;
            samples.push({ stage: step.geometry, grid: gridOf(frame.screen), cell, font, replies: [],
                expected: pixelDimensions(cell, frame.window) });
        }
        if (reply) samples.at(-1).replies.push(reply);
        seen.push(frame);
    }
    requireGeometry(same(actions, frames.filter(frame => frame.action).map(frame => frame.action)), 'resize action journal differs');
    const [initial, resized, font, restored] = samples.map(row => row.expected);
    requireGeometry(!same(initial.area, initial.cappedArea) && same(resized.area, resized.cappedArea)
        && !same(initial.area, resized.area) && same(initial.cell, resized.cell)
        && !same(resized.cell, font.cell) && same(initial.area, restored.area) && same(initial.cell, restored.cell), 'geometry controls did not distinguish resize, font, cap, and restoration');
    return { samples, actions, restored: true };
}

export function deriveRun(output) {
    const context = json(output, 'context.json'), proof = json(output, 'ownership-proof.json'), settings = json(output, 'initial-settings.json');
    const frames = journal(output, framesFile), actions = journal(output, actionsFile);
    const worker = proof.proofs?.[0], raw = worker?.privateClipboard, initial = frames[0]?.ownership;
    requireGeometry(!['failure.json', 'cleanup-failure.json'].some(file => existsSync(join(output, file)))
        && same(context.collectionScope, scope) && same(proof.collectionScope, scope) && proof.proofs?.length === 1
        && proof.completed === true && !proof.cleanupError && !proof.persistentDaemon
        && proof.sourceSnapshot?.policy === context.sourceSnapshotPolicy && Date.parse(proof.cleanedUpAt) <= Date.parse(proof.sourceSnapshot.removedAt)
        && Date.parse(frames.at(-1)?.ownership?.at) <= Date.parse(raw?.restoredAt) && Date.parse(raw?.restoredAt) <= Date.parse(proof.cleanedUpAt)
        && worker?.app.pid === context.ownerPid && same(worker.app, initial?.app) && same(worker.worker, initial?.worker)
        && worker.tty === initial.tty && typeof raw?.originalTerminalState === 'string' && raw.originalTerminalState === raw.restoredTerminalState
        && settings.SessionPersistence === 'Off' && settings.AllowOsc52ClipboardWrite === false && settings.AgentAccessActEnabled === true
        && settings.FontSize === initialFontSize, 'direct local ownership and exact PTY cleanup did not verify');
    const report = { runId: context.runId, suite: scope.id, ...deriveStages(frames, actions, context.runId) };
    const inputs = [framesFile, actionsFile, 'context.json', 'ownership-proof.json', 'source-snapshot.json', 'initial-settings.json'];
    const files = Object.fromEntries(inputs.map(file => [file, pngHash(read(output, file))]));
    requireGeometry(files['source-snapshot.json'] === context.sourceSnapshotManifestSha256
        && files['initial-settings.json'] === context.initialSettingsSha256, 'source or initial settings digest differs');
    return { context, frames, report, receipt: { schemaVersion: 1, suite: scope.id, runId: context.runId, fixtureRemoved: true, files } };
}

export function completeReceipt(output, scratch) {
    requireGeometry(!existsSync(scratch), 'fixture remains');
    const { report, receipt } = deriveRun(output);
    save(output, 'xtwinops-report.json', report);
    save(output, 'xtwinops-receipt.json', receipt);
    return { verdict: report.samples.map(({ stage, replies }) => ({ stage, outcomes: replies.map(row => row.outcome) })) };
}
