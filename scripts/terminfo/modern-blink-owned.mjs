import { appendFileSync, existsSync, lstatSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { isDeepStrictEqual as same } from 'node:util';
import { OwnedCellObserver, screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { expandCellDetails } from './cell-details.mjs';
import { capturedBlinkSourceFiles, capturedBlinkSourcePolicy, assertRetainedSourceClosure } from './owned-sources.mjs';
import { decodeCapturePng, limits } from './pixels-png.mjs';
import { blinkBind, blinkBlank, blinkCropHash, blinkCrops, blinkFinish, blinkFrames, blinkHash, blinkIntervalMs,
    blinkNonce, blinkPlain, blinkQuery, blinkScope, blinkStyled, caretRow, checkBlink, controlRow, da, gradeBlink,
    parseBlinkReply, targetRow, textCol } from './modern-blink.mjs';
import { captureBlinkWindow } from './modern-blink-native.mjs';

export const scope = blinkScope;
export const workerFile = 'modern-blink-worker.mjs';
const paneFile = 'blink-pane.jsonl';
const nativeFile = 'blink-native.jsonl';
const reportFile = 'blink-observation.json';
export const receiptFile = 'blink-receipt.json';
const stages = ['start', 'bind', 'blank', 'plain', 'styled', 'sampled', 'finish'];
const frameFile = index => `blink-${String(index).padStart(2, '0')}.png`;
const append = (output, file, value) => appendFileSync(join(output, file), JSON.stringify(value) + '\n', { mode: 0o600 });

function steps(runId) {
    return [null, blinkBind(runId) + da, blinkBlank + da, blinkPlain + da, blinkStyled + blinkQuery + da, null, blinkFinish + da];
}

function verifyStep(frame, index, start, runId) {
    const stage = stages[index], request = steps(runId)[index];
    checkBlink(frame?.stage === stage && frame.paneId === start.paneId && frame.requestBytes === request
        && frame.sha256 === snapshotHash(frame.screen), 'invalid pane stage or fixture bytes');
    checkBlink(request === null ? frame.barrier === null : /^\x1b\[\?[\d;]+c$/.test(frame.barrier), 'missing DA barrier');
    const screen = screenSnapshot(frame.screen, false, true, false, false, true);
    checkBlink(screen.rows === start.screen.rows && screen.cols === start.screen.cols && screen.rows > caretRow,
        'pane grid changed or cannot fit the specimen');
    if (stage === 'start' || stage === 'finish') {
        checkBlink(!screen.bufferState.alternateScreen && frame.sha256 === start.sha256 && screen.cursorVisible, 'main screen not restored');
    } else {
        const expected = new Array(screen.rows).fill('');
        expected[0] = blinkNonce + runId;
        if (index >= 3) expected[targetRow] = expected[controlRow] = ' '.repeat(textCol) + 'X';
        const bound = stage === 'bind';
        checkBlink(screen.bufferState.alternateScreen && same(screen.lines, expected) && screen.cursorVisible === bound
            && screen.cursorRow === (bound ? 0 : caretRow) && screen.cursorCol === (bound ? expected[0].length : 0), 'pane text, screen or caret differs from fixture');
    }
    if (stage === 'sampled') checkBlink(frame.sha256 === start.styledSha256, 'stored pane changed while sampling');
    if (stage === 'styled') parseBlinkReply((frame.response ?? '') + frame.barrier);
}

export class BlinkCollection {
    constructor(run) {
        const { app, appData, output, profileName, runId } = run;
        Object.assign(this, { run, app, output, runId, pane: [], count: 0 });
        this.observer = new OwnedCellObserver({ appData, ownerPid: app.pid, profileName, includeCellDetails: true, includeBufferState: true,
            nonce: blinkNonce + runId, prefix: 'mblink' });
    }
    async observe(request, budget) {
        checkBlink(!this.busy, 'concurrent observation');
        this.busy = true;
        try {
            const stage = request.kind.slice('mblink-'.length);
            if (stage === 'frame') return this.sample(request.frame, budget);
            await this.observePane(stage, request, budget);
        } finally { this.busy = false; }
        return {};
    }
    async observePane(stage, request, budget) {
        const index = this.pane.length;
        checkBlink(stage === stages[index] && (stage !== 'sampled' || this.count === blinkFrames), 'unexpected pane stage');
        const action = ['start', 'bind', 'finish'].includes(stage) ? stage : 'read';
        const result = await this.observer.handle('mblink-' + action, budget, stage === 'sampled' ? this.pane[4].barrier : request.barrier);
        const frame = { stage, paneId: result.paneId, requestBytes: request.requestBytes ?? null, barrier: request.barrier ?? null,
            ...(stage === 'styled' ? { response: request.response } : {}), screen: result.screen, sha256: snapshotHash(result.screen) };
        append(this.output, paneFile, frame);
        verifyStep(frame, index, { ...(this.pane[0] ?? frame), styledSha256: this.pane[4]?.sha256 }, this.runId);
        this.pane.push(frame);
        if (stage === 'blank' || stage === 'plain') {
            // The DA barrier orders terminal bytes, so allow the compositor to paint before calibrating.
            await delay(stage === 'blank' ? 650 : 150);
            this.capture(stage, `blink-${stage}.png`, budget);
        }
    }
    capture(stage, file, budget) {
        checkBlink(!this.run.stopping, 'collection stopped before native capture');
        const native = captureBlinkWindow(this.output, file, this.app.pid, budget);
        append(this.output, nativeFile, { stage, ...native });
    }
    sample(index, budget) {
        checkBlink(this.pane.length === 5 && index === this.count && index < blinkFrames, 'unexpected sample index');
        this.capture('sample', frameFile(index), budget);
        this.count++;
        return {};
    }
    get restored() { return this.pane.length === stages.length && this.observer.restored; }
}

function retainedBytes(output, file, max = limits.bytes) {
    const path = join(output, file), info = lstatSync(path);
    checkBlink(info.isFile() && info.size > 0 && info.size <= max, 'invalid retained file ' + file);
    return readFileSync(path);
}
const json = (output, file) => JSON.parse(retainedBytes(output, file));
const journal = (output, file) => retainedBytes(output, file).toString().trimEnd().split('\n').map(line => JSON.parse(line));
const png = (output, frame, stage, file) => {
    const bytes = retainedBytes(output, file);
    checkBlink(frame?.stage === stage && frame.file === file && frame.sha256 === blinkHash(bytes), 'native image identity or bytes changed');
    return decodeCapturePng(bytes);
};

function verifySources(output, context) {
    const snapshot = retainedBytes(output, 'source-snapshot.json'), manifest = JSON.parse(snapshot);
    const sourceDir = join(output, 'sources'), files = Object.values(capturedBlinkSourceFiles);
    checkBlink(manifest.schemaVersion === 1 && manifest.policy === capturedBlinkSourcePolicy
        && blinkHash(snapshot) === context.sourceSnapshotManifestSha256 && Object.keys(manifest.hashes).length === files.length
        && readdirSync(sourceDir).length === files.length && same(json(sourceDir, 'owned-source-inventory.json'), capturedBlinkSourceFiles),
    'incomplete measured source snapshot');
    for (const [key, file] of Object.entries(capturedBlinkSourceFiles)) {
        checkBlink(context[key] === manifest.hashes[key] && blinkHash(retainedBytes(sourceDir, file)) === context[key], 'changed retained source ' + file);
    }
    assertRetainedSourceClosure(sourceDir, capturedBlinkSourceFiles);
}

function validWindow(window, ownerPid) {
    return Array.isArray(window) && window.length === 6 && window.every(Number.isSafeInteger)
        && window[0] > 0 && window[1] === ownerPid && window[4] > 0 && window[5] > 0;
}

export function deriveBlinkMeasurement(output, context) {
    const panes = journal(output, paneFile);
    checkBlink(panes.length === stages.length && /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(panes[0].paneId), 'incomplete pane journal');
    panes.forEach((frame, index) => verifyStep(frame, index, { ...panes[0], styledSha256: panes[4].sha256 }, context.runId));
    const [blankFrame, plainFrame, ...sampleFrames] = journal(output, nativeFile);
    checkBlink(sampleFrames.length <= blinkFrames && validWindow(blankFrame?.before, context.ownerPid), 'invalid native journal');
    const blank = png(output, blankFrame, 'blank', 'blink-blank.png'), plain = png(output, plainFrame, 'plain', 'blink-plain.png');
    const geometry = blankFrame.before;
    const calibrationStable = [blankFrame.after, plainFrame.before, plainFrame.after].every(window => same(window, geometry));
    checkBlink(calibrationStable && blank.width / geometry[4] === blank.height / geometry[5], 'invalid calibration window geometry');
    const crops = blinkCrops(blank, plain);
    const baseline = blinkCropHash(plain, crops.control);
    const frames = sampleFrames.map((frame, index) => {
        const image = png(output, frame, 'sample', frameFile(index));
        const { before, after, startMs, endMs, file, sha256 } = frame;
        const sizeMatches = image.width === plain.width && image.height === plain.height;
        return { file, sha256, before: sizeMatches ? before : null, after, startMs, endMs,
            styled: sizeMatches ? blinkCropHash(image, crops.styled) : null, control: sizeMatches ? blinkCropHash(image, crops.control) : null };
    });
    const grade = gradeBlink(frames, geometry);
    const restoredControl = frames.every(frame => frame.control === baseline);
    if (!restoredControl && grade.conclusion !== 'inconclusive') {
        grade.conclusion = 'inconclusive';
        grade.reason = 'unstyled control differs from calibration';
        delete grade.returnFrames;
    }
    const screen = panes[4].screen;
    const cells = expandCellDetails(screen.cellDetails, screen.rows * screen.cols);
    return { ...grade, id: 'sgr.blink', runId: context.runId, paneId: panes[0].paneId, geometry, crops,
        imageSize: { width: plain.width, height: plain.height }, intervalMs: blinkIntervalMs, frames,
        controlMatchesCalibration: restoredControl, controlDigest: baseline, setup: panes.filter(frame => frame.requestBytes !== null)
            .map(({ stage, requestBytes, barrier }) => ({ stage, requestBytes, barrier })),
        corroboration: { storedBlink: Boolean(cells[targetRow * screen.cols + textCol].flags & 32), decrqss: panes[4].response } };
}

export function completeBlinkReceipt(output, scratch) {
    checkBlink(!existsSync(scratch) && !existsSync(join(output, 'failure.json')) && !existsSync(join(output, 'cleanup-failure.json')),
        'fixture still present or run failed');
    const report = verifyBlinkRun(output);
    const bytes = value => JSON.stringify(value, null, 2) + '\n';
    const receipt = makeReceipt(output);
    writeFileSync(join(output, reportFile), bytes(report), { flag: 'wx', mode: 0o600 });
    writeFileSync(join(output, receiptFile), bytes(receipt), { flag: 'wx', mode: 0o600 });
    return { runId: receipt.runId, verdict: report.conclusion };
}

function makeReceipt(output) {
    const context = json(output, 'context.json');
    return { schemaVersion: 1, runId: context.runId, contextSha256: blinkHash(retainedBytes(output, 'context.json')),
        ownershipProofSha256: blinkHash(retainedBytes(output, 'ownership-proof.json')),
        paneJournalSha256: blinkHash(retainedBytes(output, paneFile)), nativeJournalSha256: blinkHash(retainedBytes(output, nativeFile)),
        fixtureRemoved: true };
}

export function verifyBlinkRun(output, archived = false) {
    const context = json(output, 'context.json'), ownership = json(output, 'ownership-proof.json'), proof = ownership.proofs?.[0];
    const terminal = proof?.privateClipboard;
    checkBlink(!existsSync(join(output, 'failure.json')) && !existsSync(join(output, 'cleanup-failure.json'))
        && context.sourceSnapshotPolicy === capturedBlinkSourcePolicy && same(context.collectionScope, scope)
        && /^[a-f\d]{32}$/.test(context.runId) && typeof context.appVersion === 'string'
        && [context.executableSha256, context.assemblySha256].every(value => /^[a-f\d]{64}$/.test(value))
        && blinkHash(retainedBytes(output, 'initial-settings.json')) === context.initialSettingsSha256
        && same(ownership.collectionScope, scope) && ownership.completed && !ownership.cleanupError && !ownership.persistentDaemon
        && Date.parse(ownership.cleanedUpAt) <= Date.parse(ownership.sourceSnapshot?.removedAt)
        && ownership.sourceSnapshot.policy === capturedBlinkSourcePolicy && proof?.app.pid === context.ownerPid
        && proof.worker.ppid === context.ownerPid && typeof terminal?.originalTerminalState === 'string'
        && terminal.restoredTerminalState === terminal.originalTerminalState, 'missing direct-local ownership or verified cleanup');
    verifySources(output, context);
    const report = deriveBlinkMeasurement(output, context);
    if (archived) {
        checkBlink(same(json(output, receiptFile), makeReceipt(output)), 'receipt does not rederive');
        checkBlink(same(json(output, reportFile), report), 'pixel verdict does not rederive');
    }
    return report;
}
