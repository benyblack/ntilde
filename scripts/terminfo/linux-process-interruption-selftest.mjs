// Explicitly spawned fixture termination only; no App, GUI, clipboard or unrelated PID.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { realpathSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { readLinuxOwnedChildFacts } from './linux-process-provider.mjs';
import { LinuxProviderOutput, linuxProviderOutputClosed } from './fixtures/linux-provider-output.mjs';

if (process.platform !== 'linux' || process.argv.length !== 3) throw new Error('Expected Linux and one interruption-report path.');
writeFileSync(process.argv[2], '');
const runId = randomBytes(16).toString('hex'), executable = realpathSync(process.execPath);
const script = fileURLToPath(new URL('./fixtures/linux-provider-child.mjs', import.meta.url));
const helper = fileURLToPath(new URL('./fixtures/linux-provider-pty.py', import.meta.url));
const child = spawn('/usr/bin/python3', [helper, executable, script, runId], { stdio: ['pipe', 'pipe', 'pipe'] });
child.stdin.on('error', () => {});
const stdout = new LinuxProviderOutput(), stderr = new LinuxProviderOutput();
let failure, rejectReady, readyResolve, timer;
const stopReading = error => {
    failure ??= error; rejectReady?.(failure);
    child.stdout.removeListener('data', stdoutData); child.stderr.removeListener('data', stderrData);
    child.stdout.resume(); child.stderr.resume();
};
const stdoutData = bytes => {
    try {
        stdout.append(bytes);
        const first = stdout.text().split('\n')[0];
        if (stdout.text().includes('\n')) readyResolve(JSON.parse(first));
    } catch (error) { stopReading(error); }
};
const stderrData = bytes => { try { stderr.append(bytes); } catch (error) { stopReading(error); } };
const ready = new Promise((resolve, reject) => { readyResolve = resolve; rejectReady = reject; });
child.stdout.on('data', stdoutData); child.stderr.on('data', stderrData); child.once('error', stopReading);
const closed = linuxProviderOutputClosed(child, () => { if (failure) throw failure; });
async function bounded(promise, milliseconds) {
    try {
        return await Promise.race([promise, new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error('Owned fixture interruption timed out.')), milliseconds);
        })]);
    } finally { clearTimeout(timer); }
}
let report;
try {
    const target = await bounded(ready, 6000);
    assert.equal(target.parentPid, child.pid);
    const expected = { parentPid: child.pid, childPid: target.childPid, uid: process.getuid(), executable,
        argv: [executable, script, '--ntilde-terminfo-worker=' + runId], runId };
    const observed = readLinuxOwnedChildFacts(expected);
    assert.equal(observed.child.pty.descriptors[1].path, target.tty);
    assert.equal(child.kill('SIGTERM'), true); // Only our own spawned Python helper.
    const exit = await bounded(closed, 3000);
    if (exit.error) throw exit.error;
    assert.deepEqual(exit, { code: 143, signal: null }, stderr.text());
    assert.equal(stdout.text().endsWith('\n'), true);
    const frames = stdout.text().trimEnd().split('\n').map(line => JSON.parse(line));
    assert.equal(frames.length, 2);
    assert.deepEqual(frames[1], { cleanupReaped: target.childPid });
    assert.throws(() => process.kill(target.childPid, 0), error => error.code === 'ESRCH');
    report = { schemaVersion: 1, qualification: 'provider-foundation-only', terminalAppQualified: false,
        desktopQualified: false, linuxOwnedCollectionQualified: false, runId, helperSignal: 'SIGTERM',
        helperExit: exit, childWaitpidAcknowledged: true, childAbsentAfterHelperClose: true };
} finally {
    if (child.pid && child.exitCode === null && child.signalCode === null) {
        child.stdin.end('exit\n');
        const terminate = setTimeout(() => child.kill('SIGTERM'), 3000);
        const kill = setTimeout(() => child.kill('SIGKILL'), 6000);
        try { await closed; } finally { clearTimeout(terminate); clearTimeout(kill); }
    }
}
writeFileSync(process.argv[2], JSON.stringify(report, null, 2) + '\n');
console.log('Owned provider fixture SIGTERM handler reaped its PTY child before report publication.');
