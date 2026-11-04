import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { createLinuxSttyRunner } from './linux-owned-output-utility.mjs';
import { linuxOutputBudget } from './linux-owned-output-core.mjs';

class Pipe extends EventEmitter {
    resumed = false; destroyed = false;
    resume() { this.resumed = true; }
    destroy() { this.destroyed = true; }
}
function fixture({ automaticClose = true, spawnError = null } = {}) {
    const state = { time: 0n, timers: new Map(), nextTimer: 0, spawns: [], kills: [], unrefs: 0 };
    const child = new EventEmitter(); child.pid = 123; child.stdout = new Pipe(); child.stderr = new Pipe();
    child.unref = () => { state.unrefs++; };
    child.kill = signal => {
        state.kills.push(signal);
        if (automaticClose) queueMicrotask(() => { child.emit('exit', null, signal); child.emit('close', null, signal); });
        return true;
    };
    state.child = child;
    state.runner = createLinuxSttyRunner({
        spawnChild: (...args) => { state.spawns.push(args); if (spawnError) throw spawnError; return child; },
        setTimer: (callback, ms) => { const id = ++state.nextTimer; state.timers.set(id, { callback, when: state.time + BigInt(ms) * 1000000n }); return id; },
        clearTimer: id => state.timers.delete(id),
    });
    state.budget = () => linuxOutputBudget({ deadlineNs: state.time + 1000000000n }, () => state.time);
    state.run = (kind = 'mode', saved = null, budget = state.budget(), pty = { fd: 18 }) => state.runner({ fd: 17 }, pty, kind, saved, budget);
    state.advance = ms => {
        const end = state.time + BigInt(ms) * 1000000n;
        while (true) {
            const next = [...state.timers].filter(([, value]) => value.when <= end).sort((a, b) => a[1].when < b[1].when ? -1 : 1)[0];
            if (!next) break;
            state.time = next[1].when; state.timers.delete(next[0]); next[1].callback();
        }
        state.time = end;
    };
    return state;
}

test('fixed proc-FD executable/PTY mapping and minimal utility-only environment', async () => {
    const state = fixture(), promise = state.run();
    const [command, args, options] = state.spawns[0];
    assert.equal(command, '/proc/self/fd/4'); assert.deepEqual(args, ['--file=/proc/self/fd/3', '-g']);
    assert.deepEqual(options.stdio, ['ignore', 'pipe', 'pipe', 18, 17]);
    assert.deepEqual(options.env, { LC_ALL: 'C', LANG: 'C', PATH: '/usr/bin:/bin' });
    assert.equal(options.detached, false); assert.equal(options.shell, false); assert.equal(options.argv0, 'stty');
    state.child.stdout.emit('data', Buffer.from('1:5:3:4\n')); state.child.emit('exit', 0, null); state.child.emit('close', 0, null);
    assert.equal(await promise, '1:5:3:4\n'); assert.equal(state.timers.size, 0);
});

test('version has no TTY argument and no inherited personal stdin', async () => {
    const state = fixture(), promise = state.run('version', null, state.budget(), null);
    assert.deepEqual(state.spawns[0][1], ['--version']); assert.equal(state.spawns[0][2].stdio[3], 'ignore');
    state.child.stdout.emit('data', Buffer.from('stty (GNU coreutils) 9.8\n')); state.child.emit('close', 0, null);
    await promise;
});

for (const [kind, saved, pty] of [['sh', null, {}], ['restore', '-echo', {}], ['restore', '1:2:3:4;bad', {}],
    ['disable', 'extra', {}], ['mode', null, null]]) {
    test('arbitrary/invalid operation refused before spawn: ' + kind + String(saved), () => {
        const state = fixture(); assert.throws(() => state.run(kind, saved, state.budget(), pty)); assert.equal(state.spawns.length, 0);
    });
}

test('saved mode is one fixed argument, not a shell or caller-selected flag list', async () => {
    const state = fixture(), promise = state.run('restore', '1:5:3:4');
    assert.deepEqual(state.spawns[0][1], ['--file=/proc/self/fd/3', '1:5:3:4']);
    state.child.emit('close', 0, null); await promise;
});

