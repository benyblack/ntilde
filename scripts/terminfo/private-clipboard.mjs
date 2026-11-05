// Pure rederivation of source-known App-private storage facts. No OS clipboard access.
import { createHash } from 'node:crypto';
import { snapshotHash } from './agent-observer.mjs';
import { queryBinding, queryScreen, validateQueryOwnership } from './queries.mjs';
import { PRIVATE_CLIPBOARD_PROBES } from './private-clipboard-controller.mjs';
import { privateClipboardNonce, validatePrivateClipboardRequest, validatePrivateClipboardResult } from './private-clipboard-request.mjs';
import { ownedPrivateClipboardPolicy, ownedPrivateClipboardSuiteId } from './private-clipboard-worker.mjs';

export const checkPrivateClipboard = value => { if (!value) throw new Error('Invalid or incomplete owned private clipboard evidence.'); };
const check = checkPrivateClipboard;
const hash = value => createHash('sha256').update(value).digest('hex');
export const emptyPrivateClipboardReply = '\x1b]52;c;\x1b\\';
const baseBindingKeys = ['runId', 'ownerPid', 'workerPid', 'uid', 'appExecutable', 'workerExecutable', 'tty', 'profileName', 'paneId'];

/** Copy only bounded plain data; accessors, prototypes and unbounded strings cannot enter a retained journal. */
export function privateClipboardData(input) {
    let nodes = 0, bytes = 0;
    /** Copy a dense/plain subtree within the shared depth 32, node 250k and 16 MiB budgets; reject accessors. */
    const copy = (value, depth) => {
        check(++nodes <= 250000 && depth <= 32);
        if (value === null || typeof value === 'boolean') return value;
        if (typeof value === 'number') { check(Number.isFinite(value)); return value; }
        if (typeof value === 'string') { bytes += Buffer.byteLength(value); check(bytes <= 16 * 1024 * 1024); return value; }
        check(value && typeof value === 'object');
        const array = Array.isArray(value), prototype = Object.getPrototypeOf(value);
        check(array ? prototype === Array.prototype : prototype === Object.prototype || prototype === null);
        const descriptors = Object.getOwnPropertyDescriptors(value), keys = Reflect.ownKeys(descriptors);
        if (array) {
            const length = descriptors.length.value;
            check(Number.isSafeInteger(length) && length <= 32768 && keys.length === length + 1);
            return Array.from({ length }, (_, index) => {
                const field = descriptors[index]; check(field && Object.hasOwn(field, 'value') && field.enumerable);
                return copy(field.value, depth + 1);
            });
        }
        const output = {};
        for (const key of keys) {
            check(typeof key === 'string' && key.length <= 128 && key !== '__proto__');
            const field = descriptors[key]; check(Object.hasOwn(field, 'value') && field.enumerable);
            output[key] = copy(field.value, depth + 1);
        }
        return output;
    };
    return copy(input, 0);
}
/** Require the complete own-key shape after plain-data admission; unknown fields never disappear. */
function exact(value, keys) {
    check(value && !Array.isArray(value) && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key)));
}
/** Compare admitted plain data structurally without mistaking JSON object-key order for a value change. */
function equalData(actual, expected) {
    if (actual === null || typeof actual !== 'object' || expected === null || typeof expected !== 'object') return actual === expected;
    if (Array.isArray(actual) !== Array.isArray(expected)) return false;
    const keys = Object.keys(expected);
    return Object.keys(actual).length === keys.length && keys.every(key => Object.hasOwn(actual, key) && equalData(actual[key], expected[key]));
}
/** Copy exactly 15 static run/process/PTY/source facts. Birth strings corroborate identity; they grant no lease. */
export function privateClipboardBinding(input) {
    const b = privateClipboardData(input);
    exact(b, [...baseBindingKeys, 'sourceSnapshotManifestSha256', 'executableSha256', 'assemblySha256', 'appBirth', 'workerBirth', 'outputDevice']);
    const process = queryBinding(Object.fromEntries(baseBindingKeys.map(key => [key, b[key]])));
    for (const key of ['sourceSnapshotManifestSha256', 'executableSha256']) check(/^[a-f\d]{64}$/.test(b[key]));
    check(b.assemblySha256 === null || /^[a-f\d]{64}$/.test(b.assemblySha256));
    for (const key of ['appBirth', 'workerBirth']) check(typeof b[key] === 'string' && b[key].length <= 128 && /^[A-Za-z]{3} [A-Za-z]{3} +\d{1,2} \d{2}:\d{2}:\d{2} \d{4}$/.test(b[key]));
    exact(b.outputDevice, ['dev', 'rdev', 'ino']);
    check(Object.values(b.outputDevice).every(value => typeof value === 'string' && /^(?:0|[1-9]\d{0,31})$/.test(value)));
    return Object.freeze({ ...process, sourceSnapshotManifestSha256: b.sourceSnapshotManifestSha256, executableSha256: b.executableSha256,
        assemblySha256: b.assemblySha256, appBirth: b.appBirth, workerBirth: b.workerBirth, outputDevice: b.outputDevice });
}
/** Validate fresh parent-read lineage, markers, hashes, device and untranslated geometry; perform no OS reads. */
export function validatePrivateClipboardOwnership(input, binding, screen) {
    exact(input, ['app', 'worker', 'tty', 'at', 'appCommand', 'workerCommand', 'terminalOutput', 'executableSha256', 'assemblySha256', 'sourceSnapshotManifestSha256', 'outputDevice']);
    const fact = validateQueryOwnership(input, binding, screen), words = fact.appCommand.split(/\s+/);
    check(equalData(fact.outputDevice, binding.outputDevice));
    for (const key of ['sourceSnapshotManifestSha256', 'executableSha256', 'assemblySha256']) check(fact[key] === binding[key]);
    for (const side of ['app', 'worker']) {
        exact(fact[side], ['pid', 'ppid', 'uid', 'tty', 'executable', 'birth']);
        check(fact[side].birth === binding[side + 'Birth']);
    }
    exact(fact.terminalOutput, ['tool', 'outputProcessing', 'description']);
    check(fact.workerCommand.split(/\s+/).filter(word => word.startsWith('--ntilde-terminfo-worker=')).length === 1);
    check(words.filter(word => word.startsWith('--ntilde-terminfo-private-clipboard')).length === 1
        && words.includes('--ntilde-terminfo-private-clipboard=' + binding.runId));
    return fact;
}

