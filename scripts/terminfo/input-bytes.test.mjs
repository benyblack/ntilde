import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { test } from 'node:test';
import { rawInputCapture as realCapture } from './input-bytes.mjs';
const nonces = [40000, 50000];
const rawInputCapture = (input, output, timeout = 20) => realCapture(input, output, timeout, () => [...nonces]);

class Input extends EventEmitter {
    isRaw = false; paused = true;
    isPaused() { return this.paused; }
    setRawMode(value) { this.isRaw = value; }
    pause() { this.paused = true; }
    resume() { this.paused = false; }
}
const da = Buffer.from('\x1b[?40000;0$y\x1b[?50000;0$y');
const query = '\x1b[?40000$p\x1b[?50000$p';

test('independently retains high mouse octets before and after the action acknowledgement at every chunk split', async () => {
    const payload = Buffer.from([27, 91, 77, 32, 128, 255]);
    const wire = Buffer.concat([payload.subarray(2), da]);
    for (let split = 0; split <= wire.length; split++) {
        const input = new Input(), writes = [];
        const capture = rawInputCapture(input, { write(value, callback) {
            writes.push(value); callback();
            input.emit('data', wire.subarray(0, split)); input.emit('data', wire.subarray(split));
        } });
        const result = await capture.capture(async () => { input.emit('data', payload.subarray(0, 2)); });
        assert.deepEqual(result, { hex: '1b5b4d2080ff', byteLength: 6, barrier: da.toString(), nonces });
        assert.equal(capture.failed, false);
        assert.deepEqual(writes, [query]); capture.dispose();
        assert.equal(input.isRaw, false); assert.equal(input.paused, true);
        assert.equal(input.listenerCount('data'), 0);
    }
});

test('raw bytes are copied and the exact payload ceiling survives byte-by-byte DA fragmentation', async () => {
    const input = new Input(), payload = Buffer.alloc(512, 255);
    const capture = rawInputCapture(input, { write(_, callback) {
        callback(); for (const byte of da) input.emit('data', Buffer.from([byte]));
    } });
    const result = await capture.capture(async () => { input.emit('data', payload); payload.fill(65); });
    assert.equal(result.byteLength, 512); assert.equal(result.hex, 'ff'.repeat(512));
    input.emit('data', Buffer.from('late'));
    assert.equal(capture.failed, true); await assert.rejects(capture.capture(async () => {}));
    capture.dispose();
});

test('UTF8-corrupted legacy mouse input is retained as different bytes rather than normalized into a pass', async () => {
    const input = new Input();
    const capture = rawInputCapture(input, { write(_, callback) { callback(); input.emit('data', da); } });
    const result = await capture.capture(async () => input.emit('data', Buffer.from('\x1b[M \x80\xff', 'utf8')));
    assert.equal(result.hex, '1b5b4d20c280c3bf');
    assert.notEqual(result.hex, '1b5b4d2080ff'); capture.dispose();
});

test('an empty observation requires a real reply requested after the action, not a sleep', async () => {
    const input = new Input(); let completeAction;
    const capture = rawInputCapture(input, { write(_, callback) { callback(); input.emit('data', da); } });
    const pending = capture.capture(() => new Promise(resolve => { completeAction = resolve; }));
    await Promise.resolve(); completeAction();
    assert.deepEqual(await pending, { hex: '', byteLength: 0, barrier: da.toString(), nonces });
    capture.dispose();
});

for (const [name, bytes] of [
    ['foreign trailing input', Buffer.concat([da, Buffer.from('x')])],
    ['a stale fixed DA', Buffer.from('\x1b[?62;4;22c')],
    ['a mismatched challenge', Buffer.from('\x1b[?40001;0$y\x1b[?50000;0$y')],
    ['a second DA', Buffer.concat([da, da])],
    ['over-budget input', Buffer.alloc(513, 65)],
    ['an oversized reply', Buffer.from('\x1b[?' + '1'.repeat(641))],
]) test(`${name} cannot qualify or be retried as clean evidence`, async () => {
    const input = new Input();
    const capture = rawInputCapture(input, { write(_, callback) { callback(); input.emit('data', bytes); } });
    await assert.rejects(capture.capture(async () => {}));
    assert.equal(capture.failed, true); await assert.rejects(capture.capture(async () => {}));
    capture.dispose();
});

test('idle input and string-decoded input poison the capture', async () => {
    for (const value of ['decoded']) {
        const input = new Input(), writes = [];
        const capture = rawInputCapture(input, { write(value) { writes.push(value); } });
        await assert.rejects(capture.capture(async () => input.emit('data', value)));
        assert.deepEqual(writes, []); capture.dispose();
    }
    const input = new Input(), capture = rawInputCapture(input, { write() {} });
    input.emit('data', Buffer.from('human input'));
    await assert.rejects(capture.capture(async () => {}), /Unexpected/); capture.dispose();
});

test('concurrency, action failure and a write failure never leave a reusable pending phase', async () => {
    const input = new Input(), capture = rawInputCapture(input, { write() {} });
    let rejectAction;
    const first = capture.capture(() => new Promise((_, reject) => { rejectAction = reject; }));
    await assert.rejects(capture.capture(async () => {}), /Concurrent/);
    rejectAction(new Error('ownership changed'));
    await assert.rejects(first, /ownership/); capture.dispose();
    const other = new Input(), broken = rawInputCapture(other, { write() { throw new Error('write failed'); } });
    await assert.rejects(broken.capture(async () => {}), /write failed/); broken.dispose();
});

