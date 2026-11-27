import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { appendFileSync, existsSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { OwnedCellObserver, livePaneCapture, observerEndpoint, screenSnapshot } from './agent-observer.mjs';
import { validateInputBytes } from './input-state.mjs';
import { decodeCapturePng, limits } from './pixels-png.mjs';
import { pristineImageBaseline, validateImageOwnership } from './static-images.mjs';
import { journal, json, read } from './static-images-receipt.mjs';
import { classify, clockMs, frameDifference, nonce, prepare, restore, scopeFor, trials } from './osc-flash-contract.mjs';

const stages = ['start', 'prepare', ...trials.flatMap(({ name }) => ['sample-' + name, name]), 'finish', 'seal'];
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const log = 'flash.jsonl', receiptFile = 'flash-receipt.json';
const filename = (name, i) => 'flash-' + name + '-' + i + '.png';
const save = (output, file, bytes) => writeFileSync(join(output, file), bytes, { flag: 'wx', mode: 0o600 });
const snapshot = raw => screenSnapshot(raw, false, true, true, true, true, true);

export function selectScope(name) {
    assert.ok(['osc555', 'osc555-disabled'].includes(name));
    const visual = name === 'osc555';
    return { scope: scopeFor(visual), workerFile: 'osc-flash-worker.mjs', prefix: 'flash-', imageOwnership: true,
        prepareSettings(settings) { settings.BellVisualEnabled = visual; }, Collection, completeReceipt };
}

class Collection {
    constructor(run) {
        this.run = run;
        this.index = 0;
        this.observer = new OwnedCellObserver({ appData: run.appData, ownerPid: run.app.pid, profileName: run.profileName,
            includeCellDetails: true, includeOverline: true, includeCursorMode: true, includeBufferState: true, includeResolvedColors: true });
    }
    get restored() { return this.index === stages.length; }

    async sample(name, target, budget) {
        const { appData, app, output } = this.run;
        const endpoint = observerEndpoint(appData, app.pid);
        const frames = [];
        assert.ok(Number.isFinite(target) && Math.abs(target - clockMs()) < 1000);
        await delay(Math.max(0, target - 100 - clockMs()));
        do {
            assert.ok(!this.run.stopping);
            const start = clockMs();
            const shot = await livePaneCapture(endpoint, this.observer.paneId, budget()); // NOSONAR: capture distinct ordered frames.
            const end = clockMs();
            assert.ok(shot.mode === 'live' && !shot.inlineOmitted && !shot.downscaled && shot.scale === 0.5);
            const bytes = Buffer.from(shot.pngBase64, 'base64'), file = filename(name, frames.length);
            assert.ok(bytes.length === shot.byteCount && bytes.length <= limits.bytes);
            save(output, file, bytes);
            frames.push({ file, sha256: hash(bytes), start, end, width: shot.width, height: shot.height, rows: shot.rows, cols: shot.cols });
            await delay(8); // NOSONAR: bound the sampling rate.
        } while (frames.length < 48 && clockMs() < target + 300);
        return frames;
    }

    async observe(request, budget, ownership) {
        const stage = stages[this.index];
        assert.ok(!this.busy && stage && request.kind === 'flash-' + stage);
        this.busy = true;
        try {
            const entry = { stage, ownership };
            if (stage.startsWith('sample-')) {
                entry.frames = await this.sample(stage.slice(7), request.target, budget);
            } else if (stage === 'seal') {
                entry.attempt = request.attempt;
            } else {
                entry.screen = await this.observer.capture(budget);
                if (stage !== 'start') {
                    assert.equal(request.stage, stage);
                    for (const key of ['writes', 'query', 'sample', 'sent', 'fenced']) entry[key] = request[key];
                }
            }
            entry.paneId = this.observer.paneId;
            appendFileSync(join(this.run.output, log), JSON.stringify(entry) + '\n', { mode: 0o600 });
            this.index++;
            return {};
        } finally { this.busy = false; }
    }
}

function gradeSeries(output, name, frames, action, geometry) {
    assert.ok(frames.length > 0 && frames.length <= 48);
    let baseline;
    const differences = frames.map((frame, index) => {
        assert.equal(frame.file, filename(name, index));
        const bytes = read(output, frame.file, limits.bytes);
        assert.equal(frame.sha256, hash(bytes), 'changed PNG');
        const image = decodeCapturePng(bytes);
        assert.deepEqual([frame.width, frame.height, frame.rows, frame.cols], [image.width, image.height, geometry.rows, geometry.cols]);
        baseline ??= image;
        return { ...frameDifference(baseline, image), start: frame.start, end: frame.end, file: frame.file };
    });
    return { name, ...classify(differences, action.sent, action.fenced), sent: action.sent, fenced: action.fenced, frames: differences };
}

function checkPane(entries, runId) {
    const start = snapshot(entries[0].screen), prepared = snapshot(entries[1].screen);
    assert.ok(pristineImageBaseline(start) && start.cursorVisible);
    assert.deepEqual(snapshot(entries.at(-2).screen), start, 'original pane must be restored');
    assert.ok(prepared.bufferState.alternateScreen && !prepared.cursorVisible && prepared.cursorRow === 0 && prepared.cursorCol === 0);
    assert.deepEqual(prepared.colorBinding, start.colorBinding);
    assert.deepEqual(prepared.lines, Array.from({ length: start.rows }, (_, i) => i === 3 ? '  ' + nonce(runId) : ''));
    const used = new Set();
    for (const entry of entries.filter(e => e.sample)) {
        const trial = trials.find(t => t.name === entry.stage);
        const writes = trial?.writes ?? (entry.stage === 'prepare' ? [prepare(runId)] : [restore]);
        assert.deepEqual(entry.writes, writes);
        assert.equal(entry.query, trial?.query);
        validateInputBytes(entry.sample, entry.query ? Buffer.from('\x1b[1;1R').toString('hex') : '', used);
        if (trial) assert.deepEqual(snapshot(entry.screen), prepared, 'cells, cursor or palette changed');
    }
    assert.equal(used.size, trials.length + 2);
    const { sample } = entries.at(-2);
    assert.deepEqual(entries.at(-1).attempt, { hex: sample.hex + Buffer.from(sample.barrier).toString('hex'), nonces: sample.nonces,
        fenceRequested: true, completed: true, error: null });
    return start;
}

export function deriveFlash(output) {
    assert.ok(!['failure.json', 'cleanup-failure.json'].some(file => existsSync(join(output, file))));
    const entries = journal(output, log), context = json(output, 'context.json'), settings = json(output, 'initial-settings.json');
    const proof = json(output, 'ownership-proof.json'), owner = proof.proofs[0], terminal = owner.privateClipboard;
    assert.deepEqual(entries.map(e => e.stage), stages);
    assert.match(entries[0].paneId, /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/);
    assert.match(context.runId, /^[a-f\d]{32}$/);
    assert.equal(typeof settings.BellVisualEnabled, 'boolean');
    assert.deepEqual(context.collectionScope, scopeFor(settings.BellVisualEnabled));
    assert.deepEqual(proof.collectionScope, context.collectionScope);
    assert.ok(proof.completed && !proof.cleanupError && !proof.persistentDaemon && proof.proofs.length === 1
        && context.ownerPid === owner.app.pid && typeof terminal.originalTerminalState === 'string'
        && terminal.originalTerminalState === terminal.restoredTerminalState
        && Date.parse(entries.at(-1).ownership.at) <= Date.parse(terminal.restoredAt)
        && Date.parse(terminal.restoredAt) <= Date.parse(proof.cleanedUpAt)
        && Date.parse(proof.cleanedUpAt) <= Date.parse(proof.sourceSnapshot.removedAt));
    assert.ok(settings.BellAudioEnabled === false && settings.AgentAccessActEnabled === false && settings.SessionPersistence === 'Off');
    const start = checkPane(entries, context.runId);
    for (const e of entries) {
        assert.equal(e.paneId, entries[0].paneId);
        validateImageOwnership(e.ownership, owner, start);
    }
    const series = trials.map(({ name }, i) => gradeSeries(output, name, entries[2 + 2*i].frames, entries[3 + 2*i], start));
    const files = Object.fromEntries([log, 'context.json', 'source-snapshot.json', 'initial-settings.json', 'ownership-proof.json']
        .map(file => [file, hash(read(output, file))]));
    assert.equal(files['source-snapshot.json'], context.sourceSnapshotManifestSha256);
    assert.equal(files['initial-settings.json'], context.initialSettingsSha256);
    return { entries, series, context, receipt: { schemaVersion: 1, runId: context.runId, suiteId: context.collectionScope.id,
        paneId: entries[0].paneId, files, fixtureRemoved: true } };
}

function completeReceipt(output, scratch) {
    assert.ok(!existsSync(scratch));
    const { receipt, series } = deriveFlash(output);
    save(output, receiptFile, JSON.stringify(receipt, null, 2) + '\n');
    return { verdict: series.map(({ name, state }) => ({ name, state })) };
}
