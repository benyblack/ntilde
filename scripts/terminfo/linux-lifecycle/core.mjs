// Internal deterministic seam; production dependencies are fixed by the public facade.
import { isDeepStrictEqual } from 'node:util';
import { copyLinuxOutputExpected, linuxOutputBudget, linuxOutputDeadline } from '../linux-owned-output-core.mjs';

export { linuxOutputDeadline as createLinuxLifecycleDeadline };
export const lifecycleLimits = Object.freeze({ lifetimeMs: 300000, abortReserveMs: 4000,
    closeReserveMs: 250, commands: 64, frameBytes: 4096, stdoutBytes: 262144,
    stderrBytes: 4096, helperBytes: 65536, interpreterBytes: 16777216 });
const fail = message => { throw new Error(message); };
export function fields(value, names) {
    if (!value || ![Object.prototype, null].includes(Object.getPrototypeOf(value))) fail('Expected plain lifecycle input.');
    const descriptors = Object.getOwnPropertyDescriptors(value), keys = Reflect.ownKeys(descriptors);
    if (keys.length !== names.length || keys.some(key => typeof key !== 'string' || !names.includes(key)
        || !Object.hasOwn(descriptors[key], 'value') || !descriptors[key].enumerable)) fail('Unexpected lifecycle field/accessor.');
    return Object.fromEntries(names.map(name => [name, descriptors[name].value]));
}
export function copyPythonPin(value) {
    const pin = fields(value, ['path', 'sha256']);
    if (typeof pin.path !== 'string' || !/^\/usr\/bin\/python3(?:\.\d{1,2})?$/.test(pin.path)
        || typeof pin.sha256 !== 'string' || !/^[a-f0-9]{64}$/.test(pin.sha256)) fail('Expected explicitly pinned system Python.');
    return Object.freeze(pin);
}
function processMatches(actual, expected, uid) {
    if (actual?.platform !== 'linux' || actual.pid !== expected.pid || actual.startTimeTicks !== expected.startTimeTicks
        || actual.executable !== expected.executable || !isDeepStrictEqual(actual.executableFile, expected.executableFile)
        || !isDeepStrictEqual(actual.argv, expected.argv) || !Array.isArray(actual.uids)
        || actual.uids.length !== 4 || actual.uids.some(item => item !== uid)) fail('Original lifecycle launch identity changed.');
}
function pairedMatches(actual, expected) {
    if (actual?.qualification !== 'provider-foundation-only') fail('Invalid paired lifecycle provider facts.');
    processMatches(actual.parent, expected.app, expected.uid); processMatches(actual.child, expected.worker, expected.uid);
    if (actual.parent.ppid !== expected.app.ppid || actual.child.ppid !== expected.app.pid
        || actual.child.pty?.selectedFd !== 1 || actual.child.pty.descriptors?.length !== 2) fail('Lifecycle ancestry/PTY selection changed.');
    for (let fd = 0; fd < 2; fd++) {
        const descriptor = actual.child.pty.descriptors[fd];
        if (descriptor.fd !== fd || descriptor.path !== expected.pty.path
            || !isDeepStrictEqual(descriptor.device, expected.pty.device)) fail('Lifecycle private PTY identity changed.');
    }
}
function detached(value) { // Only accepted provider data, never rejected mutable argv, enters observations.
    const copy = structuredClone(value);
    const freeze = item => { if (item && typeof item === 'object') { Object.values(item).forEach(freeze); Object.freeze(item); } return item; };
    return freeze(copy);
}
export function parseObservation(frame) {
    const result = fields(frame, ['app', 'worker', 'attempts', 'pidfdsClosed']);
    for (const role of ['app', 'worker']) {
        const facts = fields(result[role], ['exitObserved', 'pollEvents']);
        if (typeof facts.exitObserved !== 'boolean' || !Number.isSafeInteger(facts.pollEvents)
            || facts.pollEvents < 0 || facts.pollEvents > 65535) fail('Invalid pidfd observation.');
        result[role] = Object.freeze(facts);
    }
    if (typeof result.pidfdsClosed !== 'boolean' || !Array.isArray(result.attempts) || result.attempts.length > 8) fail('Invalid lifecycle attempts/close observation.');
    result.attempts = result.attempts.map(item => {
        const attempt = fields(item, ['role', 'signal', 'sent', 'errno']);
        if (!['app', 'worker'].includes(attempt.role) || !['TERM', 'KILL'].includes(attempt.signal)
            || typeof attempt.sent !== 'boolean' || !(attempt.errno === null || Number.isSafeInteger(attempt.errno) && attempt.errno > 0 && attempt.errno < 4096)
            || attempt.sent !== (attempt.errno === null)) fail('Invalid fixed lifecycle signal observation.');
        return Object.freeze(attempt);
    });
    return Object.freeze({ ...result, attempts: Object.freeze(result.attempts) });
}
export function conservativeDeadline(clockNs, receivedNs, deadlineNs) {
    if (typeof clockNs !== 'string' || !/^(?:0|[1-9]\d{0,19})$/.test(clockNs)
        || BigInt(clockNs) > 18446744073709551615n) fail('Invalid helper monotonic sample.');
    const remaining = deadlineNs - receivedNs - BigInt(lifecycleLimits.closeReserveMs) * 1000000n;
    if (remaining <= 0n || remaining > 4000000000n) fail('Lifecycle phase has no remaining work budget.');
    return (BigInt(clockNs) + remaining).toString();
}
function failure(primary, others, state) {
    const error = new AggregateError([primary, ...others].filter(Boolean), 'Linux lifecycle identity/action/close failed.');
    Object.defineProperty(error, 'linuxLifecycle', { value: state }); return error;
}

