// Internal deterministic seam. Production has a fixed Linux provider, helper and socket names.
import { isDeepStrictEqual } from 'node:util';
import { linuxOutputBudget, linuxOutputDeadline } from './linux-owned-output-core.mjs';

export const linuxEndpointLimits = Object.freeze({ bytes: 4096, helperBytes: 65536, interpreterBytes: 16 * 1024 * 1024,
    pathBytes: 103, closeReserveMs: 250 });
export const linuxEndpointNames = Object.freeze({ socket: 'agent.sock', descriptor: 'agent-endpoint.json' });
const fail = message => { throw new Error(message); };
const uint64 = value => typeof value === 'string' && /^(?:0|[1-9]\d*)$/.test(value)
    && value.length <= 20 && BigInt(value) <= 18446744073709551615n;
const uid = value => Number.isSafeInteger(value) && value >= 0 && value <= 4294967294;
const pid = value => Number.isSafeInteger(value) && value >= 2 && value <= 2147483647;
const absolute = value => typeof value === 'string' && value.startsWith('/') && value.length < 4096
    && !/[\x00-\x1f\x7f]/.test(value) && value.split('/').slice(1).every(part => part && part !== '.' && part !== '..');

export function endpointFields(value, keys) {
    if (!value || typeof value !== 'object' || ![Object.prototype, null].includes(Object.getPrototypeOf(value))) fail('Expected plain endpoint data.');
    const descriptors = Object.getOwnPropertyDescriptors(value), names = Reflect.ownKeys(descriptors);
    if (names.length !== keys.length || names.some(key => typeof key !== 'string' || !keys.includes(key)
        || !Object.hasOwn(descriptors[key], 'value') || !descriptors[key].enumerable)) fail('Unexpected endpoint fields or accessor.');
    return Object.fromEntries(keys.map(key => [key, descriptors[key].value]));
}
function argv(value) {
    if (!Array.isArray(value) || Object.getPrototypeOf(value) !== Array.prototype || value.length < 1 || value.length > 128) fail('Invalid endpoint argv.');
    const descriptors = Object.getOwnPropertyDescriptors(value);
    if (Reflect.ownKeys(descriptors).length !== value.length + 1) fail('Unexpected endpoint argv properties.');
    const result = [];
    for (let index = 0; index < value.length; index++) {
        const entry = descriptors[index];
        if (!entry || !Object.hasOwn(entry, 'value') || typeof entry.value !== 'string' || entry.value.length > 8192
            || entry.value.includes('\0')) fail('Invalid endpoint argv entry.');
        result.push(entry.value);
    }
    return Object.freeze(result);
}
export function endpointIdentity(value, regular = false) {
    const fields = endpointFields(value, regular ? ['dev', 'ino', 'rdev', 'uid', 'mode', 'size', 'mtimeNs', 'ctimeNs'] : ['dev', 'ino', 'uid', 'mode']);
    for (const [key, item] of Object.entries(fields)) {
        if (['mtimeNs', 'ctimeNs'].includes(key)) {
            if (typeof item !== 'string' || !/^-?(?:0|[1-9]\d*)$/.test(item) || item.length > 25) fail('Invalid endpoint file timestamp.');
        } else if (!uint64(item)) fail('Invalid lossless endpoint identity.');
    }
    return Object.freeze(fields);
}
function copyApp(value, runId) {
    const app = endpointFields(value, ['pid', 'ppid', 'startTimeTicks', 'executable', 'executableFile', 'argv']);
    if (!pid(app.pid) || !pid(app.ppid) || !uint64(app.startTimeTicks) || !absolute(app.executable)) fail('Invalid endpoint App launch expectation.');
    app.argv = argv(app.argv); app.executableFile = endpointIdentity(app.executableFile, true);
    if (app.argv.filter(arg => arg === '--ntilde-terminfo-owner=' + runId).length !== 1) fail('Missing or duplicated endpoint App marker.');
    return Object.freeze(app);
}
function copyDirectory(value, currentUid) {
    const directory = endpointFields(value, ['path', 'device']);
    if (!absolute(directory.path) || !directory.path.endsWith('/appdata')
        || Buffer.byteLength(directory.path + '/' + linuxEndpointNames.socket) > linuxEndpointLimits.pathBytes) fail('Invalid private endpoint directory path.');
    directory.device = endpointIdentity(directory.device);
    if (directory.device.uid !== String(currentUid) || (BigInt(directory.device.mode) & 0o177777n) !== 0o40700n) fail('Expected an owned0700 endpoint directory.');
    return Object.freeze(directory);
}
function copyPython(value) {
    const python = endpointFields(value, ['path', 'sha256']);
    if (python.path !== '/usr/bin/python3' || typeof python.sha256 !== 'string' || !/^[a-f\d]{64}$/.test(python.sha256)) fail('Expected the explicitly pinned system Python3.');
    return Object.freeze(python);
}
export function copyEndpointExpected(value, currentUid) {
    const result = endpointFields(value, ['runId', 'uid', 'appDataDirectory', 'app', 'python']);
    if (typeof result.runId !== 'string' || !/^[a-f\d]{32}$/.test(result.runId) || !uid(result.uid) || result.uid !== currentUid) fail('Invalid current-user endpoint expectation.');
    return Object.freeze({ runId: result.runId, uid: result.uid, app: copyApp(result.app, result.runId),
        appDataDirectory: copyDirectory(result.appDataDirectory, result.uid), python: copyPython(result.python) });
}
export function assertEndpointApp(actual, expected) {
    const app = expected.app;
    if (actual?.platform !== 'linux' || actual.pid !== app.pid || actual.ppid !== app.ppid || actual.startTimeTicks !== app.startTimeTicks
        || actual.executable !== app.executable || !isDeepStrictEqual(actual.executableFile, app.executableFile)
        || !isDeepStrictEqual(actual.argv, app.argv) || !Array.isArray(actual.uids) || actual.uids.length !== 4
        || actual.uids.some(item => item !== expected.uid)) fail('Endpoint App launch identity changed.');
}
function credentials(value) {
    const result = endpointFields(value, ['pid', 'uid', 'gid']);
    if (!pid(result.pid) || !uid(result.uid) || !uid(result.gid)) fail('Invalid endpoint kernel credentials.');
    return Object.freeze(result);
}
function failureCodes(value) {
    if (!Array.isArray(value) || value.length > 8) fail('Invalid endpoint failure list.');
    return Object.freeze(value.map(item => {
        const record = endpointFields(item, ['code', 'errno']);
        if (!['input', 'directory', 'descriptor', 'socket', 'credentials', 'deadline', 'close', 'internal'].includes(record.code)
            || (record.errno !== null && (!Number.isInteger(record.errno) || record.errno < 1 || record.errno > 4095))) fail('Invalid endpoint failure code.');
        return Object.freeze(record);
    }));
}
function copyDescriptor(value, expected) {
    if (value === null) return null;
    const descriptor = endpointFields(value, ['file', 'version', 'pid', 'endpoint']);
    descriptor.file = endpointIdentity(descriptor.file, true);
    if (descriptor.version !== 1 || descriptor.pid !== expected.app.pid
        || descriptor.endpoint !== expected.appDataDirectory.path + '/' + linuxEndpointNames.socket
        || descriptor.file.uid !== String(expected.uid) || (BigInt(descriptor.file.mode) & 0o170000n) !== 0o100000n
        || BigInt(descriptor.file.size) > BigInt(linuxEndpointLimits.bytes)) fail('Endpoint descriptor identity mismatch.');
    return Object.freeze(descriptor);
}
function copySocket(value, expected) {
    if (value === null) return null;
    const socket = endpointFields(value, ['named', 'kernel', 'peer', 'closed']);
    socket.named = endpointIdentity(socket.named); socket.kernel = endpointIdentity(socket.kernel); socket.peer = credentials(socket.peer);
    if (typeof socket.closed !== 'boolean' || (BigInt(socket.kernel.mode) & 0o170000n) !== 0o140000n
        || socket.kernel.uid !== String(expected.uid)) fail('Invalid endpoint socket closure or kernel identity fact.');
    return Object.freeze(socket);
}
function requireSuccessfulProbe(result, expected) {
    if (result.failures.length || !result.inheritedClosed || !isDeepStrictEqual(result.directory, expected.appDataDirectory.device)
        || !result.descriptor || !result.socket?.closed || result.socket.peer.pid !== expected.app.pid
        || result.socket.peer.uid !== expected.uid || result.socket.named.uid !== String(expected.uid)
        || (BigInt(result.socket.named.mode) & 0o177777n) !== 0o140600n) fail('Endpoint probe success is not fully verified.');
}
/** Data originates in a bounded decoded helper frame, never in application response bytes. */
export function copyEndpointProbe(value, expected) {
    const result = endpointFields(value, ['schemaVersion', 'runId', 'ok', 'failures', 'directory', 'descriptor', 'socket', 'inheritedClosed', 'pythonVersion']);
    if (result.schemaVersion !== 1 || result.runId !== expected.runId || typeof result.ok !== 'boolean'
        || typeof result.inheritedClosed !== 'boolean' || !Array.isArray(result.pythonVersion) || result.pythonVersion.length !== 3
        || result.pythonVersion.some(item => !Number.isSafeInteger(item) || item < 0 || item > 999)
        || result.pythonVersion[0] !== 3 || result.pythonVersion[1] < 9) fail('Invalid endpoint helper envelope.');
    result.pythonVersion = Object.freeze([...result.pythonVersion]); result.failures = failureCodes(result.failures);
    if (result.directory !== null) result.directory = endpointIdentity(result.directory);
    result.descriptor = copyDescriptor(result.descriptor, expected); result.socket = copySocket(result.socket, expected);
    if (result.ok) requireSuccessfulProbe(result, expected);
    else if (result.failures.length === 0) fail('Failed endpoint probe lacks a failure code.');
    return Object.freeze(result);
}

