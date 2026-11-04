import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { copyEndpointExpected, copyEndpointProbe, createEndpointDeadline, observeEndpointWithIo } from './linux-owned-endpoint-core.mjs';
import { createEndpointResources, createEndpointProbeRunner, linuxEndpointHelperSha256 } from './linux-owned-endpoint-platform.mjs';
import { observeLinuxPrivateAgentEndpoint } from './linux-owned-endpoint.mjs';
import { linuxOutputBudget } from './linux-owned-output-core.mjs';

const runId = 'a'.repeat(32), appData = '/tmp/owned-endpoint/appdata';
const file = (uid = 0) => ({ dev: '3', ino: '9', rdev: '0', uid: String(uid), mode: String(0o100755), size: '8', mtimeNs: '1', ctimeNs: '2' });
const directory = () => ({ dev: '4', ino: '10', uid: '1000', mode: String(0o40700) });
const input = () => ({ runId, uid: 1000, appDataDirectory: { path: appData, device: directory() },
    app: { pid: 40, ppid: 38, startTimeTicks: '9007199254740993', executable: '/usr/bin/app', executableFile: file(), argv: ['/usr/bin/app', '--ntilde-terminfo-owner=' + runId] },
    python: { path: '/usr/bin/python3', sha256: 'b'.repeat(64) } });
const response = () => ({ schemaVersion: 1, runId, ok: true, failures: [], directory: directory(),
    descriptor: { file: { ...file(1000), mode: String(0o100644) }, version: 1, pid: 40, endpoint: appData + '/agent.sock' },
    socket: { named: { dev: '4', ino: '11', uid: '1000', mode: String(0o140600) },
        kernel: { dev: '9', ino: '100', uid: '1000', mode: String(0o140777) }, peer: { pid: 40, uid: 1000, gid: 1000 }, closed: true },
    inheritedClosed: true, pythonVersion: [3, 14, 4] });
function synthetic() {
    const expected = input();
    const s = { time: 0n, events: [], hook: null, reply: response(), fact: { platform: 'linux', ...structuredClone(expected.app), uids: [1000, 1000, 1000, 1000] } };
    const note = name => { s.events.push(name); s.hook?.(name); };
    s.io = { now: () => s.time, currentUid: () => 1000, facts() { note('facts'); return s.fact; },
        open(kind) { note('open:' + kind); return { kind, observation: Object.freeze({ kind }) }; },
        check() { note('check'); }, close(resource) { note('close:' + resource.kind); },
        async probe() { note('probe'); return s.reply; } };
    s.options = () => ({ deadlineNs: createEndpointDeadline(4000, s.io.now) });
    s.observe = (value = expected, options = s.options()) => observeEndpointWithIo(s.io, value, options);
    return s;
}

test('fixed helper source digest matches the actual checked-in bytes', () => {
    assert.equal(createHash('sha256').update(readFileSync(new URL('./linux-endpoint-probe.py', import.meta.url))).digest('hex'), linuxEndpointHelperSha256);
});

for (const [name, mutate] of [
    ['wrong UID', x => x.uid++], ['numeric imprecise birth', x => x.app.startTimeTicks = 9007199254740993],
    ['overflow birth', x => x.app.startTimeTicks = '18446744073709551616'], ['missing marker', x => x.app.argv.pop()],
    ['duplicate marker', x => x.app.argv.push(x.app.argv[1])], ['relative App', x => x.app.executable = 'app'],
    ['world-readable directory', x => x.appDataDirectory.device.mode = String(0o40755)],
    ['foreign directory', x => x.appDataDirectory.device.uid = '1001'], ['wrong directory name', x => x.appDataDirectory.path += '/other'],
    ['traversal', x => x.appDataDirectory.path = '/tmp/a/../appdata'], ['long path', x => x.appDataDirectory.path = '/tmp/' + 'x'.repeat(100) + '/appdata'],
    ['caller socket selector', x => x.socket = 'other.sock'], ['arbitrary Python', x => x.python.path = '/tmp/python'],
    ['bad Python hash', x => x.python.sha256 = 'bad'], ['symbol field', x => x[Symbol('x')] = 1],
]) test('refuse ' + name + ' before provider/files/connection effects', async () => {
    const s = synthetic(), expected = input(); mutate(expected); await assert.rejects(s.observe(expected)); assert.deepEqual(s.events, []);
});