/** No public platform/proc/PID-fd reader override. These dependencies are test-only. */
export async function acquireWithIo(io, expectedInput, pythonInput, options) {
    const expected = copyLinuxOutputExpected(expectedInput, io.currentUid()), python = copyPythonPin(pythonInput);
    const { deadlineNs, lifetimeMs } = fields(options, ['deadlineNs', 'lifetimeMs']);
    if (!Number.isSafeInteger(lifetimeMs) || lifetimeMs < 10000 || lifetimeMs > lifecycleLimits.lifetimeMs) fail('Invalid lifecycle helper lifetime.');
    const initial = linuxOutputBudget({ deadlineNs }, io.now);
    const totalDeadlineNs = io.now() + BigInt(lifetimeMs) * 1000000n;
    let channel, latest, acceptedFacts, primary, busy = false, settled, appTerminationRequested = false, admitted = false;
    const state = () => Object.freeze({ platform: 'linux', qualification: 'pidfd-lifecycle-foundation-only',
        runId: expected.runId, expected, python, acceptedFacts, observation: latest,
        admitted, aborted: !!primary, helper: channel?.resources() });
    const mark = error => { primary ??= error; return error; };
    const consume = frame => { latest = parseObservation(frame); return latest; };
    const sample = async phase => {
        phase.check(); const result = await channel.request({ op: 'clock' }, phase);
        phase.check(); const receivedNs = io.now();
        consume(result.observation);
        return { helperDeadlineNs: conservativeDeadline(result.clockNs, receivedNs, phase.deadlineNs),
            lifetimeDeadlineNs: (BigInt(result.clockNs) + totalDeadlineNs - receivedNs).toString(), clock: latest };
    };
    const verify = phase => {
        if (!latest.app.exitObserved && !latest.worker.exitObserved) {
            const facts = io.paired(expected, phase.remainingMs()); phase.check(); pairedMatches(facts, expected); acceptedFacts = detached(facts);
        } else {
            for (const role of ['app', 'worker']) {
                if (latest[role].exitObserved) continue;
                const facts = io.process(expected[role].pid, phase.remainingMs()); phase.check();
                processMatches(facts, expected[role], expected.uid);
                if (facts.ppid !== (role === 'app' ? expected.app.ppid : expected.app.pid)) fail('Surviving lifecycle ancestry changed.');
            }
        }
        if (latest.app.exitObserved && !appTerminationRequested) fail('Original App exited before its requested termination.');
    };
    const act = async (op, phase, ownership = true) => {
        const token = await sample(phase);
        if (ownership) verify(phase);
        if (primary && ownership) throw primary;
        const result = await channel.request({ op, deadlineNs: token.helperDeadlineNs }, phase);
        consume(result.observation); phase.check();
        if (ownership) verify(phase);
        if (primary && ownership) throw primary;
        return state();
    };
    try {
        channel = await io.open(expected, python, lifetimeMs, initial);
        const token = await sample(initial);
        if (latest.app.exitObserved || latest.worker.exitObserved) fail('Original lifecycle targets exited before admission.');
        verify(initial);
        consume((await channel.request({ op: 'admit', deadlineNs: token.helperDeadlineNs,
            lifetimeDeadlineNs: token.lifetimeDeadlineNs }, initial)).observation);
        initial.check();
        if (latest.app.exitObserved || latest.worker.exitObserved) fail('Original lifecycle targets exited across admission.');
        admitted = true;
    } catch (error) {
        mark(error); const errors = [];
        if (channel) { try { await channel.finish(initial); } catch (closeError) { errors.push(closeError); } }
        throw failure(primary, errors, state());
    }
    async function operation(op, phaseOptions) {
        if (settled) fail('Lifecycle lease already settled.');
        if (busy) throw mark(new Error('Overlapping lifecycle operation.'));
        if (primary) throw primary;
        busy = true;
        try {
            const phase = linuxOutputBudget(phaseOptions, io.now);
            if (op === 'terminateApp') {
                if (!latest.worker.exitObserved) fail('Worker exit must be observed before App termination.');
                appTerminationRequested = true;
            }
            return await act(op, phase);
        } catch (error) { throw mark(error); }
        finally { busy = false; }
    }
    async function settle(abort, phaseOptions) {
        if (settled) { if (settled.error) throw settled.error; return settled.result; }
        if (busy) throw mark(new Error('Overlapping lifecycle settlement.'));
        busy = true; const errors = [];
        let phase = { deadlineNs: io.now(), now: io.now };
        if (abort) mark(new Error('Explicit lifecycle abort cleanup.'));
        try {
            phase = linuxOutputBudget(phaseOptions, io.now);
            // Normal close disarms helper EOF/expiry cleanup BEFORE any transport closure.
            await act(abort ? 'abort' : 'close', phase, false);
        } catch (error) { errors.push(error); }
        finally {
            try { await channel.finish(phase); } catch (error) { errors.push(error); }
            busy = false;
        }
        if (!primary && errors.length) primary = errors.shift();
        const result = state();
        if (primary) { const error = failure(primary, errors, result); settled = { error }; throw error; }
        settled = { result }; return result;
    }
    return Object.freeze({ observation: state,
        observe: options => operation('observe', options),
        terminateWorker: options => operation('terminateWorker', options),
        terminateApp: options => operation('terminateApp', options),
        abortCleanup: options => settle(true, options), close: options => settle(false, options) });
}