/** Expected-only result fields are checked before copying worker claims into the parent journal. */
export function privateClipboardCallbackResult(input, probeId, events) {
    check(PRIVATE_CLIPBOARD_PROBES.includes(probeId));
    const observed = privateClipboardData(input);
    let expected;
    if (probeId === PRIVATE_CLIPBOARD_PROBES[1]) {
        const nonce = events.find(event => event.probeId === probeId && event.request.operation === 'armWrite')?.request.value;
        check(privateClipboardNonce(nonce));
        expected = { pass: true, observation: { outcome: 'supported', evidence: 'behavior' },
            assertions: [{ kind: 'positive', expected: 'Independent clipboard text equals OSC 52 write nonce', observed: nonce }] };
    } else expected = { pass: false, response: emptyPrivateClipboardReply,
        observation: { outcome: 'inconclusive', reason: 'insufficient-evidence', evidence: 'query' } };
    check(equalData(observed, expected));
    return observed;
}

const operations = [
    ['begin', 0], ['restore', 0], ['read', 0], ['read', 0], ['armWrite', 0], ['read', 0], ['seed', 0], ['read', 0], ['restore', 0], ['read', 0],
    ['read', 1], ['armWrite', 1], ['read', 1], ['restore', 1], ['read', 1],
    ['read', 2], ['seed', 2], ['read', 2], ['restore', 2], ['read', 2], ['read', null], ['close', null],
];
/** Replay only fixed seed/fresh nonce transitions from the journal; this never writes a clipboard. */
function advancePrivateExpectedState(state, request, runId) {
    const operation = request.operation;
    if (operation === 'armWrite' || operation === 'seed') {
        check(!state.nonces.has(request.value)); state.nonces.add(request.value);
    }
    if (operation === 'armWrite') state.pending = request.value;
    if (operation === 'seed') { state.current = request.value; state.restored = false; }
    if (operation === 'read' && state.pending) { state.current = state.pending; state.pending = undefined; state.count++; state.restored = false; }
    if (operation === 'restore') { state.current = 'terminfo-private-seed-' + runId; state.pending = undefined; state.restored = true; }
}
/** Require the exact 22-operation trajectory, four distinct nonces, two accepted writes and restored final close. */
function validateController(controller, binding) {
    exact(controller, ['profile', 'boundary', 'runId', 'paneId', 'fixtureId', 'fatal', 'complete', 'completedProbes', 'events', 'bridgeFailed', 'bridgeFinished', 'qualification']);
    check(controller.profile === 'ntilde-private-app-v1' && controller.qualification === 'caller-verifies-binding'
        && controller.boundary === 'App-private storage only; no native interchange or child-delivery attestation');
    check(controller.runId === binding.runId && controller.paneId === binding.paneId && controller.complete === true
        && controller.fatal === false && controller.bridgeFailed === false && controller.bridgeFinished === true
        && equalData(controller.completedProbes, [...PRIVATE_CLIPBOARD_PROBES]) && controller.events?.length === operations.length);
    const state = { current: 'terminfo-private-seed-' + binding.runId, count: 0, restored: false, pending: undefined, nonces: new Set() };
    for (const [index, [operation, probe]] of operations.entries()) {
        const event = controller.events[index], request = event.request;
        exact(event, ['index', 'probeId', 'request', 'rpcStarted', 'postVerified', 'result']);
        check(event.index === index && event.probeId === (probe === null ? null : PRIVATE_CLIPBOARD_PROBES[probe])
            && event.rpcStarted === true && event.postVerified === true && request.operation === operation
            && request.runId === binding.runId && request.paneId === binding.paneId);
        validatePrivateClipboardRequest(request);
        if (operation !== 'begin') check(request.fixtureId === controller.fixtureId);
        advancePrivateExpectedState(state, request, binding.runId);
        const result = validatePrivateClipboardResult(event.result, request);
        check(result.fixtureId === controller.fixtureId && result.state === (operation === 'close' ? 'closed' : 'active')
            && result.complete === (operation === 'close') && result.acceptedWrites === state.count
            && result.restored === state.restored && result.valueSha256 === hash(state.current));
        if (operation === 'read') check(result.value === state.current);
    }
    check(state.count === 2 && state.nonces.size === 4 && state.restored && !state.pending);
}
/** Check complete exposed state/hash plus chronological paired ownership against the original processes. */
function validateSample(sample, binding, original, clock) {
    exact(sample, ['beforeOwnership', 'screen', 'sha256', 'afterOwnership']);
    const screen = queryScreen(sample.screen); check(equalData(sample.screen, screen) && sample.sha256 === snapshotHash(screen));
    for (const fact of [sample.beforeOwnership, sample.afterOwnership]) {
        validatePrivateClipboardOwnership(fact, binding, screen);
        const at = Date.parse(fact.at); check(at >= clock.at); clock.at = at;
        for (const side of ['app', 'worker']) check(equalData(fact[side], original[side]));
    }
    return screen;
}
/** Derive the scoped report only after all 44 state captures, original callback results and final close agree. */
export function buildPrivateClipboardReport(input) {
    const data = privateClipboardData(input); exact(data, ['binding', 'controller', 'frames', 'callbackResults']);
    const { binding: supplied, controller, frames, callbackResults } = data, binding = privateClipboardBinding(supplied);
    validateController(controller, binding);
    check(frames?.length === 22 && callbackResults?.length === 3);
    const clock = { at: -Infinity }, baseline = frames[0].before.screen, original = frames[0].before.beforeOwnership;
    for (const [index, frame] of frames.entries()) {
        exact(frame, ['index', 'before', 'after']); check(frame.index === index);
        const before = validateSample(frame.before, binding, original, clock), after = validateSample(frame.after, binding, original, clock);
        check(equalData(before, baseline) && equalData(after, baseline)
            && Date.parse(frame.after.afterOwnership.at) - Date.parse(frame.before.beforeOwnership.at) <= 4000);
    }
    const observations = PRIVATE_CLIPBOARD_PROBES.map((probeId, index) => {
        const callback = callbackResults[index]; exact(callback, ['probeId', 'result']); check(callback.probeId === probeId);
        const result = privateClipboardCallbackResult(callback.result, probeId, controller.events);
        return { probeId, originalPass: result.pass, observedOutcome: result.observation.outcome,
            privateStorageRestored: true, acceptedTerminalWrites: index === 2 ? 0 : 1,
            emptyOsc52ReadDenial: index !== 1, nativeInterchangeQualified: false };
    });
    return { schemaVersion: 1, suiteId: ownedPrivateClipboardSuiteId, policy: ownedPrivateClipboardPolicy, runId: binding.runId, binding,
        scope: 'App-private synthetic storage only. Original callback results remain provisional until final raw-trace verification; no OS clipboard interchange or read permission. Exposed state and point-in-time ownership only; ps lstart UTC second-resolution birth is corroboration, not a held PID identity/lease. Reverse-video and hidden session/parser state are unobserved.',
        unobservedState: ['reverseVideo'], observations, controller, frames, callbackResults };
}

