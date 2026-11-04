// Own tagged Unix servers only. No desktop/App/personal socket qualification.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes, createHash } from 'node:crypto';
import { chmodSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readLinuxProcessFacts } from './linux-process-provider.mjs';
import { createLinuxEndpointDeadline, observeLinuxPrivateAgentEndpoint } from './linux-owned-endpoint.mjs';
import { LinuxProviderOutput } from './fixtures/linux-provider-output.mjs';

if (process.platform !== 'linux' || process.argv.length !== 3) throw new Error('Expected actual Linux and one report path.');
const output = process.argv[2]; writeFileSync(output, '');
const python = realpathSync('/usr/bin/python3'), script = fileURLToPath(new URL('./fixtures/linux-endpoint-server.py', import.meta.url));
const pythonPin = { path: '/usr/bin/python3', sha256: createHash('sha256').update(readFileSync(python)).digest('hex') };
const phase = () => ({ deadlineNs: createLinuxEndpointDeadline(4000) });

function fixtureEntryMode(appData, name, mode) {
    assert.ok(['agent-endpoint.json', 'agent.sock'].includes(name));
    const directory = lstatSync(appData), entry = lstatSync(join(appData, name));
    assert.equal(directory.uid, process.getuid()); assert.equal(directory.mode & 0o7777, 0o700); assert.equal(directory.isDirectory(), true);
    assert.equal(entry.uid, process.getuid()); assert.equal(entry.isSymbolicLink(), false);
    assert.equal(name === 'agent.sock' ? entry.isSocket() : entry.isFile(), true);
    chmodSync(join(appData, name), mode); // Only entries in this coordinator's new private fixture; never a caller path.
}

async function withServer(action) {
    const root = mkdtempSync(join(tmpdir(), 'ntiq-ep-')); chmodSync(root, 0o700);
    const appData = join(root, 'appdata'); mkdirSync(appData, { mode: 0o700 });
    const runId = randomBytes(16).toString('hex'), argv = [python, '-I', '-S', '-B', script, appData, runId, '--ntilde-terminfo-owner=' + runId];
    const child = spawn(python, argv.slice(1), { stdio: ['pipe', 'pipe', 'pipe'], env: { LC_ALL: 'C', PATH: '/usr/bin:/bin' } });
    const stdout = new LinuxProviderOutput(), stderr = new LinuxProviderOutput(), queue = [];
    let consumed = 0, frames = 0, pending, failure, exited = false, closed = false;
    const fail = error => {
        failure ??= error; pending?.reject(failure); pending = undefined;
        child.stdout.removeListener('data', onStdout); child.stderr.removeListener('data', onStderr);
        child.stdout.resume(); child.stderr.resume();
    };
    const onStdout = bytes => {
        try {
            stdout.append(bytes); const text = stdout.text();
            while (text.includes('\n', consumed)) {
                if (++frames > 20) throw new Error('Private server frame limit exceeded.');
                const end = text.indexOf('\n', consumed), frame = JSON.parse(text.slice(consumed, end)); consumed = end + 1;
                if (pending) { pending.resolve(frame); pending = undefined; } else queue.push(frame);
            }
        } catch (error) { fail(error); }
    };
    const onStderr = bytes => { try { stderr.append(bytes); } catch (error) { fail(error); } };
    child.stdout.on('data', onStdout); child.stderr.on('data', onStderr); child.stdin.on('error', () => {});
    child.once('error', fail); child.once('exit', () => { exited = true; });
    const closure = new Promise(resolve => child.once('close', (code, signal) => {
        closed = true;
        if (pending) fail(new Error('Private server closed during a control read.'));
        resolve({ code, signal });
    }));
    async function next() {
        if (failure) throw failure;
        if (queue.length) return queue.shift();
        if (closed || pending) throw new Error('Private server control unavailable.');
        let timer;
        try { return await new Promise((resolve, reject) => {
            pending = { resolve, reject }; timer = setTimeout(() => fail(new Error('Private server control deadline expired.')), 5000);
        }); } finally { clearTimeout(timer); }
    }
    const command = async value => { child.stdin.write(JSON.stringify(value) + '\n'); return next(); };
    let primary, result, final;
    try {
        assert.deepEqual(await next(), { ready: true, pid: child.pid });
        fixtureEntryMode(appData, 'agent-endpoint.json', 0o644);
        const facts = readLinuxProcessFacts(child.pid); assert.deepEqual(facts.argv, argv); assert.equal(facts.ppid, process.pid);
        const stat = lstatSync(appData, { bigint: true });
        const expected = { runId, uid: process.getuid(), appDataDirectory: { path: appData,
            device: Object.fromEntries(['dev', 'ino', 'uid', 'mode'].map(key => [key, stat[key].toString()])) },
            app: { pid: child.pid, ppid: process.pid, startTimeTicks: facts.startTimeTicks, executable: python,
                executableFile: facts.executableFile, argv }, python: pythonPin };
        result = await action({ expected, appData, command });
        final = await command({ kind: 'exit' }); assert.equal(final.exiting, true); assert.equal(final.payloadBytes, 0);
        let timer;
        try {
            assert.deepEqual(await Promise.race([closure, new Promise(resolve => { timer = setTimeout(() => resolve(null), 5000); })]), { code: 0, signal: null });
        } finally { clearTimeout(timer); }
        if (failure) throw failure;
        assert.equal(stderr.text(), ''); assert.equal(consumed, stdout.text().length); assert.equal(queue.length, 0);
        assert.equal(existsSync(join(appData, 'agent.sock')), false); assert.equal(existsSync(join(appData, 'agent-endpoint.json')), false);
    } catch (error) { primary = error; }
    const errors = primary ? [primary] : [];
    if (!closed) {
        if (!exited) child.stdin.end(JSON.stringify({ kind: 'exit' }) + '\n');
        const term = setTimeout(() => { if (!exited) child.kill('SIGTERM'); }, 1000);
        const kill = setTimeout(() => { if (!exited) child.kill('SIGKILL'); }, 4000);
        let timer;
        try {
            if (!(await Promise.race([closure, new Promise(resolve => { timer = setTimeout(() => resolve(null), 6000); })]))) errors.push(new Error('Owned server closure unconfirmed.'));
        } catch (error) { errors.push(error); }
        finally { clearTimeout(term); clearTimeout(kill); clearTimeout(timer); }
    }
    if (errors.length) {
        const error = new AggregateError(errors, 'Private server foundation control failed.');
        Object.defineProperty(error, 'retainedFixture', { value: root }); throw error;
    }
    rmSync(root, { recursive: true });
    return { ...result, server: { pid: child.pid, reaped: true, ...final }, fixtureRemoved: !existsSync(root) };
}