test('accessors and coercions never execute; accepted expectations are deeply detached', async () => {
    for (const location of ['argv', 'directory', 'path']) {
        const expected = input(), s = synthetic(); let called = 0;
        const get = () => { called++; throw new Error('must not execute'); };
        if (location === 'argv') Object.defineProperty(expected.app.argv, '0', { get, enumerable: true });
        if (location === 'directory') Object.defineProperty(expected.appDataDirectory.device, 'ino', { get, enumerable: true });
        if (location === 'path') expected.appDataDirectory.path = { toString: get };
        await assert.rejects(s.observe(expected)); assert.equal(called, 0); assert.deepEqual(s.events, []);
    }
    const original = input(), copy = copyEndpointExpected(original, 1000); original.app.argv.push('later');
    assert.equal(copy.app.argv.length, 2); assert.throws(() => copy.app.argv.push('mutation'));
});

for (const [name, mutate] of [
    ['PID', x => x.pid++], ['parent', x => x.ppid++], ['birth', x => x.startTimeTicks = '1'],
    ['UID quartet', x => x.uids[1]++], ['exe', x => x.executable += '-other'], ['exe file', x => x.executableFile.ino++], ['argv', x => x.argv.push('late')],
]) test('App ' + name + ' drift refuses before opening or connecting', async () => {
    const s = synthetic(); mutate(s.fact); await assert.rejects(s.observe()); assert.deepEqual(s.events, ['facts']);
});

for (const [name, mutate] of [
    ['peer PID', x => x.socket.peer.pid++], ['peer UID', x => x.socket.peer.uid++], ['socket mode', x => x.socket.named.mode = String(0o140666)],
    ['socket UID', x => x.socket.named.uid = '1001'], ['socket closed', x => x.socket.closed = false],
    ['kernel type', x => x.socket.kernel.mode = String(0o100600)], ['descriptor PID', x => x.descriptor.pid++],
    ['kernel UID', x => x.socket.kernel.uid = '1001'], ['negative GID', x => x.socket.peer.gid = -1],
    ['descriptor path', x => x.descriptor.endpoint = '/tmp/other.sock'], ['descriptor UID', x => x.descriptor.file.uid = '1001'],
    ['descriptor type', x => x.descriptor.file.mode = String(0o120777)], ['oversized descriptor', x => x.descriptor.file.size = '4097'],
    ['directory identity', x => x.directory.ino = '99'], ['helper FDs', x => x.inheritedClosed = false],
    ['unexpected field', x => x.claimsFutureRpc = true], ['missing error code', x => x.ok = false],
    ['bad Python runtime', x => x.pythonVersion = [2, 7, 0]], ['double mode evidence', x => x.socket.named.extra = true],
]) test('probe ' + name + ' cannot produce a verified result', () => {
    const value = response(); mutate(value); assert.throws(() => copyEndpointProbe(value, copyEndpointExpected(input(), 1000)));
});

test('kernel GID is unsigned and may differ from UID', () => {
    const value = response(); value.socket.peer.gid = 3000000000;
    const result = copyEndpointProbe(value, copyEndpointExpected(input(), 1000));
    assert.equal(result.socket.peer.uid, 1000); assert.equal(result.socket.peer.gid, 3000000000);
});