export const createEndpointDeadline = linuxOutputDeadline;
export async function observeEndpointWithIo(io, input, options) {
    const expected = copyEndpointExpected(input, io.currentUid()), budget = linuxOutputBudget(options, io.now);
    const held = [], errors = []; let probe, helperClosed = false, appObserved = false;
    const verifyApp = () => { assertEndpointApp(io.facts(expected.app.pid, budget.remainingMs()), expected); budget.check(); appObserved = true; };
    try {
        verifyApp();
        for (const kind of ['directory', 'python', 'helper']) held.push(io.open(kind, expected, budget));
        io.check(held, expected, budget); verifyApp();
        const response = await io.probe(held, expected, budget);
        helperClosed = true;
        budget.check(); io.check(held, expected, budget); verifyApp();
        probe = copyEndpointProbe(response, expected);
        if (!probe.ok) fail('Private endpoint probe refused.');
    } catch (error) {
        if (error.helperCloseConfirmed === true) helperClosed = true;
        errors.push(error);
    }
    const closed = [];
    for (const resource of [...held].reverse()) {
        try { io.close(resource); closed.push(resource.kind); }
        catch (error) { errors.push(error); }
    }
    const observation = Object.freeze({ schemaVersion: 1, platform: 'linux', runId: expected.runId,
        qualification: 'private-endpoint-foundation-only', connectionScope: 'probe-only',
        authenticatesSubsequentConnections: false, terminalAppQualified: false, desktopQualified: false,
        linuxOwnedCollectionQualified: false,
        expected, acceptedAppFacts: appObserved ? expected.app : null, probe,
        resources: Object.freeze(held.map(item => item.observation)), closed: Object.freeze(closed), helperClosed });
    if (errors.length) {
        const error = new AggregateError(errors, 'Linux private endpoint observation/close failed.');
        Object.defineProperty(error, 'linuxEndpointResources', { value: observation }); throw error;
    }
    return observation;
}
