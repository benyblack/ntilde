import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { EventEmitter } from 'node:events';
import { PassThrough, Writable } from 'node:stream';
import { createLaunchResources, createLaunchChannelFactory, helperSha256 } from './linux-launch/platform.mjs';
import { copyLaunchInput } from './linux-launch/core.mjs';
const source = fileURLToPath(new URL('./linux-launch/helper.py', import.meta.url));
const hash = value => createHash('sha256').update(value).digest('hex');
const phase = () => ({ remainingMs: () => 1000, check() {}, query: action => action() });
/** Model only private in-memory paths/fds so adversarial modes and closure failures cannot touch host files. */
function filesystem() {
    let next = 20, ino = 1;
    const entries = new Map(), opened = new Map(), closes = [], calls = [];
    const flags = { O_RDONLY: 0, O_WRONLY: 1, O_CREAT: 64, O_EXCL: 128, O_NOCTTY: 256, O_DIRECTORY: 65536, O_NOFOLLOW: 131072 };
    /** Install one controlled fake entry with lossless metadata for resource-policy comparisons. */
    function add(path, kind, data = Buffer.alloc(0), uid = 1000, mode = kind === 'directory' ? 0o40700 : 0o100600) {
        const item = { path, kind, data, uid: BigInt(uid), mode: BigInt(mode), ino: BigInt(ino++), nlink: 1n, dev: 1n, rdev: 0n, mtimeNs: 1n, ctimeNs: 2n };
        entries.set(path, item); return item;
    }
    /** Project the fake entry through the same BigInt filesystem shape as production. */
    function stat(item) { return { ...item, size: item.size ?? BigInt(item.data.length), isFile: () => item.kind === 'file',
        isDirectory: () => item.kind === 'directory', isCharacterDevice: () => item.kind === 'null' }; }
    /** Install one controlled fake entry with lossless metadata for resource-policy comparisons. */
    add('/private/app', 'directory'); add('/private/run', 'directory'); add('/private/run/appdata', 'directory');
    const elf = Buffer.alloc(32); Buffer.from([127, 69, 76, 70, 2, 1]).copy(elf); elf.writeUInt16LE(3, 16); elf.writeUInt16LE(62, 18);
    /** Install one controlled fake entry with lossless metadata for resource-policy comparisons. */
    add('/private/app/Ntilde', 'file', elf, 1000, 0o100700);
    /** Install one controlled fake entry with lossless metadata for resource-policy comparisons. */
    add('/private/app/Ntilde.dll', 'file', Buffer.from('managed')); add('/private/app/librusty_pty.so', 'file', Buffer.from('pty'));
    /** Install one controlled fake entry with lossless metadata for resource-policy comparisons. */
    add('/private/run/appdata/settings.json', 'file', Buffer.from('{}'));
    /** Install one controlled fake entry with lossless metadata for resource-policy comparisons. */
    add('/usr/bin/python3.14', 'file', elf, 0, 0o100755); add(source, 'file', readFileSync(source));
    const nullDevice = add('/dev/null', 'null', Buffer.alloc(0), 0, 0o20666); nullDevice.rdev = 259n;
    const fs = { constants: flags,
        realpathSync(path) { return path === '/usr/bin/python3' ? '/usr/bin/python3.14' : entries.get(path)?.alias ?? path; },
        lstatSync(path) { if (!entries.has(path)) throw new Error('missing fixture path'); return stat(entries.get(path)); },
        fstatSync(fd) { if (!opened.has(fd)) throw new Error('unknown/closed fixture fd'); return stat(opened.get(fd)); },
        mkdirSync(path) { if (entries.has(path)) throw new Error('existing directory'); add(path, 'directory'); },
        openSync(path, options) {
            calls.push({ path, options });
            if (options & flags.O_CREAT) { if (entries.has(path)) throw new Error('existing file'); add(path, 'file'); }
            if (!entries.has(path)) throw new Error('missing file');
            const fd = next++; opened.set(fd, entries.get(path)); return fd;
        },
        readSync(fd, buffer, offset, length, position) { const item = opened.get(fd); return item.data.copy(buffer, offset, position, position + length); },
        closeSync(fd) { closes.push(fd); if (!opened.delete(fd)) throw new Error('duplicate close'); } };
    const dir = path => ({ path, device: Object.fromEntries(['dev', 'ino', 'uid', 'mode'].map(key => [key, entries.get(path)[key].toString()])) });
    const input = copyLaunchInput({ directory: dir('/private/app'), sha256: { app: hash(elf), managed: hash(Buffer.from('managed')), pty: hash(Buffer.from('pty')) } },
        { runId: 'a'.repeat(32), uid: 1000, scratch: dir('/private/run'), appDataDirectory: dir('/private/run/appdata'),
            initialSettingsSha256: hash(Buffer.from('{}')), locale: 'C.UTF-8', appVersion: '0.13.0' },
        { path: '/usr/bin/python3', sha256: hash(elf) }, 1000);
    return { fs, input, entries, opened, closes, calls };
}

