// Frozen Modern fixtures. App coordinates are zero-based; DSR 6 reports one-based coordinates.
import { OwnedCellObserver, screenSnapshot } from './agent-observer.mjs';
import { cursorReportQuery, parseCursorReportFrame } from './cursor-report.mjs';
import { validateGradedJournal } from './graded-plan.mjs';

export const modernCursorSuiteId = 'ntilde-modern-cursor-v1';
export const modernCursorPrefix = 'mcur';
export const modernCursorNonce = 'NTILDE_MCUR_';
const da = '\x1b[c';
const enter = '\x1b[?47h';
const leave = '\x1b[?47l';
// Normalize the alternate screen and seed its saved cursor at home. If ANSI
// save is ignored, the later restore has a distinct known position to return to.
const reset = '\x1b[?6l\x1b[?69l\x1b[r\x1b[4l\x1b[?7h\x1b[0m\x1b[2J\x1b[H\x1b7';
const experiments = [
    { traceId: 'cha', ids: ['cursor.horizontal-absolute'], steps: [
        ['home', reset, 0, 0, ''],
        ['text', 'ABCDE', 0, 5, 'ABCDE'],
        ['target', '\x1b[3G', 0, 2, 'ABCDE'],
    ] },
    { traceId: 'cnl', ids: ['cursor.next-line'], steps: [
        ['home', reset, 0, 0, ''],
        ['text', 'ABC', 0, 3, 'ABC'],
        ['target', '\x1b[2E', 2, 0, 'ABC'],
    ] },
    { traceId: 'ansi-round-trip', ids: ['cursor.ansi-save', 'cursor.ansi-restore'], steps: [
        ['home', reset, 0, 0, ''],
        ['initial', '\x1b[4;6H', 3, 5, ''],
        ['saved', '\x1b[s', 3, 5, ''],
        ['displaced', '\x1b[11;11H', 10, 10, ''],
        ['restored', '\x1b[u', 3, 5, ''],
    ] },
];
const steps = [...experiments.flatMap(item => item.steps), ['cleanup', reset, 0, 0, '']];
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (ok, reason) => { if (!ok) throw new Error('Modern cursor: ' + reason); };
const screenOf = screen => screenSnapshot(screen, false, false, false, false, true, false, false, false, true);
const bindBytes = nonce => enter + reset + nonce;
const exactRequest = bytes => bytes + cursorReportQuery + da;
const positionOf = screen => [screen.cursorRow, screen.cursorCol];

function matchesPosition(screen, row, col) {
    return screen.cursorRow === row && screen.cursorCol === col;
}

function checkFixtureScreen(screen, text, role) {
    const state = screen.bufferState, margins = screen.horizontalMargins;
    check(state.alternateScreen && !state.originMode && !margins.leftRightMarginMode
        && state.scrollTop === 0 && state.scrollBottom === screen.rows - 1
        && margins.leftMargin === 0 && margins.rightMargin === screen.cols - 1
        && state.autoWrap && !state.pendingWrap && !state.insertMode && state.scrollbackLines === 0, 'invalid modes or margins at ' + role);
    check(screen.lines[0] === text && screen.lines.slice(1).every(line => line === ''), 'unexpected fixture text at ' + role);
}

function observedStep(frame, record, step) {
    const [role, bytes, row, col, text] = step;
    check(record?.role === role && record.request === exactRequest(bytes) && record.request === frame.request, 'wrong request bytes or step order at ' + role);
    check(typeof frame.response === 'string' && record.response === frame.response, 'worker reply differs from paired App frame at ' + role);
    const cpr = parseCursorReportFrame(frame.response);
    check(cpr?.reply && cpr.barrier === frame.barrier, 'incomplete or unordered CPR at ' + role);
    const { screen } = frame;
    checkFixtureScreen(screen, text, role);
    check(matchesPosition(screen, cpr.reply.row - 1, cpr.reply.col - 1), 'stale CPR or App cursor disagreement at ' + role);
    const matches = matchesPosition(screen, row, col) && cpr.reply.raw === `\x1b[${row + 1};${col + 1}R`;
    if (['home', 'text', 'initial', 'displaced', 'cleanup'].includes(role)) {
        check(matches, 'movement or setup control failed at ' + role);
    }
    return { role, frameIndex: frame.index, request: record.request, cpr: cpr.reply.raw,
        position: positionOf(screen), expectedPosition: [row, col], matches };
}

