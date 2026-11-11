import test from 'node:test';
import assert from 'node:assert/strict';
import { pollCommonCore } from './common-core-poll.mjs';
test('owned polling is sequential and ends after evidence arrives', async () => {
    let calls = 0,
        pending = 0,
        peak = 0;
    await pollCommonCore(
        async () => {
            pending++;
            peak = Math.max(peak, pending);
            await new Promise((resolve) => setImmediate(resolve));
            pending--;
            return ++calls === 3;
        },
        { timeoutMs: 1000, intervalMs: 1 },
    );
    assert.equal(calls, 3);
    assert.equal(peak, 1);
});
test('owned polling preserves errors and enforces deadline and attempt bounds', async () => {
    const failure = new Error('ownership lost');
    await assert.rejects(
        pollCommonCore(
            () => {
                throw failure;
            },
            { timeoutMs: 1000 },
        ),
        (error) => error === failure,
    );
    await assert.rejects(
        pollCommonCore(() => false, { timeoutMs: 0, message: 'deadline' }),
        /deadline/,
    );
    let calls = 0;
    await assert.rejects(
        pollCommonCore(
            () => {
                calls++;
                return false;
            },
            { timeoutMs: 1000, maxAttempts: 3, pause: async () => {}, message: 'attempt bound' },
        ),
        /attempt bound/,
    );
    assert.equal(calls, 3);
});
