import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { createChannelFactory, createResources } from './linux-lifecycle/platform.mjs';
import { linuxOutputBudget } from './linux-owned-output-core.mjs';

const helper = readFileSync(new URL('./linux-lifecycle/pidfd-helper.py', import.meta.url));
const elf = Buffer.from([127, 69, 76, 70, 1, 2, 3, 4]);
const pin = { path: '/usr/bin/python3', sha256: createHash('sha256').update(elf).digest('hex') };
const original = { app: { pid: 40 }, worker: { pid: 42 }, uid: 1000 };
function files() {
    const state = { events: [], mutation: null, modes: { python: 0o100755n, helper: 0o100600n },
        bytes: { python: elf, helper }, offsets: {}, closed: [] };
    const kind = fd => fd === 10 ? 'python' : 'helper';
    const stat = fd => ({ dev: 1n, ino: BigInt(fd), rdev: 0n, uid: fd === 10 ? 0n : 1000n,
        mode: state.modes[kind(fd)], size: BigInt(state.bytes[kind(fd)].length), mtimeNs: 1n, ctimeNs: 2n, isFile: () => true });
    state.fs = { constants: { O_RDONLY: 0, O_NOFOLLOW: 131072 },
        realpathSync: () => '/usr/bin/python3.14',
        openSync(path, flags) { state.events.push(['open', path, flags]); const fd = path.includes('python') ? 10 : 11; state.offsets[fd] = 0; return fd; },
        fstatSync(fd) { const value = stat(fd); state.mutation?.(value, fd); return value; },
        lstatSync(path) { return stat(path.includes('python') ? 10 : 11); },
        readSync(fd, buffer, offset, length) {
            const source = state.bytes[kind(fd)], begin = state.offsets[fd], count = Math.min(length, source.length - begin);
            source.copy(buffer, offset, begin, begin + count); state.offsets[fd] += count; return count;
        },
        closeSync(fd) { state.closed.push(fd); if (state.failClose === fd) throw new Error('close refused'); } };
    state.resources = createResources(state.fs);
    state.phase = () => linuxOutputBudget({ deadlineNs: 4000000000n }, () => 0n);
    return state;
}
test('source and interpreter are hashed through held no-follow FDs and independently closed', () => {
    const s = files(), phase = s.phase();
    const python = s.resources.open('python', original, pin, phase), source = s.resources.open('helper', original, pin, phase);
    assert.equal(python.observation.sha256, pin.sha256); assert.ok(s.events.every(([, , flags]) => flags === 131072));
    s.resources.close(source); s.resources.close(python); assert.deepEqual(s.closed, [11, 10]);
});
for (const [name, mutate] of [
    ['wrong digest', s => s.bytes.python = Buffer.from([127, 69, 76, 70, 0])],
    ['world writable interpreter', s => s.modes.python |= 2n],
    ['source group writable', s => s.modes.helper |= 16n],
    ['oversized interpreter', s => s.mutation = stat => { stat.size = 16777217n; }],
    ['changing held inode', s => { let count = 0; s.mutation = stat => { if (++count > 1) stat.ino++; }; }],
]) test('retained resource refusal closes once: ' + name, () => {
    const s = files(); mutate(s);
    const kind = name.includes('source') ? 'helper' : 'python';
    assert.throws(() => s.resources.open(kind, original, pin, s.phase())); assert.equal(s.closed.length, 1);
});

