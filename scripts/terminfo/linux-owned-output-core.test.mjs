import test from 'node:test';
import assert from 'node:assert/strict';
import { copyLinuxOutputExpected, copyLinuxOutputTool, linuxOutputDeadline, linuxOutputBudget,
    parseLinuxOutputMode, parseLinuxOutputSize, parseLinuxOutputProcessing, parseLinuxSttyVersion,
    prepareLinuxOutputWithIo } from './linux-owned-output-core.mjs';
import { prepareLinuxOwnedOutput } from './linux-owned-output.mjs';

const runId = 'a'.repeat(32);
const file = ino => ({ dev: '3', ino: String(ino), rdev: '0', uid: '1000', mode: String(0o100755), size: '100', mtimeNs: '123', ctimeNs: '124' });
const device = () => ({ dev: '4', ino: '20', rdev: '34817', uid: '1000', mode: String(0o20620) });
const input = () => ({ runId, uid: 1000,
    app: { pid: 40, ppid: 38, startTimeTicks: '9007199254740993', executable: '/opt/app', executableFile: file(10), argv: ['/opt/app', '--ntilde-terminfo-owner=' + runId] },
    worker: { pid: 42, startTimeTicks: '9007199254740995', executable: '/usr/bin/node', executableFile: file(11), argv: ['/usr/bin/node', '/private/worker.mjs', '--ntilde-terminfo-worker=' + runId] },
    pty: { path: '/dev/pts/1', device: device() } });
const tool = () => ({ path: '/usr/bin/stty', sha256: 'b'.repeat(64) });
const process = (value, ppid, controllingDevice) => ({ platform: 'linux', pid: value.pid, ppid,
    startTimeTicks: value.startTimeTicks, executable: value.executable, executableFile: structuredClone(value.executableFile),
    argv: [...value.argv], uids: [1000, 1000, 1000, 1000], processGroupId: value.pid, sessionId: value.pid, controllingDevice });
function synthetic() {
    const expected = input();
    const state = { time: 0n, events: [], timeouts: [], rows: 13, cols: 37, output: true, hook: null, pause: null,
        facts: { qualification: 'provider-foundation-only', parent: process(expected.app, 38, '0'),
            child: { ...process(expected.worker, 40, '34817'), pty: { selectedFd: 1,
                descriptors: [0, 1].map(fd => ({ fd, path: '/dev/pts/1', device: device() })) } } } };
    const note = (kind, detail) => { state.events.push([kind, detail]); state.hook?.(kind, detail); };
    const heldTool = { fd: 7, observation: Object.freeze({ path: '/usr/bin/stty', sha256: 'b'.repeat(64), file: Object.freeze(file(9)) }) };
    const heldPty = { fd: 8, observation: Object.freeze({ path: '/dev/pts/1', device: Object.freeze(device()) }) };
    state.io = {
        now: () => state.time, currentUid: () => 1000,
        facts(_expected, timeout) { note('facts', timeout); state.timeouts.push(timeout); return state.facts; },
        openTool() { note('openTool'); return heldTool; }, openPty() { note('openPty'); return heldPty; },
        checkTool() { note('checkTool'); }, checkPty() { note('checkPty'); },
        async run(_tool, _pty, kind, saved, budget) {
            budget.utilityMs(); note('run:' + kind, saved);
            if (state.pause) await state.pause(kind);
            if (kind === 'disable') state.output = false;
            if (kind === 'restore') state.output = saved.split(':')[1] === '5';
            state.hook?.('after:' + kind);
            if (kind === 'version') return 'stty (GNU coreutils) 9.8\nCopyright GNU\n';
            if (kind === 'mode') return state.output ? '1:5:3:4\n' : '1:4:3:4\n';
            if (kind === 'size') return `${state.rows} ${state.cols}\n`;
            if (kind === 'description') return 'speed 38400 baud; ' + (state.output ? 'opost' : '-opost') + ' -onlcr;\n';
            return '';
        },
        close(held) { note(held === heldTool ? 'closeTool' : 'closePty'); },
    };
    state.deadline = () => ({ deadlineNs: linuxOutputDeadline(4000, state.io.now) });
    state.prepare = (value = expected, utility = tool()) => prepareLinuxOutputWithIo(state.io, value, utility, state.deadline());
    return state;
}