test('probe-only result preserves0644 descriptor and distinguishes kernel/named socket inodes', async () => {
    const s = synthetic(), result = await s.observe();
    assert.equal(result.connectionScope, 'probe-only'); assert.equal(result.authenticatesSubsequentConnections, false);
    assert.equal(result.linuxOwnedCollectionQualified, false); assert.equal(result.helperClosed, true);
    assert.notEqual(result.probe.socket.named.ino, result.probe.socket.kernel.ino);
    assert.equal(result.probe.descriptor.file.mode, String(0o100644));
    assert.deepEqual(result.closed, ['helper', 'python', 'directory']); assert.throws(() => { result.probe.socket.peer.pid = 1; });
    assert.equal(s.events.filter(x => x === 'probe').length, 1);
});

test('failures preserve original close errors, stop later effects and never retry a failed probe', async () => {
    const s = synthetic(); s.reply.ok = false; s.reply.failures = [{ code: 'credentials', errno: null }];
    s.hook = name => { if (name.startsWith('close:')) throw new Error(name); };
    const error = await s.observe().catch(value => value);
    assert.equal(error.errors.length, 4); assert.equal(error.linuxEndpointResources.probe.failures[0].code, 'credentials');
    assert.equal(s.events.filter(x => x === 'probe').length, 1); assert.equal(s.events.filter(x => x.startsWith('close:')).length, 3);
});

test('one shared deadline expires after opening and all earlier resources close', async () => {
    const s = synthetic(); s.hook = name => { if (name === 'open:helper') s.time = 4000000000n; };
    const error = await s.observe().catch(value => value);
    assert.ok(error instanceof AggregateError); assert.equal(s.events.includes('probe'), false);
    assert.equal(s.events.filter(x => x.startsWith('close:')).length, 3);
});

test('post-probe identity drift retains closure facts but refuses an endpoint observation', async () => {
    const s = synthetic(); s.hook = name => { if (name === 'probe') s.fact.startTimeTicks = '99'; };
    const error = await s.observe().catch(value => value); assert.equal(error.linuxEndpointResources.helperClosed, true);
    assert.equal(s.events.filter(x => x === 'probe').length, 1);
});

test('public entry cannot accept platform/proc/reader overrides', async () => {
    if (process.platform === 'linux') {
        const expected = input(); expected.uid = process.getuid(); expected.appDataDirectory.device.uid = String(expected.uid);
        await assert.rejects(observeLinuxPrivateAgentEndpoint(expected, { deadlineNs: 1n, reader: {} }), /Unexpected Linux output fields/);
    } else assert.throws(() => observeLinuxPrivateAgentEndpoint(input(), { deadlineNs: 1n, platform: 'linux' }), /actual Linux/);
});

const fakeStat = (kind, values = {}) => ({ dev: 4n, ino: kind === 'directory' ? 10n : 9n, rdev: 0n,
    uid: kind === 'helper' || kind === 'directory' ? 1000n : 0n, mode: kind === 'directory' ? 0o40700n : 0o100755n,
    size: 8n, mtimeNs: 1n, ctimeNs: 2n, ...values,
    isDirectory: () => kind === 'directory', isFile: () => kind !== 'directory' });
