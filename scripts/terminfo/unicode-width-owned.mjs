import { createHash } from 'node:crypto';
import { appendFileSync, existsSync, lstatSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { isDeepStrictEqual as same } from 'node:util';
import { OwnedCellObserver, screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { cursorReportQuery, validateCursorReport } from './cursor-report.mjs';
import { assertRetainedSourceClosure, capturedUnicodeWidthSourceFiles, capturedUnicodeWidthSourcePolicy } from './owned-sources.mjs';
import { cases, da, describeFrame, gradeCase, nonce, prefix, requireWidth, scope, steps } from './unicode-width-contracts.mjs';

export { scope, prefix };
export const workerFile = 'unicode-width-worker.mjs';
const journalFile = 'unicode-width-frames.jsonl';
const reportFile = 'unicode-width-report.json';
const receiptFile = 'unicode-width-receipt.json';
const barrierPattern = /^\x1b\[\?[\d;]+c$/;
const digest = bytes => createHash('sha256').update(bytes).digest('hex');

function isBarrier(value) {
    return typeof value === 'string' && value.length <= 128 && barrierPattern.test(value);
}

function verifyFrame(frame, step, baseline) {
    const { stage, bytes, item } = step;
    requireWidth(frame.stage === stage && frame.id === (item?.id ?? null), 'unexpected observation stage or case');
    if (stage === 'width') {
        requireWidth([frame.measurements?.ascii, frame.measurements?.sample].every(measurement => isBarrier(measurement?.restoreBarrier)),
            'missing width restoration fences');
        return;
    }
    const hasCpr = stage === 'sample';
    const expectedBytes = bytes === null ? null : bytes + (hasCpr ? cursorReportQuery : '') + da;
    requireWidth(frame.requestBytes === expectedBytes && (bytes === null ? frame.barrier === null : isBarrier(frame.barrier))
        && frame.paneId === baseline.paneId && frame.sha256 === snapshotHash(frame.screen), 'wrong pane, fixture bytes, ordering fence or snapshot digest');
    const screen = screenSnapshot(frame.screen, false, true, false, false, true, false, false, false, true);
    const state = screen.bufferState, margins = screen.horizontalMargins;
    requireWidth(screen.rows === baseline.screen.rows && screen.cols === baseline.screen.cols, 'pane geometry changed');
    if (stage === 'start' || stage === 'finish') {
        requireWidth(!state.alternateScreen && frame.sha256 === baseline.sha256, 'main screen was not restored');
    } else {
        requireWidth(state.alternateScreen && !state.pendingWrap && !state.insertMode && !state.originMode
            && !margins.leftRightMarginMode && state.scrollTop === 0 && state.scrollBottom === screen.rows - 1
            && margins.leftMargin === 0 && margins.rightMargin === screen.cols - 1, 'fixture modes or geometry changed');
    }
    if (hasCpr) {
        const { reply } = validateCursorReport({ barrier: frame.barrier, reply: frame.reply });
        requireWidth(reply === null || (reply.row === screen.cursorRow + 1 && reply.col === screen.cursorCol + 1),
            'sample CPR and physical cursor disagree');
    }
}

export class Collection {
    constructor(run) {
        this.run = run;
        this.frames = [];
        this.plan = steps(run.runId);
        this.observer = new OwnedCellObserver({ appData: run.appData, ownerPid: run.app.pid, profileName: run.profileName,
            nonce: nonce(run.runId), includeCellDetails: true, includeBufferState: true, includeHorizontalMargins: true, prefix: 'uwidth' });
    }
    async observe(request, budget) {
        const step = this.plan[this.frames.length];
        requireWidth(!this.busy && step && request.kind === prefix + step.stage, 'unexpected or concurrent observation');
        this.busy = true;
        try {
            const { stage } = step;
            const frame = { stage, id: request.id ?? null };
            if (stage === 'width') {
                frame.measurements = request.measurements;
            } else {
                const action = ['start', 'bind', 'finish'].includes(stage) ? stage : 'read';
                const { paneId, screen } = await this.observer.handle(prefix + action, budget, request.barrier);
                Object.assign(frame, { paneId, screen, sha256: snapshotHash(screen), requestBytes: request.requestBytes ?? null, barrier: request.barrier ?? null });
                if (stage === 'sample') frame.reply = request.reply;
            }
            verifyFrame(frame, step, this.frames[0] ?? frame);
            appendFileSync(join(this.run.output, journalFile), JSON.stringify(frame) + '\n', { mode: 0o600 });
            this.frames.push(frame);
        } finally {
            this.busy = false;
        }
        return {};
    }
    get restored() {
        return this.observer.restored && this.frames.length === this.plan.length;
    }
}

function retained(output, file, max = 8_000_000) {
    const path = join(output, file), info = lstatSync(path);
    requireWidth(info.isFile() && info.size > 0 && info.size <= max, 'invalid retained ' + file);
    return readFileSync(path);
}
const json = (output, file) => JSON.parse(retained(output, file));

export function deriveFrames(frames, runId) {
    const plan = steps(runId), baseline = frames[0];
    requireWidth(frames.length === plan.length && /^[a-f\d-]{36}$/i.test(baseline.paneId), 'incomplete pane journal');
    frames.forEach((frame, index) => verifyFrame(frame, plan[index], baseline));
    const bind = frames[1].screen;
    requireWidth(bind.lines[0] === nonce(runId) && bind.cursorRow === 0 && bind.cursorCol === nonce(runId).length, 'pane was not bound to worker output');
    return cases.map((item, index) => {
        const [seed, blank, target, width] = frames.slice(2 + index * 4, 6 + index * 4);
        return gradeCase(item, describeFrame(seed.screen), describeFrame(blank.screen), describeFrame(target.screen), {
            ascii: width.measurements.ascii.frames,
            sample: width.measurements.sample.frames,
        });
    });
}

function deriveRun(output) {
    const context = json(output, 'context.json'), proof = json(output, 'ownership-proof.json');
    const first = proof.proofs?.[0], terminal = first?.privateClipboard;
    requireWidth(!existsSync(join(output, 'failure.json')) && !existsSync(join(output, 'cleanup-failure.json'))
        && context.sourceSnapshotPolicy === capturedUnicodeWidthSourcePolicy && same(context.collectionScope, scope)
        && /^[a-f\d]{32}$/.test(context.runId) && typeof context.appVersion === 'string'
        && [context.executableSha256, context.assemblySha256].every(value => /^[a-f\d]{64}$/.test(value))
        && digest(retained(output, 'initial-settings.json')) === context.initialSettingsSha256
        && same(proof.collectionScope, scope) && proof.completed && !proof.cleanupError && !proof.persistentDaemon
        && Date.parse(proof.cleanedUpAt) <= Date.parse(proof.sourceSnapshot?.removedAt)
        && proof.sourceSnapshot.policy === capturedUnicodeWidthSourcePolicy && first?.app.pid === context.ownerPid
        && first.worker.ppid === context.ownerPid && typeof terminal?.originalTerminalState === 'string'
        && terminal.restoredTerminalState === terminal.originalTerminalState, 'missing direct-local ownership or verified cleanup');
    const manifest = json(output, 'source-snapshot.json'), sources = join(output, 'sources'), fileCount = Object.keys(capturedUnicodeWidthSourceFiles).length;
    requireWidth(manifest.schemaVersion === 1 && manifest.policy === capturedUnicodeWidthSourcePolicy
        && digest(retained(output, 'source-snapshot.json')) === context.sourceSnapshotManifestSha256
        && Object.keys(manifest.hashes).length === fileCount && readdirSync(sources).length === fileCount
        && same(json(sources, 'owned-source-inventory.json'), capturedUnicodeWidthSourceFiles), 'incomplete measured sources');
    for (const [key, file] of Object.entries(capturedUnicodeWidthSourceFiles)) {
        requireWidth(context[key] === manifest.hashes[key] && digest(retained(sources, file)) === context[key], 'changed retained source ' + file);
    }
    assertRetainedSourceClosure(sources, capturedUnicodeWidthSourceFiles);
    const journal = retained(output, journalFile);
    const rows = deriveFrames(journal.toString().trimEnd().split('\n').map(line => JSON.parse(line)), context.runId);
    const report = { runId: context.runId, suite: scope.id, rows };
    const receipt = { schemaVersion: 1, runId: context.runId, suite: scope.id, fixtureRemoved: true,
        contextSha256: digest(retained(output, 'context.json')), journalSha256: digest(journal),
        ownershipProofSha256: digest(retained(output, 'ownership-proof.json')) };
    return { report, receipt };
}

export function completeReceipt(output, scratch) {
    requireWidth(!existsSync(scratch), 'private fixture still exists');
    const { report, receipt } = deriveRun(output);
    for (const [file, value] of [[reportFile, report], [receiptFile, receipt]]) {
        writeFileSync(join(output, file), JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
    }
    return { runId: receipt.runId, verdict: Object.fromEntries(report.rows.map(row => [row.id, row.effect])) };
}

export function verifyRun(output) {
    const { report, receipt } = deriveRun(output);
    requireWidth(same(json(output, reportFile), report) && same(json(output, receiptFile), receipt), 'report or receipt does not rederive');
    return report;
}
