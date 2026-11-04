import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { realpathSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { readLinuxProcessFacts, readLinuxOwnedChildFacts } from './linux-process-provider.mjs';
import { assertLinuxOwnedChild } from './linux-process-core.mjs';

if (process.platform !== 'linux') throw new Error('Provider self-test requires Linux, not a simulated proc root.');
if (process.argv.length !== 3) throw new Error('Expected a report path only.');
writeFileSync(process.argv[2], ''); // A failed repeat cannot retain a prior successful report.
const executable = realpathSync(process.execPath), runId = randomBytes(16).toString('hex');
const script = fileURLToPath(new URL('./fixtures/linux-provider-child.mjs', import.meta.url));
const helper = fileURLToPath(new URL('./fixtures/linux-provider-pty.py', import.meta.url));
const self = readLinuxProcessFacts(process.pid);
assert.equal(self.pid, process.pid); assert.deepEqual(self.uids, Array(4).fill(process.getuid()));
const child = spawn('/usr/bin/python3', [helper, executable, script, runId], { stdio: ['pipe', 'pipe', 'pipe'] });
child.stdin.on('error', () => {}); // Shutdown can race the explicitly spawned helper's own exit.
let output = '', errors = '', timer;
const exited = new Promise((resolve, reject) => {
    child.once('error', reject); child.once('exit', (code, signal) => resolve({ code, signal }));
});
exited.catch(() => {});
const ready = new Promise((resolve, reject) => {
    timer = setTimeout(() => reject(new Error('Provider fixture readiness timed out.')), 6000);
    child.once('error', reject);
    child.stdout.on('data', bytes => {
        output += bytes.toString('utf8');
        if (output.length > 4096) { reject(new Error('Oversized provider fixture metadata.')); return; }
        if (output.includes('\n')) {
            try { resolve(JSON.parse(output.trim())); } catch { reject(new Error('Invalid fixture metadata.')); }
        }
    });
    child.stderr.on('data', bytes => { if (errors.length < 4096) errors += bytes.toString('utf8'); });
    child.once('exit', () => reject(new Error('Provider fixture exited before readiness.')));
});
let report;
try {
    const target = await ready; clearTimeout(timer);
    assert.equal(target.parentPid, child.pid);
    assert.ok(Number.isSafeInteger(target.childPid) && target.childPid !== child.pid);
    const expected = { parentPid: child.pid, childPid: target.childPid, uid: process.getuid(), executable,
        argv: [executable, script, '--ntilde-terminfo-worker=' + runId], runId };
    const result = readLinuxOwnedChildFacts(expected);
    assert.equal(result.child.pty.descriptors[1].path, target.tty);
    for (const mutation of [{ uid: expected.uid + 1 }, { runId: '0'.repeat(32) }, { argv: expected.argv.slice(0, 2) }]) {
        assert.throws(() => assertLinuxOwnedChild(result.parent, result.child, { ...expected, ...mutation }));
    }
    child.stdin.end('exit\n');
    const exit = await Promise.race([exited, new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error('Provider fixture exit timed out.')), 6000);
    })]);
    clearTimeout(timer);
    assert.equal(exit.code, 0, errors); assert.equal(exit.signal, null);
    report = { schemaVersion: 1, platform: 'linux', qualification: 'provider-foundation-only',
        terminalAppQualified: false, desktopQualified: false, linuxOwnedCollectionQualified: false,
        checked: ['self UID/executable/start identity', 'own tagged child exact argv and parent relation',
            'private PTY stdin/stdout path/device/controlling-terminal equality', 'wrong UID/run/argv refusal', 'owned-child exit'],
        childStartTimeTicks: result.child.startTimeTicks, privatePty: result.child.pty,
        childExited: true, runId };
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