function transport() {
    const s = { writes: [], closes: [], children: [], stdoutFrames: [], stderr: [], action: null, now: 0n };
    const resources = { open(kind) { return { fd: kind === 'python' ? 10 : 11, observation: { kind, path: '/usr/bin/python3.14' } }; },
        check() {}, close(resource) { s.closes.push(resource.fd); if (s.failClose === resource.fd) throw new Error('file close failure'); } };
    const observation = { app: { exitObserved: false, pollEvents: 0 }, worker: { exitObserved: false, pollEvents: 0 }, attempts: [], pidfdsClosed: false };
    s.factory = createChannelFactory({ resources, spawnChild(command, argv, options) {
        assert.equal(command, '/proc/self/fd/5'); assert.deepEqual(argv, ['-I', '-S', '-B', '/proc/self/fd/4']);
        assert.deepEqual(options.stdio.slice(3), ['ignore', 11, 10]);
        assert.deepEqual(options.env, { LC_ALL: 'C', LANG: 'C', PATH: '/usr/bin:/bin' });
        const child = new EventEmitter(); Object.assign(child, { stdin: new PassThrough(), stdout: new PassThrough(), stderr: new PassThrough(), pid: 999,
            unref() {}, kill(signal) { s.killed = signal; queueMicrotask(() => { child.emit('exit', null, signal); child.emit('close', null, signal); }); } });
        child.stdin.on('data', data => {
            const request = JSON.parse(data.toString('utf8')); s.writes.push(request);
            if (s.action?.(child, request) === false) return;
            const frame = { seq: request.seq, kind: request.op === 'open' ? 'opened' : request.op, observation,
                ...(request.op === 'clock' ? { clockNs: '1000' } : {}) };
            child.stdout.write(JSON.stringify(frame) + '\n');
            if (request.op === 'close') queueMicrotask(() => { child.emit('exit', 0, null); child.emit('close', 0, null); });
        });
        s.children.push(child); return child;
    } });
    s.phase = () => linuxOutputBudget({ deadlineNs: s.now + 4000000000n }, () => s.now);
    s.open = () => s.factory(original, pin, 20000, s.phase());
    return s;
}
test('normal channel close is an explicit command before stdin EOF; all held resources close', async () => {
    const s = transport(), channel = await s.open();
    await channel.request({ op: 'close', deadlineNs: '100' }, s.phase()); await channel.finish(s.phase());
    assert.deepEqual(s.writes.map(x => x.op), ['open', 'close']); assert.equal(s.killed, undefined);
    assert.deepEqual(s.closes, [11, 10]); assert.equal(channel.resources().helperCloseConfirmed, true);
});
for (const [name, inject] of [
    ['stdout total overflow', child => child.stdout.write(Buffer.alloc(262145))],
    ['frame overflow', child => child.stdout.write(Buffer.alloc(4096))],
    ['stderr overflow', child => child.stderr.write(Buffer.alloc(4097))],
    ['unsolicited reply', child => child.stdout.write('{"seq":999,"kind":"clock"}\n')],
    ['malformed UTF8', child => child.stdout.write(Buffer.from([255, 10]))],
]) test('bounded transport refuses and closes exact helper: ' + name, async () => {
    const s = transport(), channel = await s.open();
    s.action = (child, request) => { if (request.op === 'clock') { inject(child); return false; } };
    await assert.rejects(channel.request({ op: 'clock' }, s.phase())); await assert.rejects(channel.finish(s.phase()));
    assert.equal(s.killed, 'SIGKILL'); assert.deepEqual(s.closes, [11, 10]);
    const evidence = channel.resources(); assert.ok(evidence.stdoutBytes <= 262144); assert.ok(evidence.stderrBytes <= 4096);
});
test('file close failure is retained and never retried against possibly reused FD', async () => {
    const s = transport(), channel = await s.open(); s.failClose = 11;
    await channel.request({ op: 'close', deadlineNs: '100' }, s.phase()); await assert.rejects(channel.finish(s.phase()));
    await assert.rejects(channel.finish(s.phase())); assert.deepEqual(s.closes, [11, 10]);
    assert.equal(channel.resources().sourceCloseConfirmed, false);
});
test('failed normal settlement kills only helper instead of manufacturing admitted EOF', async () => {
    const s = transport(), channel = await s.open(); let ended = false;
    s.children[0].stdin.on('end', () => { ended = true; });
    await assert.rejects(channel.finish(s.phase()));
    assert.equal(ended, false); assert.equal(s.killed, 'SIGKILL'); assert.deepEqual(s.closes, [11, 10]);
});
test('fragmented fatal diagnostics remain bounded and complete after request rejection', async () => {
    const s = transport(), channel = await s.open();
    s.action = (child, request) => {
        if (request.op !== 'clock') return;
        child.stderr.write(Buffer.from('{"errors":'));
        child.stderr.write(Buffer.from('[],"observation":null}\n'));
        return false;
    };
    await assert.rejects(channel.request({ op: 'clock' }, s.phase()));
    await assert.rejects(channel.finish(s.phase()));
    assert.deepEqual(JSON.parse(Buffer.from(channel.resources().stderrHex, 'hex').toString('utf8')), { errors: [], observation: null });
});