test('fixed launch resources pin bytes/private directories/null device and close only known fds', () => {
    const fake = filesystem(), policy = createLaunchResources(fake.fs, 'x64');
    const resources = policy.open(fake.input, phase()); assert.equal(resources.length, 12);
    assert.equal(resources.find(value => value.kind === 'source').sha256, helperSha256);
    assert.ok(fake.calls.every(value => value.options & fake.fs.constants.O_NOFOLLOW));
    policy.check(resources, phase(), true);
    for (const value of resources) policy.close(value);
    assert.equal(fake.opened.size, 0); assert.equal(new Set(fake.closes).size, fake.closes.length);
});

for (const defect of ['app-uid', 'app-mode', 'app-setgid', 'python-setuid', 'app-hardlink', 'app-hash', 'app-elf', 'python-owner', 'source-bytes', 'settings-mode', 'null-device', 'canonical', 'open-flags', 'size']) {
    test('rejects resource defect before spawn: ' + defect, () => {
        const fake = filesystem(), app = fake.entries.get('/private/app/Ntilde');
        if (defect === 'app-uid') app.uid++;
        if (defect === 'app-mode') app.mode |= 0o020n;
        if (defect === 'app-setgid') app.mode |= 0o2000n;
        if (defect === 'python-setuid') fake.entries.get('/usr/bin/python3.14').mode |= 0o4000n;
        if (defect === 'app-hardlink') app.nlink = 2n;
        if (defect === 'app-hash') app.data = Buffer.from('different');
        if (defect === 'app-elf') { app.data[18] = 0; fake.input = { ...fake.input, artifact: { ...fake.input.artifact, sha256: { ...fake.input.artifact.sha256, app: hash(app.data) } } }; }
        if (defect === 'python-owner') fake.entries.get('/usr/bin/python3.14').uid = 1000n;
        if (defect === 'source-bytes') fake.entries.get(source).data = Buffer.from('substituted source');
        if (defect === 'settings-mode') fake.entries.get('/private/run/appdata/settings.json').mode = 0o100666n;
        if (defect === 'null-device') fake.entries.get('/dev/null').rdev = 258n;
        if (defect === 'canonical') fake.entries.get('/private/app').alias = '/outside';
        if (defect === 'open-flags') fake.fs.constants.O_NOFOLLOW = 0;
        if (defect === 'size') app.size = 33554433n;
        assert.throws(() => createLaunchResources(fake.fs, 'x64').open(fake.input, phase()));
        assert.equal(fake.opened.size, 0); assert.equal(new Set(fake.closes).size, fake.closes.length);
    });
}

test('deadline after an allocating syscall still closes the newly acquired descriptor', () => {
    const fake = filesystem(), policy = createLaunchResources(fake.fs, 'x64');
    const budget = { ...phase(), query(action) { const value = action(); if (fake.opened.size) throw new Error('deadline after open'); return value; } };
    assert.throws(() => policy.open(fake.input, budget));
    assert.equal(fake.opened.size, 0); assert.equal(fake.closes.length, 1);
});

test('named artifact replacement is refused even while original descriptor remains open', () => {
    const fake = filesystem(), policy = createLaunchResources(fake.fs, 'x64'); const resources = policy.open(fake.input, phase());
    fake.entries.set('/private/app/Ntilde', { ...fake.entries.get('/private/app/Ntilde'), ino: 999n });
    assert.throws(() => policy.check(resources, phase(), false));
    for (const value of resources) policy.close(value);
});

test('initial settings mutation is checked before launch and is not an ongoing App-owned save invariant', () => {
    const fake = filesystem(), policy = createLaunchResources(fake.fs, 'x64'); const resources = policy.open(fake.input, phase());
    fake.entries.get('/private/run/appdata/settings.json').mtimeNs++;
    assert.throws(() => policy.check(resources, phase(), true));
    assert.doesNotThrow(() => policy.check(resources, phase(), false));
    for (const value of resources) policy.close(value);
});

test('an unconfirmed close is attempted only once and remains unconfirmed', () => {
    const fake = filesystem(), policy = createLaunchResources(fake.fs, 'x64'); const resources = policy.open(fake.input, phase());
    const app = resources.find(value => value.kind === 'app'); let attempts = 0;
    const close = fake.fs.closeSync; fake.fs.closeSync = fd => { if (fd === app.fd) { attempts++; throw new Error('Controlled close failure'); } close(fd); };
    assert.throws(() => policy.close(app)); policy.close(app);
    assert.equal(attempts, 1); assert.equal(app.closeConfirmed, undefined);
    for (const value of resources) policy.close(value);
});

