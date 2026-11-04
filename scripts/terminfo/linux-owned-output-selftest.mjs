// Actual Linux private-PTY controls; still no App/desktop/collector qualification.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { existsSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { readLinuxProcessFacts, readLinuxPtyFacts } from './linux-process-provider.mjs';
import { createLinuxOutputDeadline, prepareLinuxOwnedOutput } from './linux-owned-output.mjs';
import { LinuxProviderOutput } from './fixtures/linux-provider-output.mjs';

if (process.platform !== 'linux' || process.argv.length !== 3) throw new Error('Expected actual Linux and one report path.');
const reportPath = process.argv[2];
writeFileSync(reportPath, ''); // A failed repeat cannot retain a successful report.
const executable = realpathSync(process.execPath), python = realpathSync('/usr/bin/python3');
const helper = fileURLToPath(new URL('./fixtures/linux-output-pty.py', import.meta.url));
const worker = fileURLToPath(new URL('./fixtures/linux-output-child.mjs', import.meta.url));
// Ubuntu26 installs GNU separately from its default uutils. Choose once, never after preparation/abort.
const utilityPath = existsSync('/usr/bin/gnustty') ? '/usr/bin/gnustty' : '/usr/bin/stty';
const stty = { path: utilityPath, sha256: createHash('sha256').update(readFileSync(utilityPath)).digest('hex') };
const phase = () => ({ deadlineNs: createLinuxOutputDeadline(4000) });
const ownBefore = readLinuxProcessFacts(process.pid);
const outcomes = [];
const delay = (ms, value) => { let timer; return { promise: new Promise(resolve => { timer = setTimeout(() => resolve(value), ms); }), cancel: () => clearTimeout(timer) }; };

async function fixture(action) {
    const runId = randomBytes(16).toString('hex');
    const argv = [python, helper, executable, worker, runId, '--ntilde-terminfo-owner=' + runId];
    const child = spawn(python, argv.slice(1), { stdio: ['pipe', 'pipe', 'pipe'],
        env: { PATH: '/usr/bin:/bin', LC_ALL: 'C', PYTHONDONTWRITEBYTECODE: '1' } });
    child.stdin.on('error', () => {});
    const output = new LinuxProviderOutput(), errors = new LinuxProviderOutput(), queue = [];
    let consumed = 0, count = 0, pending, failure, exited = false, closed = false;
    const fail = error => {
        failure ??= error; pending?.reject(failure); pending = undefined;
        child.stdout.removeListener('data', stdout); child.stderr.removeListener('data', stderr);
        child.stdout.resume(); child.stderr.resume();
    };
    const stdout = chunk => {
        try {
            output.append(chunk);
            const value = output.text();
            while (value.includes('\n', consumed)) {
                if (++count > 20) throw new Error('Output fixture frame bound exhausted.');
                const end = value.indexOf('\n', consumed), frame = JSON.parse(value.slice(consumed, end)); consumed = end + 1;
                if (pending) { pending.resolve(frame); pending = undefined; } else queue.push(frame);
            }
        } catch (error) { fail(error); }
    };
    const stderr = chunk => { try { errors.append(chunk); } catch (error) { fail(error); } };
    child.stdout.on('data', stdout); child.stderr.on('data', stderr);
    child.once('error', fail);
    child.once('exit', () => { exited = true; });
    const close = new Promise(resolve => child.once('close', (code, signal) => {
        closed = true;
        if (pending) fail(new Error('Output fixture closed during control read.'));
        resolve({ code, signal });
    }));
    async function next() {
        if (failure) throw failure;
        if (queue.length) return queue.shift();
        if (pending || closed) throw new Error('Output fixture control unavailable.');
        let timer;
        try {
            return await new Promise((resolve, reject) => {
                pending = { resolve, reject };
                timer = setTimeout(() => fail(new Error('Output fixture response deadline expired.')), 5000);
            });
        } finally { clearTimeout(timer); }
    }
    const command = async name => { child.stdin.write(name + '\n'); return next(); };
    let result;
    try {
        const target = await next();
        assert.equal(target.parentPid, child.pid);
        const parent = readLinuxProcessFacts(child.pid), currentWorker = readLinuxPtyFacts(target.childPid);
        assert.deepEqual(parent.argv, argv); assert.equal(parent.executable, python);
        assert.deepEqual(currentWorker.argv, [executable, worker, '--ntilde-terminfo-worker=' + runId]);
        const expected = { runId, uid: process.getuid(),
            app: { pid: parent.pid, ppid: parent.ppid, startTimeTicks: parent.startTimeTicks,
                executable: python, executableFile: parent.executableFile, argv },
            worker: { pid: currentWorker.pid, startTimeTicks: currentWorker.startTimeTicks,
                executable, executableFile: currentWorker.executableFile, argv: currentWorker.argv },
            pty: { path: target.tty, device: currentWorker.pty.descriptors[1].device } };
        result = await action({ runId, target, expected, command });
        const cleanup = await command('exit');
        assert.equal(cleanup.childReaped, target.childPid); assert.equal(cleanup.descriptorsClosed, true);
        assert.equal(cleanup.cleanup.childReaped, target.childPid); assert.equal(cleanup.cleanup.status, 0);
        assert.deepEqual(cleanup.cleanup.final, target.initial);
        const timeout = delay(5000, null);
        let status;
        try { status = await Promise.race([close, timeout.promise]); } finally { timeout.cancel(); }
        assert.deepEqual(status, { code: 0, signal: null });
        if (failure) throw failure;
        assert.equal(errors.text(), ''); assert.equal(consumed, output.text().length); assert.equal(queue.length, 0);
        return { ...result, runId, expected, cleanup };
    } finally {
        if (!closed) {
            if (!exited) child.stdin.end('exit\n');
            const terminate = setTimeout(() => { if (!exited) child.kill('SIGTERM'); }, 1000);
            const force = setTimeout(() => { if (!exited) child.kill('SIGKILL'); }, 4000);
            const ceiling = delay(6000, null);
            try {
                if (!(await Promise.race([close, ceiling.promise]))) throw new Error('Private fixture closure is unconfirmed.');
            } finally { clearTimeout(terminate); clearTimeout(force); ceiling.cancel(); }
        }
    }
}