/** Reissue verdicts from the exact worker exchanges and the parent's paired pane reads. */
export function buildModernCursorReport(frames, run, runId) {
    check(run?.schemaVersion === 1 && run.suiteId === modernCursorSuiteId && run.runId === runId, 'run identity mismatch');
    validateGradedJournal(frames, { frameCount: steps.length + 3, prefix: modernCursorPrefix, noncePrefix: modernCursorNonce,
        runId, label: 'Modern cursor', screenOf });
    for (const frame of frames) check(same(frame.screen, frame.pairedScreen), 'missing or disagreeing paired App reads');
    check(frames[0].screen.rows >= 11 && !frames[0].screen.bufferState.alternateScreen, 'requires main screen and at least 11 rows');
    const nonce = modernCursorNonce + runId;
    checkFixtureScreen(frames[1].screen, nonce, 'bind');
    const [, bound] = frames, restored = frames.at(-1);
    check(bound.request === bindBytes(nonce) + da && restored.request === leave + da
        && same(run.bind, { request: bound.request, barrier: bound.barrier })
        && same(run.finish, { request: restored.request, barrier: restored.barrier }), 'fixture binding or restoration bytes changed');
    check(Array.isArray(run.reads) && run.reads.length === steps.length, 'incomplete transition log');
    const observed = steps.map((step, index) => observedStep(frames[index + 2], run.reads[index], step));
    let offset = 0;
    const traces = experiments.map(({ traceId, steps: planned }) => {
        const transitions = observed.slice(offset, offset + planned.length);
        offset += planned.length;
        return { traceId, transitions };
    });
    const cases = experiments.flatMap(({ ids, traceId }, index) =>
        ids.map(id => ({ id, traceId, conclusion: traces[index].transitions.every(step => step.matches) ? 'pass' : 'fail' })));
    return { schemaVersion: 1, suiteId: modernCursorSuiteId, runId, cases, traces, cleanup: observed.at(-1),
        geometry: { rows: frames[0].screen.rows, cols: frames[0].screen.cols } };
}

export async function collectModernCursor({ config, fence, owner }) {
    const reads = [];
    const initial = await owner('mcur-start');
    check(initial.screen.rows >= 11 && !initial.screen.bufferState.alternateScreen, 'requires main screen and at least 11 rows');
    let bind, primary, failed = false;
    const capture = async ([role, bytes]) => {
        const cpr = await fence.cursorReport(bytes);
        const response = (cpr.reply?.raw ?? '') + cpr.barrier;
        const request = exactRequest(bytes);
        await owner('mcur-read', cpr.barrier, { requestBytes: request, response });
        reads.push({ role, request, response });
    };
    try {
        const bytes = bindBytes(modernCursorNonce + config.runId);
        bind = { request: bytes + da, barrier: await fence.write(bytes) };
        await owner('mcur-bind', bind.barrier, { requestBytes: bind.request });
        for (const step of steps) await capture(step); // NOSONAR: each observation fences the preceding transition.
    } catch (error) { primary = error; failed = true; }
    let finish;
    try {
        const bytes = failed ? reset + leave : leave;
        finish = { request: bytes + da, barrier: await fence.write(bytes) };
        await owner('mcur-finish', finish.barrier, { requestBytes: finish.request });
    } catch (error) {
        if (failed) throw new AggregateError([primary, error], 'Modern cursor collection and restoration failed.');
        throw error;
    }
    if (failed) throw primary;
    return { schemaVersion: 1, suiteId: modernCursorSuiteId, runId: config.runId, bind, reads, finish };
}

export class ModernCursorObserver extends OwnedCellObserver {
    async capture(budget) {
        const first = await super.capture(budget);
        const second = await super.capture(budget);
        this.lastAttempt = { first, second };
        check(same(first, second), 'paired App reads disagree');
        return first;
    }

    async handleRequest({ kind, barrier, requestBytes, response }, budget) {
        if (kind === 'mcur-read') {
            const cpr = parseCursorReportFrame(response);
            check(cpr?.barrier === barrier && cpr.reply, 'missing complete CPR for App observation');
            const step = steps[this.frames.length - 2];
            check(step && requestBytes === exactRequest(step[1]), 'unexpected transition bytes');
        } else {
            check(response === undefined, 'unexpected CPR');
            if (kind === 'mcur-bind') check(requestBytes === bindBytes(this.nonce) + da, 'unexpected bind bytes');
            if (kind === 'mcur-finish') check([leave + da, reset + leave + da].includes(requestBytes), 'unexpected restoration bytes');
        }
        const result = await super.handle(kind, budget, barrier);
        Object.assign(this.frames.at(-1), { pairedScreen: this.lastAttempt.second, request: requestBytes ?? null,
            ...(response === undefined ? {} : { response }) });
        return result;
    }
}
