// Internal domain/test seam. Production callers use linux-owned-output.mjs, not an injected reader.
import { isDeepStrictEqual } from 'node:util';

export const linuxOutputLimits = Object.freeze({ timeoutMs: 4000, closeReserveMs: 250, outputBytes: 4096,
    elfBytes: 4 * 1024 * 1024, utilityCalls: 20 });
const fail = message => { throw new Error(message); };
const pid = value => Number.isSafeInteger(value) && value >= 2 && value <= 2147483647;
const uid = value => Number.isSafeInteger(value) && value >= 0 && value <= 4294967294;
const uint = value => typeof value === 'string' && /^(?:0|[1-9]\d*)$/.test(value)
    && value.length <= 20 && BigInt(value) <= 18446744073709551615n;
const path = value => typeof value === 'string' && value.length > 1 && value.length < 4096
    && value.startsWith('/') && !/[\x00-\x1f\x7f]/.test(value)
    && value.split('/').slice(1).every(part => part && part !== '.' && part !== '..');

function fields(value, keys) {
    if (!value || typeof value !== 'object' || ![Object.prototype, null].includes(Object.getPrototypeOf(value))) fail('Expected plain Linux output input.');
    const descriptors = Object.getOwnPropertyDescriptors(value), names = Reflect.ownKeys(descriptors);
    if (names.length !== keys.length || names.some(key => typeof key !== 'string' || !keys.includes(key)
        || !Object.hasOwn(descriptors[key], 'value') || !descriptors[key].enumerable)) fail('Unexpected Linux output fields or accessor.');
    return Object.fromEntries(keys.map(key => [key, descriptors[key].value]));
}
function argv(value) {
    if (!Array.isArray(value) || Object.getPrototypeOf(value) !== Array.prototype) fail('Invalid Linux output argv.');
    const descriptors = Object.getOwnPropertyDescriptors(value), length = descriptors.length.value;
    if (length < 1 || length > 128 || Reflect.ownKeys(descriptors).length !== length + 1) fail('Invalid Linux output argv length.');
    const result = [];
    for (let i = 0; i < length; i++) {
        const entry = descriptors[i];
        if (!entry || !Object.hasOwn(entry, 'value') || typeof entry.value !== 'string'
            || entry.value.length > 8192 || entry.value.includes('\0')) fail('Invalid Linux output argv value.');
        result.push(entry.value);
    }
    if (!result[0]) fail('Missing Linux output argv0.');
    return Object.freeze(result);
}
function identity(value, regular) {
    const result = fields(value, regular ? ['dev', 'ino', 'rdev', 'uid', 'mode', 'size', 'mtimeNs', 'ctimeNs'] : ['dev', 'ino', 'rdev', 'uid', 'mode']);
    for (const [key, item] of Object.entries(result)) {
        if (['mtimeNs', 'ctimeNs'].includes(key)) {
            if (typeof item !== 'string' || !/^-?(?:0|[1-9]\d*)$/.test(item) || item.length > 25) fail('Invalid Linux file timestamp.');
        } else if (!uint(item)) fail('Invalid lossless Linux output identity.');
    }
    return Object.freeze(result);
}
function processExpectation(value, parent) {
    const result = fields(value, parent ? ['pid', 'ppid', 'startTimeTicks', 'executable', 'executableFile', 'argv']
        : ['pid', 'startTimeTicks', 'executable', 'executableFile', 'argv']);
    if (!pid(result.pid) || (parent && !pid(result.ppid)) || !uint(result.startTimeTicks) || !path(result.executable)) fail('Invalid Linux output process expectation.');
    return Object.freeze({ ...result, argv: argv(result.argv), executableFile: identity(result.executableFile, true) });
}
export function copyLinuxOutputExpected(value, currentUid) {
    const result = fields(value, ['runId', 'uid', 'app', 'worker', 'pty']);
    if (typeof result.runId !== 'string' || !/^[a-f0-9]{32}$/.test(result.runId) || !uid(result.uid) || result.uid !== currentUid) fail('Invalid current-user Linux output expectation.');
    const app = processExpectation(result.app, true), worker = processExpectation(result.worker, false);
    const pty = fields(result.pty, ['path', 'device']);
    if (typeof pty.path !== 'string' || !/^\/dev\/pts\/(?:0|[1-9]\d{0,6})$/.test(pty.path) || Number(pty.path.slice(9)) > 1048575) fail('Invalid proved Linux PTY path.');
    pty.device = identity(pty.device, false);
    if (pty.device.uid !== String(result.uid) || (BigInt(pty.device.mode) & 2n) !== 0n) fail('Linux PTY ownership/world-write policy mismatch.');
    for (const [process, role] of [[app, 'owner'], [worker, 'worker']]) {
        const marker = '--ntilde-terminfo-' + role + '=' + result.runId;
        if (process.argv.filter(arg => arg === marker).length !== 1) fail('Missing or duplicated Linux output run marker.');
    }
    if (app.pid === worker.pid) fail('Linux output process roles must differ.');
    return Object.freeze({ runId: result.runId, uid: result.uid, app, worker, pty: Object.freeze(pty) });
}
export function copyLinuxOutputTool(value) {
    const tool = fields(value, ['path', 'sha256']);
    if (!['/usr/bin/stty', '/bin/stty', '/usr/bin/gnustty'].includes(tool.path) || typeof tool.sha256 !== 'string' || !/^[a-f0-9]{64}$/.test(tool.sha256)) fail('Expected the explicitly pinned system GNU stty.');
    return Object.freeze(tool);
}
export function linuxOutputDeadline(timeoutMs, now = () => process.hrtime.bigint()) {
    if (!Number.isSafeInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > linuxOutputLimits.timeoutMs) fail('Invalid Linux output operation budget.');
    return now() + BigInt(timeoutMs) * 1000000n;
}
export function linuxOutputBudget(options, now) {
    const { deadlineNs } = fields(options, ['deadlineNs']);
    if (typeof deadlineNs !== 'bigint') fail('Expected an absolute Linux output deadline.');
    const initial = deadlineNs - now();
    if (initial <= 0n || initial > BigInt(linuxOutputLimits.timeoutMs) * 1000000n) fail('Invalid or expired Linux output deadline.');
    let operations = 0, utilities = 0;
    const check = () => { if (++operations > 512 || now() >= deadlineNs) fail('Linux output operation budget exhausted.'); };
    return {
        deadlineNs, now, check,
        query(action) { check(); const value = action(); check(); return value; },
        remainingMs() { check(); const value = Number((deadlineNs - now()) / 1000000n); if (value < 1) fail('Linux output deadline exhausted.'); return value; },
        utilityMs() {
            if (++utilities > linuxOutputLimits.utilityCalls) fail('Linux output utility budget exhausted.');
            const value = this.remainingMs() - linuxOutputLimits.closeReserveMs;
            if (value < 1) fail('Linux output utility has no reserved close budget.');
            return value;
        },
    };
}
export function parseLinuxOutputMode(value) {
    if (typeof value !== 'string' || Buffer.byteLength(value) > linuxOutputLimits.outputBytes
        || !/^[0-9a-f]+(?::[0-9a-f]+){3,127}\n$/.test(value)) fail('Invalid GNU stty saved mode.');
    return value.slice(0, -1);
}
export function parseLinuxOutputSize(value) {
    if (typeof value !== 'string' || !/^[1-9]\d{0,4} [1-9]\d{0,4}\n$/.test(value)) fail('Invalid GNU stty dimensions.');
    const [rows, cols] = value.trimEnd().split(' ').map(Number);
    if (rows > 65535 || cols > 65535) fail('GNU stty dimensions exceed kernel range.');
    return Object.freeze({ rows, cols });
}
export function parseLinuxOutputProcessing(value) {
    if (typeof value !== 'string' || Buffer.byteLength(value) > linuxOutputLimits.outputBytes || /[^\x09\x0a\x20-\x7e]/.test(value)) fail('Invalid GNU stty output description.');
    const matches = [...value.matchAll(/(?:^|[\s;])(-?opost)(?=$|[\s;])/g)];
    if (matches.length !== 1) fail('Missing or ambiguous GNU stty output processing.');
    return matches[0][1] === 'opost';
}
export function parseLinuxSttyVersion(value) {
    if (typeof value !== 'string' || Buffer.byteLength(value) > linuxOutputLimits.outputBytes
        || !/^stty \(GNU coreutils\) [0-9]+(?:\.[0-9]+){1,2}\n/.test(value)) fail('Expected the pinned GNU stty utility.');
    return value.split('\n')[0];
}
function assertProcess(actual, expected, expectedUid) {
    if (!actual || actual.platform !== 'linux' || actual.pid !== expected.pid || actual.startTimeTicks !== expected.startTimeTicks
        || actual.executable !== expected.executable || !isDeepStrictEqual(actual.argv, expected.argv)
        || !Array.isArray(actual.uids) || actual.uids.length !== 4 || actual.uids.some(value => value !== expectedUid)
        || !isDeepStrictEqual(actual.executableFile, expected.executableFile)) fail('Linux output launch identity no longer matches.');
}
function assertFacts(facts, expected) {
    if (!facts || facts.qualification !== 'provider-foundation-only') fail('Invalid Linux output provider facts.');
    assertProcess(facts.parent, expected.app, expected.uid); assertProcess(facts.child, expected.worker, expected.uid);
    if (facts.parent.ppid !== expected.app.ppid || facts.child.ppid !== expected.app.pid
        || facts.child.pty?.selectedFd !== 1 || facts.child.pty.descriptors?.length !== 2) fail('Linux output direct ancestry or descriptor selection changed.');
    for (let fd = 0; fd < 2; fd++) {
        const descriptor = facts.child.pty.descriptors[fd];
        if (descriptor.fd !== fd || descriptor.path !== expected.pty.path || !isDeepStrictEqual(descriptor.device, expected.pty.device)) fail('Linux output PTY descriptor identity changed.');
    }
}
function detachedFacts(value) {
    let nodes = 0;
    const copy = (item, depth) => {
        if (++nodes > 4096 || depth > 8) fail('Oversized Linux output facts.');
        if (item === null || typeof item === 'boolean' || typeof item === 'number') return item;
        if (typeof item === 'string' && item.length <= 32768) return item;
        if (!item || typeof item !== 'object') fail('Unsupported Linux output facts.');
        const array = Array.isArray(item), prototype = Object.getPrototypeOf(item);
        if (array ? prototype !== Array.prototype : ![Object.prototype, null].includes(prototype)) fail('Non-plain Linux output facts.');
        if (array && item.length > 128) fail('Oversized Linux fact array.');
        const result = array ? [] : {};
        for (const [key, descriptor] of Object.entries(Object.getOwnPropertyDescriptors(item))) {
            if (array && key === 'length') continue;
            if (!Object.hasOwn(descriptor, 'value') || !descriptor.enumerable) fail('Accessor in Linux output facts.');
            Object.defineProperty(result, key, { value: copy(descriptor.value, depth + 1), enumerable: true });
        }
        if (Reflect.ownKeys(item).some(key => typeof key !== 'string')) fail('Symbol in Linux output facts.');
        return Object.freeze(result);
    };
    return copy(value, 0);
}
function settledError(failures, message, resources) {
    const error = new AggregateError(failures, message);
    Object.defineProperty(error, 'linuxOutputResources', { value: Object.freeze(resources), configurable: true });
    return error;
}