function resourcesFixture() {
    const script = readFileSync(new URL('./linux-endpoint-probe.py', import.meta.url));
    const s = { time: 0n, log: [], hook: null, offset: new Map(), canonical: '/usr/bin/python3.14',
        bytes: { python: Buffer.from([127, 69, 76, 70, 1, 2, 3, 4]), helper: script },
        stats: { directory: fakeStat('directory'), python: fakeStat('python'), helper: fakeStat('helper', { size: BigInt(script.length) }) } };
    const kind = value => typeof value === 'number' ? ['directory', 'python', 'helper'][value - 7]
        : value.endsWith('/appdata') ? 'directory' : value.includes('/python3') ? 'python' : 'helper';
    const note = (...args) => { s.log.push(args); s.hook?.(...args); };
    const fs = { constants: { O_RDONLY: 0, O_DIRECTORY: 0x10000, O_NOFOLLOW: 0x20000 },
        realpathSync(path) { note('realpath', path); return path.endsWith('/appdata') ? path : s.canonical; },
        openSync(path, flags) { const target = kind(path); note('open', target, flags); return ['directory', 'python', 'helper'].indexOf(target) + 7; },
        fstatSync(fd) { note('fstat', kind(fd)); return s.stats[kind(fd)]; },
        lstatSync(path) { note('lstat', kind(path)); return s.stats[kind(path)]; },
        readSync(fd, buffer) { const target = kind(fd); note('read', target); const offset = s.offset.get(fd) ?? 0;
            const count = Math.min(buffer.length, s.bytes[target].length - offset); s.bytes[target].copy(buffer, 0, offset, offset + count); s.offset.set(fd, offset + count); return count; },
        closeSync(fd) { note('close', kind(fd)); } };
    s.resources = createEndpointResources(fs); s.expected = input(); s.expected.python.sha256 = createHash('sha256').update(s.bytes.python).digest('hex');
    s.budget = () => linuxOutputBudget({ deadlineNs: createEndpointDeadline(4000, () => s.time) }, () => s.time);
    return s;
}

test('all three fixed resources stay held with no-follow and directory-only flags', () => {
    const s = resourcesFixture(), held = ['directory', 'python', 'helper'].map(kind => s.resources.open(kind, s.expected, s.budget()));
    assert.deepEqual(s.log.filter(x => x[0] === 'open'), [['open', 'directory', 0x30000], ['open', 'python', 0x20000], ['open', 'helper', 0x20000]]);
    assert.equal(held[2].observation.sha256, linuxEndpointHelperSha256); s.resources.check(held, s.expected, s.budget());
    for (const item of held.reverse()) s.resources.close(item);
});

for (const [name, mutate] of [
    ['non-system Python', s => s.canonical = '/tmp/python'], ['non-root Python', s => s.stats.python.uid = 1000n],
    ['writable Python', s => s.stats.python.mode |= 2n], ['Python not executable', s => s.stats.python.mode = 0o100644n],
    ['oversized Python', s => s.stats.python.size = 16777217n], ['script interpreter', s => s.bytes.python[0] = 35],
    ['Python digest', s => s.expected.python.sha256 = '0'.repeat(64)],
]) test('resource admission refuses ' + name + ' without leaking opened fds', () => {
    const s = resourcesFixture(); mutate(s); assert.throws(() => s.resources.open('python', s.expected, s.budget()));
    assert.equal(s.log.filter(x => x[0] === 'close').length, s.log.filter(x => x[0] === 'open').length);
});

test('helper digest/owner/size and directory replacement are checked by actual admission code', () => {
    for (const name of ['bytes', 'uid', 'size', 'mode']) {
        const s = resourcesFixture();
        if (name === 'bytes') s.bytes.helper[0] ^= 1;
        if (name === 'uid') s.stats.helper.uid = 1001n;
        if (name === 'size') s.stats.helper.size = 65537n;
        if (name === 'mode') s.stats.helper.mode |= 0o020n;
        assert.throws(() => s.resources.open('helper', s.expected, s.budget())); assert.deepEqual(s.log.at(-1), ['close', 'helper']);
    }
    const s = resourcesFixture(); s.stats.directory.ino++;
    assert.throws(() => s.resources.open('directory', s.expected, s.budget())); assert.deepEqual(s.log.at(-1), ['close', 'directory']);
});

test('post-open expiry retains the fd and primary plus close failures', () => {
    const s = resourcesFixture(); s.hook = name => { if (name === 'open') s.time = 4000000000n; if (name === 'close') throw new Error('close'); };
    const error = (() => { try { s.resources.open('directory', s.expected, s.budget()); } catch (value) { return value; } })();
    assert.equal(error.errors.length, 2); assert.equal(error.linuxEndpointOpenResource.closeConfirmed, false);
    assert.equal(s.log.filter(x => x[0] === 'close').length, 1);
});