test('deadline and disposal abort the action; late action completion cannot write a fence', async () => {
    for (const dispose of [false, true]) {
        const input = new Input(), writes = [];
        const capture = rawInputCapture(input, { write(value) { writes.push(value); } }, 10);
        let finish, signal;
        const pending = capture.capture(value => { signal = value; return new Promise(resolve => { finish = resolve; }); });
        await Promise.resolve(); if (dispose) capture.dispose();
        await assert.rejects(pending, /timed out|disposed/);
        assert.equal(signal.aborted, true); finish();
        await Promise.resolve(); await Promise.resolve();
        assert.deepEqual(writes, []); capture.dispose();
    }
});

test('exclusive undecoded stdin and finite deadline are required before changing its mode', () => {
    for (const mode of ['listener', 'encoding', 'timeout']) {
        const input = new Input();
        if (mode === 'listener') input.on('data', () => {});
        if (mode === 'encoding') input.readableEncoding = 'utf8';
        assert.throws(() => rawInputCapture(input, {}, mode === 'timeout' ? Infinity : 100));
        assert.equal(input.isRaw, false);
    }
});


test('even an exact future challenge emitted before the action finishes remains payload, not a fence', async () => {
    const input = new Input();
    const capture = rawInputCapture(input, { write(_, callback) { callback(); input.emit('data', da); } });
    const result = await capture.capture(async () => input.emit('data', da));
    assert.equal(result.hex, da.toString('hex')); assert.equal(result.byteLength, da.length);
    capture.dispose();
});

test('wrong or stale control replies before the real challenge remain visible as wrong input', async () => {
    const input = new Input(), stale = Buffer.from('\x1b[?62;4;22c');
    const capture = rawInputCapture(input, { write(_, callback) {
        callback(); input.emit('data', Buffer.concat([stale, da]));
    } });
    const result = await capture.capture(async () => {});
    assert.equal(result.hex, stale.toString('hex')); assert.equal(result.byteLength, stale.length);
    capture.dispose();
});

test('invalid, parser-clamped and reused challenges cannot prove completion', async () => {
    for (const next of [() => [40000, 40000], () => [65536, 40000], () => [1, 2], () => null]) {
        const input = new Input(), capture = realCapture(input, { write() { assert.fail('Invalid challenge must not be written.'); } }, 20, next);
        await assert.rejects(capture.capture(async () => {}), /challenge/); capture.dispose();
    }
    const input = new Input(), capture = rawInputCapture(input, { write(_, callback) { callback(); input.emit('data', da); } });
    await capture.capture(async () => {});
    await assert.rejects(capture.capture(async () => {}), /reused/); capture.dispose();
});

test('real challenges stay distinct and parser-safe through the finite capture budget', async () => {
    const input = new Input(), seen = new Set(); let actions = 0;
    const capture = realCapture(input, { write(value, callback) {
        const numbers = [...value.matchAll(/\x1b\[\?(\d+)\$p/g)].map(match => Number(match[1]));
        assert.equal(numbers.length, 2); assert.notEqual(numbers[0], numbers[1]);
        assert.equal(numbers.every(n => n >= 32768 && n <= 65535), true);
        assert.equal(seen.has(numbers.join(',')), false); seen.add(numbers.join(','));
        callback(); input.emit('data', Buffer.from(numbers.map(n => `\x1b[?${n};0$y`).join('')));
    } }, 20);
    for (let index = 0; index < 128; index++) {
        const result = await capture.capture(async () => { actions++; });
        assert.equal(result.hex, '');
    }
    await assert.rejects(capture.capture(async () => { actions++; }), /budget/);
    assert.equal(actions, 128); capture.dispose();
});

test('non-Error action failures remain immediate original failures, not timeouts or unhandled rejections', async () => {
    for (const reason of [undefined, null, false, 0, 'failure']) {
        const input = new Input(), capture = rawInputCapture(input, { write() { assert.fail('Failed action must not write.'); } });
        await assert.rejects(capture.capture(() => Promise.reject(reason)), error => {
            assert.equal(error.message, 'Generated input capture failed.'); assert.equal(error.cause, reason); return true;
        });
        assert.equal(capture.lastAttempt.error, 'Generated input capture failed.');
        assert.equal(capture.lastAttempt.completed, false); assert.equal(capture.failed, true);
        capture.dispose();
    }
});

test('observing a pending attempt cannot mutate its retained or returned challenge facts', async () => {
    const input = new Input(); let capture;
    capture = rawInputCapture(input, { write(_, callback) {
        const observation = capture.lastAttempt; observation.nonces[0] = 60000;
        assert.deepEqual(capture.lastAttempt.nonces, nonces);
        callback(); input.emit('data', da);
    } });
    const result = await capture.capture(async () => {});
    assert.deepEqual(result.nonces, nonces); result.nonces[0] = 61000;
    assert.deepEqual(capture.lastAttempt.nonces, nonces); capture.dispose();
});
