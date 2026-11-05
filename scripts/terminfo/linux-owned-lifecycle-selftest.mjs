// Actual Linux private fixtures only. No App, desktop, collector or worker-reap qualification.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes, createHash } from 'node:crypto';
import { readFileSync, realpathSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { readLinuxProcessFacts, readLinuxPtyFacts, readLinuxOwnedChildFacts } from './linux-process-provider.mjs';
import { acquireLinuxLifecycle, createLinuxLifecycleDeadline } from './linux-owned-lifecycle.mjs';
import { acquireWithIo } from './linux-lifecycle/core.mjs';
import { openLifecycleChannel } from './linux-lifecycle/platform.mjs';

if (process.platform !== 'linux' || process.argv.length !== 3) throw new Error('Expected actual Linux and one explicit result path.');
const reportPath = process.argv[2]; writeFileSync(reportPath, '');
const python = realpathSync('/usr/bin/python3'), script = fileURLToPath(new URL('./linux-lifecycle/fixture.py', import.meta.url));
const pin = { path: '/usr/bin/python3', sha256: createHash('sha256').update(readFileSync(python)).digest('hex') };
const phase = () => ({ deadlineNs: createLinuxLifecycleDeadline(4000) });
const bounded = (promise, milliseconds, message) => {
    let timer; return Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), milliseconds); })]).finally(() => clearTimeout(timer));
};
function childProcess(args) {
    const child = spawn(python, ['-I', '-S', '-B', script, ...args], { stdio: ['pipe', 'pipe', 'pipe'],
        env: { PATH: '/usr/bin:/bin', LC_ALL: 'C' } });
    let data = '', diagnostics = '', offset = 0, pending, failure, exited = false, closed = false;
    const frames = [];
    const fail = error => { failure ??= error; if (pending) { pending.reject(failure); pending = undefined; } };
    child.stdin.on('error', fail); child.on('error', fail);
    child.stdout.on('data', chunk => {
        if (failure) return;
        if (chunk.length > 16384 - Buffer.byteLength(data)) { fail(new Error('Private fixture stdout cap exceeded.')); return; }
        data += new TextDecoder('utf-8', { fatal: true }).decode(chunk);
        while (data.includes('\n', offset)) {
            const end = data.indexOf('\n', offset);
            try {
                const frame = JSON.parse(data.slice(offset, end)); offset = end + 1;
                if (frames.length >= 32) throw new Error('Private fixture frame count exceeded.');
                frames.push(frame);
                if (pending) { const read = pending; pending = undefined; read.resolve(frame); }
            } catch (error) { fail(error); }
        }
    });
    child.stderr.on('data', chunk => {
        if (chunk.length > 4096 - Buffer.byteLength(diagnostics)) { fail(new Error('Private fixture stderr cap exceeded.')); return; }
        diagnostics += chunk.toString('utf8');
    });
    child.once('exit', () => { exited = true; });
    const close = new Promise(resolve => child.once('close', (code, signal) => { closed = true; if (pending) fail(new Error('Private fixture closed during response.')); resolve({ code, signal }); }));
    let consumed = 0;
    async function next() {
        if (failure) throw failure;
        if (consumed < frames.length) return frames[consumed++];
        if (pending || closed) throw new Error('Private fixture read unavailable.');
        const result = await bounded(new Promise((resolve, reject) => { pending = { resolve, reject }; }), 5000, 'Private fixture response expired.');
        consumed++; return result;
    }
    const finish = async () => {
        if (!closed && !exited && args[0] !== '--nonparent') child.stdin.end('close\n');
        let term, kill;
        try {
            term = setTimeout(() => { if (!exited) child.kill('SIGTERM'); }, 1000);
            kill = setTimeout(() => { if (!exited) child.kill('SIGKILL'); }, 5000);
            const status = await bounded(close, 6000, 'Private fixture close unconfirmed.');
            assert.deepEqual(status, { code: 0, signal: null }); assert.equal(diagnostics, '');
            if (failure) throw failure;
            return { status, frames, diagnostics, childProcessReaped: true };
        } finally { clearTimeout(term); clearTimeout(kill); }
    };
    return { child, next, command: async command => { child.stdin.write(command + '\n'); return next(); }, finish, close };
}
async function fixture(name, action) {
    const runId = randomBytes(16).toString('hex'), control = childProcess([runId, '--ntilde-terminfo-owner=' + runId]);
    let result, primary;
    try {
        const target = await control.next(), app = readLinuxProcessFacts(target.parentPid), worker = readLinuxPtyFacts(target.childPid);
        assert.equal(target.parentPid, control.child.pid);
        assert.deepEqual(app.argv, [python, '-I', '-S', '-B', script, runId, '--ntilde-terminfo-owner=' + runId]);
        assert.deepEqual(worker.argv, [python, script, '--worker', runId, '--ntilde-terminfo-worker=' + runId]);
        const expected = { runId, uid: process.getuid(),
            app: { pid: app.pid, ppid: app.ppid, startTimeTicks: app.startTimeTicks, executable: python, executableFile: app.executableFile, argv: app.argv },
            worker: { pid: worker.pid, startTimeTicks: worker.startTimeTicks, executable: python, executableFile: worker.executableFile, argv: worker.argv },
            pty: { path: target.tty, device: worker.pty.descriptors[1].device } };
        result = await action({ expected, control, target });
    } catch (error) { primary = error; }
    let cleanup;
    try { cleanup = await control.finish(); } catch (error) { if (primary) throw new AggregateError([primary, error], name + ' and fixture cleanup failed.'); throw error; }
    if (primary) throw primary;
    const final = cleanup.frames.at(-1); assert.equal(final.fixtureCleanup, true); assert.equal(final.childReaped, true);
    return { scenario: name, ...result, fixture: cleanup };
}