/** Create an in-memory ChildProcess seam to test protocol bounds and exact-helper termination without OS processes. */
function transport(behavior = {}) {
    const fake = filesystem(); const resources = createLaunchResources(fake.fs, 'x64').open(fake.input, phase());
    const closedResources = [], writes = [], killed = [];
    let process_, hold = false;
    const policy = { open: () => resources, check() {}, close(value) { if (!value.closed) { value.closed = true; closedResources.push(value.kind); value.closeConfirmed = true; } } };
    const spawnChild = (executable, argv, options) => {
        assert.equal(executable, '/proc/self/fd/5'); assert.equal(options.argv0, '/usr/bin/python3.14');
        assert.equal(options.stdio[4], resources.find(value => value.kind === 'source').fd);
        assert.deepEqual(Object.keys(options.env).sort(), ['LC_ALL', 'PATH']);
        process_ = new EventEmitter(); process_.pid = 701; process_.stdout = new PassThrough(); process_.stderr = new PassThrough();
        process_.kill = sig => { killed.push(sig); process_.emit('exit', null, sig); process_.emit('close'); return true; };
        process_.stdin = new Writable({ write(data, _encoding, callback) {
            const frame = JSON.parse(data); writes.push(frame);
            if (!hold) queueMicrotask(() => {
                const value = { seq: frame.seq, ok: true, observation: {}, clockNs: '1000' };
                if (behavior.reply) behavior.reply(process_, value); else process_.stdout.write(JSON.stringify(value) + '\n');
            });
            callback();
        }, final(callback) { callback(); if (!behavior.neverCloses) queueMicrotask(() => { process_.stdout.end(); process_.stderr.end(); process_.emit('exit', 0, null); process_.emit('close'); }); } });
        return process_;
    };
    return { open: () => createLaunchChannelFactory({ resources: policy, spawnChild })(fake.input, phase()),
        child: () => process_, writes, killed, closedResources, hold: () => { hold = true; } };
}

test('transport executes pinned descriptor slots, fixed helper argv and minimal environment', async () => {
    const env = transport(), channel = await env.open();
    assert.equal(env.writes.length, 1); assert.equal(env.writes[0].op, 'init');
    await channel.finish(phase()); assert.equal(channel.snapshot().closeConfirmed, true);
    assert.equal(env.closedResources.length, 12); assert.equal(env.killed.length, 0);
});

test('malformed/uncorrelated/oversized helper output fails closed and closes parent resources', async () => {
    for (const reply of [(_child, value) => { value.seq++; return value; }, (_child, value) => { value.extra = true; return value; }]) {
        const env = transport({ reply(child, value) { child.stdout.write(JSON.stringify(reply(child, value)) + '\n'); } });
        await assert.rejects(env.open()); assert.equal(env.closedResources.length, 12);
    }
    const env = transport({ reply(child) { child.stdout.write(Buffer.alloc(4097, 65)); } });
    await assert.rejects(env.open()); assert.equal(env.closedResources.length, 12);
});

test('stderr cap is applied before retaining an oversized chunk', async () => {
    const env = transport(), channel = await env.open(); env.child().stderr.write(Buffer.alloc(4097));
    await assert.rejects(channel.request({ op: 'clock' }, phase()));
    assert.equal(channel.snapshot().stderrBytes, 0); await channel.finish(phase());
});

test('transport overlap aborts both requests and never retries the unknown operation', async () => {
    const env = transport(), channel = await env.open(); env.hold();
    const first = channel.request({ op: 'clock' }, phase()).catch(error => error);
    await assert.rejects(channel.request({ op: 'clock' }, phase()), /overlap/);
    assert.ok(await first instanceof Error); await channel.finish(phase());
    assert.equal(env.writes.length, 2);
});

test('close timeout can signal only the exact spawned helper and retains unknown close', async () => {
    const env = transport({ neverCloses: true }), channel = await env.open();
    await assert.rejects(channel.finish({ remainingMs: () => 15 }));
    assert.deepEqual(env.killed, ['SIGKILL']); assert.equal(env.closedResources.length, 12);
});

test('expired or oversized outbound requests close input before any further helper command', async () => {
    for (const command of [{ op: 'clock' }, { op: 'clock', oversized: 'x'.repeat(4097) }]) {
        const env = transport(), channel = await env.open();
        const budget = command.oversized ? phase() : { remainingMs() { throw new Error('expired operation'); } };
        await assert.rejects(channel.request(command, budget));
        await assert.rejects(channel.request({ op: 'release' }, phase()));
        assert.equal(env.writes.length, 1); assert.equal(channel.snapshot().stdinEnded, true);
        await channel.finish(phase());
    }
});