/** Internal deterministic backend seam. No production proc-root, platform, utility or TTY override. */
export async function prepareLinuxOutputWithIo(io, expectedInput, toolInput, options) {
    const expected = copyLinuxOutputExpected(expectedInput, io.currentUid()), tool = copyLinuxOutputTool(toolInput);
    const budget = linuxOutputBudget(options, io.now);
    let heldTool, heldPty, original, latest, version, facts, mayHaveMutated = false, utilitySafe = true;
    let busy = false, primary, settled, closedTool = false, closedPty = false;
    const resources = () => ({ platform: 'linux', qualification: 'owned-output-foundation-only', runId: expected.runId,
        expected, tool: heldTool?.observation ?? tool, pty: heldPty?.observation ?? expected.pty,
        original, latest, version, acceptedFacts: facts, mutationMayHaveOccurred: mayHaveMutated,
        utilityCloseConfirmed: utilitySafe, toolCloseConfirmed: closedTool, ptyCloseConfirmed: closedPty, aborted: !!primary });
    const mark = error => { primary ??= error; return error; };
    const verify = phase => {
        const current = io.facts(expected, phase.remainingMs()); phase.check(); assertFacts(current, expected);
        facts = detachedFacts(current); // Never retain mismatched process argv as accepted evidence.
    };
    const held = phase => { phase.query(() => io.checkTool(heldTool)); if (heldPty) phase.query(() => io.checkPty(heldPty)); };
    const run = async (kind, saved, phase, ownership = true) => {
        if (!utilitySafe) fail('Prior Linux utility closure is unconfirmed.');
        if (ownership) verify(phase);
        held(phase);
        try {
            const result = await io.run(heldTool, heldPty, kind, saved, phase);
            phase.check(); held(phase); if (ownership) verify(phase);
            return result;
        } catch (error) { if (error.utilityCloseConfirmed === false) utilitySafe = false; throw error; }
    };
    const inspect = async (phase, ownership = true) => {
        const mode = parseLinuxOutputMode(await run('mode', null, phase, ownership));
        const size = parseLinuxOutputSize(await run('size', null, phase, ownership));
        const description = await run('description', null, phase, ownership);
        return Object.freeze({ mode, ...size, outputProcessing: parseLinuxOutputProcessing(description), description });
    };
    const restoreOriginal = async phase => {
        if (!original || !mayHaveMutated) return;
        await run('restore', original.mode, phase, false);
        latest = await inspect(phase, false);
        if (latest.mode !== original.mode) fail('Linux output saved mode was not restored exactly.');
        if (latest.rows !== original.rows || latest.cols !== original.cols) fail('Linux output original dimensions were not restored by the resize owner.');
    };
    const close = () => {
        const errors = [];
        if (heldPty) { try { io.close(heldPty); closedPty = true; } catch (error) { errors.push(error); } }
        if (heldTool) { try { io.close(heldTool); closedTool = true; } catch (error) { errors.push(error); } }
        return errors;
    };
    try {
        verify(budget);
        heldTool = io.openTool(tool, budget);
        version = parseLinuxSttyVersion(await run('version', null, budget));
        heldPty = io.openPty(expected.pty, budget);
        verify(budget);
        original = await inspect(budget);
        mayHaveMutated = true; // A failed tool can have applied the change before its failure.
        await run('disable', null, budget);
        latest = await inspect(budget);
        if (latest.outputProcessing || latest.rows !== original.rows || latest.cols !== original.cols) fail('Linux output preparation did not preserve dimensions and disable translation.');
    } catch (error) {
        const errors = [mark(error)];
        try { await restoreOriginal(budget); } catch (restoreError) { errors.push(restoreError); }
        errors.push(...close());
        throw settledError(errors, 'Linux output preparation/restoration/close failed.', resources());
    }
    return Object.freeze({
        observation: () => Object.freeze(resources()),
        async observe(phaseOptions) {
            if (settled) fail('Linux output lease is already settled.');
            if (busy) throw mark(new Error('Overlapping Linux output operation.'));
            if (primary) throw primary;
            busy = true;
            try {
                const phase = linuxOutputBudget(phaseOptions, io.now);
                latest = await inspect(phase);
                if (latest.outputProcessing) fail('Linux output translation is no longer disabled.');
                if (primary) throw primary; // An overlapping refused call may have aborted this lease while awaiting the utility.
                return Object.freeze(resources());
            } catch (error) { throw mark(error); }
            finally { busy = false; }
        },
        async restore(phaseOptions) {
            if (settled) { if (settled.error) throw settled.error; return settled.result; }
            if (busy) throw mark(new Error('Overlapping Linux output operation.'));
            busy = true;
            const errors = [];
            try {
                const phase = linuxOutputBudget(phaseOptions, io.now);
                try { verify(phase); } catch (error) { mark(error); }
                await restoreOriginal(phase);
                if (!primary) verify(phase);
            } catch (error) { errors.push(error); }
            finally {
                errors.push(...close()); busy = false;
            }
            if (!primary && errors.length) primary = errors.shift();
            if (primary) errors.unshift(primary);
            if (errors.length) {
                const error = settledError(errors, 'Linux output restoration/identity/close failed.', resources());
                settled = { error }; throw error;
            }
            const result = Object.freeze({ ...resources(), restored: true });
            settled = { result }; return result;
        },
    });
}
