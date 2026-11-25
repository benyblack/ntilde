import { appendFileSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
import { isDeepStrictEqual as same } from 'node:util';
import { OwnedCellObserver, screenSnapshot, snapshotHash } from './agent-observer.mjs';
import { cursorReportQuery, validateCursorReport } from './cursor-report.mjs';
import { da, describeFrame, enter, leave, nonce } from './unicode-width-contracts.mjs';
import { boundaryPlan, gradeBoundaries, prefix, requireBoundary, scope } from './unicode-boundary-contracts.mjs';

export { prefix, scope };
export const workerFile = 'unicode-boundary-worker.mjs';
const journal = 'unicode-boundary-frames.jsonl';
const fileJson = (output, name) => JSON.parse(readFileSync(join(output, name)));
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const isBarrier = value => typeof value === 'string' && value.length <= 128 && /^\x1b\[\?[\d;]+c$/.test(value);
const planFor = (cols, runId) => [
    { stage: 'start', bytes: null }, { stage: 'bind', bytes: enter(runId) },
    ...boundaryPlan(cols), { stage: 'finish', bytes: leave },
];

function verifyFrame(frame, step, baseline) {
    requireBoundary(frame?.stage === step.stage, 'frame is missing or out of order');
    if (step.sample) {
        requireBoundary(isBarrier(frame.restoreBarrier) && Array.isArray(frame.exchanges), 'invalid width measurement');
        return null;
    }
    const query = step.query ? cursorReportQuery : '';
    const expected = step.bytes === null ? null : step.bytes + query + da;
    requireBoundary(frame.requestBytes === expected && frame.paneId === baseline.paneId
        && frame.sha256 === snapshotHash(frame.screen), 'frame bytes, pane or screen digest differs');
    const screen = screenSnapshot(frame.screen, false, true, false, false, true, false, false, false, true);
    const { bufferState: mode, horizontalMargins: margins } = screen;
    requireBoundary(screen.rows === baseline.screen.rows && screen.cols === baseline.screen.cols, 'fixture geometry changed');
    if (step.stage === 'start' || step.stage === 'finish') {
        requireBoundary(!mode.alternateScreen && frame.sha256 === baseline.sha256, 'main screen was not restored');
    } else {
        requireBoundary(mode.alternateScreen && mode.autoWrap && !mode.originMode && !mode.insertMode && !margins.leftRightMarginMode
            && mode.scrollTop === 0 && mode.scrollBottom === screen.rows - 1 && margins.leftMargin === 0
            && margins.rightMargin === screen.cols - 1, 'fixture modes or margins changed');
    }
    requireBoundary(expected === null ? frame.barrier === null : isBarrier(frame.barrier), 'missing frame fence');
    if (!step.query) return null;
    const { reply } = validateCursorReport({ barrier: frame.barrier, reply: frame.reply });
    requireBoundary(reply === null || (reply.row === screen.cursorRow + 1 && reply.col === screen.cursorCol + 1),
        'cursor reply disagrees with physical observation');
    return reply;
}

export class Collection {
    constructor(run) {
        this.run = run;
        this.frames = [];
        this.observer = new OwnedCellObserver({ appData: run.appData, ownerPid: run.app.pid, profileName: run.profileName,
            nonce: nonce(run.runId), includeCellDetails: true, includeBufferState: true, includeHorizontalMargins: true, prefix: 'uboundary' });
    }

    async observe(request, budget) {
        const stage = request.kind.slice(prefix.length);
        const step = this.plan?.[this.frames.length] ?? { stage: 'start', bytes: null };
        requireBoundary(!this.busy && !this.restored && stage === step.stage, 'unexpected or concurrent observation');
        this.busy = true;
        try {
            const frame = { stage };
            if (step.sample) {
                Object.assign(frame, { exchanges: request.exchanges, restoreBarrier: request.restoreBarrier });
            } else {
                const action = ['start', 'bind', 'finish'].includes(stage) ? stage : 'read';
                const { paneId, screen } = await this.observer.handle(prefix + action, budget, request.barrier);
                Object.assign(frame, { paneId, screen, sha256: snapshotHash(screen), requestBytes: request.requestBytes ?? null,
                    barrier: request.barrier ?? null });
                if (step.query) frame.reply = request.reply;
                if (stage === 'start') this.plan = planFor(screen.cols, this.run.runId);
            }
            verifyFrame(frame, step, this.frames[0] ?? frame);
            appendFileSync(join(this.run.output, journal), JSON.stringify(frame) + '\n', { mode: 0o600 });
            this.frames.push(frame);
            if (stage === 'start') return { cols: frame.screen.cols };
            return {};
        } finally {
            this.busy = false;
        }
    }

    get restored() {
        return this.observer.restored && this.frames.length === this.plan?.length;
    }
}

export function deriveRecords(records, runId) {
    const baseline = records[0];
    requireBoundary(baseline && /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i.test(baseline.paneId), 'missing or invalid fixture pane');
    const cols = baseline.screen.cols, plan = planFor(cols, runId);
    requireBoundary(records.length === plan.length, 'incomplete journal');
    const frames = {}, widths = {};
    let terminalReply;
    for (const [index, step] of plan.entries()) {
        const frame = records[index], reply = verifyFrame(frame, step, baseline);
        if (step.sample) {
            widths[step.stage] = frame.exchanges;
        } else {
            frames[step.stage] = { ...describeFrame(frame.screen), pendingWrap: frame.screen.bufferState.pendingWrap };
        }
        if (step.stage === 'terminal-wrap-target') terminalReply = reply;
    }
    requireBoundary(frames.bind.lines[0] === nonce(runId) && same(frames.bind.cursor, [0, nonce(runId).length]), 'pane nonce did not bind');
    return gradeBoundaries(cols, frames, widths, terminalReply);
}

export function completeReceipt(output, scratch) {
    const contextFile = 'context.json', proofFile = 'ownership-proof.json';
    const context = fileJson(output, contextFile), proof = fileJson(output, proofFile);
    const worker = proof.proofs?.[0], rawMode = worker?.privateClipboard;
    requireBoundary(!existsSync(scratch) && !existsSync(join(output, 'failure.json')) && !existsSync(join(output, 'cleanup-failure.json'))
        && same(context.collectionScope, scope) && same(proof.collectionScope, scope)
        && worker?.app.pid === context.ownerPid && worker.worker.ppid === context.ownerPid
        && typeof rawMode?.originalTerminalState === 'string' && rawMode.originalTerminalState === rawMode.restoredTerminalState
        && proof.completed && !proof.cleanupError && !proof.persistentDaemon
        && proof.sourceSnapshot?.policy === context.sourceSnapshotPolicy && Date.parse(proof.cleanedUpAt) <= Date.parse(proof.sourceSnapshot.removedAt),
    'missing direct-local ownership or verified cleanup');
    const records = readFileSync(join(output, journal), 'utf8').trimEnd().split('\n').map(line => JSON.parse(line));
    const rows = deriveRecords(records, context.runId);
    const files = Object.fromEntries([contextFile, proofFile, journal, 'source-snapshot.json', 'initial-settings.json']
        .map(name => [name, digest(readFileSync(join(output, name)))]));
    const report = { runId: context.runId, suite: scope.id, rows };
    for (const [name, value] of [['unicode-boundary-report.json', report],
        ['unicode-boundary-receipt.json', { schemaVersion: 1, runId: context.runId, suite: scope.id, fixtureRemoved: true, files }]]) {
        writeFileSync(join(output, name), JSON.stringify(value, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
    }
    return { runId: context.runId, verdict: Object.fromEntries(rows.map(row => [row.id, row.effect])) };
}
