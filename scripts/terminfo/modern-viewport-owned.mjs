import { createHash } from 'node:crypto';
import { appendFileSync, existsSync, lstatSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { isDeepStrictEqual as same } from 'node:util';
import { OwnedCellObserver, screenSnapshot } from './agent-observer.mjs';
import { copyPaneMetadata } from './metadata-state.mjs';
import { runViewAction } from './osc720-action.mjs';
import { assertRetainedSourceClosure, capturedViewportSourceFiles, capturedViewportSourcePolicy } from './owned-sources.mjs';
import { readOwnedOutputMode, validateOutputGeometry } from './terminal-output.mjs';
import { clear, da, gradeViewport, gradeViewportPhase, maxWheelNotches, phases, requireViewport, scope, stages, syncReset, viewportFixture } from './modern-viewport.mjs';

export { prefix, scope } from './modern-viewport.mjs';
export const workerFile = 'modern-viewport-worker.mjs';
export const allowGeneratedInput = true;
export const receiptFile = 'viewport-receipt.json';
const journalFile = 'viewport-journal.jsonl';
const reportFile = 'viewport-observation.json';
const maxHistory = 257;

function project({ screen: rawScreen, history, metadata: rawMetadata }, paneId) {
    const screen = screenSnapshot(rawScreen);
    const { buffer, viewport, cellGeometry, fontGeometry, presentation, paneId: observedPane } = copyPaneMetadata(rawMetadata);
    requireViewport(paneId === observedPane && presentation.windowVisible && presentation.paneActive && !buffer.alternateScreen
        && cellGeometry && fontGeometry && screen.rows === buffer.rows && screen.cols === buffer.cols
        && screen.cursorRow === buffer.cursorRow && screen.cursorCol === buffer.cursorCol
        && history.startLine === 0 && buffer.scrollbackLines === history.totalLines && history.totalLines <= maxHistory
        && history.totalLines === history.lines.length && history.lines.every(line => typeof line === 'string' && line.length <= screen.cols),
    'incoherent direct-local pane observation');
    requireViewport(viewport?.maxScrollOffset === history.totalLines, 'view and history extents disagree');
    return { geometry: { cellGeometry, fontGeometry }, cursor: [screen.cursorRow, screen.cursorCol, screen.cursorVisible],
        history: history.lines, live: screen.lines, offset: viewport.scrollOffset, extent: viewport.maxScrollOffset,
        generation: buffer.historyGeneration, rowsEvicted: buffer.rowsEvicted };
}

function sameRestoredState(before, after) {
    const { generation: initial, ...baseline } = before;
    const { generation: final, ...restored } = after;
    return final >= initial && same(restored, baseline);
}

function expectedBytes(kind, fixture) {
    if (kind === 'bottom') return fixture.seedBytes + da;
    if (kind === 'output') return fixture.markerBytes;
    if (kind === 'finish') return syncReset + clear + da;
    return null;
}

function validateWrite({ kind, requestBytes, barrier }, fixture) {
    const expected = expectedBytes(kind, fixture);
    const fenced = kind === 'bottom' || kind === 'finish';
    requireViewport(requestBytes === expected && (fenced ? /^\x1b\[\?[\d;]+c$/.test(barrier) : barrier === null), 'changed PTY write or barrier');
}

function verifyCapture(frame, baseline) {
    const [first, second] = frame.reads.map(read => project(read, frame.paneId));
    requireViewport(frame.reads.length === 2 && same(first, second), 'unstable paired viewport readback');
    requireViewport(!baseline || same(first.geometry, baseline.geometry), 'App geometry changed during collection');
    validateOutputGeometry(frame.terminalOutput.description, first.live.length, first.geometry.cellGeometry.viewCols);
    return first;
}

function verifyWheel(frame, expected, paneId) {
    const [notch] = frame.action.notches;
    requireViewport(frame.action.action === expected && frame.action.notches.length === 1 && notch.eventDispatched
        && notch.handled && notch.postVerified && same(notch.request, {
            kind: 'mouseWheel', wheelDelta: expected === 'scroll-up' ? 1 : -1, column: 1, row: 1, paneId,
        }), 'the App wheel route was not proved');
    const metadata = copyPaneMetadata(frame.metadata);
    requireViewport(metadata.paneId === paneId && metadata.viewport && metadata.presentation.windowVisible && metadata.presentation.paneActive,
        'wheel observation no longer belongs to the visible pane');
    return metadata.viewport;
}

export class Collection {
    constructor(run) {
        this.run = run;
        this.observer = new OwnedCellObserver({ appData: run.appData, ownerPid: run.app.pid, profileName: run.profileName });
        this.phaseIndex = 0;
        this.stageIndex = 0;
        this.wheelCount = 0;
    }

    retain(frame) {
        appendFileSync(join(this.run.output, journalFile), JSON.stringify(frame) + '\n', { mode: 0o600 });
    }

    async observe(request, budget) {
        requireViewport(!this.busy && !this.restored, 'unexpected concurrent or completed observation');
        this.busy = true;
        try {
            const kind = request.kind.slice('mviewport-'.length);
            if (kind === 'scroll-up' || kind === 'scroll-down') return await this.wheel(kind, request, budget);
            return await this.capture(kind, request, budget);
        } finally {
            this.busy = false;
        }
    }

    async wheel(kind, request, budget) {
        requireViewport(request.phase === phases[this.phaseIndex] && (kind === 'scroll-up' ? this.stageIndex === 1 : this.stageIndex === 3)
            && ++this.wheelCount <= maxWheelNotches, 'unexpected wheel action');
        const { app, appData, executable, image, profileName, runId, identities } = this.run;
        const { paneId } = this.observer;
        const config = { app, appData, executable, image, profileName, runId, paneId, uid: process.getuid(),
            workerPid: request.workerPid, workerIdentity: identities.worker, tty: request.tty };
        const frame = { kind, phase: request.phase, paneId };
        try {
            frame.action = await runViewAction({ action: kind, config, budget });
            frame.metadata = await this.observer.request('readPaneMetadata', { paneId }, budget);
            const viewport = verifyWheel(frame, kind, paneId);
            this.viewport = viewport;
            return { offset: viewport.scrollOffset };
        } catch (error) {
            frame.error = error.message;
            frame.failedAttempt = error.inputAttempt ?? null;
            throw error;
        } finally {
            this.retain(frame);
        }
    }

    async read(paneId, budget) {
        const params = { paneId };
        const screen = await this.observer.request('readScreen', params, budget);
        const history = await this.observer.request('readScrollback', { paneId, startLine: 0, maxLines: maxHistory }, budget);
        const metadata = await this.observer.request('readPaneMetadata', params, budget);
        return { screen, history, metadata };
    }

    admit(kind, phase) {
        if (!this.baseline) return kind === 'start' && phase === null;
        if (this.phaseIndex === phases.length) return kind === 'finish' && phase === null;
        return kind === stages[this.stageIndex] && phase === phases[this.phaseIndex];
    }

    async capture(kind, request, budget) {
        const phase = request.phase ?? null;
        requireViewport(this.admit(kind, phase), 'unexpected capture stage');
        const before = await this.observer.session(budget);
        this.observer.paneId = before.paneId;
        const frame = { kind, phase, paneId: before.paneId, requestBytes: request.requestBytes ?? null, barrier: request.barrier ?? null, reads: [] };
        try {
            validateWrite(frame, viewportFixture(this.run.runId, phase, before.rows));
            frame.reads.push(await this.read(before.paneId, budget), await this.read(before.paneId, budget));
            frame.terminalOutput = readOwnedOutputMode(request.tty, budget());
            const after = await this.observer.session(budget);
            const capture = verifyCapture(frame, this.baseline);
            requireViewport(before.rows === after.rows && before.cols === after.cols && before.rows === capture.live.length
                && before.cols === capture.geometry.cellGeometry.viewCols, 'session grid changed');
            this.advance(kind, capture);
            return { rows: capture.live.length };
        } catch (error) {
            frame.error = error.message;
            throw error;
        } finally {
            this.retain(frame);
        }
    }

    advance(kind, capture) {
        if (kind === 'start') {
            requireViewport(capture.history.length === 0 && capture.offset === 0
                && capture.live.every(line => line === '') && capture.cursor[0] === 0 && capture.cursor[1] === 0, 'initial main pane is not blank');
            this.baseline = capture;
            return;
        }
        if (kind === 'finish') {
            requireViewport(sameRestoredState(this.baseline, capture), 'initial screen was not restored');
            this.restored = true;
            return;
        }
        if (kind === 'anchor' || kind === 'restored') {
            requireViewport(this.viewport?.scrollOffset === capture.offset && this.viewport.maxScrollOffset === capture.extent,
                'wheel readback changed before capture');
        }
        this.stageIndex++;
        this.viewport = null;
        this.wheelCount = 0;
        if (kind === 'restored') {
            this.stageIndex = 0;
            this.phaseIndex++;
        }
    }
}

function loadBytes(output, file) {
    const path = join(output, file), stat = lstatSync(path);
    requireViewport(stat.isFile() && stat.size > 0 && stat.size < 16 * 1024 * 1024, 'invalid retained file ' + file);
    return readFileSync(path);
}

const load = (output, file) => JSON.parse(loadBytes(output, file));
const retainedJournal = output => loadBytes(output, journalFile).toString().trimEnd().split('\n').map(line => JSON.parse(line));

function verifySources(output, context) {
    const manifestBytes = loadBytes(output, 'source-snapshot.json'), manifest = JSON.parse(manifestBytes);
    const sources = join(output, 'sources'), files = Object.entries(capturedViewportSourceFiles);
    requireViewport(manifest.schemaVersion === 1 && manifest.policy === capturedViewportSourcePolicy
        && hash(manifestBytes) === context.sourceSnapshotManifestSha256 && Object.keys(manifest.hashes).length === files.length
        && readdirSync(sources).length === files.length && same(load(sources, 'owned-source-inventory.json'), capturedViewportSourceFiles),
    'incomplete captured source graph');
    for (const [key, file] of files) {
        requireViewport(context[key] === manifest.hashes[key] && context[key] === hash(loadBytes(sources, file)), 'changed source ' + file);
    }
    assertRetainedSourceClosure(sources, capturedViewportSourceFiles);
}

function readPhase(frames, phase, paneId, baseline, { runId }) {
    const fixture = viewportFixture(runId, phase, baseline.live.length);
    const captures = {};
    for (const stage of stages) {
        let viewport, count = 0;
        const direction = stage === 'anchor' ? 'scroll-up' : 'scroll-down';
        while (frames[0]?.kind === direction && (stage === 'anchor' || stage === 'restored')) {
            requireViewport(++count <= maxWheelNotches, 'too many wheel actions');
            viewport = verifyWheel(frames.shift(), direction, paneId);
        }
        const frame = frames.shift();
        requireViewport(frame?.kind === stage && ((stage !== 'anchor' && stage !== 'restored') || count > 0), 'missing capture or wheel control');
        validateWrite(frame, fixture);
        const snapshot = verifyCapture(frame, baseline);
        if (viewport) requireViewport(viewport.scrollOffset === snapshot.offset && viewport.maxScrollOffset === snapshot.extent,
            'wheel position different from the captured viewport');
        captures[stage] = snapshot;
    }
    return gradeViewportPhase({ phase, runId, ...captures });
}

function deriveReport(output, context, settings) {
    const frames = retainedJournal(output);
    const paneId = frames[0]?.paneId;
    requireViewport(/^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(paneId)
        && frames.every(frame => !frame.error && frame.paneId === paneId), 'failed observation or pane changed');
    const first = frames.shift();
    requireViewport(first.kind === 'start' && first.phase === null, 'missing baseline');
    validateWrite(first);
    const baseline = verifyCapture(first);
    const results = phases.map(phase => {
        const current = frames.filter(frame => frame.phase === phase);
        requireViewport(current.length > 0 && same(current, frames.splice(0, current.length)), 'phase order changed');
        const grade = readPhase(current, phase, paneId, baseline, context);
        requireViewport(current.length === 0, 'unexpected phase records');
        return grade;
    });
    const [last] = frames;
    requireViewport(frames.length === 1 && last.kind === 'finish' && last.phase === null, 'missing final restoration');
    validateWrite(last);
    requireViewport(sameRestoredState(baseline, verifyCapture(last, baseline)) && baseline.offset === 0
        && baseline.extent === 0 && baseline.live.every(line => line === ''), 'screen restoration failed');
    return { conclusion: gradeViewport(results), runId: context.runId, paneId, geometry: baseline.geometry,
        settings: { smoothScrolling: settings.SmoothScrolling, sessionPersistence: settings.SessionPersistence },
        phases: results, restoredMainScreen: true };
}

function hash(bytes) {
    return createHash('sha256').update(bytes).digest('hex');
}

function verifyOwnership(output, context, ownership, settings) {
    const proof = ownership.proofs?.[0], terminal = proof?.privateClipboard;
    requireViewport(!existsSync(join(output, 'failure.json')) && !existsSync(join(output, 'cleanup-failure.json'))
        && context.sourceSnapshotPolicy === capturedViewportSourcePolicy && same(context.collectionScope, scope)
        && /^[a-f\d]{32}$/.test(context.runId) && typeof context.appVersion === 'string'
        && [context.executableSha256, context.assemblySha256].every(value => /^[a-f\d]{64}$/.test(value))
        && settings.SmoothScrolling === false && settings.SessionPersistence === 'Off'
        && settings.AgentAccessObserveEnabled === true && settings.AgentAccessActEnabled === true
        && same(ownership.collectionScope, scope) && ownership.completed && !ownership.cleanupError && !ownership.persistentDaemon
        && Date.parse(ownership.cleanedUpAt) <= Date.parse(ownership.sourceSnapshot?.removedAt)
        && ownership.sourceSnapshot.policy === capturedViewportSourcePolicy && proof?.app.pid === context.ownerPid
        && proof.worker.ppid === context.ownerPid && typeof terminal?.originalTerminalState === 'string'
        && terminal.restoredTerminalState === terminal.originalTerminalState, 'missing direct-local ownership or verified cleanup');
}

function deriveReceipt(output) {
    const context = load(output, 'context.json'), ownership = load(output, 'ownership-proof.json');
    const settingsBytes = loadBytes(output, 'initial-settings.json'), settings = JSON.parse(settingsBytes);
    requireViewport(hash(settingsBytes) === context.initialSettingsSha256, 'private settings changed');
    verifyOwnership(output, context, ownership, settings);
    verifySources(output, context);
    const report = deriveReport(output, context, settings);
    const digestOf = file => hash(loadBytes(output, file));
    const receipt = { schemaVersion: 1, runId: context.runId, contextSha256: digestOf('context.json'),
        ownershipProofSha256: digestOf('ownership-proof.json'), journalSha256: digestOf(journalFile),
        fixtureRemoved: true, verdict: report.conclusion };
    return { report, receipt };
}

export function completeReceipt(output, scratch) {
    requireViewport(!existsSync(scratch), 'fixture not removed');
    const { report, receipt } = deriveReceipt(output);
    for (const [file, value] of [[reportFile, report], [receiptFile, receipt]]) {
        writeFileSync(join(output, file), JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
    }
    return receipt;
}

export function verifyViewportRun(output) {
    const derived = deriveReceipt(output);
    requireViewport(same(load(output, receiptFile), derived.receipt) && same(load(output, reportFile), derived.report),
        'viewport report or receipt does not rederive');
    return derived;
}