for (const [name, mutate] of [
    ['wrong UID', x => x.uid++], ['bad birth precision', x => x.app.startTimeTicks = 9007199254740993],
    ['bad birth overflow', x => x.worker.startTimeTicks = '18446744073709551616'],
    ['relative executable', x => x.app.executable = 'opt/app'], ['traversal', x => x.worker.executable = '/opt/../app'],
    ['missing App marker', x => x.app.argv.pop()], ['duplicate worker marker', x => x.worker.argv.push(x.worker.argv[2])],
    ['same PID', x => x.worker.pid = x.app.pid], ['fake Darwin TTY', x => x.pty.path = '/dev/ttys001'],
    ['arbitrary TTY', x => x.pty.path = '/dev/tty'], ['world-write PTY', x => x.pty.device.mode = String(0o20622)],
    ['different device UID', x => x.pty.device.uid = '1001'], ['unsafe identity number', x => x.pty.device.ino = 9007199254740993],
    ['unknown field', x => x.anyCommand = '-echo'], ['symbol field', x => x[Symbol('x')] = 1],
]) {
    test('refuse ' + name + ' before any provider/resource/utility side effect', async () => {
        const state = synthetic(), expected = input(); mutate(expected);
        await assert.rejects(state.prepare(expected)); assert.deepEqual(state.events, []);
    });
}

test('nested getters and coercion objects are rejected without executing user code', async () => {
    for (const location of ['runId', 'argv', 'device', 'path', 'hash']) {
        const state = synthetic(), expected = input(), utility = tool(); let calls = 0;
        const get = () => { calls++; throw new Error('must not call getter'); };
        if (location === 'runId') Object.defineProperty(expected, 'runId', { get, enumerable: true });
        if (location === 'argv') Object.defineProperty(expected.worker.argv, '1', { get, enumerable: true });
        if (location === 'device') Object.defineProperty(expected.pty.device, 'mode', { get, enumerable: true });
        if (location === 'path') expected.pty.path = { toString: get };
        if (location === 'hash') utility.sha256 = { toString: get };
        await assert.rejects(state.prepare(expected, utility)); assert.equal(calls, 0); assert.deepEqual(state.events, []);
    }
});

for (const value of [{ path: 'stty', sha256: 'b'.repeat(64) }, { path: '/tmp/stty', sha256: 'b'.repeat(64) },
    { path: '/usr/bin/stty', sha256: 'no' }, { ...tool(), args: ['-echo'] }]) {
    test('no arbitrary command/tool option surface: ' + value.path + Object.keys(value).join(','), () => assert.throws(() => copyLinuxOutputTool(value)));
}

test('ordinary0620 PTYs and lossless identities are accepted and defensively copied', () => {
    const expected = input(), value = copyLinuxOutputExpected(expected, 1000);
    expected.worker.argv.push('mutation'); expected.pty.device.ino = '99';
    assert.equal(value.worker.startTimeTicks, '9007199254740995'); assert.equal(value.worker.argv.length, 3);
    assert.equal(value.pty.device.ino, '20'); assert.equal(value.pty.device.mode, String(0o20620));
    assert.throws(() => value.worker.argv.push('bad')); assert.throws(() => { value.pty.device.mode = '0'; });
});

test('GNU grammar treats-g andsize separately, requiring unambiguous actual output flags', () => {
    assert.equal(parseLinuxOutputMode('1:5:3:4\n'), '1:5:3:4');
    assert.deepEqual(parseLinuxOutputSize('13 37\n'), { rows: 13, cols: 37 });
    assert.equal(parseLinuxOutputProcessing('opost -onlcr;\n'), true);
    assert.equal(parseLinuxOutputProcessing('\topost\t-onlcr;\n'), true);
    assert.equal(parseLinuxOutputProcessing('-opost -onlcr;\n'), false);
    assert.equal(parseLinuxSttyVersion('stty (GNU coreutils) 9.8\nlicense\n'), 'stty (GNU coreutils) 9.8');
    for (const value of ['-echo\n', '1:2:3\n', '1:2:3:4\r\n', '1:2:3:4\nextra', '1:2:3:4;echo x\n']) assert.throws(() => parseLinuxOutputMode(value));
    for (const value of ['0 37\n', '65536 37\n', '13;37\n', '13 037\n', '13 37\nmore']) assert.throws(() => parseLinuxOutputSize(value));
    for (const value of ['', 'xopost\n', '-opost opost\n', '-opost -opost\n', '-opost\0', 'opost🙂\n', 'opost\r\n', 'opost\u0085\n']) assert.throws(() => parseLinuxOutputProcessing(value));
    assert.throws(() => parseLinuxSttyVersion('stty (BSD)\n'));
});

