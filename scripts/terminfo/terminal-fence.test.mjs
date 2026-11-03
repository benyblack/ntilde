import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { test } from 'node:test';
import { terminalFence } from './terminal-fence.mjs';

class Input extends EventEmitter {
    isRaw = false; paused = true;
    isPaused() { return this.paused; }
    setRawMode(value) { this.isRaw = value; }
    pause() { this.paused = true; }
    resume() { this.paused = false; }
}

test('fences output with a complete DA reply and restores the input mode', async () => {
    const input = new Input(); let written;
    const fence = terminalFence(input, { write(value, callback) { written = value; callback(); } });
    const reply = fence.write('Hello');
    assert.equal(written, 'Hello\x1b[c'); assert.equal(input.isRaw, true);
    input.emit('data', Buffer.from('\x1b[?62;'));
    input.emit('data', Buffer.from('4;22c'));
    assert.equal(await reply, '\x1b[?62;4;22c');
    fence.dispose(); assert.equal(input.isRaw, false); assert.equal(input.paused, true);
    assert.equal(input.listenerCount('data'), 0);
});

test('concurrent, unexpected and oversized input cannot qualify a sample', async () => {
    const input = new Input(); const fence = terminalFence(input, { write(_, callback) { callback(); } });
    const first = fence.write('');
    await assert.rejects(fence.write(''), /Concurrent/);
    input.emit('data', Buffer.from('user input'));
    await assert.rejects(first, /Invalid/);
    await assert.rejects(fence.write(''), /Invalid/); fence.dispose();
    const other = new Input(), next = terminalFence(other, { write(_, callback) { callback(); } });
    const pending = next.write(''); other.emit('data', Buffer.from('\x1b' + 'x'.repeat(128)));
    await assert.rejects(pending, /Invalid/); next.dispose();
});

test('idle input, write errors, timeout and disposal reject pending barriers', async () => {
    const input = new Input(), fence = terminalFence(input, { write(_, callback) { callback(); } }, 5);
    await assert.rejects(fence.write(''), /timed out/); fence.dispose();
    const errorInput = new Input(), failed = terminalFence(errorInput, { write(_, callback) { callback(new Error('write failed')); } });
    await assert.rejects(failed.write(''), /write failed/); failed.dispose();
    const disposeInput = new Input(), disposed = terminalFence(disposeInput, { write(_, callback) { callback(); } });
    const pending = disposed.write(''); disposed.dispose(); await assert.rejects(pending, /disposed/);
    const unsolicited = new Input(), idle = terminalFence(unsolicited, { write() {} });
    unsolicited.emit('data', Buffer.from('x')); await assert.rejects(idle.write(''), /Unexpected/); idle.dispose();
});

test('typed query uses one ordering fence and handles fragmented replies', async () => {
    const input = new Input(); let written;
    const fence = terminalFence(input, { write(value, callback) { written = value; callback(); } });
    const pending = fence.query({ code: 4, index: 200 }, '\x1b[0m');
    assert.equal(written, '\x1b[0m\x1b]4;200;?\x1b\\\x1b[c');
    await assert.rejects(fence.write(''), /Concurrent/);
    const raw = '\x1b]4;200;rgb:1212/3434/5656\x1b\\';
    for (const byte of Buffer.from(raw + '\x1b[?62;4;22c')) input.emit('data', Buffer.from([byte]));
    assert.deepEqual(await pending, { barrier: '\x1b[?62;4;22c', reply: { raw, channels16: [0x1212, 0x3434, 0x5656] } });
    fence.dispose(); assert.equal(input.isRaw, false); assert.equal(input.paused, true);
});

test('explicit absent query reply completes only with DA, and late input poisons the fence', async () => {
    const input = new Input(), fence = terminalFence(input, { write(_, callback) { callback(); } });
    const pending = fence.query({ code: 17 });
    input.emit('data', Buffer.from('\x1b[?62;4;22c'));
    assert.deepEqual(await pending, { barrier: '\x1b[?62;4;22c', reply: null });
    input.emit('data', Buffer.from('\x1b]17;rgb:1212/3434/5656\x1b\\'));
    await assert.rejects(fence.write(''), /Unexpected/); fence.dispose();
});

test('typed query mismatches and over-budget frames remain fatal', async () => {
    for (const response of ['\x1b]19;rgb:1212/3434/5656\x1b\\\x1b[?62c', '\x1b]' + 'x'.repeat(513)]) {
        const input = new Input(), fence = terminalFence(input, { write(_, callback) { callback(); } });
        const pending = fence.query({ code: 17 });
        input.emit('data', Buffer.from(response));
        await assert.rejects(pending); await assert.rejects(fence.write('')); fence.dispose();
    }
});
