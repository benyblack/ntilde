import assert from 'node:assert/strict';
import { test } from 'node:test';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { basename, join } from 'node:path';
import { tmpdir } from 'node:os';
import { processBirth, assertPrivateFile, assertProcessOwnership, bindOutput, matchesRecordedWorker, parseProcessRecord, processRecord, requestOwner, terminateRecordedWorker, workerExists } from './ownership.mjs';

const ownerSocket = dir => process.platform === 'win32' ? String.raw`\\.\pipe\ntilde-owner-${process.pid}-${basename(dir)}` : join(dir, 'owner.sock');

const proof = () => ({
    app: { pid: 100, uid: 501, executable: '/test/Ntilde' },
    worker: { pid: 101, ppid: 100, uid: 501, tty: 'ttys012' },
    ownerPid: 100, uid: 501, executable: '/test/Ntilde', runId: 'a'.repeat(32),
    appCommand: '/test/Ntilde --ntilde-terminfo-owner=' + 'a'.repeat(32), tty: '/dev/ttys012',
});

test('accepts an identified child on the claimed PTY', () => {
    assert.doesNotThrow(() => assertProcessOwnership(proof()));
});

for (const [name, change] of [
    ['unrelated pane', p => { p.worker.ppid = 200; }],
    ['reused owner PID', p => { p.app.pid = 200; }],
    ['different executable', p => { p.app.executable = '/test/Other'; }],
    ['different app user', p => { p.app.uid = 502; }],
    ['different worker user', p => { p.worker.uid = 502; }],
    ['another run marker', p => { p.appCommand = '/test/Ntilde --ntilde-terminfo-owner=' + 'b'.repeat(32); }],
    ['marker suffix', p => { p.appCommand += '-suffix'; }],
    ['no controlling TTY', p => { p.worker.tty = '??'; }],
    ['swapped PTY', p => { p.tty = '/dev/ttys013'; }],
    ['invalid run ID', p => { p.runId = 'not-a-run'; }],
]) {
    test(`refuses ${name}`, () => {
        const p = proof();
        change(p);
        assert.throws(() => assertProcessOwnership(p));
    });
}

test('a stream-like object cannot obtain a PTY binding', () => {
    assert.throws(() => bindOutput({ fd: 1 }, '/dev/ttys012'), /real stdin and stdout PTYs/);
});

test('worker configuration refuses symlinks and group-readable files', { skip: process.platform === 'win32' ? 'Requires Unix UID and permission-mode semantics' : false }, () => {
    const dir = mkdtempSync(join(tmpdir(), 'ntilde-owner-test-'));
    try {
        const path = join(dir, 'worker.json');
        writeFileSync(path, '{}', { mode: 0o600 });
        assert.doesNotThrow(() => assertPrivateFile(path));
        symlinkSync(path, join(dir, 'link'));
        assert.throws(() => assertPrivateFile(join(dir, 'link')), /private file/);
        writeFileSync(join(dir, 'public.json'), '{}', { mode: 0o644 });
        assert.throws(() => assertPrivateFile(join(dir, 'public.json')), /private file/);
    } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('an owner refusal propagates instead of granting a worker capability', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'ntilde-owner-test-'));
    const path = ownerSocket(dir);
    const server = createServer(socket => {
        socket.once('data', () => socket.end('{"ok":false,"error":"wrong owner"}\n'));
    });
    try {
        await new Promise(resolve => server.listen(path, resolve));
        await assert.rejects(requestOwner({ socket: path, token: 'test', runId: 'a'.repeat(32) }, 'prove'), /wrong owner/);
    } finally {
        await new Promise(resolve => server.close(resolve));
        rmSync(dir, { recursive: true, force: true });
    }
});

test('process records preserve spaces in executable names without backtracking', () => {
    assert.deepEqual(parseProcessRecord('  100   10  501  ttys012  /Applications/My  App/Ntilde  '),
        { pid: 100, ppid: 10, uid: 501, tty: 'ttys012', executable: '/Applications/My  App/Ntilde' });
    assert.throws(() => parseProcessRecord('100 10 501 ttys012'), /Could not identify/);
    assert.throws(() => parseProcessRecord('invalid 10 501 ttys012 /Ntilde'), /Could not identify/);
    assert.throws(() => parseProcessRecord(' '.repeat(8192)), /Could not identify/);
});

const recordedWorker = { pid: 101, ppid: 100, uid: 501, tty: 'ttys012', executable: '/runtime/node' };
const workerRun = 'a'.repeat(32);
const workerCommand = `/runtime/node /private/worker.mjs /private/worker.json --ntilde-terminfo-worker=${workerRun}`;

test('cleanup recognizes the recorded worker after reparenting or PTY hangup', () => {
    assert.ok(matchesRecordedWorker({ ...recordedWorker, ppid: 1 }, recordedWorker, workerCommand, workerRun));
    assert.ok(matchesRecordedWorker({ ...recordedWorker, ppid: 1, tty: '??' }, recordedWorker, workerCommand, workerRun));
});

for (const [field, value] of [['pid', 202], ['uid', 502], ['tty', 'ttys099'], ['executable', '/other/node']]) {
    test(`cleanup refuses a reused worker identity with a different ${field}`, () => {
        assert.equal(matchesRecordedWorker({ ...recordedWorker, [field]: value }, recordedWorker, workerCommand, workerRun), false);
    });
}

test('cleanup refuses another run marker or a missing ownership record', () => {
    assert.equal(matchesRecordedWorker(recordedWorker, recordedWorker, workerCommand + '-suffix', workerRun), false);
    assert.equal(matchesRecordedWorker(recordedWorker, recordedWorker, workerCommand, 'b'.repeat(32)), false);
    assert.equal(matchesRecordedWorker(recordedWorker, null, workerCommand, workerRun), false);
});