const privateFailureReasons = new Set([
    'Private clipboard response timed out; do not retry.', 'Private clipboard transport failed; do not retry.',
    'Invalid private clipboard response; do not retry.', 'Private clipboard state/hash/count mismatch.',
    'Missing private clipboard readback.', 'Private clipboard binding changed.',
    'Private clipboard event deadline exhausted.', 'Private clipboard lifecycle cannot resume.',
]);
/** Hash at most 8192 code units, preserve length/truncation, and expose plaintext only for closed generic reasons. */
function failureText(value) {
    const prefix = value.slice(0, 8192);
    const fact = { messageSha256: hash(prefix), messageCodeUnits: value.length, messageTruncated: value.length > 8192 };
    if (value.length <= 256 && privateFailureReasons.has(value)) fact.reason = value;
    return fact;
}
/** Inspect an own data descriptor only; an exception getter is never invoked. */
function ownFailureValue(value, key) {
    const descriptor = Object.getOwnPropertyDescriptor(value, key);
    if (descriptor && Object.hasOwn(descriptor, 'value')) return { present: true, value: descriptor.value };
    return { present: false };
}
/** Retain at most eight indexed reasons; the supplied inspector enforces the shared depth/node budget. */
function failureChildren(values, inspect, depth) {
    const length = ownFailureValue(values, 'length').value, errors = [];
    for (let index = 0; index < Math.min(length, 8); index++) {
        const child = ownFailureValue(values, String(index));
        errors.push(child.present ? inspect(child.value, depth + 1) : { kind: 'unavailable' });
    }
    return { errors, errorsTruncated: length > 8 };
}
/** Privacy-safe failure correlation only. Never call arbitrary getters or stringify opaque values. */
export function privateClipboardFailureFacts(error) {
    let nodes = 0;
    /** Preserve rejection presence and bounded error/cause facts without coercing opaque objects. */
    const inspect = (value, depth) => {
        if (++nodes > 32 || depth > 6) return { kind: 'limit' };
        if (typeof value === 'string') return { kind: 'rejection', type: 'string', ...failureText(value) };
        if (value === null) return { kind: 'rejection', type: 'null' };
        if (typeof value !== 'object') return { kind: 'rejection', type: typeof value };
        const message = ownFailureValue(value, 'message'), children = ownFailureValue(value, 'errors');
        const fact = { kind: 'error' };
        if (message.present && typeof message.value === 'string') Object.assign(fact, failureText(message.value));
        else fact.messageUnavailable = true;
        if (children.present && Array.isArray(children.value)) {
            fact.kind = 'aggregate';
            Object.assign(fact, failureChildren(children.value, inspect, depth));
        }
        const cause = ownFailureValue(value, 'cause');
        if (cause.present) fact.cause = inspect(cause.value, depth + 1);
        return fact;
    };
    try { return inspect(error, 0); }
    catch { return { kind: 'opaque', inspectionFailed: true }; }
}
/** Record original and distinct abort outcomes even if cleanup rejects. No outcome is replaced by formatting. */
export async function abortPrivateClipboardWithFacts(state, error, phase, budget) {
    const failure = { phase, primary: privateClipboardFailureFacts(error), abort: 'settled' };
    try { await state.observer.abort(error, budget); }
    catch (error_) {
        failure.abort = 'rejected';
        if (error_ !== error) failure.cleanup = privateClipboardFailureFacts(error_);
        else failure.samePrimary = true;
    }
    state.failures ??= [];
    if (state.failures.length < 8) state.failures.push(failure);
    else state.failureFactsTruncated = true;
}
