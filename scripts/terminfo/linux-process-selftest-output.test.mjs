import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { LinuxProviderOutput, linuxProviderOutputClosed, assertLinuxProviderOutputComplete } from './fixtures/linux-provider-output.mjs';

test('fixture stdout/stderr buffers reject before append and latch overflow', () => {
    for (const initial of [0, 4090, 4096]) {
        const output = new LinuxProviderOutput();
        output.append(Buffer.alloc(initial, 120));
        assert.throws(() => output.append(Buffer.alloc(4097 - initial, 121)), /Oversized/);
        assert.equal(Buffer.byteLength(output.text()), initial);
        assert.throws(() => output.append(Buffer.from('late')), /already refused/);
        assert.equal(Buffer.byteLength(output.text()), initial);
    }
});

test('fixture buffers retain immutable bytes up to the exact ceiling', () => {
    const output = new LinuxProviderOutput(), first = Buffer.from('first');
    output.append(first); first.fill(120);
    output.append(Buffer.alloc(4091, 121));
    assert.equal(Buffer.byteLength(output.text()), 4096); assert.ok(output.text().startsWith('first'));
});


test('late invalid buffered output between exit and close prevents success', async () => {
    const child = new EventEmitter(), stream = new EventEmitter(), output = new LinuxProviderOutput();
    let failure, settled = false;
    stream.on('data', bytes => { try { output.append(bytes); } catch (error) { failure = error; } });
    const closed = linuxProviderOutputClosed(child, () => { if (failure) throw failure; });
    const observed = closed.finally(() => { settled = true; });
    child.emit('exit', 0, null); await Promise.resolve();
    assert.equal(settled, false);
    stream.emit('data', Buffer.alloc(4097));
    child.emit('close', 0, null);
    assert.match((await observed).error.message, /Oversized/);
    assert.equal(output.text(), '');
});

test('valid output is checked after close and preserves actual exit facts', async () => {
    const child = new EventEmitter(), output = new LinuxProviderOutput();
    const closed = linuxProviderOutputClosed(child, () => assert.equal(output.text(), 'final'));
    child.emit('exit', 0, null); output.append(Buffer.from('final')); child.emit('close', 0, null);
    assert.deepEqual(await closed, { code: 0, signal: null });
});


test('unterminated trailing bytes after valid frames refuse output at close', async () => {
    const child = new EventEmitter(), output = new LinuxProviderOutput();
    output.append(Buffer.from('{}\n{}\npartial'));
    const closed = linuxProviderOutputClosed(child, () => assertLinuxProviderOutputComplete(output, 6));
    child.emit('exit', 0, null); child.emit('close', 0, null);
    const result = await closed;
    assert.match(result.error.message, /unterminated/);
    assert.equal(result.code, 0); // Actual process exit is retained but cannot become successful output validation.
});
