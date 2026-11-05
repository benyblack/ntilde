import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { acquireWithIo, conservativeDeadline, createLinuxLifecycleDeadline, copyPythonPin, parseObservation } from './linux-lifecycle/core.mjs';
import { helperSha256 } from './linux-lifecycle/platform.mjs';
import { acquireLinuxLifecycle } from './linux-owned-lifecycle.mjs';

const runId = 'a'.repeat(32);
const file = ino => ({ dev: '3', ino: String(ino), rdev: '0', uid: '1000', mode: String(0o100755), size: '100', mtimeNs: '123', ctimeNs: '124' });
const device = () => ({ dev: '4', ino: '20', rdev: '34817', uid: '1000', mode: String(0o20620) });
const input = () => ({ runId, uid: 1000,
    app: { pid: 40, ppid: 38, startTimeTicks: '9007199254740993', executable: '/opt/app', executableFile: file(10), argv: ['/opt/app', '--ntilde-terminfo-owner=' + runId] },
    worker: { pid: 42, startTimeTicks: '9007199254740995', executable: '/usr/bin/node', executableFile: file(11), argv: ['/usr/bin/node', '/private/worker.mjs', '--ntilde-terminfo-worker=' + runId] },
    pty: { path: '/dev/pts/1', device: device() } });
const pin = () => ({ path: '/usr/bin/python3', sha256: 'b'.repeat(64) });
const proc = (value, ppid) => ({ platform: 'linux', ...structuredClone(value), ppid, uids: [1000, 1000, 1000, 1000] });
function synthetic() {
    const expected = input();
    const s = { time: 1000000000n, events: [], alive: { app: true, worker: true }, attempts: [], closed: false, hook: null,
        facts: { qualification: 'provider-foundation-only', parent: proc(expected.app, 38),
            child: { ...proc(expected.worker, 40), pty: { selectedFd: 1,
                descriptors: [0, 1].map(fd => ({ fd, path: '/dev/pts/1', device: device() })) } } } };
    const observation = () => ({ app: { exitObserved: !s.alive.app, pollEvents: s.alive.app ? 0 : 1 },
        worker: { exitObserved: !s.alive.worker, pollEvents: s.alive.worker ? 0 : 1 }, attempts: [...s.attempts], pidfdsClosed: s.closed });
    const note = async (name, detail) => { s.events.push([name, detail]); await s.hook?.(name, detail); };
    const channel = { resources: () => ({ helperCloseConfirmed: s.closed }),
        async request(value) {
            await note(value.op, value);
            if (value.op === 'clock') return { clockNs: String(s.time + 10000n), observation: observation() };
            if (value.op === 'terminateWorker' || value.op === 'terminateApp' || value.op === 'abort') {
                for (const role of value.op === 'abort' ? ['worker', 'app'] : [value.op === 'terminateWorker' ? 'worker' : 'app']) {
                    if (s.alive[role]) { s.attempts.push({ role, signal: 'TERM', sent: true, errno: null }); s.alive[role] = false; }
                }
            }
            if (value.op === 'close' || value.op === 'abort') s.closed = true;
            return { observation: observation() };
        }, async finish() { await note('finish'); s.closed = true; } };
    s.io = { now: () => s.time, currentUid: () => 1000,
        paired() { s.events.push(['paired']); s.onFacts?.(); return s.facts; },
        process(pid) { s.events.push(['process', pid]); return pid === 40 ? s.facts.parent : s.facts.child; },
        async open() { await note('open'); return channel; } };
    s.phase = () => ({ deadlineNs: createLinuxLifecycleDeadline(4000, s.io.now) });
    s.acquire = (value = expected, tool = pin(), options = { ...s.phase(), lifetimeMs: 20000 }) => acquireWithIo(s.io, value, tool, options);
    return s;
}

