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

test('cleanup cannot bypass a healthy fence, revive failed reads or run after disposal', async () => {
    const input = new Input(), writes = [];
    const fence = terminalFence(input, { write(value, callback) { writes.push(value); callback(); } });
    await assert.rejects(fence.writeCleanup('reset'), /failed/);
    assert.deepEqual(writes, []);
    input.emit('data', Buffer.from('unsolicited'));
    assert.equal(fence.failed, true);
    await assert.rejects(fence.writeCleanup('x'.repeat(4097)), /bounded/);
    await fence.writeCleanup('reset'); assert.deepEqual(writes, ['reset']);
    await assert.rejects(fence.query({ code: 10 }), /Unexpected/);
    fence.dispose(); await assert.rejects(fence.writeCleanup('reset'), /active/);
});
for (const mode of ['callback-error', 'throw', 'timeout']) test(`cleanup remains bounded when output ${mode}`, async () => {
    const input = new Input();
    const fence = terminalFence(input, { write(_, callback) {
        if (mode === 'callback-error') callback(new Error('cleanup output failed'));
        else if (mode === 'throw') throw new Error('cleanup output threw');
    } }, 5);
    input.emit('data', Buffer.from('unsolicited'));
    try { await assert.rejects(fence.writeCleanup('reset'), /cleanup output|timed out/); }
    finally { fence.dispose(); }
});

test('healthy idle input handoff preserves modes, is exclusive and leaves restoration to outer dispose', async () => {
    const input = new Input(), writes = [];
    const fence = terminalFence(input, { write(value) { writes.push(value); } });
    fence.detachInput();
    assert.equal(input.listenerCount('data'), 0); assert.equal(input.listenerCount('error'), 0);
    assert.equal(input.isRaw, true); assert.equal(input.paused, false);
    await assert.rejects(fence.write('forbidden'), /detached|active/);
    await assert.rejects(fence.query({ code: 10 }), /detached|active/);
    assert.throws(() => fence.detachInput(), /detached|active/); assert.deepEqual(writes, []);
    fence.dispose(); assert.equal(input.isRaw, false); assert.equal(input.paused, true);
});
test('pending, fatal or disposed fences cannot transfer input ownership', async () => {
    const input = new Input(), fence = terminalFence(input, { write() {} });
    const pending = fence.write('');
    assert.throws(() => fence.detachInput(), /idle|pending/);
    assert.equal(input.listenerCount('data'), 1);
    input.emit('data', Buffer.from('bad')); await assert.rejects(pending);
    assert.throws(() => fence.detachInput(), /healthy|failed/);
    fence.dispose(); assert.throws(() => fence.detachInput(), /active|disposed/);
});

for (const detached of [false, true]) test('private stock handoff retires the fence without restoring modes, detached=' + detached, async () => {
    const input = new Input(), fence = terminalFence(input, { write() { throw Error('retired writer'); } });
    if (detached) fence.detachInput();
    fence.retireInput();
    assert.equal(input.isRaw, true); assert.equal(input.paused, false);
    assert.equal(input.listenerCount('data'), 0); assert.equal(input.listenerCount('error'), 0);
    await assert.rejects(fence.write('forbidden'), /active|disposed|detached/);
    assert.throws(() => fence.detachInput()); assert.throws(() => fence.retireInput());
    fence.dispose(); assert.equal(input.isRaw, false); assert.equal(input.paused, true);
});

test('a color exchange writes the caller\'s exact BEL or ST request and retains the raw reply or its explicit absence before the DA barrier', async () => {
    const input = new Input(); let written;
    const fence = terminalFence(input, { write(value, callback) { written = value; callback(); } });
    const answered = fence.colorExchange('\x1b]10;?\x07', { code: 10 });
    assert.equal(written, '\x1b]10;?\x07\x1b[c', 'the pinned BEL spelling reaches the terminal byte for byte');
    input.emit('data', Buffer.from('\x1b]10;rgb:d3d3/d3d3/d3d3\x1b\\\x1b[?62;4;22c'));
    assert.deepEqual(await answered, { barrier: '\x1b[?62;4;22c', reply: { raw: '\x1b]10;rgb:d3d3/d3d3/d3d3\x1b\\', channels16: [0xd3d3, 0xd3d3, 0xd3d3] } });
    const silent = fence.colorExchange('\x1b]4;7;?\x1b\\', { code: 4, index: 7 });
    input.emit('data', Buffer.from('\x1b[?62;4;22c'));
    assert.deepEqual(await silent, { barrier: '\x1b[?62;4;22c', reply: null });
    const wrong = fence.colorExchange('\x1b]11;?\x07', { code: 11 });
    input.emit('data', Buffer.from('\x1b]12;rgb:0000/0000/0000\x1b\\'));
    await assert.rejects(wrong, /Unexpected color reply/);
    await assert.rejects(fence.colorExchange('\x1b]11;?\x07', { code: 11 }), /Unexpected color reply/);
    const another = terminalFence(new Input(), { write(_, callback) { callback(); } });
    await assert.rejects(another.colorExchange('', { code: 10 }), /Invalid owned color query request/);
    await assert.rejects(another.colorExchange('x'.repeat(65), { code: 10 }), /Invalid owned color query request/);
    await assert.rejects(another.colorExchange('\x1b]10;?\x07', { code: 99 }), /Invalid owned color query/);
});

test('a caller-parsed exchange writes the exact request and the fence accepts only what the parser accepts', async () => {
    const { modeReportQuery, parseModeReportFrame } = await import('./mode-report.mjs');
    const exchange = async (chunks, mode = 5) => {
        const input = new Input(); let written;
        const fence = terminalFence(input, { write(value, callback) { written = value; callback(); } });
        const result = fence.exchange(modeReportQuery(mode), bytes => parseModeReportFrame(bytes, mode));
        assert.equal(written, `\x1b[?${mode}$p\x1b[c`);
        for (const chunk of chunks) input.emit('data', Buffer.from(chunk));
        try { return await result; } finally { fence.dispose(); }
    };
    // A DECRPM reply split across reads, then the DA barrier.
    const split = await exchange(['\x1b[?5;', '1$y', '\x1b[?62;4c']);
    assert.equal(split.reply.raw, '\x1b[?5;1$y'); assert.equal(split.reply.status, 1); assert.equal(split.barrier, '\x1b[?62;4c');
    // A DA barrier alone is an explicit absence, never a reply.
    assert.deepEqual(await exchange(['\x1b[?62;4c']), { barrier: '\x1b[?62;4c', reply: null });
    // A reply for another mode, a malformed status, or stray bytes are refused.
    for (const bad of ['\x1b[?7;1$y\x1b[?62;4c', '\x1b[?5;9$y\x1b[?62;4c', 'x\x1b[?62;4c']) await assert.rejects(exchange([bad]), /different mode|status|Unexpected|Invalid/);
    // The fence rejects a malformed exchange request without writing it.
    const input = new Input(), fence = terminalFence(input, { write() { throw new Error('written'); } });
    for (const args of [['', () => null], ['x'.repeat(65), () => null], ['x', null], [5, () => null]]) await assert.rejects(fence.exchange(...args), /Invalid owned exchange/);
    fence.dispose();
});