test('both byte buffers hard-cap before append, latch overflow and discard subsequent chunks', async () => {
    for (const pipe of ['stdout', 'stderr']) {
        const state = fixture(), promise = state.run();
        state.child[pipe].emit('data', Buffer.alloc(100, 65));
        state.child[pipe].emit('data', Buffer.alloc(4097, 66));
        state.child[pipe].emit('data', Buffer.alloc(100000, 67));
        const failure = await promise.catch(error => error);
        assert.equal(failure.utilityCloseConfirmed, true); assert.equal(failure.output[pipe + 'Bytes'], 100);
        assert.equal(failure.output[pipe + 'Hex'].length, 200); assert.deepEqual(state.kills, ['SIGKILL']);
        assert.equal(state.child[pipe].resumed, true);
    }
});

test('accepted chunks are copied, not mutable borrowed buffers', async () => {
    const state = fixture(), promise = state.run(), bytes = Buffer.from('1:5:3:4\n');
    state.child.stdout.emit('data', bytes); bytes.fill(0); state.child.emit('close', 0, null);
    assert.equal(await promise, '1:5:3:4\n');
});

test('work timeout kills only the owned child handle and still awaits actual close', async () => {
    const state = fixture({ automaticClose: false }), promise = state.run(); let ended = false;
    promise.then(() => { ended = true; }, () => { ended = true; });
    state.advance(750); await Promise.resolve(); assert.equal(ended, false); assert.deepEqual(state.kills, ['SIGKILL']);
    state.child.emit('exit', null, 'SIGKILL'); await Promise.resolve(); assert.equal(ended, false);
    state.child.emit('close', null, 'SIGKILL');
    const failure = await promise.catch(error => error); assert.equal(failure.utilityCloseConfirmed, true);
});

test('exit alone is not stdio closure; no signal after known exit and late close cannot heal timeout', async () => {
    const state = fixture({ automaticClose: false }), promise = state.run();
    state.child.emit('exit', 0, null); state.advance(1000);
    const failure = await promise.catch(error => error); assert.equal(failure.utilityCloseConfirmed, false);
    assert.deepEqual(state.kills, []); assert.equal(state.child.stdout.destroyed, true); assert.equal(state.unrefs, 1);
    state.child.emit('close', 0, null); assert.equal(failure.utilityCloseConfirmed, false);
});

test('no observed close by shared deadline stays fatal even after successful kill signal', async () => {
    const state = fixture({ automaticClose: false }), promise = state.run();
    state.advance(1000); const failure = await promise.catch(error => error);
    assert.deepEqual(state.kills, ['SIGKILL']); assert.equal(failure.utilityCloseConfirmed, false);
    assert.equal(state.unrefs, 1); assert.equal(state.timers.size, 0);
});

test('nonzero exit and bounded diagnostics preserve process and output facts', async () => {
    const state = fixture(), promise = state.run();
    state.child.stderr.emit('data', Buffer.from('ioctl failed\n')); state.child.emit('close', 1, null);
    const failure = await promise.catch(error => error);
    assert.equal(failure.utilityCloseConfirmed, true); assert.equal(failure.output.exitCode, 1);
    assert.equal(Buffer.from(failure.output.stderrHex, 'hex').toString(), 'ioctl failed\n');
});

test('unexpected stderr, invalid UTF-8 and stream errors cannot produce success', async () => {
    const noisy = fixture(), noise = noisy.run(); noisy.child.stderr.emit('data', Buffer.from('warning\n')); noisy.child.emit('close', 0, null);
    await assert.rejects(noise);
    const invalid = fixture(), bytes = invalid.run(); invalid.child.stdout.emit('data', Buffer.from([255])); invalid.child.emit('close', 0, null);
    const decoded = await bytes.catch(error => error); assert.equal(decoded.utilityCloseConfirmed, true);
    const broken = fixture(), stream = broken.run(); broken.child.stdout.emit('error', new Error('broken pipe'));
    await assert.rejects(stream); assert.deepEqual(broken.kills, ['SIGKILL']);
});

test('spawn throw preserves its failure and no deferred child exists', async () => {
    const original = Object.freeze(new Error('spawn refused')), state = fixture({ spawnError: original });
    const error = await state.run().catch(value => value); assert.strictEqual(error.errors[0], original); assert.equal(error.utilityCloseConfirmed, true);
});

test('an already expired or too-short shared budget cannot schedule a command', () => {
    const state = fixture(), expired = state.budget(); state.time = 1000000000n;
    assert.throws(() => state.run('disable', null, expired)); assert.equal(state.spawns.length, 0);
    const short = linuxOutputBudget({ deadlineNs: state.time + 100000000n }, () => state.time);
    assert.throws(() => state.run('disable', null, short)); assert.equal(state.spawns.length, 0);
});
