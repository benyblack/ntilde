// Internal deterministic seam. Public callers cannot replace proc, spawn or filesystem readers.
import { isDeepStrictEqual } from 'node:util';
import { fields, copyPythonPin, conservativeDeadline } from '../linux-lifecycle/core.mjs';
import { linuxOutputBudget } from '../linux-owned-output-core.mjs';
export { linuxOutputDeadline as createLinuxLaunchDeadline } from '../linux-owned-output-core.mjs';

export const launchLimits = Object.freeze({ lifetimeMs: 300000, abortReserveMs: 4000, closeReserveMs: 250,
    commands: 64, frameBytes: 4096, stdoutBytes: 262144, stderrBytes: 4096, logBytes: 1048576,
    helperBytes: 65536, pythonBytes: 16777216, appBytes: 33554432, managedBytes: 67108864, ptyBytes: 16777216 });
const fail = message => { throw new Error(message); };
const pid = value => Number.isSafeInteger(value) && value >= 2 && value <= 2147483647;
const hash = value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
const path = value => typeof value === 'string' && value.length < 4096 && value.startsWith('/')
    && !/[\x00-\x1f\x7f]/.test(value) && value.split('/').slice(1).every(part => part && part !== '.' && part !== '..');
const uint = value => typeof value === 'string' && /^(?:0|[1-9]\d{0,19})$/.test(value) && BigInt(value) <= 18446744073709551615n;
/** Copy only a canonical current-user private-directory expectation; no filesystem authority is inferred. */
function directory(value, uid) {
    const item = fields(value, ['path', 'device']);
    if (!path(item.path)) fail('Invalid launch directory path.');
    const device = fields(item.device, ['dev', 'ino', 'uid', 'mode']);
    if (!Object.values(device).every(uint) || device.uid !== String(uid)
        || (BigInt(device.mode) & 0o177777n) !== 0o40700n) fail('Expected a current-user private launch directory.');
    return Object.freeze({ path: item.path, device: Object.freeze(device) });
}
/** Freeze the exact source-selected pins and private fixture fields; reject selectors and accessors. */
export function copyLaunchInput(artifactInput, fixtureInput, pythonInput, uid) {
    const artifact = fields(artifactInput, ['directory', 'sha256']);
    const sha256 = fields(artifact.sha256, ['app', 'managed', 'pty']);
    if (!Object.values(sha256).every(hash)) fail('Invalid fixed launch artifact pins.');
    const fixture = fields(fixtureInput, ['runId', 'uid', 'scratch', 'appDataDirectory', 'initialSettingsSha256', 'locale', 'appVersion']);
    if (!Number.isSafeInteger(uid) || uid < 0 || uid > 4294967294 || fixture.uid !== uid
        || typeof fixture.runId !== 'string' || !/^[a-f0-9]{32}$/.test(fixture.runId)
        || !hash(fixture.initialSettingsSha256)) fail('Invalid current-user launch fixture.');
    if (typeof fixture.locale !== 'string' || !/^[A-Za-z0-9_@.-]{1,48}(?:UTF-8|utf8)$/.test(fixture.locale)
        || typeof fixture.appVersion !== 'string' || !/^\d{1,4}\.\d{1,4}\.\d{1,4}(?:[-.][a-zA-Z0-9.-]{1,32})?$/.test(fixture.appVersion)) fail('Invalid declared launch locale/version.');
    fixture.scratch = directory(fixture.scratch, uid); fixture.appDataDirectory = directory(fixture.appDataDirectory, uid);
    if (fixture.appDataDirectory.path !== fixture.scratch.path + '/appdata') fail('Appdata must be the fixed private fixture child.');
    artifact.directory = directory(artifact.directory, uid);
    return Object.freeze({ artifact: Object.freeze({ directory: artifact.directory, sha256: Object.freeze(sha256) }),
        fixture: Object.freeze(fixture), python: copyPythonPin(pythonInput) });
}
/** Detach retained accepted facts so later caller mutation cannot rewrite the observation. */
function copied(value) {
    const clone = structuredClone(value);
    const freeze = item => { if (item && typeof item === 'object') { Object.values(item).forEach(freeze); Object.freeze(item); } return item; };
    return freeze(clone);
}
/** Validate consuming wait metadata without treating readiness, EOF or ECHILD as a wait. */
function copyWait(value, childPid) {
    if (value === null) return null;
    const wait = fields(value, ['source', 'pid', 'uid', 'code', 'status']);
    if (!['P_PIDFD', 'waitpid-preexec'].includes(wait.source) || wait.pid !== childPid
        || !Number.isSafeInteger(wait.status) || wait.status < 0 || wait.status > 65535) fail('Invalid owned-child wait status.');
    if (wait.source === 'P_PIDFD' && (!Number.isSafeInteger(wait.uid) || wait.uid < 0 || wait.uid > 4294967294
        || ![1, 2, 3].includes(wait.code))) fail('Invalid consuming pidfd wait result.');
    if (wait.source === 'waitpid-preexec' && (wait.uid !== null || wait.code !== null)) fail('Pre-exec wait cannot claim pidfd metadata.');
    return Object.freeze(wait);
}
/** Bound helper facts and enforce distinct pre-exec-child versus admitted-App reaping semantics. */
export function copyLaunchObservation(value) {
    const result = fields(value, ['childPid', 'forked', 'pidfdOpened', 'preExecReady', 'gateReleased', 'execPipeClosed', 'admitted',
        'exitObserved', 'childReaped', 'preExecChildReaped', 'appReaped', 'wait', 'childErrno', 'attempts',
        'pidfdClosed', 'childPipesClosed', 'appLogClosed', 'appLogBytes', 'aborted', 'errors']);
    const flags = ['forked', 'pidfdOpened', 'preExecReady', 'gateReleased', 'execPipeClosed', 'admitted', 'exitObserved',
        'childReaped', 'preExecChildReaped', 'appReaped', 'pidfdClosed', 'childPipesClosed', 'appLogClosed', 'aborted'];
    if (flags.some(key => typeof result[key] !== 'boolean') || !(result.childPid === null || pid(result.childPid))
        || result.forked !== (result.childPid !== null) || !Number.isSafeInteger(result.appLogBytes)
        || result.appLogBytes < 0 || result.appLogBytes > launchLimits.logBytes) fail('Invalid launch observation flags.');
    result.wait = copyWait(result.wait, result.childPid);
    if (result.childReaped !== (result.wait !== null) || result.appReaped && (!result.admitted || !result.childReaped || result.wait.source !== 'P_PIDFD')
        || result.preExecChildReaped && (!result.childReaped || result.gateReleased) || result.admitted && (!result.preExecReady || !result.gateReleased || !result.pidfdOpened)) fail('Incoherent launch wait/admission facts.');
    if (!(result.childErrno === null || Number.isSafeInteger(result.childErrno) && result.childErrno > 0 && result.childErrno < 4096)
        || !Array.isArray(result.attempts) || result.attempts.length > 2 || !Array.isArray(result.errors)
        || result.errors.length > 8 || result.errors.some(code => typeof code !== 'string' || !/^[A-Za-z][A-Za-z0-9]{0,63}$/.test(code))) fail('Invalid bounded launch outcomes.');
    result.attempts = result.attempts.map(value => {
        const item = fields(value, ['signal', 'sent', 'errno']);
        if (!['TERM', 'KILL'].includes(item.signal) || typeof item.sent !== 'boolean'
            || !(item.errno === null || Number.isSafeInteger(item.errno) && item.errno > 0 && item.errno < 4096)
            || item.sent !== (item.errno === null)) fail('Invalid original-pidfd signal fact.');
        return Object.freeze(item);
    });
    return copied(result);
}
/** Compare exact original launch/birth and isolated-session facts from the fixed public provider. */
function assertProcess(actual, expected, uid) {
    if (actual?.platform !== 'linux' || actual.pid !== expected.pid || actual.ppid !== expected.ppid
        || actual.executable !== expected.executable || !isDeepStrictEqual(actual.executableFile, expected.file)
        || !isDeepStrictEqual(actual.argv, expected.argv) || !Array.isArray(actual.uids)
        || actual.uids.length !== 4 || actual.uids.some(item => item !== uid)
        || !uint(actual.startTimeTicks) || (expected.birth != null && actual.startTimeTicks !== expected.birth)
        || expected.isolated && (actual.processGroupId !== actual.pid || actual.sessionId !== actual.pid || actual.controllingDevice !== '0')) fail('Original launch process identity changed.');
}
class Launch {
    constructor(io, input, options) {
        this.io = io; this.input = input;
        const limits = fields(options, ['deadlineNs', 'lifetimeMs']);
        if (!Number.isSafeInteger(limits.lifetimeMs) || limits.lifetimeMs < 10000 || limits.lifetimeMs > launchLimits.lifetimeMs) fail('Invalid launch lifetime.');
        this.initial = linuxOutputBudget({ deadlineNs: limits.deadlineNs }, io.now);
        this.totalDeadlineNs = io.now() + BigInt(limits.lifetimeMs) * 1000000n;
        this.lifetimeMs = limits.lifetimeMs; this.busy = false;
    }
    /** Keep the first failure sticky while cleanup facts remain separately observable. */
    mark(error) { this.primary ??= error; return error; }
    /** Retain actual helper facts before a later deadline or identity failure can refuse the call. */
    consume(frame) { if (frame?.observation) this.observation = copyLaunchObservation(frame.observation); return frame; }
    /** Copy the latest factual state and sticky abort; never provide whole-run completion or a PID selector. */
    snapshot() {
        const helper = this.channel?.snapshot() ?? null;
        const observation = helper?.observation ? copyLaunchObservation(helper.observation) : this.observation ?? null;
        return copied({ platform: 'linux', qualification: 'source-owned-launch-foundation-only',
            runId: this.input.fixture.runId, expected: this.input, acceptedPreExec: this.preExec ?? null,
            acceptedApp: this.app ?? null, observation,
            aborted: !!this.primary || !!observation?.aborted || !!helper?.failed, helper });
    }
    /** Share the caller phase around a fixed helper operation, preserving an actual late result before refusal. */
    async call(op, phase, extra = {}) {
        phase.check();
        try {
            const frame = this.consume(await this.channel.request({ op, ...extra }, phase));
            phase.check(); return frame;
        } catch (error) { if (error.launchObservation) this.observation = copyLaunchObservation(error.launchObservation); throw error; }
    }
    /** Translate remaining parent time conservatively from a received helper monotonic sample. */
    async clock(phase) {
        phase.check(); const frame = await this.call('clock', phase); phase.check(); const received = this.io.now();
        return { deadlineNs: conservativeDeadline(frame.clockNs, received, phase.deadlineNs),
            lifetimeDeadlineNs: (BigInt(frame.clockNs) + this.totalDeadlineNs - received).toString() };
    }
    /** Require the original acquired, setup-ready reference before admission or ordinary operations. */
    assertHeldAlive() { if (!this.observation?.pidfdOpened || !this.observation.preExecReady || this.observation.exitObserved || this.observation.aborted) fail('Original launch child is not available for admission.'); }
    /** Read only the explicitly created process through the public provider within remaining phase time. */
    facts(pid_, phase) { phase.check(); const value = this.io.process(pid_, phase.remainingMs()); phase.check(); return value; }
    /** Bind the exact spawned helper, interpreter bytes, original parent and isolated session. */
    helperFacts(phase, birth = null) {
        const resources = this.channel.resources(); const actual = this.facts(resources.helperPid, phase);
        assertProcess(actual, { pid: resources.helperPid, ppid: this.io.selfPid(), executable: resources.python.path,
            file: resources.python.file, argv: resources.helperArgv, birth, isolated: true }, this.input.fixture.uid);
        return actual;
    }
    /** Pin the setup-ready child birth while the source-owned exec gate is still closed. */
    preExecFacts(phase) {
        const resources = this.channel.resources(); const helper = this.helperFacts(phase);
        const child = this.facts(this.observation.childPid, phase);
        assertProcess(child, { pid: this.observation.childPid, ppid: helper.pid, executable: resources.python.path,
            file: resources.python.file, argv: resources.helperArgv, isolated: true }, this.input.fixture.uid);
        this.preExec = copied({ helper, child });
    }
    /** Bind the same original birth to exact post-exec App bytes, argv, parent and terminal isolation. */
    appFacts(phase) {
        const resources = this.channel.resources(); const helper = this.helperFacts(phase, this.preExec.helper.startTimeTicks);
        const child = this.facts(this.observation.childPid, phase);
        assertProcess(child, { pid: this.preExec.child.pid, ppid: helper.pid, birth: this.preExec.child.startTimeTicks,
            executable: resources.app.path, file: resources.app.file, argv: resources.appArgv, isolated: true }, this.input.fixture.uid);
        this.app = copied({ helper, child });
    }
    /** Acquire the original reference and independent pre/post-exec facts before returning any lease. */
    async acquire() {
        this.channel = await this.io.open(this.input, this.initial);
        const clock = await this.clock(this.initial);
        await this.call('prepare', this.initial, clock); this.assertHeldAlive();
        this.preExecFacts(this.initial);
        await this.call('observe', this.initial, { deadlineNs: clock.deadlineNs }); this.assertHeldAlive();
        await this.call('release', this.initial, { deadlineNs: clock.deadlineNs });
        this.assertHeldAlive(); this.appFacts(this.initial);
        await this.call('observe', this.initial, { deadlineNs: clock.deadlineNs }); this.assertHeldAlive();
        this.channel.check(this.initial, false);
        await this.call('admitApp', this.initial, { deadlineNs: clock.deadlineNs });
        this.initial.check(); this.assertHeldAlive();
        return Object.freeze({ observe: options => this.operation('observe', options), stop: options => this.settle(false, options),
            abortCleanup: options => this.settle(true, options), snapshot: () => this.snapshot() });
    }
    /** Refuse overlap or sticky failure; ordinary observations cannot retarget or renew the lifetime. */
    async operation(op, options) {
        if (this.settled) fail('Launch already settled.');
        if (this.busy) throw this.mark(new Error('Overlapping launch operation.'));
        if (this.primary) throw this.primary;
        this.busy = true;
        try {
            const phase = linuxOutputBudget(options, this.io.now); const clock = await this.clock(phase);
            this.assertHeldAlive(); this.appFacts(phase); this.channel.check(phase, false);
            await this.call(op, phase, { deadlineNs: clock.deadlineNs }); phase.check();
            this.assertHeldAlive(); if (this.primary) throw this.primary; return this.snapshot();
        } catch (error) { throw this.mark(error); }
        finally { this.busy = false; }
    }
    /** Settle once with genuine owner-wait and resource facts, preserving primary and cleanup failures. */
    async settle(abort, options) {
        if (this.settled) { if (this.settled.error) throw this.settled.error; return this.settled.value; }
        if (this.busy) throw this.mark(new Error('Overlapping launch settlement.'));
        this.busy = true; if (abort) this.mark(new Error('Explicit owned launch abort.'));
        const errors = []; let phase;
        try {
            phase = linuxOutputBudget(options, this.io.now); const clock = await this.clock(phase);
            if (!abort && !this.primary) { this.assertHeldAlive(); this.appFacts(phase); }
            await this.call(abort || this.primary ? 'abort' : 'stop', phase, { deadlineNs: clock.deadlineNs });
        } catch (error) { this.mark(error); errors.push(error); }
        finally {
            if (phase) {
                try {
                    let closing = phase;
                    if (errors.length || this.primary) {
                        this.cleanupDeadlineNs ??= this.io.now() + 4000000000n;
                        const limit = this.cleanupDeadlineNs < this.totalDeadlineNs ? this.cleanupDeadlineNs : this.totalDeadlineNs;
                        closing = linuxOutputBudget({ deadlineNs: limit }, this.io.now);
                    }
                    const final = await this.channel.finish(closing);
                    if (final) this.observation = copyLaunchObservation(final);
                    if (!errors.length && !this.primary) phase.check();
                } catch (error) { this.mark(error); errors.push(error); }
            }
            this.busy = false;
        }
        if (!abort && !this.primary && (!this.observation?.appReaped || !this.observation.pidfdClosed
            || !this.observation.childPipesClosed || !this.observation.appLogClosed)) errors.push(new Error('Owned App reap/resource closure is unconfirmed.'));
        if (errors.length || this.primary) {
            const error = new AggregateError([this.primary, ...errors].filter(Boolean), 'Owned launch action/wait/close failed.');
            Object.defineProperty(error, 'linuxLaunch', { value: this.snapshot() }); this.settled = { error }; throw error;
        }
        const value = this.snapshot(); this.settled = { value }; return value;
    }
    /** Use only the preauthorized remaining abort reserve after an indeterminate admission. */
    async acquireFailure(error) {
        this.mark(error); const errors = [];
        if (this.channel) {
            const limit = this.io.now() + 4000000000n;
            const deadlineNs = limit < this.totalDeadlineNs ? limit : this.totalDeadlineNs;
            try { await this.settle(true, { deadlineNs }); } catch (cleanup) { errors.push(cleanup); }
        }
        const failure = new AggregateError([error, ...errors], 'Owned launch admission/cleanup failed.');
        Object.defineProperty(failure, 'linuxLaunch', { value: this.snapshot() }); throw failure;
    }
}
/** Internal deterministic entry point; the public facade supplies all production I/O dependencies. */
export async function launchWithIo(io, artifact, fixture, python, options) {
    const input = copyLaunchInput(artifact, fixture, python, io.currentUid());
    const lease = new Launch(io, input, options);
    try { return await lease.acquire(); } catch (error) { return lease.acquireFailure(error); }
}