for (const [name, mutate] of [
    ['App birth', x => x.parent.startTimeTicks = '9'], ['App executable file', x => x.parent.executableFile.ino = '99'],
    ['App argv', x => x.parent.argv.push('extra')], ['App parent', x => x.parent.ppid = 39],
    ['worker birth', x => x.child.startTimeTicks = '9'], ['worker argv', x => x.child.argv.pop()],
    ['worker UID', x => x.child.uids[1] = 1001], ['worker parent', x => x.child.ppid = 41],
    ['fd selection', x => x.child.pty.selectedFd = 0], ['stdin split', x => x.child.pty.descriptors[0].path = '/dev/pts/2'],
    ['descriptor device', x => x.child.pty.descriptors[1].device.ino = '99'],
]) {
    test('observed ' + name + ' mismatch refuses admission without opening a resource', async () => {
        const state = synthetic(); mutate(state.facts); await assert.rejects(state.prepare());
        assert.equal(state.events.some(([kind]) => kind.startsWith('open') || kind.startsWith('run:')), false);
    });
}

test('prepare verifies-opost, observe stays read-only, restore exact mode/size closes and is idempotent', async () => {
    const state = synthetic(), lease = await state.prepare(); assert.equal(state.output, false);
    const observed = await lease.observe(state.deadline()); assert.equal(observed.latest.outputProcessing, false);
    assert.equal(observed.latest.rows, 13); assert.equal(observed.latest.cols, 37);
    assert.throws(() => observed.acceptedFacts.child.argv.push('mutation'));
    const result = await lease.restore(state.deadline()); assert.equal(state.output, true);
    assert.equal(result.restored, true); assert.equal(result.aborted, false);
    assert.equal(result.ptyCloseConfirmed, true); assert.equal(result.toolCloseConfirmed, true);
    const count = state.events.length; assert.strictEqual(await lease.restore({}), result); assert.equal(state.events.length, count);
    await assert.rejects(lease.observe(state.deadline()));
    assert.deepEqual(state.events.filter(([kind]) => kind.startsWith('run:')).map(([kind]) => kind).filter(kind => /disable|restore/.test(kind)), ['run:disable', 'run:restore']);
});

test('caller mutation while awaiting cannot change accepted launch expectations', async () => {
    const state = synthetic(), expected = input(), pending = state.prepare(expected);
    expected.app.argv.push('late'); expected.worker.startTimeTicks = '1'; expected.pty.device.ino = '99';
    const lease = await pending;
    assert.equal(lease.observation().expected.worker.startTimeTicks, '9007199254740995');
    await lease.restore(state.deadline());
});

test('overlapping refusal aborts a pending observation and cannot be promoted by a later restore', async () => {
    const state = synthetic(), lease = await state.prepare();
    let entered, release;
    const enqueued = new Promise(resolve => { entered = resolve; });
    const held = new Promise(resolve => { release = resolve; });
    state.pause = async kind => { if (kind === 'mode') { entered(); await held; } };
    const pending = lease.observe(state.deadline());
    await enqueued;
    await assert.rejects(lease.restore(state.deadline()), /Overlapping/);
    release(); await assert.rejects(pending, /Overlapping/);
    state.pause = null;
    const error = await lease.restore(state.deadline()).catch(value => value);
    assert.equal(error.linuxOutputResources.aborted, true);
    assert.equal(state.output, true);
});

test('one absolute deadline decreases across public provider calls and refuses late operations', async () => {
    const state = synthetic(); state.hook = () => { state.time += 1000000n; };
    const lease = await state.prepare();
    assert.ok(state.timeouts[0] > state.timeouts.at(-1)); assert.ok(state.timeouts.every((n, i, a) => i === 0 || n <= a[i - 1]));
    await lease.restore(state.deadline());
    assert.throws(() => linuxOutputBudget({ deadlineNs: 0n }, () => 1n));
    assert.throws(() => linuxOutputDeadline(4001));
});