const scenarios = [];
scenarios.push(await withServer(async ({ expected, appData, command }) => {
    const originalDescriptor = readFileSync(join(appData, 'agent-endpoint.json'));
    await assert.rejects(observeLinuxPrivateAgentEndpoint({ ...expected, python: { ...pythonPin, sha256: '0'.repeat(64) } }, phase()));
    assert.deepEqual(await command({ kind: 'verify', connections: 0 }), { accepted: 0, closed: 0, payloadBytes: 0 });
    const observation = await observeLinuxPrivateAgentEndpoint(expected, phase());
    assert.equal(observation.probe.socket.peer.pid, expected.app.pid); assert.equal(observation.probe.socket.peer.uid, process.getuid());
    assert.equal(observation.authenticatesSubsequentConnections, false); assert.equal(observation.helperClosed, true);
    assert.equal(observation.probe.descriptor.file.mode, String(0o100644));
    assert.notEqual(observation.probe.socket.named.ino, observation.probe.socket.kernel.ino);
    assert.deepEqual(readFileSync(join(appData, 'agent-endpoint.json')), originalDescriptor);
    const count = await command({ kind: 'verify', connections: 1 }); assert.deepEqual(count, { accepted: 1, closed: 1, payloadBytes: 0 });
    return { scenario: 'probe-only-0644-and-distinct-inodes', observation, count };
}));

scenarios.push(...await Promise.all(['socket-mode', 'descriptor-symlink', 'close-listener', 'socket-file', 'directory-mode'].map(fault =>
    withServer(async ({ expected, appData, command }) => {
        if (fault === 'directory-mode') chmodSync(appData, 0o755);
        else if (fault === 'socket-mode') fixtureEntryMode(appData, 'agent.sock', 0o666);
        else assert.deepEqual(await command({ kind: fault }), { changed: fault });
        const error = await observeLinuxPrivateAgentEndpoint(expected, phase()).then(() => assert.fail('Fault must refuse.'), error_ => error_);
        assert.ok(error instanceof AggregateError);
        if (fault === 'directory-mode') chmodSync(appData, 0o700);
        const count = await command({ kind: 'verify', connections: 0 }); assert.equal(count.payloadBytes, 0);
        return { scenario: fault, refused: true, resources: error.linuxEndpointResources, count };
    }))));

scenarios.push(await withServer(async a => {
    const other = await withServer(async b => {
        assert.deepEqual(await b.command({ kind: 'claim', pid: a.expected.app.pid }), { changed: 'claim' });
        const error = await observeLinuxPrivateAgentEndpoint({ ...b.expected, runId: a.expected.runId, app: a.expected.app }, phase()).then(() => assert.fail(), error_ => error_);
        assert.ok(error instanceof AggregateError);
        assert.equal(error.linuxEndpointResources.probe.socket.peer.pid, b.expected.app.pid);
        assert.equal(error.linuxEndpointResources.probe.failures[0].code, 'credentials');
        const count = await b.command({ kind: 'verify', connections: 1 }); assert.deepEqual(count, { accepted: 1, closed: 1, payloadBytes: 0 });
        return { scenario: 'different-owned-peer-despite-matching-descriptor', refused: true, resources: error.linuxEndpointResources, count };
    });
    assert.deepEqual(await a.command({ kind: 'verify', connections: 0 }), { accepted: 0, closed: 0, payloadBytes: 0 });
    return { scenario: 'other-peer-control', other };
}));

writeFileSync(output, JSON.stringify({ schemaVersion: 1, qualification: 'private-endpoint-foundation-only',
    platform: 'linux', terminalAppQualified: false, desktopQualified: false, linuxOwnedCollectionQualified: false,
    authenticatesSubsequentConnections: false, scenarios }, null, 2) + '\n');
console.log('Linux probe-only endpoint foundation passed; no App/desktop/collection qualification.');
