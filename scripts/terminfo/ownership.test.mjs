import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { assertPrivateFile, assertProcessOwnership, bindOutput, matchesRecordedWorker, parseProcessRecord, requestOwner } from './ownership.mjs';

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

test('worker configuration refuses symlinks and group-readable files', () => {
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
    const dir = mkdtempSync('/tmp/ntilde-owner-test-');
    const path = join(dir, 'owner.sock');
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
    const path = join(dir, 'owner.sock');
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
