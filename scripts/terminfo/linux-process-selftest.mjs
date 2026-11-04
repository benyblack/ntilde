import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { lstatSync, realpathSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { readLinuxProcessFacts, readLinuxOwnedChildFacts } from './linux-process-provider.mjs';
import { assertLinuxOwnedChild } from './linux-process-core.mjs';
import { LinuxProviderOutput } from './fixtures/linux-provider-output.mjs';

if (process.platform !== 'linux') throw new Error('Provider self-test requires Linux, not a simulated proc root.');
if (process.argv.length !== 3) throw new Error('Expected a report path only.');
writeFileSync(process.argv[2], ''); // A failed repeat cannot retain a prior successful report.
writeFileSync(process.argv[2] + '.activity.json', '');
const executable = realpathSync(process.execPath), runId = randomBytes(16).toString('hex');
const script = fileURLToPath(new URL('./fixtures/linux-provider-child.mjs', import.meta.url));
const helper = fileURLToPath(new URL('./fixtures/linux-provider-pty.py', import.meta.url));
const self = readLinuxProcessFacts(process.pid);
assert.equal(self.pid, process.pid); assert.deepEqual(self.uids, Array(4).fill(process.getuid()));
const child = spawn('/usr/bin/python3', [helper, executable, script, runId], { stdio: ['pipe', 'pipe', 'pipe'] });
child.stdin.on('error', () => {}); // Shutdown can race the explicitly spawned helper's own exit.
const stdout = new LinuxProviderOutput(), stderr = new LinuxProviderOutput(), messages = [];
let processed = 0, messageCount = 0, pending, outputFailure, timer;
const failOutput = error => {
    outputFailure ??= error;
    pending?.reject(outputFailure); pending = undefined;
    child.stdout.removeListener('data', stdoutData); child.stderr.removeListener('data', stderrData);
    child.stdout.resume(); child.stderr.resume(); // Discard later chunks without retaining or blocking the owned child.
};
const stdoutData = bytes => {
    try {
        stdout.append(bytes);
        const text = stdout.text();
        while (text.indexOf('\n', processed) >= 0) {
            const end = text.indexOf('\n', processed);
            if (++messageCount > 2) throw new Error('Unexpected extra provider fixture message.');
            const message = JSON.parse(text.slice(processed, end)); processed = end + 1;
            if (pending) { pending.resolve(message); pending = undefined; } else messages.push(message);
        }
    } catch (error) { failOutput(error); }
};
const stderrData = bytes => { try { stderr.append(bytes); } catch (error) { failOutput(error); } };
child.stdout.on('data', stdoutData); child.stderr.on('data', stderrData);
child.once('error', failOutput);
child.once('exit', () => { if (pending) failOutput(new Error('Provider fixture exited while awaiting metadata.')); });
const exited = new Promise((resolve, reject) => {
    child.once('error', reject); child.once('exit', (code, signal) => resolve({ code, signal }));
});
exited.catch(() => {});
async function nextMessage() {
    if (outputFailure) throw outputFailure;
    if (messages.length) return messages.shift();
    if (pending) throw new Error('Overlapping provider fixture read.');
    try {
        return await new Promise((resolve, reject) => {
            pending = { resolve, reject };
            timer = setTimeout(() => failOutput(new Error('Provider fixture response timed out.')), 6000);
        });
    } finally { clearTimeout(timer); }
}
const stableDevice = stat => Object.fromEntries(['dev', 'ino', 'rdev', 'uid', 'mode'].map(key => [key, stat[key].toString()]));
let report;
try {
    const target = await nextMessage();
    assert.equal(target.parentPid, child.pid);
    assert.ok(Number.isSafeInteger(target.childPid) && target.childPid !== child.pid);
    const expected = { parentPid: child.pid, childPid: target.childPid, uid: process.getuid(), executable,
        argv: [executable, script, '--ntilde-terminfo-worker=' + runId], runId };
    const result = readLinuxOwnedChildFacts(expected);
    assert.equal(result.child.pty.descriptors[1].path, target.tty);
    for (const mutation of [{ uid: expected.uid + 1 }, { runId: '0'.repeat(32) }, { argv: expected.argv.slice(0, 2) }]) {
        assert.throws(() => assertLinuxOwnedChild(result.parent, result.child, { ...expected, ...mutation }));
    }
    const before = lstatSync(target.tty, { bigint: true });
    // tty_update_time masks the low three seconds bits. Cross its actual eight-second bucket.
    // This bounded controlled idle interval fits the existing fixture deadlines; no ceiling is raised.
    const nextBucketMs = ((before.mtimeNs / 1000000000n) / 8n + 1n) * 8000n;
    const delayMs = Math.max(0, Number(nextBucketMs - BigInt(Date.now())) + 100);
    if (delayMs > 8100) throw new Error('Private PTY timestamp is outside the expected clock bucket.');
    await new Promise(resolve => setTimeout(resolve, delayMs));
    child.stdin.write('activity\n');
    assert.deepEqual(await nextMessage(), { activityComplete: true });
    const after = lstatSync(target.tty, { bigint: true });
    const afterResult = readLinuxOwnedChildFacts(expected);
    const activity = { qualification: 'provider-foundation-only', runId, terminalAppQualified: false,
        beforeMtimeNs: before.mtimeNs.toString(), afterMtimeNs: after.mtimeNs.toString(),
        mtimeChanged: before.mtimeNs !== after.mtimeNs,
        stableDeviceFactsSame: JSON.stringify(stableDevice(before)) === JSON.stringify(stableDevice(after)),
        publicDeviceProjectionSame: JSON.stringify(result.child.pty) === JSON.stringify(afterResult.child.pty),
        executableIdentitySame: JSON.stringify(result.child.executableFile) === JSON.stringify(afterResult.child.executableFile) };
    writeFileSync(process.argv[2] + '.activity.json', JSON.stringify(activity, null, 2) + '\n');
    assert.equal(activity.mtimeChanged, true, 'Controlled private PTY traffic did not change its timestamp.');
    assert.equal(activity.stableDeviceFactsSame, true);
    assert.equal(activity.publicDeviceProjectionSame, true, 'Legitimate private PTY traffic changed provider device identity.');
    assert.equal(activity.executableIdentitySame, true);
    if (outputFailure) throw outputFailure;
    child.stdin.end('exit\n');
    const exit = await Promise.race([exited, new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error('Provider fixture exit timed out.')), 6000);
    })]);
    clearTimeout(timer);
    assert.equal(exit.code, 0, stderr.text()); assert.equal(exit.signal, null);
    if (outputFailure) throw outputFailure;
    report = { schemaVersion: 1, platform: 'linux', qualification: 'provider-foundation-only',
        terminalAppQualified: false, desktopQualified: false, linuxOwnedCollectionQualified: false,
        checked: ['self UID/executable/start identity', 'own tagged child exact argv and parent relation',
            'private PTY stdin/stdout path/device/controlling-terminal equality', 'wrong UID/run/argv refusal',
            'legitimate PTY activity preserves stable device identity', 'owned-child exit'],
        childStartTimeTicks: result.child.startTimeTicks, privatePty: result.child.pty,
        activity, childExited: true, runId };
} finally {
    clearTimeout(timer);
    if (child.pid && child.exitCode === null && child.signalCode === null) {
        child.stdin.end('exit\n');
        const timeout = setTimeout(() => child.kill('SIGTERM'), 5000);
        const escalation = setTimeout(() => child.kill('SIGKILL'), 6000);
        try { await exited; } finally { clearTimeout(timeout); clearTimeout(escalation); }
    }
}
writeFileSync(process.argv[2], JSON.stringify(report, null, 2) + '\n');
console.log('Linux provider foundation self-test passed; App/desktop/owned collection remain unqualified.');