test('an owner success response resolves with its measured owner PID', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'nto-'));
    const path = ownerSocket(dir);
    const server = createServer(socket => {
        socket.once('data', () => socket.end('{"ok":true,"ownerPid":100}\n'));
    });
    try {
        await new Promise(resolve => server.listen(path, resolve));
        const response = await requestOwner({ socket: path, token: 'test', runId: 'a'.repeat(32) }, 'prove');
        assert.equal(response.ownerPid, 100);
    } finally {
        await new Promise(resolve => server.close(resolve));
        rmSync(dir, { recursive: true, force: true });
    }
});

test('owner refusal transport preserves only the known optional RGB budget code', async () => {
    let code = 'screenDetailsUnavailable';
    const server = createServer(socket => {
        socket.once('data', () => socket.end(JSON.stringify({ ok: false, error: 'refused', code }) + '\n'));
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    try {
        const config = { socket: { host: '127.0.0.1', port: server.address().port }, token: 'test', runId: 'a'.repeat(32) };
        await assert.rejects(requestOwner(config, 'rgb-start'), error => error.code === code);
        for (const [kind, expected] of [['resize-start', 'resizeGeometryUnavailable'], ['input-start', 'inputGeometryUnavailable'],
            ['images-start', 'imageGeometryUnavailable'], ['saved-start', 'savedGeometryUnavailable']]) {
            code = expected;
            await assert.rejects(requestOwner(config, kind), error => error.code === expected);
        }
        for (const kind of ['metadata-start', 'shi-start', 'lnk-start', 'tst-start', 'ost-start']) {
            code = 'metadataGeometryUnavailable';
            await assert.rejects(requestOwner(config, kind), error => error.code === code);
        }
        code = 'paneMetadataUnavailable';
        await assert.rejects(requestOwner(config, 'ost-start'), error => error.code === code);
        await assert.rejects(requestOwner(config, 'ost-read'), error => error.code === undefined);
        code = 'untrustedCode';
        await assert.rejects(requestOwner(config, 'rgb-start'), error => error.code === undefined);
    } finally { await new Promise(resolve => server.close(resolve)); }
});

async function withRecordedChild(program, action) {
    const child = spawn(process.execPath, ['--input-type=module', '--eval', program, '--',
        `--ntilde-terminfo-worker=${workerRun}`], { stdio: ['ignore', 'pipe', 'pipe'] });
    const exited = once(child, 'exit');
    try {
        // Readiness follows handler installation; an early exit must fail promptly.
        await Promise.race([once(child.stdout, 'data'), exited.then(() => { throw new Error('Worker exited before readiness.'); })]);
        const recorded = { ...processRecord(child.pid), tty: 'ttysDetached' };
        await action(child, recorded, exited);
    } finally {
        if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
        await exited;
    }
}

const delayedWorker = `process.on('SIGTERM', () => setTimeout(() => process.exit(0), 100));
    setInterval(() => {}, 1000); process.stdout.write('ready');`;
const stubbornWorker = `process.on('SIGTERM', () => {});
    setInterval(() => {}, 1000); process.stdout.write('ready');`;

test('worker cleanup waits for a delayed exit after SIGTERM', { skip: process.platform !== 'darwin' }, async () => {
    await withRecordedChild(delayedWorker, async (child, recorded) => {
        await terminateRecordedWorker(recorded, workerRun);
        assert.equal(workerExists(child.pid), false);
        assert.equal(child.exitCode, 0);
    });
});

test('worker cleanup escalates a verified SIGTERM-resistant worker and waits for its exit', {
    skip: process.platform !== 'darwin',
}, async () => {
    await withRecordedChild(stubbornWorker, async (child, recorded) => {
        await terminateRecordedWorker(recorded, workerRun);
        assert.equal(workerExists(child.pid), false);
        assert.equal(child.signalCode, 'SIGKILL');
    });
});

test('worker cleanup refuses a live process with a different run identity', { skip: process.platform !== 'darwin' }, async () => {
    await withRecordedChild(stubbornWorker, async (child, recorded) => {
        await assert.rejects(terminateRecordedWorker(recorded, 'b'.repeat(32)), /identity changed/);
        assert.equal(workerExists(child.pid), true);
    });
});

test('worker cleanup tolerates a process that has already exited', { skip: process.platform !== 'darwin' }, async () => {
    await withRecordedChild(stubbornWorker, async (child, recorded, exited) => {
        child.kill('SIGKILL');
        await exited;
        await terminateRecordedWorker(recorded, workerRun);
        assert.equal(workerExists(child.pid), false);
    });
});

test('process birth corroboration is bounded UTC/C locale and separate from legacy process fields', () => {
    let options;
    const value = processBirth(42, 87, (tool, args, supplied) => {
        assert.equal(tool, '/bin/ps'); assert.deepEqual(args, ['-p', '42', '-o', 'lstart=']); options = supplied;
        return 'Thu Nov  5 09:00:00 2026\n';
    });
    assert.equal(value, 'Thu Nov  5 09:00:00 2026'); assert.equal(options.timeout, 87); assert.equal(options.maxBuffer, 256);
    assert.equal(options.env.LC_ALL, 'C'); assert.equal(options.env.LANG, 'C'); assert.equal(options.env.TZ, 'UTC');
    assert.deepEqual(Object.keys(parseProcessRecord('42 10 501 ttys1 /test/node')), ['pid', 'ppid', 'uid', 'tty', 'executable']);
    for (const invalid of ['', 'unknown', 'Thu Nov  5 09:00:00 2026\nEXTRA']) assert.throws(() => processBirth(42, 87, () => invalid));
});