test('retained helper source pin matches exact checked-in bytes', () => {
    assert.equal(createHash('sha256').update(readFileSync(new URL('./linux-lifecycle/pidfd-helper.py', import.meta.url))).digest('hex'), helperSha256);
});
test('unsupported hosts fail before resource/provider access', () => {
    if (process.platform !== 'linux') assert.throws(() => acquireLinuxLifecycle({}, {}, {}), /actual Linux/);
});
for (const [name, change] of [
    ['UID', x => x.uid++], ['birth precision', x => x.worker.startTimeTicks = 9007199254740995],
    ['marker', x => x.worker.argv.pop()], ['same PID', x => x.worker.pid = 40],
    ['TTY', x => x.pty.path = '/dev/tty'], ['world write', x => x.pty.device.mode = String(0o20622)],
    ['extra selector', x => x.signal = 'USR1'], ['getter', x => Object.defineProperty(x, 'runId', { get() { throw new Error('getter called'); }, enumerable: true })],
]) test('input refusal before side effects: ' + name, async () => {
    const s = synthetic(), value = input(); change(value); await assert.rejects(s.acquire(value)); assert.deepEqual(s.events, []);
});
for (const bad of ['/tmp/python3', 'python3', '/usr/bin/python3/../python', null, { toString() { assert.fail('coercion'); } }]) {
    test('no arbitrary interpreter: ' + typeof bad + String(bad === null), () => assert.throws(() => copyPythonPin({ ...pin(), path: bad })));
}
for (const [name, change] of [
    ['App birth', f => f.parent.startTimeTicks = '10'], ['worker birth', f => f.child.startTimeTicks = '10'],
    ['App file', f => f.parent.executableFile.ino = '99'], ['worker argv', f => f.child.argv.push('extra')],
    ['UID quartet', f => f.child.uids[2]++], ['App parent', f => f.parent.ppid++],
    ['worker parent', f => f.child.ppid++], ['PTY', f => f.child.pty.descriptors[0].device.ino = '99'],
]) test('provider mismatch never admits or signals: ' + name, async () => {
    const s = synthetic(); change(s.facts); await assert.rejects(s.acquire());
    assert.equal(s.events.some(([name]) => name === 'admit'), false); assert.deepEqual(s.attempts, []); assert.equal(s.closed, true);
});
test('death across bracket refuses admission without re-acquisition', async () => {
    const s = synthetic(); s.onFacts = () => { s.alive.worker = false; };
    await assert.rejects(s.acquire()); assert.equal(s.events.filter(([n]) => n === 'open').length, 1); assert.deepEqual(s.attempts, []);
});
test('normal close disarms without target signals; repeats exact original result', async () => {
    const s = synthetic(), lease = await s.acquire(), result = await lease.close(s.phase());
    assert.deepEqual(s.alive, { app: true, worker: true }); assert.deepEqual(s.attempts, []);
    assert.equal(result.observation.pidfdsClosed, true); assert.equal(result.aborted, false);
    assert.equal('workerReaped' in result, false); assert.equal('complete' in result, false); assert.equal('exitStatus' in result, false);
    const count = s.events.length; assert.strictEqual(await lease.close({}), result); assert.equal(s.events.length, count);
    assert.ok(s.events.findIndex(([n]) => n === 'close') < s.events.findIndex(([n]) => n === 'finish'));
});
test('worker then App fixed operations observe exit, not reap', async () => {
    const s = synthetic(), lease = await s.acquire();
    await assert.rejects(lease.terminateApp(s.phase()), /Worker exit/);
    await assert.rejects(lease.abortCleanup(s.phase())); // Early invalid order is sticky, not a permission extension.
    const t = synthetic(), active = await t.acquire();
    const worker = await active.terminateWorker(t.phase()); assert.equal(worker.observation.worker.exitObserved, true);
    assert.equal(worker.observation.app.exitObserved, false);
    const app = await active.terminateApp(t.phase()); assert.equal(app.observation.app.exitObserved, true);
    await active.terminateApp(t.phase()); // Previously submitted original target needs no duplicate signal.
    assert.deepEqual(t.attempts.map(x => [x.role, x.signal]), [['worker', 'TERM'], ['app', 'TERM']]);
    const closed = await active.close(t.phase()); assert.equal(closed.aborted, false);
    assert.equal(t.events.filter(([n]) => n === 'open').length, 1);
});
test('App exit before fresh terminateApp observation is unexpected and cannot arm termination', async () => {
    const s = synthetic(), lease = await s.acquire(); await lease.terminateWorker(s.phase());
    s.alive.app = false;
    await assert.rejects(lease.terminateApp(s.phase()), /Original App exited/);
    assert.equal(s.events.some(([name]) => name === 'terminateApp'), false);
    assert.equal(s.attempts.some(item => item.role === 'app'), false);
    await assert.rejects(lease.close(s.phase()));
});
test('App exit at dispatch without successful signal cannot become expected termination', async () => {
    const s = synthetic(), lease = await s.acquire(); await lease.terminateWorker(s.phase());
    s.hook = name => { if (name === 'terminateApp') s.alive.app = false; };
    await assert.rejects(lease.terminateApp(s.phase()), /without a new successful/);
    assert.equal(s.attempts.some(item => item.role === 'app'), false);
    assert.equal(lease.observation().aborted, true);
    s.hook = null; await assert.rejects(lease.close(s.phase()));
});
test('fatal identity drift cleanup signals only retained original roles and remains aborted', async () => {
    const s = synthetic(), lease = await s.acquire(); s.facts.child.argv.push('drift');
    await assert.rejects(lease.observe(s.phase()), /identity changed/);
    const error = await lease.abortCleanup(s.phase()).catch(error => error);
    assert.equal(error.linuxLifecycle.aborted, true); assert.equal(error.linuxLifecycle.observation.pidfdsClosed, true);
    assert.deepEqual(s.attempts.map(x => x.role), ['worker', 'app']);
    assert.deepEqual(error.linuxLifecycle.acceptedFacts.child.argv, input().worker.argv);
    assert.strictEqual(await lease.abortCleanup({}).catch(error => error), error);
});
test('close of aborted identity lease still leaves live targets unsignalled', async () => {
    const s = synthetic(), lease = await s.acquire(); s.facts.parent.argv.push('drift');
    await assert.rejects(lease.observe(s.phase())); await assert.rejects(lease.close(s.phase()));
    assert.deepEqual(s.attempts, []); assert.deepEqual(s.alive, { app: true, worker: true });
});
test('expired phase cannot enqueue any clock or signal', async () => {
    const s = synthetic(), lease = await s.acquire(), count = s.events.length;
    await assert.rejects(lease.terminateWorker({ deadlineNs: s.time })); assert.equal(s.events.length, count);
    await assert.rejects(lease.close(s.phase())); assert.deepEqual(s.attempts, []);
});
test('conservative helper clock transfer subtracts elapsed transport/fact time and close reserve', () => {
    assert.equal(conservativeDeadline('100000000000', 8000000000n, 10000000000n), '101750000000');
    assert.throws(() => conservativeDeadline('100', 9000000000n, 9000000001n));
    assert.throws(() => conservativeDeadline('18446744073709551616', 0n, 1000000000n));
});
test('ownership check that burns the entire ceiling cannot enqueue later action', async () => {
    const s = synthetic(), lease = await s.acquire(); s.onFacts = () => { s.time += 4000000001n; };
    const before = s.events.filter(([n]) => n === 'terminateWorker').length;
    await assert.rejects(lease.terminateWorker(s.phase())); assert.equal(s.events.filter(([n]) => n === 'terminateWorker').length, before);
    s.onFacts = null; await assert.rejects(lease.close(s.phase()));
});
test('overlap abort cannot allow the outstanding operation to return clean', async () => {
    const s = synthetic(), lease = await s.acquire(); let release, entered;
    const signal = new Promise(resolve => { entered = resolve; });
    s.hook = async op => { if (op === 'observe') { entered(); await new Promise(resolve => { release = resolve; }); } };
    const first = lease.observe(s.phase()); await signal;
    await assert.rejects(lease.observe(s.phase()), /Overlapping/); release(); await assert.rejects(first, /Overlapping/);
    s.hook = null; await assert.rejects(lease.close(s.phase()));
});
test('primary identity error and later helper close failure are both retained', async () => {
    const s = synthetic(), lease = await s.acquire(); s.facts.child.argv.pop(); await assert.rejects(lease.observe(s.phase()));
    s.hook = op => { if (op === 'finish') throw new Error('close failure'); };
    const error = await lease.close(s.phase()).catch(error => error);
    assert.equal(error.errors.length, 2); assert.match(error.errors[0].message, /identity/); assert.match(error.errors[1].message, /close failure/);
});
test('failed close command still closes helper transport/resources and keeps failure', async () => {
    const s = synthetic(), lease = await s.acquire();
    s.hook = op => { if (op === 'close') throw new Error('close command failure'); };
    const error = await lease.close(s.phase()).catch(error => error);
    assert.match(error.errors[0].message, /close command failure/);
    assert.equal(s.events.at(-1)[0], 'finish'); assert.equal(s.closed, true); assert.deepEqual(s.attempts, []);
});
test('invalid pidfd reports cannot fabricate fixed signal actions or reap fields', () => {
    const good = { app: { exitObserved: false, pollEvents: 0 }, worker: { exitObserved: true, pollEvents: 1 }, attempts: [], pidfdsClosed: false };
    for (const change of [x => x.workerReaped = true, x => x.app.pollEvents = -1,
        x => x.attempts.push({ role: 'other', signal: 'TERM', sent: true, errno: null }),
        x => x.attempts.push({ role: 'app', signal: 'USR1', sent: true, errno: null })]) {
        const value = structuredClone(good); change(value); assert.throws(() => parseObservation(value));
    }
});
