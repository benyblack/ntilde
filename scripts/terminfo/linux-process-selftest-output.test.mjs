import test from 'node:test';
import assert from 'node:assert/strict';
import { LinuxProviderOutput } from './fixtures/linux-provider-output.mjs';

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
