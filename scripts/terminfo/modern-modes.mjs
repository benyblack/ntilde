// App protocol arm of the frozen Modern criteria. DA brackets each reply,
// including explicit silence, and the parent binds the exchange to its pane.
import { OwnedCellObserver, screenSnapshot } from './agent-observer.mjs';
import { validateGradedJournal } from './graded-plan.mjs';

export const modernModesSuiteId = 'ntilde-modern-modes-v1';
export const modernModesNonce = 'NTILDE_MMOD_';
export const modernModeRequirements = Object.freeze({
    2026: 'modes.synchronized-output',
    2031: 'modes.color-scheme-reporting',
});

// Matches TerminalBuffer's existing synchronized-output timeout.
const syncTimeoutMs = 200;
const da = '\x1b[c';
const leave = '\x1b[?47l';
const finishBytes = '\x1b[2J\x1b[H' + leave;
const bindBytes = nonce => '\x1b[?47h\x1b[2J\x1b[H' + nonce;
const modeBytes = (mode, command) => `\x1b[?${mode}${command}`;
const query = mode => modeBytes(mode, '$p');
const reset = mode => modeBytes(mode, 'l');
// Both queries and their mutations reach the PTY in one write. In particular
// 2026's DECSET and query have no intervening observer round trip or timer.
const transition = mode => modeBytes(mode, 'h') + query(mode) + da + reset(mode) + query(mode);
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const check = (condition, reason) => { if (!condition) throw new Error('Modern modes: ' + reason); };
const screenOf = screen => screenSnapshot(screen, false, false, false, false, true);