test('expiry after disable cannot start a later restoration or claim cleanup success', async () => {
    const state = synthetic(); state.hook = kind => { if (kind === 'after:disable') state.time = 4000000000n; };
    const error = await state.prepare().then(() => assert.fail('must fail'), value => value);
    assert.equal(state.output, false); assert.equal(state.events.some(([kind]) => kind === 'run:restore'), false);
    assert.equal(error.linuxOutputResources.mutationMayHaveOccurred, true);
    assert.equal(error.linuxOutputResources.ptyCloseConfirmed, true);
});

test('unconfirmed utility closure forbids later restoration even with a still-held original device', async () => {
    const state = synthetic(); state.hook = kind => { if (kind === 'after:disable') throw Object.assign(new Error('unclosed'), { utilityCloseConfirmed: false }); };
    const error = await state.prepare().then(() => assert.fail(), value => value);
    assert.equal(error.linuxOutputResources.utilityCloseConfirmed, false);
    assert.equal(state.events.some(([kind]) => kind === 'run:restore'), false);
});

test('ownership abort is sticky while original held device may be best-effort restored, never reopened', async () => {
    const state = synthetic(), lease = await state.prepare(); state.facts.child.startTimeTicks = '1';
    await assert.rejects(lease.observe(state.deadline()));
    const failure = await lease.restore(state.deadline()).then(() => assert.fail(), value => value);
    assert.equal(state.output, true); assert.equal(failure.linuxOutputResources.aborted, true);
    const count = state.events.length;
    assert.strictEqual(await lease.restore(state.deadline()).catch(value => value), failure); assert.equal(state.events.length, count);
    assert.equal(state.events.filter(([kind]) => kind === 'openPty').length, 1);
});

test('held-device drift refuses cleanup mutation and retains primary plus close failures', async () => {
    const state = synthetic(), lease = await state.prepare(); state.facts.child.argv.push('changed');
    await assert.rejects(lease.observe(state.deadline()));
    state.hook = kind => { if (['checkPty', 'closePty', 'closeTool'].includes(kind)) throw new Error(kind); };
    const failure = await lease.restore(state.deadline()).catch(value => value);
    assert.equal(state.output, false); assert.equal(failure.errors.length, 4);
    assert.equal(failure.linuxOutputResources.ptyCloseConfirmed, false); assert.equal(failure.linuxOutputResources.toolCloseConfirmed, false);
    assert.equal(state.events.filter(([kind]) => kind === 'run:restore').length, 0);
});

test('restore verifies original dimensions without introducing a resize command', async () => {
    const state = synthetic(), lease = await state.prepare(); state.rows = 14;
    const failure = await lease.restore(state.deadline()).catch(value => value);
    assert.equal(state.output, true); assert.equal(failure.linuxOutputResources.latest.rows, 14);
    assert.equal(failure.linuxOutputResources.aborted, true);
    assert.match(failure.errors[0].message, /dimensions/);
    assert.equal(state.events.some(([kind]) => /rows|cols|resize/.test(kind)), false);
});

test('preparation primary, restoration and both closes survive in order', async () => {
    const state = synthetic();
    state.hook = kind => { if (['after:disable', 'run:restore', 'closePty', 'closeTool'].includes(kind)) throw new Error(kind); };
    const error = await state.prepare().catch(value => value);
    assert.deepEqual(error.errors.map(e => e.message), ['after:disable', 'run:restore', 'closePty', 'closeTool']);
});

test('the public API cannot inject a fake proc/utility reader on any actual platform', async () => {
    if (globalThis.process.platform === 'linux') {
        const expected = input(); expected.uid = globalThis.process.getuid(); expected.pty.device.uid = String(expected.uid);
        await assert.rejects(prepareLinuxOwnedOutput(expected, tool(), { deadlineNs: 1n, reader: {} }), /Unexpected Linux output fields/);
    } else {
        assert.throws(() => prepareLinuxOwnedOutput(input(), tool(), { deadlineNs: 1n, reader: {} }), /actual Linux/);
    }
});