const probe = async (command, runId, translated) => {
    const response = await command('probe');
    const expected = 'QA-' + runId + '\nEND-' + runId + '\n';
    assert.equal(response.hex, Buffer.from(translated ? expected.replaceAll('\n', '\r\n') : expected).toString('hex'));
    return response.hex;
};

outcomes.push(await fixture(async ({ runId, target, expected, command }) => {
    // Wrong identity and wrong executable digest are actual read-only admission refusals.
    await assert.rejects(prepareLinuxOwnedOutput({ ...expected, worker: { ...expected.worker,
        startTimeTicks: (BigInt(expected.worker.startTimeTicks) + 1n).toString() } }, stty, phase()));
    await assert.rejects(prepareLinuxOwnedOutput(expected, { ...stty, sha256: '0'.repeat(64) }, phase()));
    const unsupportedDefaultChecked = utilityPath === '/usr/bin/gnustty'
        && !['/usr/bin/stty', '/bin/stty', '/usr/bin/gnustty'].includes(realpathSync('/usr/bin/stty'));
    if (unsupportedDefaultChecked) {
        const defaultTool = { path: '/usr/bin/stty', sha256: createHash('sha256').update(readFileSync('/usr/bin/stty')).digest('hex') };
        await assert.rejects(prepareLinuxOwnedOutput(expected, defaultTool, phase()), 'The default uutils path must not silently qualify as GNU.');
    }
    assert.deepEqual((await command('snapshot')).snapshot, target.initial);
    const beforeHex = await probe(command, runId, true);
    const lease = await prepareLinuxOwnedOutput(expected, stty, phase());
    let restored = false;
    try {
        const prepared = (await command('snapshot')).snapshot;
        const want = structuredClone(target.initial); want.termios[1] &= ~1; // Linux OPOST=1, independently checked below by bytes.
        assert.deepEqual(prepared, want, 'Only OPOST may change; geometry and every other termios field must remain exact.');
        const preparedHex = await probe(command, runId, false);
        const observation = await lease.observe(phase());
        assert.equal(observation.latest.outputProcessing, false); assert.equal(observation.latest.rows, 13); assert.equal(observation.latest.cols, 37);
        const restoration = await lease.restore(phase()); restored = true;
        assert.equal(restoration.restored, true); assert.equal(restoration.aborted, false);
        assert.deepEqual((await command('snapshot')).snapshot, target.initial);
        const afterHex = await probe(command, runId, true);
        assert.strictEqual(await lease.restore({}), restoration); // Idempotence performs no fresh command or validation.
        return { scenario: 'prepare-observe-restore', original: target.initial, prepared,
            beforeHex, preparedHex, afterHex, unsupportedDefaultChecked, observation, restoration };
    } finally { if (!restored) await lease.restore(phase()).catch(() => {}); }
}));

outcomes.push(await fixture(async ({ runId, target, expected, command }) => {
    const lease = await prepareLinuxOwnedOutput(expected, stty, phase());
    try {
        assert.deepEqual(await command('mutate-argv'), { mutated: true });
        await assert.rejects(lease.observe(phase()));
        const refusal = await lease.restore(phase()).then(() => assert.fail('Aborted lease cannot complete.'), error => error);
        const resources = refusal.linuxOutputResources;
        assert.equal(resources.aborted, true); assert.equal(resources.utilityCloseConfirmed, true);
        assert.equal(resources.ptyCloseConfirmed, true); assert.equal(resources.toolCloseConfirmed, true);
        assert.equal(resources.latest.mode, resources.original.mode);
        assert.deepEqual((await command('snapshot')).snapshot, target.initial);
        const restoredHex = await probe(command, runId, true);
        assert.strictEqual(await lease.restore({}).catch(error => error), refusal);
        return { scenario: 'sticky-identity-abort-held-restore', original: target.initial, restoredHex, resources };
    } finally { await lease.restore(phase()).catch(() => {}); }
}));

const ownAfter = readLinuxProcessFacts(process.pid);
assert.equal(ownAfter.controllingDevice, ownBefore.controllingDevice, 'Coordinator acquired a controlling terminal.');
writeFileSync(reportPath, JSON.stringify({ schemaVersion: 1, platform: 'linux', qualification: 'owned-output-foundation-only',
    terminalAppQualified: false, desktopQualified: false, linuxOwnedCollectionQualified: false,
    coordinatorControllingDeviceUnchanged: true, utility: stty, outcomes }, null, 2) + '\n');
console.log('Linux held-PTY output foundation passed; App/desktop/owned collection remain unqualified.');