class Pipe extends EventEmitter { resume() {} destroy() { this.destroyed = true; } end(value) { this.value = value; } }
function runnerFixture({ autoClose = true } = {}) {
    const s = { time: 0n, timers: new Map(), next: 0, spawns: [], kills: [], unrefs: 0 };
    const child = Object.assign(new EventEmitter(), { pid: 999, stdin: new Pipe(), stdout: new Pipe(), stderr: new Pipe(),
        kill(signal) { s.kills.push(signal); if (autoClose) queueMicrotask(() => child.emit('close', null, signal)); return true; }, unref() { s.unrefs++; } });
    s.child = child;
    s.runner = createEndpointProbeRunner({ spawnChild: (...args) => { s.spawns.push(args); return child; },
        setTimer: (fn, ms) => { const id = ++s.next; s.timers.set(id, { fn, end: s.time + BigInt(ms) * 1000000n }); return id; },
        clearTimer: id => s.timers.delete(id) });
    s.advance = ms => { s.time += BigInt(ms) * 1000000n;
        for (const [id, timer] of [...s.timers]) { if (timer.end <= s.time && s.timers.delete(id)) timer.fn(); } };
    s.resources = [{ fd: 7 }, { fd: 8, observation: { path: '/usr/bin/python3.14' } }, { fd: 9 }];
    s.run = () => s.runner(s.resources, input(), linuxOutputBudget({ deadlineNs: 1000000000n }, () => s.time));
    return s;
}

test('runner executes held interpreter/helper with isolated flags and the held directory; no App bytes', async () => {
    const s = runnerFixture(), pending = s.run();
    const [command, args, options] = s.spawns[0];
    assert.equal(command, '/proc/self/fd/5'); assert.deepEqual(args, ['-I', '-S', '-B', '/proc/self/fd/4']);
    assert.deepEqual(options.stdio, ['pipe', 'pipe', 'pipe', 7, 9, 8]); assert.equal(options.shell, false);
    assert.deepEqual(Object.keys(options.env).sort(), ['LANG', 'LC_ALL', 'PATH']);
    const control = JSON.parse(s.child.stdin.value); assert.equal(control.appDataPath, appData); assert.equal(control.remainingMs, 750);
    s.child.stdout.emit('data', Buffer.from(JSON.stringify(response()) + '\n')); s.child.emit('close', 0, null);
    assert.equal((await pending).runId, runId); assert.deepEqual(s.kills, []);
});

test('runner hard-caps both streams before append and waits for close after exact owned-helper kill', async () => {
    for (const stream of ['stdout', 'stderr']) {
        const s = runnerFixture(), pending = s.run(); s.child[stream].emit('data', Buffer.alloc(4097));
        s.child[stream].emit('data', Buffer.alloc(100000));
        const error = await pending.catch(value => value); assert.equal(error.helperCloseConfirmed, true);
        assert.equal(error.output[stream + 'Bytes'], 0); assert.deepEqual(s.kills, ['SIGKILL']);
    }
});

test('work timeout, exit-only and late close never establish a completed helper', async () => {
    const s = runnerFixture({ autoClose: false }), pending = s.run();
    s.child.emit('exit', 0, null); s.advance(1000);
    const error = await pending.catch(value => value); assert.equal(error.helperCloseConfirmed, false);
    assert.deepEqual(s.kills, []); assert.equal(s.unrefs, 1); s.child.emit('close', 0, null);
    assert.equal(error.helperCloseConfirmed, false);
});

for (const bytes of [Buffer.from('{}'), Buffer.from('{}\n{}\n'), Buffer.from([0xff, 10])]) {
    test('malformed or multiple helper frames refuse after actual close', async () => {
        const s = runnerFixture(), pending = s.run(); s.child.stdout.emit('data', bytes); s.child.emit('close', 0, null);
        const error = await pending.catch(value => value); assert.equal(error.helperCloseConfirmed, true);
    });
}