const outcomes = [], sentinel = childProcess(['--sentinel']);
const sentinelReady = await sentinel.next(); assert.equal(sentinelReady.sentinelReady, true);
let sentinelCleanup;
try {
outcomes.push(await fixture('normal-close-leaves-live-targets-unsignalled', async ({ expected, control }) => {
    await assert.rejects(acquireLinuxLifecycle({ ...expected, worker: { ...expected.worker, startTimeTicks: '0' } }, pin, { ...phase(), lifetimeMs: 20000 }));
    await assert.rejects(acquireLinuxLifecycle(expected, { ...pin, sha256: '0'.repeat(64) }, { ...phase(), lifetimeMs: 20000 }));
    const lease = await acquireLinuxLifecycle(expected, pin, { ...phase(), lifetimeMs: 20000 });
    const result = await lease.close(phase());
    assert.equal(result.observation.app.exitObserved, false); assert.equal(result.observation.worker.exitObserved, false);
    assert.deepEqual(result.observation.attempts, []); assert.equal(result.helper.helperCloseConfirmed, true);
    assert.deepEqual(await control.command('alive'), { appTerms: 0, workerTerms: 0 });
    return { result, liveTargetsUnsignalled: true };
}));
outcomes.push(await fixture('term-resistant-worker-original-pidfd-kill', async ({ expected, control }) => {
    assert.deepEqual(await control.command('resist'), { resistant: true });
    const lease = await acquireLinuxLifecycle(expected, pin, { ...phase(), lifetimeMs: 20000 });
    const worker = await lease.terminateWorker(phase());
    assert.equal(worker.observation.worker.exitObserved, true);
    assert.deepEqual(worker.observation.attempts.map(item => [item.role, item.signal]), [['worker', 'TERM'], ['worker', 'KILL']]);
    // Parent-owned wait evidence is recorded as an independent fixture control, not a primitive result.
    const parentReap = await control.command('reap'); assert.equal(parentReap.childReaped, true);
    const app = await lease.terminateApp(phase()); assert.equal(app.observation.app.exitObserved, true);
    const result = await lease.close(phase()); assert.equal(result.aborted, false);
    return { worker, parentReap, result };
}));
outcomes.push(await fixture('zombie-readable-not-reaped-nonparent-echild', async ({ expected, control, target }) => {
    const lease = await acquireLinuxLifecycle(expected, pin, { ...phase(), lifetimeMs: 20000 });
    const zombie = await control.command('zombie'); assert.equal(zombie.parentWaitWasNowait, true);
    const observation = await lease.observe(phase()); assert.equal(observation.observation.worker.exitObserved, true);
    assert.equal('workerReaped' in observation, false); assert.equal('complete' in observation, false);
    const witness = childProcess(['--nonparent', String(target.childPid)]);
    const nonparent = await witness.next(); assert.deepEqual(nonparent, { nonParentECHILD: true, workerReaped: false });
    const witnessClose = await witness.finish();
    const parentReap = await control.command('reap'); assert.equal(parentReap.childReaped, true);
    const result = await lease.close(phase());
    assert.equal(result.observation.app.exitObserved, false);
    return { zombie, observation, nonparent, witnessClose, parentReap, result };
}));
outcomes.push(await fixture('admitted-eof-aborts-original-targets', async ({ expected }) => {
    let channel;
    const lease = await acquireWithIo({ now: () => process.hrtime.bigint(), currentUid: () => process.getuid(),
        paired: (value, timeoutMs) => readLinuxOwnedChildFacts({ parentPid: value.app.pid, childPid: value.worker.pid,
            uid: value.uid, runId: value.runId, executable: value.worker.executable, argv: value.worker.argv }, { timeoutMs }),
        process: (pid, timeoutMs) => readLinuxProcessFacts(pid, { timeoutMs }),
        async open(...args) { channel = await openLifecycleChannel(...args); return channel; } }, expected, pin, { ...phase(), lifetimeMs: 20000 });
    // Internal test-only transport seam: deliberately end stdin without normal close/disarm.
    const refusal = await channel.interrupt({ deadlineNs: createLinuxLifecycleDeadline(4000), now: () => process.hrtime.bigint() }).catch(error => error);
    assert.ok(refusal instanceof Error); assert.equal(channel.resources().helperCloseConfirmed, true);
    return { helper: channel.resources(), admittedBeforeInterruption: lease.observation().admitted, interrupted: true };
}));
outcomes.push(await fixture('identity-drift-aborts-original-held-targets', async ({ expected, control }) => {
    const lease = await acquireLinuxLifecycle(expected, pin, { ...phase(), lifetimeMs: 20000 });
    assert.deepEqual(await control.command('drift'), { drifted: true });
    await assert.rejects(lease.observe(phase()));
    const error = await lease.abortCleanup(phase()).catch(error => error);
    assert.ok(error instanceof AggregateError); assert.equal(error.linuxLifecycle.aborted, true);
    assert.equal(error.linuxLifecycle.observation.app.exitObserved, true);
    assert.equal(error.linuxLifecycle.observation.worker.exitObserved, true);
    assert.equal(error.linuxLifecycle.helper.helperCloseConfirmed, true);
    return { aborted: error.linuxLifecycle };
}));
const sentinelAlive = await sentinel.command('alive');
assert.deepEqual(sentinelAlive, { sentinelAlive: true, pid: sentinelReady.pid });
} finally { sentinelCleanup = await sentinel.finish(); }

writeFileSync(reportPath, JSON.stringify({ schemaVersion: 1, platform: 'linux', qualification: 'pidfd-lifecycle-foundation-only',
    terminalAppQualified: false, desktopQualified: false, linuxOwnedCollectionQualified: false, python: pin,
    sentinel: { ready: sentinelReady, cleanup: sentinelCleanup, survivedAllTargetSignals: true }, outcomes }, null, 2) + '\n');
console.log('Linux original-pidfd lifecycle controls passed; exit observation is not worker reaping.');