function parseModeFrame(bytes, mode, count) {
    check(bytes.length <= 512, 'oversized DECRPM exchange');
    const frames = [];
    let rest = bytes;
    for (let index = 0; index < count; index++) {
        const end = rest.indexOf('c');
        if (end < 0) return null;
        const frame = rest.slice(0, end + 1);
        const match = /^(?:(\x1b\[\?(\d+);([0-4])\$y))?(\x1b\[\?[\d;]+c)$/.exec(frame);
        check(match && (match[1] === undefined || match[2] === String(mode)), 'malformed or mismatched DECRPM exchange');
        frames.push({ reply: match[1] ?? null, pm: match[3] === undefined ? null : Number(match[3]), barrier: match[4] });
        rest = rest.slice(end + 1);
    }
    check(rest === '', 'unexpected bytes after DECRPM exchange');
    return frames;
}

function grade(mode, initial, afterSet) {
    if (initial === 1 || initial === 3) return { conclusion: 'pass', outcome: 'supported without mutation' };
    if (initial === 0 || initial === 4) return { conclusion: 'fail', outcome: 'unsupported without mutation' };
    if (initial === null || afterSet === null) return { conclusion: 'inconclusive', outcome: 'no DECRPM reply' };
    if (afterSet === 1 || afterSet === 3) return { conclusion: 'pass', outcome: 'supported' };
    if (mode === 2031 && afterSet === 2) return { conclusion: 'fail', outcome: 'unsupported: DECSET kept the mode reset' };
    return { conclusion: 'inconclusive', outcome: 'probe-invalid: DECRPM did not establish the set state' };
}

function requireTimelyExchange(mode, role, elapsedMs) {
    check(Number.isFinite(elapsedMs) && elapsedMs >= 0, 'invalid exchange duration');
    if (mode === 2026 && role === 'set-restore') {
        check(elapsedMs < syncTimeoutMs, 'synchronized-output exchange exceeded 200 ms');
    }
}

function readExchange(frames, record, index, mode, role) {
    const isInitial = role === 'initial';
    const frame = frames[index];
    const request = (isInitial ? query(mode) : transition(mode)) + da;
    check(record?.mode === mode && record.role === role && record.request === request && frame?.request === request
        && typeof record.response === 'string' && record.response === frame.response, 'unexpected exchange bytes or order');
    requireTimelyExchange(mode, role, record.elapsedMs);
    const replies = parseModeFrame(record.response, mode, isInitial ? 1 : 2);
    check(replies?.at(-1).barrier === frame.barrier, 'missing or stale DECRPM fence');
    return { role, frameIndex: index, request, response: record.response, elapsedMs: record.elapsedMs, replies };
}

export function buildModernModesReport(frames, run, runId) {
    check(run?.schemaVersion === 1 && run.suiteId === modernModesSuiteId && run.runId === runId
        && Array.isArray(run.exchanges) && run.exchanges.length >= 2 && run.exchanges.length <= 4, 'run identity or exchange count mismatch');
    validateGradedJournal(frames, { frameCount: run.exchanges.length + 3, prefix: 'mmod', noncePrefix: modernModesNonce,
        runId, label: 'Modern modes', screenOf });
    const [start, bound] = frames, finish = frames.at(-1);
    check(!start.screen.bufferState.alternateScreen && bound.screen.bufferState.alternateScreen, 'expected a disposable alternate-screen fixture');
    check(bound.request === bindBytes(modernModesNonce + runId) + da && finish.request === finishBytes + da
        && same(run.bind, { request: bound.request, barrier: bound.barrier })
        && same(run.finish, { request: finish.request, barrier: finish.barrier }), 'fixture binding or restoration bytes changed');
    check(frames.slice(2, -1).every(frame => frame.sha256 === bound.sha256), 'pane changed during the mode exchanges');
    let index = 0;
    const cases = Object.entries(modernModeRequirements).map(([ps, id]) => {
        const mode = Number(ps);
        const initial = readExchange(frames, run.exchanges[index], index + 2, mode, 'initial');
        index++;
        const exchanges = [initial];
        const pm = initial.replies[0].pm;
        let afterSet = null;
        if (pm === 2) {
            const changed = readExchange(frames, run.exchanges[index], index + 2, mode, 'set-restore');
            index++;
            check(changed.replies[1].pm === 2, 'mode ' + mode + ' restoration was not verified');
            afterSet = changed.replies[0].pm;
            exchanges.push(changed);
        }
        return { id, mode, ...grade(mode, pm, afterSet), exchanges };
    });
    check(index === run.exchanges.length, 'unexpected extra exchange');
    return { schemaVersion: 1, suiteId: modernModesSuiteId, runId, cases,
        geometry: { rows: start.screen.rows, cols: start.screen.cols } };
}

async function failAfterCleanup({ fence, run, pendingReset, retainFailed, error }) {
    const cleanup = (pendingReset === undefined ? '' : reset(pendingReset)) + leave;
    run.cleanup = { request: fence.failed ? cleanup : cleanup + da };
    let failure = error;
    try {
        if (fence.failed) await fence.writeCleanup(cleanup);
        else run.cleanup.barrier = await fence.write(cleanup);
    } catch (cleanupError) {
        failure = new AggregateError([error, cleanupError], 'Modern modes collection and cleanup failed.');
    }
    try {
        retainFailed?.(run);
    } catch (retentionError) {
        throw new AggregateError([failure, retentionError], 'Modern modes collection and attempt retention failed.');
    }
    throw failure;
}

export async function collectModernModes({ config, fence, owner, retainFailed }) {
    const run = { schemaVersion: 1, suiteId: modernModesSuiteId, runId: config.runId, exchanges: [] };
    const start = await owner('mmod-start');
    check(!start.screen.bufferState.alternateScreen, 'requires the main screen before binding');
    let pendingReset;
    const capture = async (mode, role, bytes, count) => {
        const record = { mode, role, request: bytes + da, response: '' };
        run.exchanges.push(record);
        const started = performance.now();
        let replies;
        try {
            replies = await fence.exchange(bytes, received => {
                record.response = received;
                return parseModeFrame(received, mode, count);
            });
        } finally {
            record.elapsedMs = performance.now() - started;
        }
        requireTimelyExchange(mode, role, record.elapsedMs);
        await owner('mmod-read', replies.at(-1).barrier, { requestBytes: record.request, response: record.response });
        return replies;
    };
    const bind = bindBytes(modernModesNonce + config.runId);
    try {
        run.bind = { request: bind + da, barrier: await fence.write(bind) };
        await owner('mmod-bind', run.bind.barrier, { requestBytes: run.bind.request });
        for (const mode of [2026, 2031]) {
            const [initial] = await capture(mode, 'initial', query(mode), 1); // NOSONAR: finish before deciding whether to mutate this mode.
            if (initial.pm === 2) {
                pendingReset = mode;
                const [, restored] = await capture(mode, 'set-restore', transition(mode), 2); // NOSONAR: keep each mode's experiment independent.
                check(restored.pm === 2, 'mode ' + mode + ' restoration was not verified');
                pendingReset = undefined;
            }
        }
        run.finish = { request: finishBytes + da, barrier: await fence.write(finishBytes) };
        await owner('mmod-finish', run.finish.barrier, { requestBytes: run.finish.request });
        return run;
    } catch (error) {
        await failAfterCleanup({ fence, run, pendingReset, retainFailed, error });
    }
}

export class ModernModesObserver extends OwnedCellObserver {
    async handleRequest({ kind, barrier, requestBytes, response }, budget) {
        const result = await super.handle(kind, budget, barrier);
        Object.assign(this.frames.at(-1), { request: requestBytes ?? null, ...(response === undefined ? {} : { response }) });
        return result;
    }
}
